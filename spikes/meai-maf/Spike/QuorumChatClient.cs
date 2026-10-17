using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

/// <summary>
/// Adapter B: an IChatClient written directly over the OpenAI-compatible wire format. It is what is left of Quorum's
/// OpenAiChatStream once the loop and the executor are gone: request mapping, SSE parsing, reasoning fields, tool-call assembly.
/// </summary>
sealed class QuorumChatClient(HttpClient http, Uri baseUrl, string model) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        this.GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl.ToString().TrimEnd('/')}/chat/completions"))
        {
            Content = new StringContent(BuildBody(messages, options), Encoding.UTF8, "application/json"),
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var calls = new Assembler();
        await foreach (var item in SseParser.Create(body).EnumerateAsync(cancellationToken))
        {
            if (item.Data == "[DONE]") break;
            using var doc = JsonDocument.Parse(item.Data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (!choices[0].TryGetProperty("delta", out var delta)) continue;

            var update = new ChatResponseUpdate(ChatRole.Assistant, (string?)null);
            update.Contents.Clear();
            foreach (var field in new[] { "reasoning_content", "reasoning" })
                if (delta.TryGetProperty(field, out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } rt)
                    update.Contents.Add(new TextReasoningContent(rt));
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } ct)
                update.Contents.Add(new TextContent(ct));
            if (delta.TryGetProperty("tool_calls", out var tc)) calls.Add(tc);
            if (update.Contents.Count > 0) yield return update;
        }

        var built = calls.Build();
        if (built.Count > 0) yield return new ChatResponseUpdate(ChatRole.Assistant, [.. built]);
    }

    private string BuildBody(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var wire = new JsonArray();
        foreach (var m in messages)
        {
            if (m.Role == ChatRole.Tool)
            {
                foreach (var r in m.Contents.OfType<FunctionResultContent>())
                    wire.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.CallId, ["content"] = r.Result?.ToString() ?? "" });
                continue;
            }
            var node = new JsonObject { ["role"] = m.Role.Value, ["content"] = string.Concat(m.Contents.OfType<TextContent>().Select(t => t.Text)) };
            var fc = m.Contents.OfType<FunctionCallContent>().ToList();
            if (fc.Count > 0)
                node["tool_calls"] = new JsonArray(fc.Select(f => (JsonNode)new JsonObject
                {
                    ["id"] = f.CallId, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = f.Name, ["arguments"] = JsonSerializer.Serialize(f.Arguments ?? new Dictionary<string, object?>()) },
                }).ToArray());
            wire.Add(node);
        }

        var body = new JsonObject { ["model"] = options?.ModelId ?? model, ["stream"] = true, ["messages"] = wire };
        var tools = options?.Tools?.OfType<AIFunctionDeclaration>().ToList();
        if (tools is { Count: > 0 })
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = JsonNode.Parse(t.JsonSchema.GetRawText()) },
            }).ToArray());
        return body.ToJsonString();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    /// <summary>Same rules as Quorum's ToolCallAssembler (index-keyed fragments, whole calls, arguments as string or object, missing ids).</summary>
    private sealed class Assembler
    {
        private sealed class Partial { public string Id = ""; public string Name = ""; public StringBuilder Args = new(); }

        private readonly List<Partial> _calls = [];
        private readonly Dictionary<int, Partial> _byIndex = [];

        public void Add(JsonElement array)
        {
            foreach (var f in array.EnumerateArray())
            {
                var id = f.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                var fn = f.GetProperty("function");
                var name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                int? index = f.TryGetProperty("index", out var x) && x.TryGetInt32(out var xi) ? xi : null;

                Partial cur;
                if (index is { } k && _byIndex.TryGetValue(k, out var existing) && (id is null || existing.Id == "" || existing.Id == id)) cur = existing;
                else
                {
                    cur = new Partial();
                    _calls.Add(cur);
                    if (index is { } k2) _byIndex[k2] = cur;
                }

                if (cur.Id == "" && id is not null) cur.Id = id;
                if (cur.Name == "" && name is not null) cur.Name = name;

                if (fn.TryGetProperty("arguments", out var a))
                {
                    if (a.ValueKind == JsonValueKind.String) cur.Args.Append(a.GetString());
                    else if (a.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) cur.Args.Clear().Append(a.GetRawText());
                }
            }
        }

        public List<AIContent> Build() => _calls.Select(c =>
        {
            var id = c.Id.Length > 0 ? c.Id : $"call_{Guid.NewGuid():N}";
            var args = string.IsNullOrWhiteSpace(c.Args.ToString()) ? "{}" : c.Args.ToString();
            return (AIContent)new FunctionCallContent(id, c.Name, JsonSerializer.Deserialize<Dictionary<string, object?>>(args));
        }).ToList();
    }
}
