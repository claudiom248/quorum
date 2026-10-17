using Quorum.Modules.Agents.Contracts;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// A message of the request to the model. The public <see cref="ChatTurn"/> only knows the user's and the assistant's text;
/// the loop adds the assistant messages that carry tool calls and the tool messages that answer them.
/// </summary>
internal abstract record ModelMessage
{
    /// <summary>How many characters the message adds to the request, the unit of the context budget.</summary>
    public abstract int Length { get; }
}

internal sealed record SystemMessage(string Content) : ModelMessage
{
    public override int Length => Content.Length;
}

internal sealed record UserMessage(string Content) : ModelMessage
{
    public override int Length => Content.Length;
}

internal sealed record AssistantMessage(string Content, IReadOnlyList<ModelToolCall> ToolCalls) : ModelMessage
{
    public override int Length => Content.Length + ToolCalls.Sum(call => call.Id.Length + call.Name.Length + call.Arguments.Length);
}

/// <summary>The result of one tool call, answering the call with the same <paramref name="ToolCallId"/>.</summary>
internal sealed record ToolMessage(string ToolCallId, string Content) : ModelMessage
{
    public override int Length => Content.Length;
}

/// <summary>A call the model asked for. <paramref name="Arguments"/> is JSON text, as the model wrote it.</summary>
internal sealed record ModelToolCall(string Id, string Name, string Arguments);

/// <summary>What one model round produced, in order: text and reasoning as they arrive, then the tool calls once the round ends.</summary>
internal readonly record struct RoundPart(AnswerChunk? Chunk, IReadOnlyList<ModelToolCall>? ToolCalls)
{
    public static RoundPart Of(AnswerChunk chunk) => new(chunk, null);

    public static RoundPart Calls(IReadOnlyList<ModelToolCall> calls) => new(null, calls);
}
