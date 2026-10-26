using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Quorum.Integration.Tests.KnowledgeBases;

/// <summary>Routes and body shapes of the dictionary API, as a client would use them.</summary>
internal static class DictionaryApi
{
    public static Uri Dictionary { get; } = new("/api/knowledge-bases/dictionary", UriKind.Relative);

    public static Uri Field(Guid id) => new($"/api/knowledge-bases/dictionary/{id}", UriKind.Relative);

    public static Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, string type = "Text", string? description = null, string scope = "Tenant", string? cluster = null) =>
        client.PostAsJsonAsync(Dictionary, new { name, type, description, scope, cluster }, TestContext.Current.CancellationToken);

    /// <summary>Creates a field that must be accepted and returns its id.</summary>
    public static async Task<Guid> CreateOkAsync(HttpClient client, string name, string type = "Text", string? description = null, string scope = "Tenant", string? cluster = null)
    {
        using var response = await CreateAsync(client, name, type, description, scope, cluster);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedBody>(TestContext.Current.CancellationToken))!.Id;
    }

    public static async Task<IReadOnlyList<FieldBody>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<KnowledgeBaseApi.PageBody<FieldBody>>(Dictionary, TestContext.Current.CancellationToken))!.Items;

    /// <summary>The stable <c>code</c> of a ProblemDetails response.</summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public sealed record CreatedBody(Guid Id);

    public sealed record FieldBody(Guid Id, string Name, string Type, string? Description, KnowledgeBaseApi.ScopeBody Scope, int UsedByCount, string? Cluster = null, bool Shared = false);
}
