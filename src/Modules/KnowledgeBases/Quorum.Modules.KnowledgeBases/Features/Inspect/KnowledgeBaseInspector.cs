using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Dictionary;
using Quorum.Modules.KnowledgeBases.Features.Verification;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Persistence;
using Quorum.Shared.Networking;
using Quorum.Shared.Results;

namespace Quorum.Modules.KnowledgeBases.Features.Inspect;

/// <summary>
/// Looks at a collection before it is registered: is it reachable, what vectors does it have, how does its payload look, and does
/// an embedding of the configured model fit its vectors. Everything it finds is a draft or a warning; nothing is saved.
/// </summary>
internal sealed partial class KnowledgeBaseInspector(
    KnowledgeBasesDbContext db,
    StoreProbe probe,
    OutboundUrlGuard guard,
    ILogger<KnowledgeBaseInspector> logger)
{
    public const string UrlBlockedCode = "knowledge_base.url_blocked";

    public static readonly Error UrlBlocked = Error.Unprocessable(
        UrlBlockedCode,
        "The address is not allowed: link-local and cloud metadata addresses are blocked.");

    public async Task<Result<InspectResponse>> InspectAsync(InspectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var started = Stopwatch.GetTimestamp();
        var validated = ConnectionRules.Validate(request.Kind, request.Connection, request.EmbeddingModel);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        var (connection, embeddingModel) = validated.Value;
        if (await guard.IsBlockedAsync(connection.Url, cancellationToken))
        {
            return UrlBlocked;
        }

        var schemaDiscovery = SchemaText(request.Schema) is { } schema ? JsonSchemaReader.Read(schema) : null;
        if (schemaDiscovery is { IsSuccess: false })
        {
            return schemaDiscovery.Error;
        }

        var found = await probe.RunAsync(connection, embeddingModel, sample: schemaDiscovery is null, cancellationToken);
        if (found.Blocked)
        {
            return UrlBlocked;
        }

        var warnings = found.Warnings.ToList();
        if (found.Info is { PointsCount: 0 })
        {
            warnings.Add(new InspectWarning(
                WarningCodes.EmptyCollection,
                "The collection has no points yet, so there is nothing to sample: write the mapping by hand or paste the payload's JSON Schema.",
                Path: null));
        }

        var discovery = schemaDiscovery is { IsSuccess: true } ? schemaDiscovery.Value : PayloadSampler.Sample(found.Sample);
        warnings.AddRange(discovery.Warnings);

        var vectors = found.Info?.Vectors.Select(v => new VectorDto(v.Name, v.Size, v.Distance)).ToList() ?? [];
        var draft = MappingConventions.Build(discovery.Properties);
        var visible = await DictionaryVisibility.Fields(db).AsNoTracking().ToListAsync(cancellationToken);
        var fields = draft.Fields.Select(field => Resolve(field, visible)).ToList();
        var proposed = fields
            .Where(f => f.DictionaryFieldId is null && f.Issues.Count == 0)
            .DistinctBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new ProposedFieldDto(f.Name, f.Type))
            .ToList();

        var unindexed = new List<string>();
        if (found.Info is { } info)
        {
            foreach (var field in fields.Where(f => !info.PayloadIndexes.ContainsKey(f.Path)))
            {
                unindexed.Add(field.Path);
                warnings.Add(new InspectWarning(
                    WarningCodes.FieldUnindexed,
                    $"'{field.Path}' has no payload index in Qdrant: filters on it work but can be slow on large collections. Ask the data owner to index it if you plan to filter on it.",
                    field.Path));
            }
        }

        var response = new InspectResponse(
            found.Reachable,
            found.Reachable && found.Info is not null,
            vectors,
            found.EmbeddingSize,
            found.DimensionMatch,
            new DraftMappingDto(
                new RolesDto(draft.Roles.Text, draft.Roles.DocumentId, draft.Roles.Title, draft.Roles.Position, draft.Roles.SourceUrl),
                fields),
            proposed,
            unindexed,
            warnings.Select(w => new WarningDto(w.Code, w.Message, w.Path)).ToList());

        var elapsedMilliseconds = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogInspected(logger, response.Reachable, response.CollectionExists, fields.Count, warnings.Count, elapsedMilliseconds);
        return response;
    }

    /// <summary>The schema as text, whether the client sent a string or the schema object itself; <see langword="null"/> when none was sent.</summary>
    private static string? SchemaText(JsonElement? schema) => schema switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.String } element => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString(),
        { } element => element.GetRawText(),
    };

    private static DraftFieldDto Resolve(DraftField field, List<DictionaryField> visible)
    {
        var existing = visible.FirstOrDefault(f => f.Name == field.Name);
        var issues = field.Issues.ToList();
        if (existing is not null && existing.Type != field.Type)
        {
            issues.Add("knowledge_base.field_type_conflict");
        }

        return new DraftFieldDto(field.Path, field.Name, field.Type.ToString(), existing?.Id, issues);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Inspected a knowledge base store: reachable {Reachable}, collection found {CollectionExists}, {FieldCount} fields, {WarningCount} warnings, {ElapsedMilliseconds} ms")]
    private static partial void LogInspected(ILogger logger, bool reachable, bool collectionExists, int fieldCount, int warningCount, int elapsedMilliseconds);
}
