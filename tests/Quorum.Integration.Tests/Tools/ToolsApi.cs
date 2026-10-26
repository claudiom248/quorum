using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Quorum.Integration.Tests.Tools;

/// <summary>Routes and body shapes of the tools API, as a client would use them.</summary>
internal static class ToolsApi
{
    public static Uri Tools { get; } = new("/api/tools", UriKind.Relative);

    public static Uri ToolSettings { get; } = new("/api/settings/tools", UriKind.Relative);

    public static Uri Assistants { get; } = new("/api/assistants", UriKind.Relative);

    public static Uri Tool(Guid id) => new($"/api/tools/{id}", UriKind.Relative);

    public static Uri Servers { get; } = new("/api/tools/servers", UriKind.Relative);

    public static Uri ServerTest { get; } = new("/api/tools/servers/test", UriKind.Relative);

    public static Uri Server(Guid id) => new($"/api/tools/servers/{id}", UriKind.Relative);

    public static Uri ServerRefresh(Guid id) => new($"/api/tools/servers/{id}/refresh", UriKind.Relative);

    public static object TenantScope { get; } = new { kind = "Tenant" };

    /// <summary>Registers a server; the scope is the tenant's unless the test says otherwise.</summary>
    public static async Task<HttpResponseMessage> RegisterServerAsync(
        HttpClient client,
        string name,
        string url,
        object? headers = null,
        object? scope = null) =>
        await client.PostAsJsonAsync(Servers, new { name, url, headers, scope = scope ?? TenantScope }, TestContext.Current.CancellationToken);

    /// <summary>Registers a server that must be accepted and returns what the API answered.</summary>
    public static async Task<ServerCreatedBody> RegisterServerOkAsync(HttpClient client, string name, string url, object? headers = null, object? scope = null)
    {
        using var response = await RegisterServerAsync(client, name, url, headers, scope);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ServerCreatedBody>(TestContext.Current.CancellationToken))!;
    }

    public static async Task<ServerBody> GetServerAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ServerBody>(Server(id), TestContext.Current.CancellationToken))!;

    public static async Task<IReadOnlyList<ServerBody>> ListServersAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<KnowledgeBases.KnowledgeBaseApi.PageBody<ServerBody>>(Servers, TestContext.Current.CancellationToken))!.Items;

    public static async Task<RefreshBody> RefreshServerOkAsync(HttpClient client, Guid id)
    {
        using var response = await client.PostAsync(ServerRefresh(id), content: null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RefreshBody>(TestContext.Current.CancellationToken))!;
    }

    public sealed record ScopeBody(string Kind, List<Guid> TenantIds, List<Guid> AssistantIds);

    public sealed record ServerBody(
        Guid Id,
        string Name,
        string Url,
        bool HasHeaders,
        string Status,
        string? LastError,
        ScopeBody Scope,
        int ToolCount,
        int PendingReviewCount,
        bool Shared,
        List<ToolBody>? Tools);

    public sealed record ServerCreatedBody(Guid Id, string Status, string? Warning, List<ToolBody> Tools);

    public sealed record RefreshBody(string Status, List<string> Added, List<string> Changed, List<string> Removed);

    public static Uri AssistantTools(Guid assistantId) => new($"/api/assistants/{assistantId}/tools", UriKind.Relative);

    public static async Task<IReadOnlyList<ToolBody>> ListToolsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ToolBody>>(Tools, TestContext.Current.CancellationToken))!;

    public static async Task<Guid> DefaultAssistantIdAsync(HttpClient client)
    {
        var assistants = await client.GetFromJsonAsync<List<AssistantBody>>(Assistants, TestContext.Current.CancellationToken);
        return Assert.Single(assistants!).Id;
    }

    public static async Task<AssistantToolsBody> GetAssistantToolsAsync(HttpClient client, Guid assistantId) =>
        (await client.GetFromJsonAsync<AssistantToolsBody>(AssistantTools(assistantId), TestContext.Current.CancellationToken))!;

    /// <summary>The stable <c>code</c> of a ProblemDetails response.</summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public sealed record ToolBody(Guid Id, string Origin, string? ServerName, string Name, string Description, string State, string Policy, JsonElement? Annotations = null);

    public sealed record AssistantBody(Guid Id, string Name);

    public sealed record PoolToolBody(Guid ToolId, string Name, bool Active);

    public sealed record WarningBody(string Code, string Message);

    public sealed record AssistantToolsBody(IReadOnlyList<PoolToolBody> Pool, IReadOnlyList<WarningBody> Warnings);

    public sealed record ToolSettingsBody(
        int MaxRounds,
        int TotalTimeoutSeconds,
        int CallTimeoutSeconds,
        int ConfirmationTimeoutSeconds,
        int MaxResultCharacters,
        int AuditRetentionDays);
}
