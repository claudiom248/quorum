using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools.Rag;
using Quorum.Modules.KnowledgeBases.Contracts;
using Quorum.Shared.Results;

namespace Quorum.Modules.Agents.Features.Tools;

/// <param name="Policy">The policy asked for, or <see langword="null"/> when the request left it out.</param>
internal sealed record ValidatedRagTool(string Name, string Description, ToolPolicy? Policy, RagConfiguration Configuration);

/// <summary>What an administrator may enter for a rag tool (spec 003 D-4, D-11, D-13 and the rule between scopes).</summary>
internal sealed partial class RagToolValidation(AgentsDbContext db, IKnowledgeBaseCatalog catalog)
{
    public const int MaxNameLength = 40;

    public const int MaxDescriptionLength = 1_000;

    private static readonly Error InvalidName = Error.Validation(
        "rag.invalid_name",
        $"The name must be 1 to {MaxNameLength} characters: lower-case letters, digits, '-' and '_', starting with a letter or digit.");

    private static readonly Error InvalidDescription = Error.Validation(
        "rag.invalid_description",
        $"The description is required, up to {MaxDescriptionLength} characters: the model reads it to decide when to search.");

    private static readonly Error InvalidPolicy = Error.Validation("rag.invalid_policy", "The policy must be Auto or Confirm.");

    private static readonly Error NoKnowledgeBase = Error.Validation("rag.no_knowledge_base", "A rag tool needs at least one knowledge base.");

    private static readonly Error TooManyKnowledgeBases = Error.Validation(
        "rag.too_many_knowledge_bases",
        $"A rag tool can search up to {RagTool.MaxKnowledgeBases} knowledge bases.");

    private static readonly Error NotVisible = Error.Validation(
        "rag.knowledge_base_not_visible",
        "Every knowledge base and field of a rag tool must be visible to everyone the tool is for. Check the scope of the knowledge bases and of the fields, or narrow the scope of the tool.");

