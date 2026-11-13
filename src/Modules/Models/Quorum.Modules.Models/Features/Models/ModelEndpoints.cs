using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quorum.Modules.Models.Contracts;
using Quorum.Modules.Models.Domain;
using Quorum.Modules.Models.Persistence;
using Quorum.Modules.Models.Probing;
using Quorum.Modules.Models.Secrets;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.Models.Features.Models;

/// <summary>The models of the installation (spec 019): the owner's tenant edits them, the tenants the scope names select them.</summary>
internal sealed class ModelEndpoints : IEndpointGroup
{
    private static readonly Error NotFound = Error.NotFound("model.not_found", "The model does not exist.");

    private static ProblemHttpResult NotOwner() => TypedResults.Problem(
        detail: "Only the tenant that owns the model can change it.",
        statusCode: StatusCodes.Status403Forbidden,
        extensions: new Dictionary<string, object?> { ["code"] = "model.not_owner" });

    private static readonly Error NameTaken = Error.Conflict("model.name_taken", "Another model already has this name.");

    public void Map(IEndpointRouteBuilder app)
    {
        var models = app.MapGroup("/api/models").WithTags("Models");

        models.MapGet("/", ListAsync).RequireAuthorization(QuorumPolicies.ModelsRead).WithName("ListModels");
        models.MapGet("/{id:guid}", GetAsync).RequireAuthorization(QuorumPolicies.ModelsRead).WithName("GetModel");
        models.MapPost("/", CreateAsync).RequireAuthorization(QuorumPolicies.ModelsManage).WithName("CreateModel");
        models.MapPost("/test", TestAsync).RequireAuthorization(QuorumPolicies.ModelsManage).WithName("TestModel");
        models.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(QuorumPolicies.ModelsManage).WithName("UpdateModel");
        models.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(QuorumPolicies.ModelsManage).WithName("DeleteModel");
    }

    /// <summary>The models the tenant sees: its own, the global ones, and those that list it (D-7).</summary>
    private static async Task<Ok<List<ModelResponse>>> ListAsync(
        string? capability,
        ModelsDbContext db,
        ITenantContext tenant,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? Guid.Empty;
        var query = db.Models.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.TenantId == tenantId || m.Scope == ModelScope.Global || m.ScopeTenantIds.Contains(tenantId));

        // The filter runs in the query, over the stored combinations.
        if (ModelCapabilitiesExtensions.TryParse(capability, out var wanted))
        {
            // The combinations that include the wanted flag, as the column holds them: an IN over the stored values.
            var matching = Enumerable.Range(1, 7).Select(value => (ModelCapabilities)value).Where(value => value.HasFlag(wanted)).ToArray();
            query = query.Where(m => matching.Contains(m.Capabilities));
        }

