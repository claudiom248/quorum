using System.Text.RegularExpressions;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>One source under an answer, ready to show.</summary>
/// <param name="Href">Only for <c>http</c> and <c>https</c> addresses; the model and the store decide what a source says, so nothing else becomes a link.</param>
public sealed record SourceItem(int N, string Label, string Detail, string? Href);

/// <summary>
/// The sources of an answer split by whether its text cites them with a <c>[n]</c> marker: those cited come first and open,
/// the others follow collapsed.
/// </summary>
public sealed partial class SourcesView
{
    public SourcesView(IReadOnlyList<MessageSourceDto>? sources, string text)
    {
        var cited = CitedNumbers(text);
        var items = (sources ?? []).OrderBy(source => source.N).Select(Item).ToList();
        Cited = [.. items.Where(item => cited.Contains(item.N))];
        Others = [.. items.Where(item => !cited.Contains(item.N))];
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

    private static SourceItem Item(MessageSourceDto source)
    {
        var label = !string.IsNullOrWhiteSpace(source.Title) ? source.Title
            : !string.IsNullOrWhiteSpace(source.DocumentId) ? source.DocumentId
            : $"Source {source.N}";

        var detail = string.Join(" · ", new[] { source.KnowledgeBase, source.Position }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return new SourceItem(source.N, label, detail, WebAddress(source.Url));
    }

    private static string? WebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
}
