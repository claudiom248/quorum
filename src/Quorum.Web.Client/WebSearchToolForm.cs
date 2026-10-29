using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>
/// What an administrator types for a web search tool (spec 005). The engines are a line each, because that is how they read on the
/// instance's own page; what goes to the server is the list they make, in the order the server keeps them in.
/// </summary>
public sealed class WebSearchToolForm
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = "web";

    public string Description { get; set; } = "Searches the open web for what is newest.";

    public int TopN { get; set; } = WebSearchToolLimits.DefaultTopN;

    public string EnginesText { get; set; } = string.Empty;

    public string? Language { get; set; }

    public int SafeSearch { get; set; } = WebSearchToolLimits.DefaultSafeSearch;

    public string? TimeRange { get; set; }

    public int MaxInvocations { get; set; } = WebSearchToolLimits.DefaultMaxInvocations;

    public string Policy { get; set; } = ToolPolicies.Auto;

    public ScopeForm Scope { get; set; } = new();

    public bool IsNew => Id is null;

    public string OriginalName { get; set; } = string.Empty;

    public static WebSearchToolForm From(WebSearchToolDetail detail) => new()
    {
        Id = detail.Id,
        Name = detail.Name,
        OriginalName = detail.Name,
        Description = detail.Description,
        TopN = detail.TopN,
        EnginesText = string.Join(Environment.NewLine, detail.SearchEngines ?? []),
        Language = detail.Language,
        SafeSearch = detail.SafeSearch,
        TimeRange = detail.TimeRange,
        MaxInvocations = detail.MaxInvocationsPerConversation,
        Policy = detail.Policy,
        Scope = ScopeForm.From(detail.Scope),
    };

    /// <summary>What is wrong with what was typed. The server checks every value again; this only saves the round trip.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add("Give the tool a name.");
        }

        if (string.IsNullOrWhiteSpace(Description))
        {
            problems.Add("Say in one line when the model should search the web: it reads this to decide.");
        }

        if (Engines().Count > WebSearchToolLimits.MaxSearchEngines)
        {
            problems.Add($"At most {WebSearchToolLimits.MaxSearchEngines} engines, one per line.");
        }

        if (Scope.Build().Error is { } scope)
        {
            problems.Add(scope);
        }

        return problems;
    }

    /// <summary>The request to send. Call it when <see cref="Problems"/> is empty.</summary>
    public WebSearchToolRequest ToRequest() => new(
        Name.Trim(),
        Description.Trim(),
        TopN,
        Engines() is { Count: > 0 } engines ? engines : null,
        string.IsNullOrWhiteSpace(Language) ? null : Language.Trim().ToLowerInvariant(),
        SafeSearch,
        string.IsNullOrWhiteSpace(TimeRange) ? null : TimeRange,
        MaxInvocations,
        Scope.Build().Scope!,
        Policy);

    /// <summary>One engine per line (or separated by commas), as the instance names it; the server keeps them in its own order.</summary>
    private List<string> Engines() => EnginesText
        .Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(engine => engine.ToLowerInvariant())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();
}