    public async Task<Result<ValidatedRagTool>> ValidateAsync(RagToolRequest request, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim();
        if (name is null || !ValidNamePattern().IsMatch(name))
        {
            return InvalidName;
        }

        var description = request.Description?.Trim();
        if (string.IsNullOrEmpty(description) || description.Length > MaxDescriptionLength)
        {
            return InvalidDescription;
        }

        ToolPolicy? policy = null;
        if (request.Policy is not null)
        {
            if (!Enum.TryParse<ToolPolicy>(request.Policy, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(request.Policy, out _))
            {
                return InvalidPolicy;
            }

            policy = parsed;
        }

        var knowledgeBaseIds = (request.KnowledgeBaseIds ?? []).Distinct().ToList();
        if (knowledgeBaseIds.Count == 0 || knowledgeBaseIds.Contains(Guid.Empty))
        {
            return NoKnowledgeBase;
        }

        if (knowledgeBaseIds.Count > RagTool.MaxKnowledgeBases)
        {
            return TooManyKnowledgeBases;
        }

        var exposedRequests = request.ExposedFields ?? [];
        if (exposedRequests.Any(e => e.FieldId is null || e.FieldId == Guid.Empty))
        {
            return Error.Validation("rag.invalid_filter", "Every exposed field needs the id of a dictionary field.");
        }

        if (exposedRequests.GroupBy(e => e.FieldId).Any(g => g.Count() > 1))
        {
            return AliasConflict("A field can be exposed only once.");
        }

        var exposedFieldIds = exposedRequests.Select(e => e.FieldId!.Value).ToList();
        if (exposedFieldIds.Count > RagTool.MaxExposedFields)
        {
            return Error.Validation("rag.limit_exceeded", $"A rag tool can expose up to {RagTool.MaxExposedFields} fields to the model.");
        }

        var aliases = new Dictionary<Guid, string>();
        foreach (var exposed in exposedRequests.Where(e => !string.IsNullOrWhiteSpace(e.Alias)))
        {
            var alias = exposed.Alias!.Trim();
            if (!ValidAliasPattern().IsMatch(alias) || IsReservedAlias(alias))
            {
                return Error.Validation(
                    "rag.invalid_alias",
                    $"The alias '{(alias.Length <= 40 ? alias : alias[..40])}' is not allowed: 1 to 40 lower-case letters, digits and '_', starting with a letter, and not 'query', 'knowledge_base' or ending in _min, _max, _from or _to.");
            }

            aliases[exposed.FieldId!.Value] = alias;
        }

        var limits = Limits(request);
        if (!limits.IsSuccess)
        {
            return limits.Error;
        }

        // The invoker refuses a result longer than the tenant's limit, and the text adds a notice and a header and markers per source to the chunks.
        var toolLimit = (await db.ToolSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken))?.MaxResultCharacters ?? ToolSettings.DefaultMaxResultCharacters;
        if (RagTool.FitResultSize(limits.Value.MaxResultCharacters, limits.Value.TopK, toolLimit) < limits.Value.MaxResultCharacters)
        {
            return Error.Validation(
                "rag.limit_exceeded",
                $"The result size plus the formatting of up to {limits.Value.TopK} sources must fit the limit on tool results ({toolLimit} characters). Lower the result size or raise the limit in the tool settings.");
        }

        var scope = await ToolScopeRules.ValidateAsync(request.Scope, db, cancellationToken);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        var seen = await catalog.DescribeAsync(new BindingQuery(tenantId, AssistantId: null, knowledgeBaseIds, exposedFieldIds), cancellationToken);
        if (seen.KnowledgeBases.Count != knowledgeBaseIds.Count)
        {
            return NotVisible;
        }

        // The model can filter on what at least one of the knowledge bases maps; anything else could never match.
        var mapped = seen.KnowledgeBases.SelectMany(k => k.FieldIds).ToHashSet();
        if (exposedFieldIds.Any(id => !mapped.Contains(id)))
        {
            return Error.Validation("rag.invalid_filter", "A field exposed to the model must be one that the knowledge bases of the tool map.");
        }

        // Every parameter the tool generates must be distinct: `query`, `knowledge_base` and one group per field under its alias (A1-3).
        var fieldsById = seen.Fields.ToDictionary(f => f.Id);
        var parameters = new HashSet<string>(StringComparer.Ordinal) { RagToolDefinition.QueryParameter };
        if (knowledgeBaseIds.Count > 1)
        {
            parameters.Add(RagToolDefinition.KnowledgeBaseParameter);
        }

        var aliasByParameter = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var id in exposedFieldIds)
        {
            var field = fieldsById[id];
            var alias = aliases.GetValueOrDefault(id) ?? field.Name;
            foreach (var parameter in RagToolDefinition.ParameterNames(alias, field.Type))
            {
                if (!parameters.Add(parameter))
                {
                    return AliasConflict($"The parameter '{parameter}' would be generated twice. Give one of the fields a different alias.");
                }
            }

            if (!aliasByParameter.TryAdd(alias, id))
            {
                return AliasConflict($"The alias '{alias}' is given to two fields. Give each exposed field its own.");
            }
        }

        var fixedFilters = await FixedFiltersAsync(request.FixedFilters, tenantId, [.. mapped], aliasByParameter, cancellationToken);
        if (!fixedFilters.IsSuccess)
        {
            return fixedFilters.Error;
        }

        var (filtersJson, filterFieldIds) = fixedFilters.Value;

        var check = await catalog.CheckScopeAsync(
            new ScopeCheckQuery(
                tenantId,
                new BindingScope(KindOf(scope.Value.Kind), scope.Value.TenantIds, scope.Value.AssistantIds),
                knowledgeBaseIds,
                [.. exposedFieldIds.Concat(filterFieldIds).Distinct()]),
            cancellationToken);
        if (!check.IsVisible)
        {
            return NotVisible;
        }

