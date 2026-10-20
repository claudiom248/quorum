using System.Globalization;
using System.Text.RegularExpressions;

namespace Quorum.Modules.KnowledgeBases.Mapping;

/// <summary>The date strings a dictionary date field accepts: ISO 8601 calendar dates, with an optional time and offset.</summary>
internal static partial class Iso8601
{
    public static bool IsDateOrDateTime(string? value)
    {
        if (value is null || !DateShape().IsMatch(value))
        {
            return false;
        }

        // The shape only says the digits are in the right places; 2024-13-45 must not pass.
        var datePart = value[..10];
        return DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && (value.Length == 10 || DateTimeOffset.TryParse(value.Replace(' ', 'T'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _));
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d{1,9})?)?(Z|[+-]\d{2}(:?\d{2})?)?)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DateShape();
}
