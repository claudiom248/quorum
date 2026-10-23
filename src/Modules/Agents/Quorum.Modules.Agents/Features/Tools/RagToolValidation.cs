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
internal sealed partial class RagToolValidation(AgentsDbContext db, IKnowledgeBaseCatalog catalog, ToolFilterRules filters)
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

    private static readonly Error DocumentIdsNotSupported = Error.Validation(
        "rag.document_ids_not_supported",
        "None of the knowledge bases of this tool maps the document id, so the model cannot be allowed to search inside documents. Map the document id in a knowledge base of the tool, or turn the switch off.");

    /// <param name="documentIdsWhenOmitted">What <c>allowDocumentIds</c> is when the request leaves it out: the current value of a tool being changed, <see langword="null"/> for a new tool, which gets it on whenever a knowledge base can support it.</param>
    public async Task<Result<ValidatedRagTool>> ValidateAsync(RagToolRequest request, Guid tenantId, CancellationToken cancellationToken, bool? documentIdsWhenOmitted = null)
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

        var exposed = ToolFilterRules.ParseExposed(request.ExposedFields, FilterErrorCodes.Rag, "rag tool", RagTool.MaxExposedFields, ReservedAliasNames);
        if (!exposed.IsSuccess)
        {
            return exposed.Error;
        }

        var exposedFieldIds = exposed.Value.Ids;
        var aliases = exposed.Value.Aliases;

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

        var fixedFilters = await filters.ValidateAsync(
            seen,
            exposed.Value,
            ReservedParameters(knowledgeBaseIds.Count > 1),
            request.FixedFilters,
            tenantId,
            FilterErrorCodes.Rag,
            "rag tool",
            cancellationToken);
        if (!fixedFilters.IsSuccess)
        {
            return fixedFilters.Error;
        }

        var (filtersJson, filterFieldIds) = (fixedFilters.Value.Json, fixedFilters.Value.FieldIds);

        // Asked for explicitly it must be possible; left out it is what it was (on for a new tool) as far as a knowledge base can support it.
        var supportsDocumentIds = seen.KnowledgeBases.Any(k => k.MapsDocumentId);
        if (request.AllowDocumentIds is true && !supportsDocumentIds)
        {
            return DocumentIdsNotSupported;
        }

        var allowDocumentIds = request.AllowDocumentIds ?? ((documentIdsWhenOmitted ?? true) && supportsDocumentIds);

        var check = await catalog.CheckScopeAsync(
            new ScopeCheckQuery(
                tenantId,
                new BindingScope(ToolScopeRules.KindOf(scope.Value.Kind), scope.Value.TenantIds, scope.Value.AssistantIds),
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
                aliases,
                allowDocumentIds));
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

    // `query` and `knowledge_base` are the tool's own parameters; an alias may not take either, whether or not the tool has several knowledge bases.
    private static readonly string[] ReservedAliasNames = [RagToolDefinition.QueryParameter, RagToolDefinition.KnowledgeBaseParameter, RagToolDefinition.DocumentIdsParameter];

    private static List<string> ReservedParameters(bool hasKnowledgeBaseParameter) =>
        hasKnowledgeBaseParameter
            ? [RagToolDefinition.QueryParameter, RagToolDefinition.KnowledgeBaseParameter, RagToolDefinition.DocumentIdsParameter]
            : [RagToolDefinition.QueryParameter, RagToolDefinition.DocumentIdsParameter];

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
    string? Policy,
    bool? AllowDocumentIds = null);

/// <param name="Alias">The parameter name the model sees for the field; the field's own name when left out.</param>
internal sealed record ExposedFieldRequest(Guid? FieldId, string? Alias = null);
