using System.Text.Json.Nodes;

namespace Quorum.Modules.Agents.Tools.Lookup;

/// <summary>
/// The lean manifest of a lookup tool (spec 012 D-8): two functions whatever the number of lists and knowledge bases it binds. The
/// model asks <c>describe</c> for the details of the targets it needs, then <c>query</c> to read one of them.
/// </summary>
internal static class LookupLeanDefinition
{
    public const string DescribeOperation = "describe";

    public const string QueryOperation = "query";

    public const string TargetsParameter = "targets";

    public const string TargetParameter = "target";

    public const string OperationParameter = "operation";

    public const string FiltersParameter = "filters";

    /// <summary>The parameters <c>query</c> takes: anything else beside them is refused, so a filter written outside <c>filters</c> is never dropped silently.</summary>
    public static readonly string[] QueryParameters = [TargetParameter, OperationParameter, FiltersParameter, "search", "sort", "limit", "cursor"];


    /// <summary>The operations <c>query</c> takes on a list: its entries, and the count of the entries that match the search.</summary>
    public static readonly string[] LookupOperations = [LookupToolDefinition.EntriesOperation, LookupToolDefinition.CountOperation];

    /// <summary>The count of the values of a list that match the search: what <c>count</c> on a list runs inside the tool.</summary>
    public const string ListCountOperation = "listCount";

    public static readonly string[] KnowledgeBaseOperations = [LookupToolDefinition.DocumentsOperation, LookupToolDefinition.CountOperation];

    public static GeneratedLookupDefinition Build(string toolName, string toolDescription, ResolvedLookupTool resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var lookups = resolved.Targets.Keys.Order(StringComparer.Ordinal).ToList();
        var bases = resolved.KnowledgeBases.Keys.Order(StringComparer.Ordinal).ToList();
        var targets = lookups.Concat(bases).ToList();
        var listed = string.Join("; ", lookups.Select(name => $"{name} ({resolved.Targets[name].Kind}){Described(resolved.Targets[name].Description)}")
            .Concat(bases.Select(name => $"{name} (knowledge base)")));

        var describe = $"Describe the lists and knowledge bases of the lookup tool \"{toolName}\" before reading one: the operations each takes, the fields it can be filtered by, the order it can be sorted in and its limits. What it returns is binding: {QueryOperation} can do only what it lists for a target. {toolDescription} Targets: {listed}.";
        var query = $"Read one list or knowledge base of \"{toolName}\". A list takes the operations entries and count only; documents is for a knowledge base only, and count is for both. Call {DescribeOperation} first, and write the call from what it returns: for each target it lists the operations, the filters, the sort values and the limits that target takes, and query takes nothing else for it. A filter, sort or search that {DescribeOperation} does not list for the target is refused. A value is written as it is: a number or a date with a range is written as {{\"rating\": {{\"min\": 4}}}}, or {{\"max\": v}}, under its name in filters. Targets: {listed}.";

        return new GeneratedLookupDefinition(
        [
            new GeneratedLookupFunction(DescribeOperation, describe, DescribeSchema(targets).ToJsonString()),
            new GeneratedLookupFunction(QueryOperation, query, QuerySchema(lookups, bases).ToJsonString()),
        ]);
    }

    private static string Described(string? description) => string.IsNullOrWhiteSpace(description) ? string.Empty : $" - {description}";

    private static JsonObject DescribeSchema(IReadOnlyList<string> targets) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [TargetsParameter] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Names(targets) },
                ["description"] = "The lists and knowledge bases to describe, by name. One call describes several.",
            },
        },
        ["required"] = Names([TargetsParameter]),
    };

    // The operations of a target depend on what it is: a list takes entries and count, a knowledge base documents and count. The rule is
    // written per target so the model sees which operation fits the target it chose (spec 012 A1-6); the read still refuses the others.
    // Commented out: the argument validator ignores allOf, and the model did not choose better with it. The read refuses the others.
    private static JsonObject QuerySchema(List<string> lookups, List<string> bases)
    {
        var targets = lookups.Concat(bases).ToList();
        // var rules = new JsonArray();
        // if (lookups.Count > 0)
        // {
        //     rules.Add(OperationRule(lookups, LookupLeanDefinition.LookupOperations));
        // }
        //
        // if (bases.Count > 0)
        // {
        //     rules.Add(OperationRule(bases, KnowledgeBaseOperations));
        // }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = QueryProperties(targets),
            ["required"] = Names([TargetParameter, OperationParameter]),
            // ["allOf"] = rules,
        };
    }

    // private static JsonObject OperationRule(IReadOnlyList<string> targets, IEnumerable<string> operations) => new()
    // {
    //     ["if"] = new JsonObject
    //     {
    //         ["properties"] = new JsonObject { [TargetParameter] = new JsonObject { ["enum"] = Names(targets) } },
    //         ["required"] = Names([TargetParameter]),
    //     },
    //     ["then"] = new JsonObject
    //     {
    //         ["properties"] = new JsonObject { [OperationParameter] = new JsonObject { ["enum"] = Names(operations) } },
    //     },
    // };
    private static JsonObject QueryProperties(IReadOnlyList<string> targets) => new()
    {
        [TargetParameter] = new JsonObject { ["type"] = "string", ["enum"] = Names(targets) },
        [OperationParameter] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = Names([.. LookupOperations, .. KnowledgeBaseOperations.Where(o => !LookupOperations.Contains(o))]),
            ["description"] = "The operation describe names for the target: entries and count for a list, documents and count for a knowledge base.",
        },
        [FiltersParameter] = new JsonObject { ["type"] = "object", ["description"] = "Filters by field or attribute, as describe lists them: a value, a list of values, or for a range of a number or a date {\"min\": v} or {\"max\": v}, as in {\"rating\": {\"min\": 4}}." },
        ["search"] = new JsonObject { ["type"] = "string", ["description"] = "Text to find among the entries of a list. Only for a list: a knowledge base takes no search, filter it by the fields describe lists." },
        ["sort"] = new JsonObject { ["type"] = "string", ["description"] = "Only for a list: an order that describe lists for the target. Put '-' in front for the largest first, as in -headcount." },
        ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = LookupToolDefinition.MaxLimit },
        ["cursor"] = new JsonObject { ["type"] = "string" },
    };

    private static JsonArray Names(IEnumerable<string> names)
    {
        var array = new JsonArray();
        foreach (var name in names)
        {
            array.Add(name);
        }

        return array;
    }
}
