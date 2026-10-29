using Quorum.Web.Client.Api;

namespace Quorum.Web.Client;

/// <summary>
/// What an administrator types for a web fetch tool (spec 006). A budget or a limit left empty is not a missing value: it is the
/// tool choosing to follow what the platform's web row says, now and when it changes, so the form keeps those fields nullable and
/// sends them on as they are.
/// </summary>
public sealed class WebFetchToolForm
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = "read";

    public string Description { get; set; } = "Reads the content of web pages the model names.";

    public int? TokenBudgetPerPage { get; set; }

    public int? TotalTokenBudget { get; set; }

    public int? MaxUrlsPerInvocation { get; set; }

    public int? Timeout { get; set; }

    public string Policy { get; set; } = ToolPolicies.Auto;

    public ScopeForm Scope { get; set; } = new();

    public bool IsNew => Id is null;

    public string OriginalName { get; set; } = string.Empty;

    public static WebFetchToolForm From(WebFetchToolDetail detail) => new()
    {
        Id = detail.Id,
        Name = detail.Name,
        OriginalName = detail.Name,
        Description = detail.Description,
        TokenBudgetPerPage = detail.TokenBudgetPerPage,
        TotalTokenBudget = detail.TotalTokenBudget,
        MaxUrlsPerInvocation = detail.MaxUrlsPerInvocation,
        Timeout = detail.Timeout,
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
            problems.Add("Say in one line when the model should read a page: it reads this to decide.");
        }

        AddRange(problems, TokenBudgetPerPage, "the per-page budget", WebFetchToolLimits.MinTokenBudgetPerPage, WebFetchToolLimits.MaxTokenBudgetPerPage, "tokens");
        AddRange(problems, TotalTokenBudget, "the whole-call budget", WebFetchToolLimits.MinTotalTokenBudget, WebFetchToolLimits.MaxTotalTokenBudget, "tokens");
        AddRange(problems, MaxUrlsPerInvocation, "the number of pages per call", WebFetchToolLimits.MinMaxUrlsPerInvocation, WebFetchToolLimits.MaxMaxUrlsPerInvocation, "pages");
        AddRange(problems, Timeout, "the timeout", WebFetchToolLimits.MinTimeoutSeconds, WebFetchToolLimits.MaxTimeoutSeconds, "seconds");

        if (Scope.Build().Error is { } scope)
        {
            problems.Add(scope);
        }

        return problems;
    }

    /// <summary>The request to send. Call it when <see cref="Problems"/> is empty.</summary>
    public WebFetchToolRequest ToRequest() => new(
        Name.Trim(),
        Description.Trim(),
        TokenBudgetPerPage,
        TotalTokenBudget,
        MaxUrlsPerInvocation,
        Timeout,
        Scope.Build().Scope!,
        Policy);

    /// <summary>An empty field is an inheritance and is never wrong; a filled one has to be inside the server's range.</summary>
    private static void AddRange(List<string> problems, int? value, string what, int min, int max, string unit)
    {
        if (value is { } number && (number < min || number > max))
        {
            problems.Add($"{char.ToUpperInvariant(what[0])}{what[1..]} must be between {min} and {max} {unit}, or left empty to follow the platform.");
        }
    }
}
