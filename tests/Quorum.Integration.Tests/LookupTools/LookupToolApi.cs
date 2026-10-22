using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases;
using Quorum.Integration.Tests.KnowledgeBases.Lookups;
using Quorum.Integration.Tests.Rag;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.LookupTools;

/// <summary>Route and body shapes of the lookup tool API, as a client would use them, and the setup most lookup tool tests share.</summary>
internal static class LookupToolApi
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Uri All { get; } = new("/api/tools/lookup", UriKind.Relative);

    public static Uri One(Guid id) => new($"/api/tools/lookup/{id}", UriKind.Relative);

    public static object OwnTenant { get; } = new { kind = "Tenant" };

    public static object Bind(Guid lookupId, string? alias = null) => new { lookupId, alias };

    public static object Body(
        string name = "lists",
        string description = "Reads the lists of values.",
        object[]? lookups = null,
        IEnumerable<Guid>? knowledgeBaseIds = null,
        object[]? exposedFields = null,
        object? fixedFilters = null,
        object? scope = null,
        string? policy = null) => new
        {
            name,
            description,
            lookups = lookups ?? [],
            knowledgeBaseIds = knowledgeBaseIds?.ToArray() ?? [],
            exposedFields = exposedFields ?? [],
            fixedFilters,
            scope = scope ?? OwnTenant,
            policy,
        };

    public static async Task<HttpResponseMessage> CreateAsync(HttpClient client, object body) =>
        await client.PostAsJsonAsync(All, body, Ct);

    /// <summary>Creates a lookup tool that must be accepted and returns it.</summary>
    public static async Task<CreatedBody> CreateOkAsync(HttpClient client, object body)
    {
        using var response = await CreateAsync(client, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CreatedBody>(Ct))!;
    }

    public static async Task<DetailBody> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<DetailBody>(One(id), Ct))!;

    public static async Task<IReadOnlyList<ListItemBody>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ListItemBody>>(All, Ct))!;

    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response) => await LookupApi.ProblemCodeAsync(response);

    public static object Mapping(params (string Path, string Name, bool Listable)[] fields) => new
    {
        // With a document id the lists count documents, which is what the tests of the tool read.
        roles = new { text = "text", documentId = "document_id", title = "title" },
        fields = fields.Select(f => new { path = f.Path, name = f.Name, listable = f.Listable }).ToArray(),
    };

    /// <summary>The ordinary mapping of the tests: <c>department</c> is listable, <c>year</c> and <c>confidential</c> are there to filter on.</summary>
    public static object DepartmentMapping { get; } = Mapping(("department", "department", true), ("year", "year", false), ("confidential", "confidential", false));

    /// <summary>
    /// Registers a knowledge base with <paramref name="mapping"/>, whose <c>department</c> field is listable: the registration creates the computed
    /// lookup with the slice of the whole knowledge base. The computation is held back, so the list stays "being prepared" until the test publishes one.
    /// Returns both ids.
    /// </summary>
    public static async Task<(Guid KnowledgeBase, Guid Lookup)> RegisterWithLookupAsync(RagApi.Setup setup, string name, object? mapping = null)
    {
        setup.App.LookupComputer.Handler = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable: the delay only ends by cancellation.");
        };
        var knowledgeBase = await setup.RegisterAsync(name, mapping: mapping ?? DepartmentMapping);
        var fieldId = (await setup.FieldsAsync())["department"];

        await using var services = setup.App.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var lookup = await db.Lookups.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.KnowledgeBaseId == knowledgeBase && l.DictionaryFieldId == fieldId, Ct);
        return (knowledgeBase, lookup.Id);
    }

    /// <summary>Writes a generation of values and makes it current in the slice of the lookup with the filters given (the whole knowledge base when none).</summary>
    public static async Task PublishAsync(QuorumApp app, Guid lookupId, IReadOnlyList<(string Value, int Count)> values, string? filterHash = null, int generation = 1)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var hash = filterHash ?? LookupSliceKey.WholeKnowledgeBase.Hash;
        var slice = await db.LookupSlices.IgnoreQueryFilters().SingleAsync(s => s.LookupId == lookupId && s.FilterHash == hash, Ct);
        await LookupApi.AddGenerationAsync(db, slice.TenantId, slice, generation, values);
    }

    /// <summary>The slices of a lookup as the tables hold them, with the tools that use them.</summary>
    public static async Task<List<SliceRow>> SlicesAsync(QuorumApp app, Guid lookupId)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var slices = await db.LookupSlices.IgnoreQueryFilters().AsNoTracking().Where(s => s.LookupId == lookupId).OrderBy(s => s.FilterHash).ToListAsync(Ct);
        return [.. slices.Select(s => new SliceRow(s.FilterHash, s.Filters, [.. s.ToolIds]))];
    }

    public sealed record SliceRow(string Hash, string Filters, List<Guid> ToolIds)
    {
        public bool IsWholeKnowledgeBase => Hash == LookupSliceKey.WholeKnowledgeBase.Hash;
    }

    public sealed record PreviewBody(string Name, string Description, JsonElement InputSchema, int EstimatedTokens)
    {
        /// <summary>The names of the parameters of the generated schema.</summary>
        public string[] Parameters => [.. InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name)];

        public JsonElement Parameter(string name) => InputSchema.GetProperty("properties").GetProperty(name);
    }

    public sealed record CreatedBody(Guid Id, PreviewBody? Definition);

    public sealed record NamedBody(Guid Id, string Name);

    public sealed record WarningBody(string Code, string Message);

    public sealed record ScopeBody(string Kind, List<Guid> TenantIds, List<Guid> AssistantIds);

    public sealed record ListItemBody(Guid Id, string Name, List<NamedBody> Lookups, List<NamedBody> KnowledgeBases, ScopeBody Scope, string State, string Policy, bool Shared, List<WarningBody> Warnings);

    public sealed record BoundLookupBody(Guid LookupId, string? Alias);

    public sealed record DetailBody(
        Guid Id,
        string Name,
        string Description,
        List<BoundLookupBody> Lookups,
        List<Guid> KnowledgeBaseIds,
        JsonElement FixedFilters,
        ScopeBody Scope,
        string State,
        string Policy,
        PreviewBody? Definition,
        List<WarningBody> Warnings);
}
