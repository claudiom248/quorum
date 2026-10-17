using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Conversations.Domain;
using Quorum.Modules.Conversations.Persistence;

namespace Quorum.Modules.Conversations.Streaming;

/// <summary>
/// Relays the assistant's answer as server-sent events (<c>reasoning</c>, <c>delta</c>, <c>tool_call</c> and <c>tool_result</c> events, then one <c>done</c> event) and stores it
/// when it ends: <see cref="MessageStatus.Completed"/>, <see cref="MessageStatus.Failed"/> with the partial text when the
/// model breaks, or <see cref="MessageStatus.Interrupted"/> when the client goes away or the answer is stopped.
/// </summary>
internal static class AnswerStream
{
    public static async IAsyncEnumerable<SseItem<object>> RelayAsync(
        ConversationsDbContext db,
        Conversation conversation,
        IAsyncEnumerable<AnswerChunk> answer,
        ConversationLocks.Lease lease,
        TimeProvider clock)
    {
        var content = new StringBuilder();
        Message? stored = null;

        try
        {
            var status = MessageStatus.Completed;
            string? error = null;

            await using (var chunks = answer.GetAsyncEnumerator(lease.Token))
            {
                while (true)
                {
                    AnswerChunk chunk;
                    try
                    {
                        if (!await chunks.MoveNextAsync())
                        {
                            break;
                        }

                        chunk = chunks.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        status = MessageStatus.Interrupted;
                        break;
                    }
                    catch (ModelStreamException exception)
                    {
                        status = MessageStatus.Failed;
                        error = exception.Message;
                        break;
                    }

                    if (chunk.Kind == AnswerChunkKind.Reasoning)
                    {
                        // Shown live to the user, never stored: the message keeps only the answer.
                        yield return new SseItem<object>(new AnswerReasoning(chunk.Text), "reasoning");
                        continue;
                    }

                    if (chunk.Tool is { } tool)
                    {
                        // Tool calls are shown live and, like the reasoning, never stored (D-4).
                        yield return tool switch
                        {
                            ToolCallEvent call => new SseItem<object>(
                                new ToolCallEventData(call.CallId, call.Tool, ArgumentsOf(call.Arguments), call.RequiresConfirmation, call.ExpiresAt),
                                "tool_call"),
                            ToolResultEvent result => new SseItem<object>(
                                new ToolResultEventData(result.CallId, StatusName(result.Status), result.Content, result.SizeCharacters),
                                "tool_result"),
                            _ => throw new InvalidOperationException($"Unknown tool event {tool.GetType().Name}."),
                        };
                        continue;
                    }

                    content.Append(chunk.Text);
                    yield return new SseItem<object>(new AnswerDelta(chunk.Text), "delta");
                }
            }

            stored = await StoreAsync(db, conversation, content.ToString(), status, clock);
            yield return new SseItem<object>(new AnswerDone(stored.Id, status.ToString(), error), "done");
        }
        finally
        {
            // The client went away while an event was being written: keep what it had received.
            stored ??= await StoreAsync(db, conversation, content.ToString(), MessageStatus.Interrupted, clock);
            lease.Dispose();
        }
    }

    /// <summary>The arguments as JSON when they are valid, so clients get an object; otherwise the text the model sent.</summary>
    private static object ArgumentsOf(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return arguments;
        }
    }

    private static string StatusName(ToolResultStatus status) => status switch
    {
        ToolResultStatus.Succeeded => "succeeded",
        ToolResultStatus.Failed => "failed",
        ToolResultStatus.Denied => "denied",
        ToolResultStatus.Cancelled => "cancelled",
        ToolResultStatus.TimedOut => "timed_out",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static async Task<Message> StoreAsync(
        ConversationsDbContext db,
        Conversation conversation,
        string content,
        MessageStatus status,
        TimeProvider clock)
    {
        var message = conversation.AddAnswer(content, status, clock.GetUtcNow());
        // The request may already be aborted; the answer must be stored regardless.
        await db.SaveChangesAsync(CancellationToken.None);
        return message;
    }
}

internal sealed record AnswerDelta(string Text);

internal sealed record AnswerReasoning(string Text);

internal sealed record ToolCallEventData(Guid CallId, string Tool, object Arguments, bool RequiresConfirmation, DateTimeOffset? ExpiresAt);

internal sealed record ToolResultEventData(Guid CallId, string Status, string Content, int SizeCharacters);

internal sealed record AnswerDone(Guid MessageId, string Status, string? Error);
