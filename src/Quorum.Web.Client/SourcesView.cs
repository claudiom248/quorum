using System.Text.RegularExpressions;
using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>One place in a document the answer drew on.</summary>
/// <param name="Numbers">The numbers the text can cite it by: one for an answer the server numbered by source, several for one stored before it did.</param>
/// <param name="Position">Says whether it is a page or a section; <see langword="null"/> when the source gave none.</param>
/// <param name="Href">Only when the sections of the document do not all share one address.</param>
public sealed record SourceSection(IReadOnlyList<int> Numbers, string? Position, string? Href);

/// <summary>A document under an answer and the places in it that were used.</summary>
/// <param name="Detail">The knowledge base it comes from and, when there is no link to say which document it is, its id.</param>
/// <param name="Href">Only for <c>http</c> and <c>https</c> addresses; the model and the store decide what a source says, so nothing else becomes a link.</param>
public sealed record SourceDocument(string Label, string Detail, string? Href, IReadOnlyList<SourceSection> Sections)
{
    /// <summary>A document cited as a whole, once: it fits on one line with its number.</summary>
    public bool IsOneLine => Sections is [{ Position: null }];
}

/// <summary>
/// The sources of an answer grouped by document, split by whether the text cites them with a <c>[n]</c> marker: those cited come first and
/// open, the others follow collapsed. The same place twice (an answer stored before the server numbered by source) is shown once, under
/// all its numbers, so every marker in the text still finds its entry.
/// </summary>
public sealed partial class SourcesView
{
    private sealed record Place(MessageSourceDto First, List<int> Numbers);

    public SourcesView(IReadOnlyList<MessageSourceDto>? sources, string text)
    {
        var cited = CitedNumbers(text);
        var places = Merge((sources ?? []).OrderBy(source => source.N));
        Cited = Group(places.Where(place => place.Numbers.Any(cited.Contains)));
        Others = Group(places.Where(place => !place.Numbers.Any(cited.Contains)));
    }

    public IReadOnlyList<SourceDocument> Cited { get; }

    public IReadOnlyList<SourceDocument> Others { get; }

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

    private static List<Place> Merge(IEnumerable<MessageSourceDto> sources)
    {
        var places = new List<Place>();
        var positions = new Dictionary<(string, string?, string?, string?, string?), Place>();
        foreach (var source in sources)
        {
            var key = (source.KnowledgeBase, source.DocumentId, source.Title, source.Position, source.Url);
            if (positions.TryGetValue(key, out var place))
            {
                place.Numbers.Add(source.N);
            }
            else
            {
                place = new Place(source, [source.N]);
                positions[key] = place;
                places.Add(place);
            }
        }

        return places;
    }

    private static List<SourceDocument> Group(IEnumerable<Place> places)
    {
        // Two sources with neither a title nor an id cannot be told to be the same document: each stands alone.
        var groups = places.GroupBy(place => string.IsNullOrWhiteSpace(place.First.Title) && string.IsNullOrWhiteSpace(place.First.DocumentId)
            ? (place.First.KnowledgeBase, (string?)null, (string?)null, place.Numbers[0])
            : (place.First.KnowledgeBase, place.First.DocumentId, place.First.Title, 0));

        return [.. groups.Select(Document)];
    }

    private static SourceDocument Document(IGrouping<(string KnowledgeBase, string? DocumentId, string? Title, int Alone), Place> group)
    {
        var first = group.First().First;
        var label = !string.IsNullOrWhiteSpace(first.Title) ? first.Title
            : !string.IsNullOrWhiteSpace(first.DocumentId) ? first.DocumentId
            : $"Source {group.First().Numbers[0]}";

        var addresses = group.Select(place => WebAddress(place.First.Url)).Distinct().ToList();
        var shared = addresses is [{ } only] ? only : null;

        // With a link nothing else says which document it is, and when the id already is the label it would only repeat.
        var file = shared is null && label != first.DocumentId && !string.IsNullOrWhiteSpace(first.DocumentId) ? $"File: {first.DocumentId}" : null;
        var detail = string.Join(" · ", new[] { first.KnowledgeBase, file }.Where(part => !string.IsNullOrWhiteSpace(part)));

        var sections = group.Select(place => new SourceSection([.. place.Numbers], PositionText(place.First.Position), shared is null ? WebAddress(place.First.Url) : null));
        return new SourceDocument(label, detail, shared, [.. sections]);
    }

    // The position is a page or a section, whatever the data owner put there: a bare number is a page.
    private static string? PositionText(string? position) =>
        string.IsNullOrWhiteSpace(position) ? null
        : position.All(char.IsAsciiDigit) ? $"Page: {position}"
        : $"Section: {position}";

    private static string? WebAddress(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
}
