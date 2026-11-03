namespace Quorum.Web.Client;

/// <summary>How the tool calls of an answer are drawn. A cookie picks the style so they can be compared side by side.</summary>
public enum LedgerStyle
{
    Classic,
    Cards,
    Timeline,
    Bars,
}

public static class LedgerStyles
{
    public const string CookieName = "quorum.ledger";

    public const string QueryName = "ledger";

    /// <summary>The style a cookie or a query value names: <c>a</c>, <c>b</c> or <c>c</c>; anything else is the classic ledger.</summary>
    public static LedgerStyle Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "a" or "cards" => LedgerStyle.Cards,
        "b" or "timeline" => LedgerStyle.Timeline,
        "c" or "bars" => LedgerStyle.Bars,
        _ => LedgerStyle.Classic,
    };

    public static string Key(LedgerStyle style) => style switch
    {
        LedgerStyle.Cards => "a",
        LedgerStyle.Timeline => "b",
        LedgerStyle.Bars => "c",
        _ => "classic",
    };

    public static string CssClass(LedgerStyle style) => style switch
    {
        LedgerStyle.Cards => "lg-cards",
        LedgerStyle.Timeline => "lg-timeline",
        LedgerStyle.Bars => "lg-bars",
        _ => string.Empty,
    };

    /// <summary>One of eight stable tints per agent, so the same agent keeps its colour from one answer to the next.</summary>
    public const int Tints = 8;

    public static int TintOf(string slug)
    {
        ArgumentNullException.ThrowIfNull(slug);

        var sum = 17;
        foreach (var letter in slug)
        {
            sum = unchecked((sum * 31) + letter);
        }

        return (sum & int.MaxValue) % Tints;
    }

    /// <summary><c>contract-analyst</c> as <c>Contract analyst</c>: the slug read as a name.</summary>
    public static string NameOf(string slug)
    {
        ArgumentNullException.ThrowIfNull(slug);

        var spaced = slug.Replace('-', ' ').Replace('_', ' ').Trim();
        return spaced.Length == 0 ? slug : string.Concat(char.ToUpperInvariant(spaced[0]).ToString(), spaced.AsSpan(1));
    }
}
