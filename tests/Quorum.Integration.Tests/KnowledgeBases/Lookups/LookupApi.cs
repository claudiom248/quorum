using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>Routes and body shapes of the lookup API, as a client would use them, and seeding for lookups the API cannot create yet.</summary>
internal static class LookupApi
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static Uri ManualLookups { get; } = new("/api/knowledge-bases/manual-lookups", UriKind.Relative);

    public static Uri ManualLookup(Guid id) => new($"/api/knowledge-bases/manual-lookups/{id}", UriKind.Relative);

    public static Uri Values(Guid lookupId, string? query = null) =>
        new($"/api/knowledge-bases/lookups/{lookupId}/values{(query is null ? string.Empty : "?" + query)}", UriKind.Relative);

    public static object Value(string value, string? label = null, string[]? synonyms = null) => new { value, label, synonyms };

    public static object ManualBody(
        string name = "nationality",
        string? description = null,
        string scope = "Tenant",
        Guid[]? scopeTenantIds = null,
        object[]? values = null) => new
        {
            name,
            description,
            scope,
            scopeTenantIds,
            values = values ?? [Value("IT", "Italy", ["Italia", "italiana"]), Value("FR", "France")],
        };

    public static Task<HttpResponseMessage> CreateManualAsync(HttpClient client, object body) =>
        client.PostAsJsonAsync(ManualLookups, body, Ct);

    /// <summary>Creates a manual lookup that must be accepted and returns its id.</summary>
    public static async Task<Guid> CreateManualOkAsync(HttpClient client, object? body = null)
    {
        using var response = await CreateManualAsync(client, body ?? ManualBody());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedBody>(Ct))!.Id;
    }

    public static async Task<IReadOnlyList<ManualLookupBody>> ListManualAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ManualLookupBody>>(ManualLookups, Ct))!;

    public static async Task<ValuesBody> ReadValuesOkAsync(HttpClient client, Guid lookupId, string? query = null)
    {
        using var response = await client.GetAsync(Values(lookupId, query), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ValuesBody>(Ct))!;
    }

    /// <summary>The stable <c>code</c> of a ProblemDetails response.</summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// A computed lookup of a knowledge base with one slice of the whole knowledge base and one generation of values, which a
    /// computation will write once the refresh service exists. <paramref name="values"/> are ranked by count, then value.
    /// </summary>
    public static async Task<(Guid LookupId, Guid SliceId)> SeedComputedAsync(
        QuorumApp app,
        Guid tenantId,
        IReadOnlyList<(string Value, int Count)> values,
        LookupUnit unit = LookupUnit.Documents,
        int generation = 1)
    {
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var registration = KnowledgeBaseFixtures.Create(tenantId, $"policies-{Guid.NewGuid():N}"[..20], "http://qdrant.test:6334", "docs", KnowledgeBaseScope.Tenant, []);
        db.KnowledgeBases.Add(registration);
        var lookup = Lookup.Computed(tenantId, registration.Id, Guid.NewGuid(), unit, Now);
        var slice = LookupSlice.Create(tenantId, lookup.Id, LookupSliceKey.WholeKnowledgeBase.Hash, LookupSliceKey.WholeKnowledgeBase.Filters, Now);
        db.Lookups.Add(lookup);
        db.LookupSlices.Add(slice);
        await db.SaveChangesAsync(Ct);

        await AddGenerationAsync(db, tenantId, slice, generation, values);
        return (lookup.Id, slice.Id);
    }

    /// <summary>Writes a generation of values and makes it the current one of the slice.</summary>
    public static async Task AddGenerationAsync(
        KnowledgeBasesDbContext db,
        Guid tenantId,
        LookupSlice slice,
        int generation,
        IReadOnlyList<(string Value, int Count)> values)
    {
        var ranked = values.OrderByDescending(v => v.Count).ThenBy(v => v.Value, StringComparer.Ordinal).ToList();
        db.LookupValues.AddRange(ranked.Select((v, position) => LookupValue.Create(tenantId, slice.Id, generation, position, v.Value, v.Count)));
        slice.Publish(generation, ranked.Count, Now, nextRefreshAt: null);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>Inserts a manual lookup of a tenant that does not exist in the app: the API has no way to create a second tenant yet.</summary>
    public static async Task<Guid> SeedManualOfAnotherTenantAsync(QuorumApp app, string name, LookupScope scope, params Guid[] scopeTenantIds)
    {
        var tenantId = Guid.NewGuid();
        await using var services = app.Services.CreateAsyncScope();
        var db = services.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var lookup = Lookup.Manual(tenantId, name, "Another tenant's list.", scope, scopeTenantIds, Now);
        var slice = LookupSlice.Create(tenantId, lookup.Id, LookupSliceKey.WholeKnowledgeBase.Hash, LookupSliceKey.WholeKnowledgeBase.Filters, Now);
        db.Lookups.Add(lookup);
        db.LookupSlices.Add(slice);
        db.LookupValues.Add(LookupValue.Create(tenantId, slice.Id, 1, 0, "secret-value", label: "Secret"));
        slice.Publish(1, 1, Now, nextRefreshAt: null);
        await db.SaveChangesAsync(Ct);
        return lookup.Id;
    }

    public sealed record CreatedBody(Guid Id);

    public sealed record ManualLookupBody(Guid Id, string Name, string? Description, string Scope, int ValueCount, bool Shared, Guid[]? ScopeTenantIds);

    public sealed record ValueBody(string Value, string? Label, string[]? Synonyms, int? Count, JsonElement? Attributes);

    public sealed record ValuesBody(string? Unit, DateTimeOffset? AsOf, bool Stale, bool Truncated, List<ValueBody> Values, string? Cursor, bool CursorRestarted);
}
