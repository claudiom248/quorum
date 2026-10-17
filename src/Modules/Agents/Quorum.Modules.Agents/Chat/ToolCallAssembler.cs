using System.Text;
using System.Text.Json;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// Puts the tool calls of one round back together from the streamed <c>tool_calls</c> deltas. Servers differ: OpenAI sends the
/// name first and the arguments in string fragments, all keyed by <c>index</c>; Ollama and llama.cpp send each call whole; some
/// builds send <c>arguments</c> as a JSON object and leave out the id.
/// </summary>
internal sealed class ToolCallAssembler
{
    private readonly List<Partial> _calls = [];
    private readonly Dictionary<int, Partial> _byIndex = [];

    public bool HasCalls => _calls.Count > 0;

    /// <param name="toolCalls">The <c>tool_calls</c> array of one delta.</param>
    public void Add(JsonElement toolCalls)
    {
        if (toolCalls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var fragment in toolCalls.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object))
        {
            var id = Text(fragment, "id");
            var function = fragment.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
            var name = function.ValueKind == JsonValueKind.Object ? Text(function, "name") : null;

            var call = Find(fragment, id, name);
            if (id is not null && call.Id.Length == 0)
            {
                call.Id = id;
            }

            if (name is not null)
            {
                if (call.Name.Length == 0)
                {
                    call.Name = name;
                }
                else if (call.Name != name)
                {
                    call.Name += name;
                }
            }

            if (function.ValueKind == JsonValueKind.Object && function.TryGetProperty("arguments", out var arguments))
            {
                switch (arguments.ValueKind)
                {
                    case JsonValueKind.String:
                        call.Arguments.Append(arguments.GetString());
                        break;
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        break;
                    default:
                        // Some servers send the whole object at once instead of text fragments.
                        call.Arguments.Clear().Append(arguments.GetRawText());
                        break;
                }
            }
        }
    }

    /// <summary>The calls in the order the model emitted them, each with a unique id and arguments that are at least <c>{}</c>.</summary>
    public IReadOnlyList<ModelToolCall> Build()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var calls = new List<ModelToolCall>(_calls.Count);

        foreach (var partial in _calls)
        {
            var id = partial.Id;
            if (id.Length == 0 || !used.Add(id))
            {
                do
                {
                    id = $"call_{Guid.NewGuid():N}";
                }
                while (!used.Add(id));
            }

            var arguments = partial.Arguments.ToString();
            calls.Add(new ModelToolCall(id, partial.Name, string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments));
        }

        return calls;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private Partial Find(JsonElement fragment, string? id, string? name)
    {
        int? index = fragment.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out var number)
            ? number
            : null;

        if (index is { } key)
        {
            // A server that numbers every parallel call 0 still gives each its own id.
            if (_byIndex.TryGetValue(key, out var existing) && (id is null || existing.Id.Length == 0 || existing.Id == id))
            {
                return existing;
            }

            return _byIndex[key] = Start();
        }

        // Without an index, a fragment that carries an id or a name begins a call and one that carries neither continues the last.
        return (id is not null || name is not null || _calls.Count == 0) ? Start() : _calls[^1];
    }

    private Partial Start()
    {
        var call = new Partial();
        _calls.Add(call);
        return call;
    }

    private sealed class Partial
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public StringBuilder Arguments { get; } = new();
    }
}