        var rows = await query.OrderBy(m => m.Name).Select(ModelRow.Projection).ToListAsync(cancellationToken);
        return TypedResults.Ok(rows.Select(row => ModelResponse.From(row, tenantId)).ToList());
    }

    private static async Task<Results<Ok<ModelResponse>, ProblemHttpResult>> GetAsync(Guid id, ModelsDbContext db, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? Guid.Empty;
        var row = await db.Models.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Id == id && (m.TenantId == tenantId || m.Scope == ModelScope.Global || m.ScopeTenantIds.Contains(tenantId)))
            .Select(ModelRow.Projection)
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? NotFound.ToProblem() : TypedResults.Ok(ModelResponse.From(row, tenantId));
    }

    private static async Task<Results<Created<ModelResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        ModelRequest request,
        ModelsDbContext db,
        ITenantContext tenant,
        ApiKeyProtector apiKeyProtector,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");
        if (Validate(request, out var baseUrl, out var capabilities, out var scope, out var scopeTenants) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var name = request.Name!.Trim();
        if (await db.Models.IgnoreQueryFilters().AnyAsync(m => m.TenantId == tenantId && m.Name == name, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var model = Model.Create(
            tenantId,
            name,
            baseUrl!,
            request.ModelName!.Trim(),
            string.IsNullOrWhiteSpace(request.ApiKey) ? null : apiKeyProtector.Protect(request.ApiKey.Trim()),
            capabilities,
            request.ContextTokenBudget,
            request.QueryPrefix,
            request.EmbeddingDimensions,
            scope,
            scopeTenants,
            clock.GetUtcNow());
        db.Models.Add(model);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        return TypedResults.Created($"/api/models/{model.Id}", ModelResponse.From(model, tenantId));
    }

    /// <summary>
    /// The connection test (D-5): the server's models and tool support, and with a model name the vector size of an embedding model.
    /// Each probe is independent, so a chat-only server still reports what it can do, with the code of the embeddings failure.
    /// </summary>
    private static async Task<Results<Ok<ModelTestResult>, ValidationProblem, ProblemHttpResult>> TestAsync(
        ModelTestRequest request,
        ModelsDbContext db,
        ITenantContext tenant,
        ApiKeyProtector apiKeyProtector,
        IModelProbe probe,
        CancellationToken cancellationToken)
    {
        if (!ModelRules.TryParseBaseUrl(request.BaseUrl, out var baseUrl))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["baseUrl"] = [ModelRules.InvalidBaseUrl] });
        }

        var apiKey = request.ApiKey;
        if (apiKey is null && request.ModelId is { } modelId)
        {
            // The stored key goes only to the address of a model the caller owns. A model that is merely visible (global, or listing the
            // tenant) keeps its key to itself: anyone who knows its id could otherwise send the key to an address of their choice.
            var tenantId = tenant.TenantId ?? Guid.Empty;
            var stored = await db.Models.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.Id == modelId && m.TenantId == tenantId)
                .Select(m => new { m.ProtectedApiKey })
                .SingleOrDefaultAsync(cancellationToken);
            if (stored is null)
            {
                return NotFound.ToProblem();
            }

            apiKey = apiKeyProtector.Unprotect(stored.ProtectedApiKey);
        }

        var chat = await probe.ProbeAsync(baseUrl, apiKey, request.ModelName, cancellationToken);
        if (!chat.IsSuccess)
        {
            return chat.Error.ToProblem();
        }

        var suggested = new List<string> { "chat" };
        if (chat.Value.SupportsTools == true)
        {
            suggested.Add("tools");
        }

        int? dimensions = null;
        string? embeddingsProblem = null;
        if (!string.IsNullOrWhiteSpace(request.ModelName))
        {
            var embeddings = await probe.ProbeEmbeddingsAsync(baseUrl, apiKey, request.ModelName, cancellationToken);
            if (embeddings.IsSuccess)
            {
                suggested.Add("embeddings");
                dimensions = embeddings.Value;
            }
            else
            {
                embeddingsProblem = embeddings.Error.Code;
            }
        }

        return TypedResults.Ok(new ModelTestResult(true, chat.Value.Models, suggested, dimensions, embeddingsProblem));
    }

    /// <summary>
    /// Only the owner edits a model; <c>apiKey</c> omitted keeps the key, an empty string removes it. Removing a capability, or
    /// narrowing the scope, is refused while a consumer relies on it (D-11, D-12).
    /// </summary>
    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Guid id,
        ModelRequest request,
        ModelsDbContext db,
        ITenantContext tenant,
        ApiKeyProtector apiKeyProtector,
        ModelUsageGuard guard,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? Guid.Empty;
        if (Validate(request, out var baseUrl, out var capabilities, out var scope, out var scopeTenants) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await Visible(db, id, tenantId, cancellationToken) is not { } model)
        {
            return NotFound.ToProblem();
        }

        if (model.TenantId != tenantId)
        {
            return NotOwner();
        }

        var name = request.Name!.Trim();
        if (await db.Models.IgnoreQueryFilters().AnyAsync(m => m.TenantId == tenantId && m.Name == name && m.Id != id, cancellationToken))
        {
            return NameTaken.ToProblem();
        }

        var lost = await guard.ForCapabilityRemovalAsync(id, model.Capabilities, capabilities, cancellationToken);
        if (lost.Count > 0)
        {
            return ModelConflict("model.capability_in_use", "Consumers rely on a capability this change removes. Point them to another model first.", lost);
        }

        var narrowed = await guard.ForScopeNarrowingAsync(model, scope, scopeTenants, cancellationToken);
        if (narrowed.Count > 0)
        {
            return ModelConflict("model.scope_in_use", "Consumers would no longer reach this model with the new scope. Remove them or widen the scope.", narrowed);
        }

        var now = clock.GetUtcNow();
        model.Rename(name, now);
        model.Configure(baseUrl!, request.ModelName!.Trim(), capabilities, request.ContextTokenBudget, request.QueryPrefix, request.EmbeddingDimensions, scope, scopeTenants, now);
        if (request.ApiKey is not null)
        {
            model.ReplaceApiKey(string.IsNullOrWhiteSpace(request.ApiKey) ? null : apiKeyProtector.Protect(request.ApiKey.Trim()), now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            return NameTaken.ToProblem();
        }

        return TypedResults.NoContent();
    }

    /// <summary>Refused while a consumer uses the model: the database would not cascade it, and the consumers would fail (D-10).</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(Guid id, ModelsDbContext db, ITenantContext tenant, ModelUsageGuard guard, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? Guid.Empty;
        if (await Visible(db, id, tenantId, cancellationToken) is not { } model)
        {
            return NotFound.ToProblem();
        }

        if (model.TenantId != tenantId)
        {
            return NotOwner();
        }

        var uses = await guard.ForDeleteAsync(id, cancellationToken);
        if (uses.Count > 0)
        {
            return ModelConflict("model.in_use", "The model is in use. Point these consumers to another model first.", uses);
        }

        db.Models.Remove(model);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    /// <summary>The model if the tenant sees it: its own, global, or listing the tenant (D-7). Anything else is not found.</summary>
    private static Task<Model?> Visible(ModelsDbContext db, Guid id, Guid tenantId, CancellationToken cancellationToken) =>
        db.Models.IgnoreQueryFilters()
            .SingleOrDefaultAsync(m => m.Id == id && (m.TenantId == tenantId || m.Scope == ModelScope.Global || m.ScopeTenantIds.Contains(tenantId)), cancellationToken);

    /// <summary>The 409 naming what is in the way, in the same shape as the agents' conflicts (spec 013 D-16).</summary>
    private static ProblemHttpResult ModelConflict(string code, string detail, IReadOnlyList<ModelUse> uses) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["references"] = uses.Select(use => new { kind = use.Kind, id = use.Id, name = use.Name }).ToList(),
            });

    private static Dictionary<string, string[]> Validate(
        ModelRequest request,
        out Uri? baseUrl,
        out ModelCapabilities capabilities,
        out Domain.ModelScope scope,
        out List<Guid> scopeTenants)
    {
        var errors = new Dictionary<string, string[]>();

        if (!ModelRules.IsValidName(request.Name))
        {
            errors["name"] = [ModelRules.InvalidName];
        }

        if (!ModelRules.TryParseBaseUrl(request.BaseUrl, out baseUrl))
        {
            errors["baseUrl"] = [ModelRules.InvalidBaseUrl];
        }

        if (!ModelRules.IsValidModelName(request.ModelName))
        {
            errors["modelName"] = [ModelRules.InvalidModelName];
        }

        capabilities = ModelCapabilities.None;
        foreach (var name in request.Capabilities ?? [])
        {
            if (ModelCapabilitiesExtensions.TryParse(name, out var capability))
            {
                capabilities |= capability;
            }
            else
            {
                errors["capabilities"] = [$"Unknown capability '{name}'."];
            }
        }

        if (!errors.ContainsKey("capabilities") && !ModelRules.IsValidCapabilities(capabilities))
        {
            errors["capabilities"] = [capabilities == ModelCapabilities.None ? ModelRules.NoCapability : ModelRules.ToolsNeedChat];
        }

        if (capabilities.HasFlag(ModelCapabilities.Chat) && !ModelRules.IsValidContextTokenBudget(request.ContextTokenBudget ?? 0))
        {
            errors["contextTokenBudget"] = [ModelRules.ChatNeedsBudget];
        }

        if (!ModelRules.IsValidQueryPrefix(request.QueryPrefix))
        {
            errors["queryPrefix"] = [$"The query prefix can be at most {ModelRules.MaxQueryPrefixLength} characters."];
        }

        if (!ModelRules.IsValidEmbeddingDimensions(request.EmbeddingDimensions))
        {
            errors["embeddingDimensions"] = [$"The dimensions must be between 1 and {ModelRules.MaxEmbeddingDimensions}."];
        }

        scope = Domain.ModelScope.Tenant;
        if (string.Equals(request.Scope, "Global", StringComparison.OrdinalIgnoreCase))
        {
            scope = Domain.ModelScope.Global;
        }
        else if (!string.IsNullOrEmpty(request.Scope) && !string.Equals(request.Scope, "Tenant", StringComparison.OrdinalIgnoreCase))
        {
            errors["scope"] = ["The scope is Global or Tenant."];
        }

        scopeTenants = [.. (request.ScopeTenantIds ?? []).Distinct()];
        return errors;
    }
}

