namespace Quorum.Web.Client;

/// <summary>How the tool calls of an answer are drawn. The person picks it in the settings; the browser remembers it in a cookie.</summary>
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

    public static readonly IReadOnlyList<LedgerStyle> All = [LedgerStyle.Classic, LedgerStyle.Cards, LedgerStyle.Timeline, LedgerStyle.Bars];

    public static string Label(LedgerStyle style) => style switch
    {
        LedgerStyle.Cards => "Cards",
        LedgerStyle.Timeline => "Timeline",
        LedgerStyle.Bars => "Bars",
        _ => "Classic",
    };

    public static string Description(LedgerStyle style) => style switch
    {
        LedgerStyle.Cards => "One card per sub-agent with its calls inside; direct tool calls share a Tools card.",
        LedgerStyle.Timeline => "A vertical line with one dot per sub-agent and its calls as small chips. The most compact.",
        LedgerStyle.Bars => "A coloured bar per sub-agent, with every call and its arguments on show. The most detailed.",
        _ => "A numbered list; the calls of a sub-agent are indented under it.",
    };

    /// <summary>The style a cookie value names: <c>a</c>, <c>b</c> or <c>c</c>; anything else is the classic ledger.</summary>
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
