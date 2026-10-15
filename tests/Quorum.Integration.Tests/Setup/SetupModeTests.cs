using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Setup;

public sealed class SetupModeTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-1
    public async Task NewInstallationAppliesMigrationsEntersSetupModeAndLogsTheToken()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateClient();

        Assert.True(await SetupApi.IsSetupModeAsync(client));
        Assert.Contains(app.Logs.Messages, message => message.Contains(app.SetupToken, StringComparison.Ordinal));

        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var schema in new[] { "tenancy", "agents" })
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name = '__EFMigrationsHistory'",
                connection);
            command.Parameters.AddWithValue("schema", schema);
            Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
        }
    }

    [Fact] // AC-1b (spec 001 amendment)
    public async Task StatusExposesTheDefaultSystemPromptOnlyDuringSetup()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var anonymous = app.CreateClient();

        var during = await anonymous.GetFromJsonAsync<StatusWithPrompt>(SetupApi.Status, Ct);
        using var _ = await app.SetUpAsync();
        var after = await anonymous.GetFromJsonAsync<StatusWithPrompt>(SetupApi.Status, Ct);

        Assert.False(string.IsNullOrWhiteSpace(during!.DefaultSystemPrompt));
        Assert.Null(after!.DefaultSystemPrompt);
    }

    [Fact]
    public async Task FirstStartOnAnEmptyDatabaseLogsNoErrors()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);

        Assert.Empty(app.Logs.ErrorsAndAbove);
    }

    [Fact] // AC-2
    public async Task SetupApiRejectsMissingOrWrongToken()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var anonymous = app.CreateClient();
        using var wrongToken = app.CreateClient();
        wrongToken.DefaultRequestHeaders.Add("X-Setup-Token", "AAAA-AAAA-AAAA-AAAA-AAAA");

        using var verify = await anonymous.PostAsJsonAsync(SetupApi.VerifyToken, new { token = "AAAA-AAAA-AAAA-AAAA-AAAA" }, Ct);
        using var testWithoutToken = await anonymous.PostAsJsonAsync(SetupApi.TestModel, new { baseUrl = SetupApi.OllamaBaseUrl }, Ct);
        using var completeWithWrongToken = await wrongToken.PostAsJsonAsync(SetupApi.Complete, SetupApi.CompleteRequest(), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, verify.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, testWithoutToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, completeWithWrongToken.StatusCode);
    }

    [Fact]
    public async Task CorrectTokenIsAcceptedIgnoringCaseAndSpaces()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateClient();
        var typed = $"  {app.SetupToken.ToLowerInvariant()} ";

        using var response = await client.PostAsJsonAsync(SetupApi.VerifyToken, new { token = typed }, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact] // AC-9
    public async Task RestartDuringSetupIssuesANewTokenAndInvalidatesTheOldOne()
    {
        await using var first = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var oldToken = first.SetupToken;

        await using var restarted = first.Restart();
        using var client = restarted.CreateClient();
        using var withOldToken = await client.PostAsJsonAsync(SetupApi.VerifyToken, new { token = oldToken }, Ct);

        Assert.NotEqual(oldToken, restarted.SetupToken);
        Assert.Equal(HttpStatusCode.Unauthorized, withOldToken.StatusCode);
        Assert.Contains(restarted.Logs.Messages, message => message.Contains(restarted.SetupToken, StringComparison.Ordinal));
    }

    private sealed record StatusWithPrompt(bool IsSetupMode, string? DefaultSystemPrompt);
}
