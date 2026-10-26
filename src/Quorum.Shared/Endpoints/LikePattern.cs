namespace Quorum.Shared.Endpoints;

/// <summary>Turns text typed by an admin into the operand of an <c>ILIKE</c> with <see cref="EscapeCharacter"/> as escape: text, not a pattern.</summary>
public static class LikePattern
{
    public const string EscapeCharacter = "\\";

    /// <summary>The text with its wildcards and the escape character escaped.</summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text
            .Replace(EscapeCharacter, EscapeCharacter + EscapeCharacter, StringComparison.Ordinal)
            .Replace("%", EscapeCharacter + "%", StringComparison.Ordinal)
            .Replace("_", EscapeCharacter + "_", StringComparison.Ordinal);
    }

    /// <summary>A pattern that matches the trimmed text anywhere.</summary>
    public static string Contains(string text) => "%" + Escape(text.Trim()) + "%";
}
