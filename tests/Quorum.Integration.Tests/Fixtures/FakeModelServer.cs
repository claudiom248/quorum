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
    private readonly ConcurrentDictionary<string, string[]> _capabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<JsonElement> _chatRequests = new();
    private readonly ConcurrentQueue<ChatScript> _rounds = new();

    public string? LastAuthorization { get; private set; }

    public ChatScript Chat { get; set; } = ChatScript.Reply("Hello", " from", " the", " model.");

    /// <summary>The JSON bodies of the chat completion requests received, oldest first.</summary>
    public IReadOnlyList<JsonElement> ChatRequests => _chatRequests.ToList();

    /// <summary>
    /// Scripts for the next chat requests, one per request in order (the rounds of a tool-calling answer).
    /// When they run out, requests follow <see cref="Chat"/> again.
    /// </summary>
    public void EnqueueRounds(params ChatScript[] scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        foreach (var script in scripts)
        {
            _rounds.Enqueue(script);
        }
    }

    public void Serve(string baseUrl, params string[] models) => _endpoints[baseUrl.TrimEnd('/')] = models;

    /// <summary>
    /// Makes the server describe a model the way Ollama's <c>POST /api/show</c> does. Models without a description answer 404,
    /// like servers that have no such API.
    /// </summary>
    public void Describe(string baseUrl, string model, params string[] capabilities) =>
        _capabilities[$"{new Uri(baseUrl).Authority}|{model}"] = capabilities;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        LastAuthorization = request.Headers.Authorization?.ToString();

        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/api/show"
            && _endpoints.Keys.Any(baseUrl => new Uri(baseUrl).Authority.Equals(request.RequestUri.Authority, StringComparison.OrdinalIgnoreCase)))
        {
            var model = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("model").GetString();
            return _capabilities.TryGetValue($"{request.RequestUri.Authority}|{model}", out var capabilities)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { capabilities }) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

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
                var chatRequest = JsonDocument.Parse(json).RootElement.Clone();
                _chatRequests.Enqueue(chatRequest);
                var script = _rounds.TryDequeue(out var round) ? round : Chat;

                if (script.RejectsTools && chatRequest.TryGetProperty("tools", out _))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("{\"error\":\"llama3.2 does not support tools\"}", Encoding.UTF8, "application/json"),
                    };
                }

                if (script.HttpErrorStatus is { } status)
                {
                    return new HttpResponseMessage((HttpStatusCode)status)
                    {
                        Content = new StringContent("{\"error\":{\"message\":\"model overloaded\"}}", Encoding.UTF8, "application/json"),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new ScriptedSseStream(script)) { Headers = { { "Content-Type", "text/event-stream" } } },
                };
            }
        }

        throw new HttpRequestException($"Connection refused ({request.RequestUri!.Authority})");
    }

    /// <summary>What the fake model streams back: chunks, an optional delay before each, an optional failure.</summary>
    public sealed record ChatScript(IReadOnlyList<string> Chunks, TimeSpan DelayPerChunk, int? FailAfterChunks)
    {
        /// <summary>Reasoning streamed before the answer, in the field named by <see cref="ReasoningField"/>.</summary>
        public IReadOnlyList<string> Reasoning { get; init; } = [];

        /// <summary>When set, the chat endpoint answers with this HTTP error status instead of streaming.</summary>
        public int? HttpErrorStatus { get; init; }

        /// <summary><c>reasoning</c> as Ollama sends it, or <c>reasoning_content</c> as LiteLLM does.</summary>
        public string ReasoningField { get; init; } = "reasoning";

        /// <summary>Tool calls streamed after the text, in the shape named by <see cref="ToolCallShape"/>.</summary>
        public IReadOnlyList<FakeToolCall> ToolCalls { get; init; } = [];

        public ToolCallShape ToolCallShape { get; init; } = ToolCallShape.OpenAiFragments;

        /// <summary>When set, a request carrying <c>tools</c> is refused with HTTP 400, as models without tool support do.</summary>
        public bool RejectsTools { get; init; }

        public static ChatScript Reply(params string[] chunks) => new(chunks, TimeSpan.Zero, null);

        /// <summary>A round in which the model asks for tools instead of answering.</summary>
        public static ChatScript CallTools(params FakeToolCall[] calls) => new([], TimeSpan.Zero, null) { ToolCalls = calls };

        public static ChatScript Slow(int chunkCount, TimeSpan delayPerChunk) =>
            new(Enumerable.Range(1, chunkCount).Select(i => $"word{i} ").ToList(), delayPerChunk, null);

        public static ChatScript FailAfter(int chunks, params string[] content) => new(content, TimeSpan.Zero, chunks);
    }

    /// <summary>A call the fake model makes. <paramref name="Arguments"/> is JSON text; a null <paramref name="Id"/> sends no id.</summary>
    public sealed record FakeToolCall(string Name, string Arguments, string? Id = "call_1");

    public enum ToolCallShape
    {
        /// <summary>OpenAI: the name arrives first, the arguments in small string fragments, all keyed by <c>index</c>.</summary>
        OpenAiFragments,

        /// <summary>Ollama/llama.cpp: each call whole in one chunk, with the arguments as a JSON string.</summary>
        WholeCallStringArguments,

        /// <summary>Some Ollama builds: each call whole, the arguments as a JSON object and neither <c>index</c> nor <c>id</c> if the call has none.</summary>
        WholeCallObjectArguments,
    }

    /// <summary>OpenAI chat completion chunks as server-sent events, produced lazily so delays and failures happen mid-stream.</summary>
    private sealed class ScriptedSseStream(ChatScript script) : Stream
    {
        private readonly Queue<byte[]> _pending = new();
        private int _nextChunk;
        private int _nextReasoning;
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

                if (_nextReasoning < script.Reasoning.Count)
                {
                    var delta = new Dictionary<string, string> { ["content"] = string.Empty, [script.ReasoningField] = script.Reasoning[_nextReasoning++] };
                    _pending.Enqueue(Event(delta, finishReason: null));
                }
                else if (_nextChunk < script.Chunks.Count)
                {
                    if (script.DelayPerChunk > TimeSpan.Zero)
                    {
                        await Task.Delay(script.DelayPerChunk, cancellationToken);
                    }

                    _pending.Enqueue(Event(new { content = script.Chunks[_nextChunk++] }, finishReason: null));
                }
                else
                {
                    foreach (var delta in ToolCallDeltas())
                    {
                        _pending.Enqueue(Event(delta, finishReason: null));
                    }

                    _pending.Enqueue(Event(new { }, finishReason: script.ToolCalls.Count > 0 ? "tool_calls" : "stop"));
                    _pending.Enqueue(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
                    _finished = true;
                }
            }

            _current = _pending.Dequeue();
            _offset = 0;
            return true;
        }

        private IEnumerable<object> ToolCallDeltas()
        {
            for (var index = 0; index < script.ToolCalls.Count; index++)
            {
                var call = script.ToolCalls[index];
                switch (script.ToolCallShape)
                {
                    case ToolCallShape.OpenAiFragments:
                        yield return new
                        {
                            tool_calls = new[] { new { index, id = call.Id, type = "function", function = new { name = call.Name, arguments = string.Empty } } },
                        };
                        foreach (var fragment in call.Arguments.Chunk(7))
                        {
                            yield return new { tool_calls = new[] { new { index, function = new { arguments = new string(fragment) } } } };
                        }

                        break;

                    case ToolCallShape.WholeCallStringArguments:
                        yield return new
                        {
                            tool_calls = new[] { new { index, id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments } } },
                        };
                        break;

                    default:
                        var arguments = JsonDocument.Parse(call.Arguments).RootElement.Clone();
                        yield return new
                        {
                            tool_calls = new[] { new { id = call.Id, type = "function", function = new { name = call.Name, arguments } } },
                        };
                        break;
                }
            }
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
