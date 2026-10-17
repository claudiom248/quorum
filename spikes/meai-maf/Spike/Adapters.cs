using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

/// <summary>Cheapest adapter (A): wraps the OpenAI-SDK-based client and re-reads the raw chunk for the reasoning fields.</summary>
sealed class ReasoningSurfacingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            // The SDK already maps reasoning_content (LiteLLM style) to TextReasoningContent; only Ollama's `reasoning` is lost.
            if (update.RawRepresentation is StreamingChatCompletionUpdate raw && !update.Contents.OfType<TextReasoningContent>().Any())
            {
                foreach (var path in ReasoningPaths)
                {
                    if (raw.Patch.TryGetValue(path, out string? text) && !string.IsNullOrEmpty(text))
                    {
                        update.Contents.Add(new TextReasoningContent(text));
                    }
                }
            }
            yield return update;
        }
    }

    private static readonly byte[][] ReasoningPaths =
    [
        Encoding.UTF8.GetBytes("$.choices[0].delta.reasoning"),
        Encoding.UTF8.GetBytes("$.choices[0].delta.reasoning_content"),
    ];
}
