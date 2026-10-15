using System.Net.Http.Json;

namespace Quorum.Integration.Tests.Setup;

/// <summary>Request bodies and helpers for the setup API, as a client would send them.</summary>
internal static class SetupApi
{
    public const string OllamaBaseUrl = "http://ollama.test:11434/v1";
    public const string ValidPassword = "correct horse battery";

    public static Uri Status { get; } = new("/api/setup/status", UriKind.Relative);

    public static Uri VerifyToken { get; } = new("/api/setup/verify-token", UriKind.Relative);

    public static Uri TestModel { get; } = new("/api/setup/model/test", UriKind.Relative);

    public static Uri Complete { get; } = new("/api/setup/complete", UriKind.Relative);

    public static object CompleteRequest(
        string email = "admin@acme.test",
        string password = ValidPassword,
        string baseUrl = OllamaBaseUrl,
        string modelName = "llama3.2",
        string? apiKey = null,
        string? systemPrompt = "You are Acme's assistant.") => new
        {
            admin = new { email, displayName = "Acme Admin", password },
            model = new { baseUrl, modelName, apiKey, contextTokenBudget = 4096 },
            systemPrompt,
        };

    public static async Task<bool> IsSetupModeAsync(HttpClient client)
    {
        var status = await client.GetFromJsonAsync<StatusBody>(Status, TestContext.Current.CancellationToken);
        return status!.IsSetupMode;
    }

    public sealed record StatusBody(bool IsSetupMode);

    public sealed record ModelTestBody(bool Reachable, string[] Models);
}
