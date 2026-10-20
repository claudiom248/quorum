using System.Text.RegularExpressions;

namespace Quorum.Modules.KnowledgeBases.Domain;

internal static partial class DictionaryFieldRules
{
    /// <summary>Names the generated tool parameters already use: <c>query</c> and <c>knowledge_base</c> are fixed, the suffixes build range filters.</summary>
    private static readonly string[] ReservedNames = ["query", "knowledge_base"];

    private static readonly string[] ReservedSuffixes = ["_min", "_max", "_from", "_to"];

    public static bool IsValidName(string? name) => name is not null && ValidName().IsMatch(name);

    public static bool IsReservedName(string name) =>
        ReservedNames.Contains(name, StringComparer.Ordinal)
        || ReservedSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));

    /// <summary>
    /// Matches the exact name of an enum member, ignoring case. Enum.TryParse is not used: it accepts numbers, padding and comma
    /// lists ("Text,Number" is a bitwise OR, a member of its own).
    /// </summary>
    public static bool TryParseMember<TEnum>(string? value, out TEnum member)
        where TEnum : struct, Enum
    {
        member = default;
        var name = Enum.GetNames<TEnum>().FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out member);
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();
}
