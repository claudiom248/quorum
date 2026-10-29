using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// The SearXNG instance an answer talks to: it records the addresses it was asked for and answers with what the test scripted —
/// results, an error, or nothing at all (for a timeout).
/// </summary>
public sealed class FakeSearxng : HttpMessageHandler
{
    private string? _json;
    private HttpStatusCode _status = HttpStatusCode.OK;
    private bool _hang;

    /// <summary>The address of every request that reached the instance, oldest first.</summary>
    public ConcurrentQueue<Uri> Requests { get; } = new();

    public int Attempts => Requests.Count;

    /// <summary>The parameters of the nth request's query string (0 is the first).</summary>
    public IReadOnlyDictionary<string, string> QueryOf(int index) =>
        Requests.ToArray()[index].Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : string.Empty),
                StringComparer.Ordinal);

    /// <summary>Answers every search with these results, in the shape SearXNG sends.</summary>
    public void Answers(params (string Title, string Url, string Content)[] results) =>
        AnswersRaw($$"""{"query":"asked","number_of_results":{{results.Length}},"results":[""" +
            string.Join(",", results.Select(r =>
                $$"""{"title":{{JsonSerializer.Serialize(r.Title)}},"url":{{JsonSerializer.Serialize(r.Url)}},"content":{{JsonSerializer.Serialize(r.Content)}},"engine":"google","score":0.9,"category":"general"}""")) +
            """]}""");

    /// <summary>Answers with the text a real instance would send, so a test can try a shape the client has not been given.</summary>
    public void AnswersRaw(string json) => Set(() => { _json = json; _status = HttpStatusCode.OK; _hang = false; });

    public void AnswersWithStatus(HttpStatusCode status, string json = "{}") => Set(() => { _json = json; _status = status; _hang = false; });

    /// <summary>Never answers: every request waits until the caller gives up on it.</summary>
    public void NeverAnswers() => Set(() => { _json = null; _hang = true; });

    public void Reset() => Set(() => { _json = null; _status = HttpStatusCode.OK; _hang = false; });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        if (_hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (_json is null)
        {
            throw new InvalidOperationException("The test has not scripted what the SearXNG instance answers.");
        }

        return new HttpResponseMessage(_status) { Content = new StringContent(_json, Encoding.UTF8, "application/json") };
    }

    // One scripted answer at a time, and the calls the fake already served are behind it.
    private void Set(Action script)
    {
        script();
        while (!Requests.IsEmpty)
        {
            Requests.TryDequeue(out _);
        }
    }
}
