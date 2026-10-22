using System.Text.RegularExpressions;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>One source under an answer, ready to show.</summary>
/// <param name="Numbers">The numbers the answer can cite it by: several when the same passage came back from more than one search.</param>
/// <param name="Detail">Where it is: the knowledge base and the position, and the document id when there is no link to say which document it is.</param>
/// <param name="Href">Only for <c>http</c> and <c>https</c> addresses; the model and the store decide what a source says, so nothing else becomes a link.</param>
public sealed record SourceItem(IReadOnlyList<int> Numbers, string Label, string Detail, string? Href);

/// <summary>
/// The sources of an answer split by whether its text cites them with a <c>[n]</c> marker: those cited come first and open,
/// the others follow collapsed. Sources that point at the same place are shown once, under all their numbers, so every marker
/// in the text still finds its entry.
/// </summary>
public sealed partial class SourcesView
{
    public SourcesView(IReadOnlyList<MessageSourceDto>? sources, string text)
    {
        var cited = CitedNumbers(text);
        var items = Merge((sources ?? []).OrderBy(source => source.N));
        Cited = [.. items.Where(item => item.Numbers.Any(cited.Contains))];
        Others = [.. items.Where(item => !item.Numbers.Any(cited.Contains))];
    }

    public IReadOnlyList<SourceItem> Cited { get; }

    public IReadOnlyList<SourceItem> Others { get; }

    public bool HasSources => Cited.Count + Others.Count > 0;

    [GeneratedRegex(@"\[(\d{1,4}(?:\s*,\s*\d{1,4})*)\]")]
    private static partial Regex Marker();

    private static HashSet<int> CitedNumbers(string text)
    {
        var numbers = new HashSet<int>();
        foreach (Match marker in Marker().Matches(text))
        {
            foreach (var part in marker.Groups[1].Value.Split(','))
            {
                numbers.Add(int.Parse(part.Trim(), System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return numbers;
    }

    private static List<SourceItem> Merge(IEnumerable<MessageSourceDto> sources)
    {
        var items = new List<SourceItem>();
        var positions = new Dictionary<(string, string?, string?, string?, string?), int>();
        foreach (var source in sources)
        {
            var key = (source.KnowledgeBase, source.DocumentId, source.Title, source.Position, source.Url);
            if (positions.TryGetValue(key, out var index))
            {
                items[index] = items[index] with { Numbers = [.. items[index].Numbers, source.N] };
            }
            else
            {
                positions[key] = items.Count;
                items.Add(Item(source));
            }
        }

        return items;
    }

    private static SourceItem Item(MessageSourceDto source)
    {
        var label = !string.IsNullOrWhiteSpace(source.Title) ? source.Title
            : !string.IsNullOrWhiteSpace(source.DocumentId) ? source.DocumentId
            : $"Source {source.N}";

        var href = WebAddress(source.Url);

        // Without a link nothing else says which document it is; with a link, or when it already is the label, it would only repeat.
        var file = href is null && label != source.DocumentId && !string.IsNullOrWhiteSpace(source.DocumentId) ? $"File: {source.DocumentId}" : null;
        var detail = string.Join(" · ", new[] { source.KnowledgeBase, PositionText(source.Position), file }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new SourceItem([source.N], label, detail, href);
    }

    // The position is a page or a section, whatever the data owner put there: a bare number is a page.
    private static string? PositionText(string? position) =>
        string.IsNullOrWhiteSpace(position) ? null
        : position.All(char.IsAsciiDigit) ? $"Page: {position}"
        : $"Section: {position}";

    private static string? WebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
}
