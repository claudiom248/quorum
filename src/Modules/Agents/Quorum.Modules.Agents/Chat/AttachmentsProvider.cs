using Microsoft.Agents.AI;

namespace Quorum.Modules.Agents.Chat;

/// <summary>
/// Adds the attachments of the conversation to the instructions of the agent. The agent joins what a provider adds to its own
/// instructions with a single line feed, and <see cref="AttachmentBlock"/> begins with the two line feeds that separate it from the
/// system prompt, so the provider hands over the block without the first one: the model reads the same text, and the context
/// budget, which counts the whole block, stays exact.
/// </summary>
internal sealed class AttachmentsProvider(string block) : AIContextProvider
{
    private const string Join = "\n";

    private readonly string _instructions = block.StartsWith(Join, StringComparison.Ordinal)
        ? block[Join.Length..]
        : throw new ArgumentException("The attachments block starts with the separator that joins it to the system prompt.", nameof(block));

    protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) =>
        new(new AIContext { Instructions = _instructions });
}
