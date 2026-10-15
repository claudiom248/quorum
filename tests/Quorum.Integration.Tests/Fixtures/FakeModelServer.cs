using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>
/// Stands in for OpenAI-compatible endpoints. Base URLs registered with <see cref="Serve"/> answer
/// <c>GET {baseUrl}/models</c> and stream <c>POST {baseUrl}/chat/completions</c> following <see cref="Chat"/>;
/// every other host behaves as unreachable.
/// </summary>
public sealed class FakeModelServer : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, string[]> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<JsonElement> _chatRequests = new();

    public string? LastAuthorization { get; private set; }

    public ChatScript Chat { get; set; } = ChatScript.Reply("Hello", " from", " the", " model.");

    /// <summary>The JSON bodies of the chat completion requests received, oldest first.</summary>
    public IReadOnlyList<JsonElement> ChatRequests => _chatRequests.ToList();

    public void Serve(string baseUrl, params string[] models) => _endpoints[baseUrl.TrimEnd('/')] = models;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        LastAuthorization = request.Headers.Authorization?.ToString();

        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        foreach (var (baseUrl, models) in _endpoints)
        {
            if (url.Equals($"{baseUrl}/models", StringComparison.OrdinalIgnoreCase))
            {
                var body = new { @object = "list", data = models.Select(id => new { id, @object = "model" }) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
            }

            if (url.Equals($"{baseUrl}/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                _chatRequests.Enqueue(JsonDocument.Parse(json).RootElement.Clone());
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ScriptedSseStream(Chat)) { Headers = { { "Content-Type", "text/event-stream" } } },
                };
            }
        }

        throw new HttpRequestException($"Connection refused ({request.RequestUri!.Authority})");
    }

    /// <summary>What the fake model streams back: chunks, an optional delay before each, an optional failure.</summary>
    public sealed record ChatScript(IReadOnlyList<string> Chunks, TimeSpan DelayPerChunk, int? FailAfterChunks)
    {
        public static ChatScript Reply(params string[] chunks) => new(chunks, TimeSpan.Zero, null);

        public static ChatScript Slow(int chunkCount, TimeSpan delayPerChunk) =>
            new(Enumerable.Range(1, chunkCount).Select(i => $"word{i} ").ToList(), delayPerChunk, null);

        public static ChatScript FailAfter(int chunks, params string[] content) => new(content, TimeSpan.Zero, chunks);
    }

    /// <summary>OpenAI chat completion chunks as server-sent events, produced lazily so delays and failures happen mid-stream.</summary>
    private sealed class ScriptedSseStream(ChatScript script) : Stream
    {
        private readonly Queue<byte[]> _pending = new();
        private int _nextChunk;
        private bool _finished;
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current is null || _offset == _current.Length)
            {
                if (!await MoveNextAsync(cancellationToken))
                {
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _current!.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async Task<bool> MoveNextAsync(CancellationToken cancellationToken)
        {
            if (_pending.Count == 0)
            {
                if (_finished)
                {
                    return false;
                }

                if (script.FailAfterChunks is { } failAfter && _nextChunk >= failAfter)
                {
                    throw new IOException("The model connection was reset.");
                }

                if (_nextChunk < script.Chunks.Count)
                {
                    if (script.DelayPerChunk > TimeSpan.Zero)
                    {
                        await Task.Delay(script.DelayPerChunk, cancellationToken);
                    }

                    _pending.Enqueue(Event(new { content = script.Chunks[_nextChunk++] }, finishReason: null));
                }
                else
                {
                    _pending.Enqueue(Event(new { }, finishReason: "stop"));
                    _pending.Enqueue(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
                    _finished = true;
                }
            }

            _current = _pending.Dequeue();
            _offset = 0;
            return true;
        }

        private static byte[] Event(object delta, string? finishReason)
        {
            var chunk = new
            {
                id = "chatcmpl-test",
                @object = "chat.completion.chunk",
                created = 1_790_000_000,
                model = "fake",
                choices = new[] { new { index = 0, delta, finish_reason = finishReason } },
            };
            return Encoding.UTF8.GetBytes($"data: {JsonSerializer.Serialize(chunk)}\n\n");
        }
    }
}
