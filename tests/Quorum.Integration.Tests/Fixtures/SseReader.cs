using System.Text.Json;

namespace Quorum.Integration.Tests.Fixtures;

/// <summary>Reads Quorum's chat stream: <c>delta</c> events with text, then one <c>done</c> event.</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadEventsAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? eventType = null;
        string? data = null;

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                if (data is not null)
                {
                    yield return new SseEvent(eventType ?? "message", JsonDocument.Parse(data).RootElement.Clone());
                }

                eventType = null;
                data = null;
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data = line["data:".Length..].Trim();
            }
        }
    }

    public static async Task<IReadOnlyList<SseEvent>> ReadAllAsync(HttpResponseMessage response)
    {
        var events = new List<SseEvent>();
        await foreach (var sseEvent in ReadEventsAsync(response))
        {
            events.Add(sseEvent);
        }

        return events;
    }

    public static string Text(IEnumerable<SseEvent> events) =>
        string.Concat(events.Where(e => e.Type == "delta").Select(e => e.Data.GetProperty("text").GetString()));
}

public sealed record SseEvent(string Type, JsonElement Data);
