using System.Globalization;
using System.Text.Json;

namespace Quorum.Modules.Agents.Tools.Native;

/// <summary>Tells the model what day and time it is, which it cannot know.</summary>
internal sealed class CurrentDateTimeTool(TimeProvider clock) : INativeTool
{
    public string Name => NativeToolCatalog.CurrentDateTime;

    public Task<string> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        var zoneId = "UTC";
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("timezone", out var requested)
            && requested.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(requested.GetString()))
        {
            zoneId = requested.GetString()!.Trim();
        }

        var zone = FindZone(zoneId);
        var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone);
        var offset = now.ToString("zzz", CultureInfo.InvariantCulture);

        return Task.FromResult(
            $"{now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)} " +
            $"({now.ToString("dddd", CultureInfo.InvariantCulture)}), time zone {zone.Id} (UTC{offset})");
    }

    private static TimeZoneInfo FindZone(string id)
    {
        if (id.Equals("UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            throw new ToolFailedException(
                $"Unknown time zone '{Shorten(id)}'. Use an IANA time zone id such as Europe/Rome or America/New_York, or leave it out for UTC.",
                exception);
        }
    }

    private static string Shorten(string value) => value.Length <= 64 ? value : value[..64] + "…";
}
