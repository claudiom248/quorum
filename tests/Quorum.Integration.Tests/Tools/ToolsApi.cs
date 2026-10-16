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

    public sealed record ToolBody(Guid Id, string Origin, string? ServerName, string Name, string Description, string State, string Policy);

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
