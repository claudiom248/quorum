using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Conversations.Domain;
using Quorum.Modules.Conversations.Persistence;

namespace Quorum.Modules.Conversations.Streaming;

/// <summary>
/// Relays the assistant's answer as server-sent events (<c>reasoning</c>, <c>delta</c>, <c>tool_call</c>, <c>ask_user</c> and <c>tool_result</c> events,
/// <c>ping</c> events while it is silent, then one <c>done</c> event) and stores it
/// when it ends: <see cref="MessageStatus.Completed"/>, <see cref="MessageStatus.Failed"/> with the partial text when the
/// model breaks, or <see cref="MessageStatus.Interrupted"/> when the client goes away or the answer is stopped.
/// </summary>
internal static class AnswerStream
{
    /// <summary>How long the stream may stay silent before a <c>ping</c> event: reverse proxies commonly cut a connection that is idle for 60 seconds.</summary>
    internal static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

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
                Task<bool>? next = null;
                try
                {
                    while (true)
                    {
                        next = chunks.MoveNextAsync().AsTask();

                        // While the answer waits (for the user, or a slow model) the connection stays busy, so a proxy does not cut it.
                        while (!await ArrivedAsync(next, clock))
                        {
                            yield return new SseItem<object>(new AnswerPing(), "ping");
                        }

                        AnswerChunk chunk;
                        try
                        {
                            if (!await next)
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
                                UserQuestionEvent question => new SseItem<object>(
                                    new AskUserEventData(question.CallId, question.Question, question.ExpiresAt),
                                    "ask_user"),
                                _ => throw new InvalidOperationException($"Unknown tool event {tool.GetType().Name}."),
                            };
                            continue;
                        }

                        content.Append(chunk.Text);
                        yield return new SseItem<object>(new AnswerDelta(chunk.Text), "delta");
                    }
                }
                finally
                {
                    // The client went away while the answer was still producing: it must end before its enumerator is disposed.
                    if (next is { IsCompleted: false })
                    {
                        await lease.CancelAsync();
                        try
                        {
                            await next;
                        }
                        catch (Exception exception) when (exception is OperationCanceledException or ModelStreamException)
                        {
                            // The answer is over either way; what it stored is kept by the code below.
                        }
                    }
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

    /// <summary>Whether the next chunk is there, after waiting up to <see cref="PingInterval"/> for it.</summary>
    private static async Task<bool> ArrivedAsync(Task next, TimeProvider clock)
    {
        if (next.IsCompleted)
        {
            return true;
        }

        using var tick = new CancellationTokenSource();
        var delay = Task.Delay(PingInterval, clock, tick.Token);
        var first = await Task.WhenAny(next, delay);
        await tick.CancelAsync();
        return first == next;
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

internal sealed record AskUserEventData(Guid CallId, string Question, DateTimeOffset ExpiresAt);

internal sealed record AnswerPing;

internal sealed record AnswerDone(Guid MessageId, string Status, string? Error);
