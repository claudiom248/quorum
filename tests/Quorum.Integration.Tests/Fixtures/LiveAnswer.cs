using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Chat;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// An answer being read as it streams, so a test can act while the answer is paused (decide a call, answer a question, stop reading).
/// Events are read one at a time; nothing is skipped without being kept in <see cref="Seen"/>.
/// </summary>
internal sealed class LiveAnswer : IAsyncDisposable
{
    private static readonly TimeSpan PatientTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpResponseMessage _response;
    private readonly IAsyncEnumerator<SseEvent> _events;
    private Task<bool>? _next;

    private LiveAnswer(HttpResponseMessage response)
    {
        _response = response;
        _events = SseReader.ReadEventsAsync(response).GetAsyncEnumerator(TestContext.Current.CancellationToken);
    }

    /// <summary>Everything read so far, in order.</summary>
    public List<SseEvent> Seen { get; } = [];

    public static async Task<LiveAnswer> StartAsync(HttpClient client, Guid conversationId, string content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ChatApi.Messages(conversationId)) { Content = JsonContent.Create(new { content }) };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new LiveAnswer(response);
    }

    public static async Task<LiveAnswer> RegenerateAsync(HttpClient client, Guid conversationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ChatApi.Regenerate(conversationId));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new LiveAnswer(response);
    }

    /// <summary>The next event, or <see langword="null"/> when none arrived within <paramref name="timeout"/> (it is still awaited by the next call).</summary>
    public async Task<SseEvent?> NextAsync(TimeSpan timeout)
    {
        _next ??= _events.MoveNextAsync().AsTask();
        try
        {
            if (!await _next.WaitAsync(timeout, TestContext.Current.CancellationToken))
            {
                throw new InvalidOperationException("The stream ended.");
            }
        }
        catch (TimeoutException)
        {
            return null;
        }

        _next = null;
        Seen.Add(_events.Current);
        return _events.Current;
    }

    /// <summary>Reads until an event of this type arrives and returns it.</summary>
    public async Task<SseEvent> UntilAsync(string type)
    {
        while (true)
        {
            var next = await NextAsync(PatientTimeout) ?? throw new TimeoutException($"No '{type}' event arrived. Seen: {string.Join(", ", Seen.Select(e => e.Type))}.");
            if (next.Type == type)
            {
                return next;
            }
        }
    }

    /// <summary>Reads to the end of the stream.</summary>
    public async Task<IReadOnlyList<SseEvent>> FinishAsync()
    {
        while (true)
        {
            try
            {
                if (await NextAsync(PatientTimeout) is null)
                {
                    throw new TimeoutException($"The stream did not end. Seen: {string.Join(", ", Seen.Select(e => e.Type))}.");
                }
            }
            catch (InvalidOperationException)
            {
                return Seen;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _response.Dispose();
        try
        {
            if (_next is not null)
            {
                await _next.WaitAsync(PatientTimeout);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or HttpRequestException or TimeoutException or ObjectDisposedException)
        {
            // The response was dropped on purpose.
        }

        await _events.DisposeAsync();
    }
}