/// <summary>The request of a model. <c>ApiKey</c> omitted keeps the stored key on update; an empty one removes it.</summary>
internal sealed record ModelRequest(
    string? Name,
    string? BaseUrl,
    string? ModelName,
    string? ApiKey,
    string[]? Capabilities,
    int? ContextTokenBudget,
    string? QueryPrefix,
    int? EmbeddingDimensions,
    string? Scope,
    Guid[]? ScopeTenantIds);

internal sealed record ModelTestRequest(string? BaseUrl, string? ApiKey, string? ModelName, Guid? ModelId = null);

internal sealed record ModelTestResult(bool Reachable, IReadOnlyList<string> Models, IReadOnlyList<string> SuggestedCapabilities, int? EmbeddingDimensions, string? EmbeddingsProblem);

/// <summary>
/// The columns a model is read with: the key is never loaded, only whether there is one. Reads project into this, so the key is not
/// read into memory for a list or a get (spec 019 D-9).
/// </summary>
internal sealed record ModelRow(
    Guid Id,
    Guid TenantId,
    string Name,
    string ModelName,
    string BaseUrl,
    bool HasApiKey,
    ModelCapabilities Capabilities,
    int? ContextTokenBudget,
    int? EmbeddingDimensions,
    string? QueryPrefix,
    ModelScope Scope,
    List<Guid> ScopeTenantIds)
{
    public static readonly System.Linq.Expressions.Expression<Func<Model, ModelRow>> Projection = m => new ModelRow(
        m.Id,
        m.TenantId,
        m.Name,
        m.ModelName,
        m.BaseUrl,
        m.ProtectedApiKey != null,
        m.Capabilities,
        m.ContextTokenBudget,
        m.EmbeddingDimensions,
        m.QueryPrefix,
        m.Scope,
        m.ScopeTenantIds);
}

