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

    /// <summary>The operations <c>query</c> takes on a list: its values, and the count of the values that match the search.</summary>
    public static readonly string[] LookupOperations = [LookupToolDefinition.ValuesOperation, LookupToolDefinition.CountOperation];

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

        var describe = $"Describe the lists and knowledge bases of the lookup tool \"{toolName}\" before reading one: the operations each takes, the fields it can be filtered by, the order it can be sorted in and its limits. {toolDescription} Targets: {listed}.";
        var query = $"Read one list or knowledge base of \"{toolName}\": operation values or count for a list, documents or count for a knowledge base. Call {DescribeOperation} first for the filters it takes. Targets: {listed}.";

        return new GeneratedLookupDefinition(
        [
            new GeneratedLookupFunction(DescribeOperation, describe, DescribeSchema(targets).ToJsonString()),
            new GeneratedLookupFunction(QueryOperation, query, QuerySchema(targets).ToJsonString()),
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

    private static JsonObject QuerySchema(IReadOnlyList<string> targets) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            [TargetParameter] = new JsonObject { ["type"] = "string", ["enum"] = Names(targets) },
            [OperationParameter] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = Names([.. LookupOperations, .. KnowledgeBaseOperations.Where(o => !LookupOperations.Contains(o))]),
                ["description"] = "The operation describe names for the target.",
            },
            [FiltersParameter] = new JsonObject { ["type"] = "object", ["description"] = "Filters by field or attribute, as describe lists them." },
            ["search"] = new JsonObject { ["type"] = "string" },
            ["sort"] = new JsonObject { ["type"] = "string" },
            ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = LookupToolDefinition.MaxLimit },
            ["cursor"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = Names([TargetParameter, OperationParameter]),
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