        return new ValidatedRagTool(
            name,
            description,
            policy,
            new RagConfiguration(
                knowledgeBaseIds,
                exposedFieldIds,
                filtersJson,
                filterFieldIds,
                limits.Value.TopK,
                limits.Value.ScoreThreshold,
                limits.Value.MaxResultCharacters,
                scope.Value.Kind,
                scope.Value.TenantIds,
                scope.Value.AssistantIds,
                aliases));
    }

    private static Result<(int TopK, double? ScoreThreshold, int MaxResultCharacters)> Limits(RagToolRequest request)
    {
        var topK = request.TopK ?? RagTool.DefaultTopK;
        if (topK is < 1 or > RagTool.MaxTopK)
        {
            return Error.Validation("rag.limit_exceeded", $"The number of results must be between 1 and {RagTool.MaxTopK}.");
        }

        if (request.ScoreThreshold is { } threshold && !double.IsFinite(threshold))
        {
            return Error.Validation("rag.limit_exceeded", "The score threshold must be a number.");
        }

        var cap = request.MaxResultCharacters ?? RagTool.DefaultMaxResultCharacters;
        if (cap is < RagTool.MinMaxResultCharacters or > RagTool.MaxMaxResultCharacters)
        {
            return Error.Validation(
                "rag.limit_exceeded",
                $"The result size must be between {RagTool.MinMaxResultCharacters} and {RagTool.MaxMaxResultCharacters} characters.");
        }

        return (topK, request.ScoreThreshold, cap);
    }

    private async Task<Result<(string Json, List<Guid> FieldIds)>> FixedFiltersAsync(
        JsonElement? filters,
        Guid tenantId,
        List<Guid> mappedFieldIds,
        IReadOnlyDictionary<string, Guid> aliases,
        CancellationToken cancellationToken)
    {
        if (filters is not { } value || value.ValueKind == JsonValueKind.Null)
        {
            return ("{}", []);
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return Error.Validation("rag.invalid_filter", "The fixed filters must be an object whose keys are filter parameters, like {\"department\": [\"hr\"]}.");
        }

        var parameters = value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        if (parameters.Count == 0)
        {
            return ("{}", []);
        }

        var check = await catalog.CheckFixedFiltersAsync(new FixedFilterQuery(tenantId, mappedFieldIds, parameters, aliases), cancellationToken);
        return check.Problem is null
            ? (JsonSerializer.Serialize(parameters), check.FieldIds.ToList())
            : Error.Validation("rag.invalid_filter", check.Problem);
    }

    private static BindingScopeKind KindOf(ToolScopeKind kind) => kind switch
    {
        ToolScopeKind.Global => BindingScopeKind.Global,
        ToolScopeKind.Tenant => BindingScopeKind.Tenant,
        ToolScopeKind.Assistant => BindingScopeKind.Assistant,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only Global, Tenant and Assistant scopes are validated."),
    };

    private static Error AliasConflict(string message) => Error.Validation("rag.alias_conflict", message);

    private static bool IsReservedAlias(string alias) =>
        alias is RagToolDefinition.QueryParameter or RagToolDefinition.KnowledgeBaseParameter
        || alias.EndsWith("_min", StringComparison.Ordinal) || alias.EndsWith("_max", StringComparison.Ordinal)
        || alias.EndsWith("_from", StringComparison.Ordinal) || alias.EndsWith("_to", StringComparison.Ordinal);

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex ValidAliasPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex ValidNamePattern();
}

/// <param name="FixedFilters">The filters the model never sees, in the shape of the tool's parameters: <c>{"department": ["hr"], "year_min": 2024}</c>.</param>
internal sealed record RagToolRequest(
    string? Name,
    string? Description,
    List<Guid>? KnowledgeBaseIds,
    List<ExposedFieldRequest>? ExposedFields,
    JsonElement? FixedFilters,
    int? TopK,
    double? ScoreThreshold,
    int? MaxResultCharacters,
    ScopeRequest? Scope,
    string? Policy);

/// <param name="Alias">The parameter name the model sees for the field; the field's own name when left out.</param>
internal sealed record ExposedFieldRequest(Guid? FieldId, string? Alias = null);