/// <summary>A model as the tenant sees it. The URL and the key state appear only to the owner (D-9).</summary>
internal sealed record ModelResponse(
    Guid Id,
    string Name,
    string ModelName,
    IReadOnlyList<string> Capabilities,
    int? ContextTokenBudget,
    int? EmbeddingDimensions,
    string? QueryPrefix,
    string Scope,
    IReadOnlyList<Guid> ScopeTenantIds,
    bool Owned,
    string? BaseUrl,
    bool? HasApiKey)
{
    public static ModelResponse From(ModelRow row, Guid tenantId)
    {
        var owned = row.TenantId == tenantId;
        return new ModelResponse(
            row.Id,
            row.Name,
            row.ModelName,
            row.Capabilities.Names(),
            row.ContextTokenBudget,
            row.EmbeddingDimensions,
            row.QueryPrefix,
            row.Scope.ToString(),
            row.ScopeTenantIds,
            owned,
            owned ? row.BaseUrl : null,
            owned ? row.HasApiKey : null);
    }

    public static ModelResponse From(Model model, Guid tenantId) => From(
        new ModelRow(
            model.Id,
            model.TenantId,
            model.Name,
            model.ModelName,
            model.BaseUrl,
            model.ProtectedApiKey is not null,
            model.Capabilities,
            model.ContextTokenBudget,
            model.EmbeddingDimensions,
            model.QueryPrefix,
            model.Scope,
            model.ScopeTenantIds),
        tenantId);
}
