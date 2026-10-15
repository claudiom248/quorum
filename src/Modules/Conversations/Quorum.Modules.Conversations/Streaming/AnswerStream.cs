using System.Net.ServerSentEvents;
using System.Text;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Conversations.Domain;
using Quorum.Modules.Conversations.Persistence;

namespace Quorum.Modules.Conversations.Streaming;

/// <summary>
/// Relays the assistant's answer as server-sent events (<c>delta</c> events, then one <c>done</c> event) and stores it
/// when it ends: <see cref="MessageStatus.Completed"/>, <see cref="MessageStatus.Failed"/> with the partial text when the
/// model breaks, or <see cref="MessageStatus.Interrupted"/> when the client goes away or the answer is stopped.
/// </summary>
internal static class AnswerStream
{
    public static async IAsyncEnumerable<SseItem<object>> RelayAsync(
        ConversationsDbContext db,
        Conversation conversation,
        IAsyncEnumerable<string> answer,
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
                    string chunk;
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

                    content.Append(chunk);
                    yield return new SseItem<object>(new AnswerDelta(chunk), "delta");
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

internal sealed record AnswerDone(Guid MessageId, string Status, string? Error);
