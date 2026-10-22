using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Dictionary;
using Quorum.Modules.KnowledgeBases.Features.Inspect;
using Quorum.Modules.KnowledgeBases.Features.Verification;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Modules.KnowledgeBases.Storage;
using Quorum.Shared.Persistence;
using Quorum.Shared.Results;
using Quorum.Shared.Tenancy;

namespace Quorum.Modules.KnowledgeBases.Features.Registry;

/// <summary>
/// Registers, edits and verifies knowledge bases. Registering looks at the store (D-8) but a store that is not ready never stops
/// it: the knowledge base is saved <see cref="KnowledgeBaseStatus.Unavailable"/> with warnings that name the cause. What does stop
/// it is a mapping that is wrong whatever the store says, or one the store contradicts (an unchosen vector, values that do not fit).
/// </summary>
internal sealed partial class KnowledgeBaseRegistrar(
    KnowledgeBasesDbContext db,
    ITenantContext tenant,
    StoreProbe probe,
    StoreAdapters adapters,
    IAssistantDirectory assistants,
    TimeProvider clock,
    ILogger<KnowledgeBaseRegistrar> logger)
{
    private static readonly Error NameTaken = Error.Conflict("knowledge_base.name_taken", "A knowledge base with this name already exists.");

    private static readonly Error NotFound = Error.NotFound("knowledge_base.not_found", "The knowledge base does not exist.");

    private static readonly Error FieldRemoved = Error.Conflict(
        "knowledge_base.field_removed",
        "A dictionary field of the mapping was deleted while the knowledge base was being saved. Review the mapping and try again.");

    public async Task<Result<RegisteredResponse>> CreateAsync(KnowledgeBaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Authorized requests have a tenant.");
        var prepared = await PrepareAsync(request, existing: null, tenantId, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return prepared.Error;
        }

        var input = prepared.Value;
        var now = clock.GetUtcNow();
        var registration = KnowledgeBase.Create(
            tenantId,
            input.Name,
            input.Description,
            input.Kind,
            input.Settings,
            input.EmbeddingModel,
            input.QueryPrefix,
            input.Mapping,
            input.Scope,
            input.ScopeTenantIds,
            input.ScopeAssistantIds,
            now,
            input.LookupRefreshHours);
        Apply(registration, input.Verdict, now);

        db.KnowledgeBases.Add(registration);
        if (await SaveAsync(input, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        LogRegistered(logger, registration.Id, registration.Status, input.Verdict.Warnings.Count);
        return new RegisteredResponse(registration.Id, registration.Status.ToString(), ToDtos(input.Verdict.Warnings));
    }

    public async Task<Result<VerificationResponse>> UpdateAsync(Guid id, KnowledgeBaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The tenant filter makes a knowledge base another tenant shares with this one not found here: it is read-only.
        if (await db.KnowledgeBases.SingleOrDefaultAsync(k => k.Id == id, cancellationToken) is not { } registration)
        {
            return NotFound;
        }

        var prepared = await PrepareAsync(request, registration, registration.TenantId, cancellationToken);
        if (!prepared.IsSuccess)
        {
            return prepared.Error;
        }

        var input = prepared.Value;
        var now = clock.GetUtcNow();
        registration.Edit(
            input.Name,
            input.Description,
            input.Kind,
            input.Settings,
            input.EmbeddingModel,
            input.QueryPrefix,
            input.Mapping,
            input.Scope,
            input.ScopeTenantIds,
            input.ScopeAssistantIds,
            now,
            input.LookupRefreshHours);
        Apply(registration, input.Verdict, now);

        if (await SaveAsync(input, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        LogRegistered(logger, registration.Id, registration.Status, input.Verdict.Warnings.Count);
        return new VerificationResponse(registration.Status.ToString(), ToDtos(input.Verdict.Warnings));
    }

    /// <summary>Looks at the store again with the stored settings and updates the status (D-8, AC-17).</summary>
    public async Task<Result<VerificationResponse>> VerifyAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await db.KnowledgeBases.SingleOrDefaultAsync(k => k.Id == id, cancellationToken) is not { } registration)
        {
            return NotFound;
        }

        var adapter = adapters.For(registration.Kind);
        var now = clock.GetUtcNow();
        if (!adapter.TryOpen(registration.Connection, out var connection))
        {
            var required = adapter.SecretRequired;
            registration.MarkUnavailable(required.Description, now);
            await db.SaveChangesAsync(cancellationToken);
            return new VerificationResponse(registration.Status.ToString(), [new WarningDto(required.Code, required.Description, null)]);
        }

        var found = await probe.RunAsync(adapter, connection, registration.EmbeddingModel, sample: true, cancellationToken);
        var verdict = Evaluate(found, registration.Mapping);
        Apply(registration, verdict, now);
        await db.SaveChangesAsync(cancellationToken);

        LogVerified(logger, registration.Id, registration.Status, verdict.Warnings.Count);
        return new VerificationResponse(registration.Status.ToString(), ToDtos(verdict.Warnings));
    }

    private async Task<Result<PreparedInput>> PrepareAsync(KnowledgeBaseRequest request, KnowledgeBase? existing, Guid ownerId, CancellationToken cancellationToken)
    {
        var basics = ValidateBasics(request);
        if (!basics.IsSuccess)
        {
            return basics.Error;
        }

        var (name, description, queryPrefix, scope, scopeTenantIds, scopeAssistantIds, roles, requestedFields, countedPoint, entityName, adapter, requested, model) = basics.Value;

        // A mistyped id would leave the knowledge base visible to nobody, silently.
        if (scope == KnowledgeBaseScope.Assistant && (await assistants.MissingAsync(scopeAssistantIds, cancellationToken)).Count > 0)
        {
            return Error.Validation("knowledge_base.invalid_scope", "Every assistant in the scope must exist.");
        }

        var ownId = existing?.Id ?? Guid.Empty;
        if (await db.KnowledgeBases.AnyAsync(k => k.Name == name && k.Id != ownId, cancellationToken))
        {
            return NameTaken;
        }

        var resolved = await MappingFieldResolver.ResolveAsync(db, ownerId, scope, requestedFields, request.CreateFields ?? [], clock, cancellationToken);
        if (!resolved.IsSuccess)
        {
            return resolved.Error;
        }

        // An omitted secret keeps the stored one, so a form can be saved without the administrator retyping it. What is stored
        // belongs to the kind it was saved for: another kind starts from nothing.
        var kept = adapter.KeepSecrets(requested, existing is not null && existing.Kind == adapter.Kind ? existing.Connection : null);
        if (!kept.IsSuccess)
        {
            return kept.Error;
        }

        var connection = kept.Value;
        var found = await probe.RunAsync(adapter, connection, model, sample: true, cancellationToken);
        if (adapter.ValidateVector(found.Info, connection) is { } vector)
        {
            return vector;
        }

        if (MappingValidation.Values(found.Sample, resolved.Value.Types) is { } misfit)
        {
            return misfit;
        }

        if (MappingValidation.CountedPointFits(countedPoint, resolved.Value.Types, found.Sample) is { } countedMisfit)
        {
            return countedMisfit;
        }

        var mapping = new KnowledgeBaseMapping(roles, resolved.Value.Fields, countedPoint, entityName);
        var settings = adapter.ToSettings(connection);
        return new PreparedInput(
            name,
            description,
            adapter.Kind,
            queryPrefix,
            scope,
            scopeTenantIds,
            scopeAssistantIds,
            model,
            settings,
            mapping,
            resolved.Value.Created,
            Evaluate(found, mapping),
            request.LookupRefreshHours ?? existing?.LookupRefreshHours ?? KnowledgeBase.DefaultLookupRefreshHours);
    }

    private Result<BasicInput> ValidateBasics(KnowledgeBaseRequest request)
    {
        var name = request.Name?.Trim();
        if (name is null || !ValidName().IsMatch(name))
        {
            return Error.Validation(
                "knowledge_base.invalid_name",
                $"The name must be 1 to {KnowledgeBase.MaxNameLength} characters: lower-case letters, digits, '-' and '_', starting with a letter or digit.");
        }

        var description = request.Description?.Trim();
        if (string.IsNullOrEmpty(description) || description.Length > KnowledgeBase.MaxDescriptionLength)
        {
            return Error.Validation(
                "knowledge_base.invalid_description",
                $"The description is required, up to {KnowledgeBase.MaxDescriptionLength} characters: the model reads it to choose where to search.");
        }

        var validated = ConnectionRules.Validate(adapters, request.Kind, request.Connection, request.EmbeddingModel);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        if (request.QueryPrefix?.Length > KnowledgeBase.MaxQueryPrefixLength)
        {
            return Error.Validation("knowledge_base.invalid_query_prefix", $"The query prefix can have up to {KnowledgeBase.MaxQueryPrefixLength} characters.");
        }

        var scope = ValidateScope(request.Scope);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        var roles = MappingValidation.Roles(request.Mapping?.Roles);
        if (!roles.IsSuccess)
        {
            return roles.Error;
        }

        var entityName = MappingValidation.EntityName(request.Mapping?.EntityName);
        if (!entityName.IsSuccess)
        {
            return entityName.Error;
        }

        var countedPoint = MappingValidation.ReadCountedPoint(request.Mapping?.CountedPoint);
        if (!countedPoint.IsSuccess)
        {
            return countedPoint.Error;
        }

        IReadOnlyList<MappedFieldRequest> fields = request.Mapping?.Fields ?? [];
        if (MappingValidation.Paths(roles.Value, entityName.Value, fields) is { } pathProblem)
        {
            return pathProblem;
        }

        if (request.LookupRefreshHours is < KnowledgeBase.MinLookupRefreshHours or > KnowledgeBase.MaxLookupRefreshHours)
        {
            return Error.Validation(
                "knowledge_base.invalid_refresh_interval",
                $"The lookups can be refreshed every {KnowledgeBase.MinLookupRefreshHours} to {KnowledgeBase.MaxLookupRefreshHours} hours.");
        }

        var (kind, tenantIds, assistantIds) = scope.Value;
        var (adapter, connection, model) = validated.Value;
        return new BasicInput(
            name,
            description,
            string.IsNullOrEmpty(request.QueryPrefix) ? null : request.QueryPrefix,
            kind,
            tenantIds,
            assistantIds,
            roles.Value,
            fields,
            countedPoint.Value,
            entityName.Value,
            adapter,
            connection,
            model);
    }

    private static Result<(KnowledgeBaseScope Kind, List<Guid> TenantIds, List<Guid> AssistantIds)> ValidateScope(ScopeRequest? scope)
    {
        if (string.Equals(scope?.Kind, "Agent", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("knowledge_base.scope_not_supported", "The agent scope is not available yet: agents do not exist. Use Global, Tenant or Assistant.");
        }

        if (scope is null || !DictionaryFieldRules.TryParseMember<KnowledgeBaseScope>(scope.Kind, out var kind))
        {
            return Error.Validation("knowledge_base.invalid_scope", "The scope kind must be Global, Tenant or Assistant.");
        }

        var tenantIds = (scope.TenantIds ?? []).Distinct().ToList();
        var assistantIds = (scope.AssistantIds ?? []).Distinct().ToList();
        var problem = kind switch
        {
            KnowledgeBaseScope.Global when tenantIds.Count > 0 || assistantIds.Count > 0 => "A global scope has no tenants or assistants.",
            KnowledgeBaseScope.Tenant when assistantIds.Count > 0 => "A tenant scope lists tenants, not assistants.",
            KnowledgeBaseScope.Assistant when tenantIds.Count > 0 => "An assistant scope lists assistants, not tenants.",
            KnowledgeBaseScope.Assistant when assistantIds.Count == 0 => "An assistant scope needs at least one assistant.",
            _ when tenantIds.Contains(Guid.Empty) || assistantIds.Contains(Guid.Empty) => "An id in the scope is empty.",
            _ => null,
        };

        return problem is null
            ? (kind, tenantIds, assistantIds)
            : Error.Validation("knowledge_base.invalid_scope", problem);
    }

    /// <summary>
    /// What the store says about the knowledge base. It is available when nothing stops a search; every problem the probe found does,
    /// while a mapped path missing from the sample only warns.
    /// </summary>
    private static Verdict Evaluate(StoreProbeResult found, KnowledgeBaseMapping mapping)
    {
        var warnings = found.Warnings.Concat(MappingValidation.MissingPaths(found.Sample, mapping)).ToList();
        var error = found.Warnings.Count == 0 ? null : string.Join(' ', found.Warnings.Select(w => w.Message));
        return new Verdict(error, warnings);
    }

    private static void Apply(KnowledgeBase registration, Verdict verdict, DateTimeOffset now)
    {
        if (verdict.Error is null)
        {
            registration.MarkAvailable(now);
        }
        else
        {
            registration.MarkUnavailable(verdict.Error, now);
        }
    }

    private static List<WarningDto> ToDtos(IEnumerable<InspectWarning> warnings) => [.. warnings.Select(w => new WarningDto(w.Code, w.Message, w.Path))];

    /// <summary>
    /// Saves the knowledge base with the dictionary fields it creates. The fields were resolved before the store was probed, which
    /// can take seconds, so under the dictionary lock it checks they still exist: a field deleted meanwhile would leave the mapping
    /// pointing at nothing. The lock covers only this last step, never the call to the store.
    /// </summary>
    /// <returns>The conflict to answer with, or <see langword="null"/> when everything was saved.</returns>
    private async Task<Error?> SaveAsync(PreparedInput input, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DictionaryLock.AcquireAsync(db, cancellationToken);

        var newIds = input.NewFields.Select(f => f.Id).ToHashSet();
        var existingIds = input.Mapping.Fields.Select(f => f.DictionaryFieldId).Where(id => !newIds.Contains(id)).ToList();
        if (await DictionaryVisibility.Fields(db).CountAsync(f => existingIds.Contains(f.Id), cancellationToken) != existingIds.Count)
        {
            return FieldRemoved;
        }

        db.DictionaryFields.AddRange(input.NewFields);
        var conflict = await SaveChangesAsync(input.NewFields.Count > 0, cancellationToken);
        if (conflict is null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return conflict;
    }

    private async Task<Error?> SaveChangesAsync(bool createdFields, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (exception.IsUniqueViolation())
        {
            // A concurrent request took the name between the check and the insert.
            return createdFields
                ? Error.Conflict("knowledge_base.field_name_taken", "A field with this name already exists where this field would be visible.")
                : NameTaken;
        }
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();

    // Only ids, statuses and counts: never the address, the collection, the description or what the store said.
    [LoggerMessage(Level = LogLevel.Information, Message = "Knowledge base {KnowledgeBaseId} saved as {Status} with {WarningCount} warnings")]
    private static partial void LogRegistered(ILogger logger, Guid knowledgeBaseId, KnowledgeBaseStatus status, int warningCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Knowledge base {KnowledgeBaseId} verified as {Status} with {WarningCount} warnings")]
    private static partial void LogVerified(ILogger logger, Guid knowledgeBaseId, KnowledgeBaseStatus status, int warningCount);

    private sealed record BasicInput(
        string Name,
        string Description,
        string? QueryPrefix,
        KnowledgeBaseScope Scope,
        List<Guid> ScopeTenantIds,
        List<Guid> ScopeAssistantIds,
        MappingRoles Roles,
        IReadOnlyList<MappedFieldRequest> Fields,
        CountedPoint? CountedPoint,
        string? EntityName,
        IStoreAdapter Adapter,
        IStoreConnection Connection,
        string EmbeddingModel);

    private sealed record PreparedInput(
        string Name,
        string Description,
        KnowledgeBaseKind Kind,
        string? QueryPrefix,
        KnowledgeBaseScope Scope,
        List<Guid> ScopeTenantIds,
        List<Guid> ScopeAssistantIds,
        string EmbeddingModel,
        ConnectionSettings Settings,
        KnowledgeBaseMapping Mapping,
        List<DictionaryField> NewFields,
        Verdict Verdict,
        int LookupRefreshHours);

    /// <param name="Error">Why the knowledge base cannot be searched yet, or <see langword="null"/> when it can.</param>
    private sealed record Verdict(string? Error, List<InspectWarning> Warnings);
}
