using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Setup;

public sealed class SetupCompletionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-5
    public async Task ValidSetupCreatesTenantAdminAssistantAndModelThenSignsIn()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        app.ModelServer.Serve(SetupApi.OllamaBaseUrl, "llama3.2");
        using var client = app.CreateSetupClient();

        using var response = await client.PostAsJsonAsync(SetupApi.Complete, SetupApi.CompleteRequest(apiKey: "sk-secret"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("quorum.auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(await SetupApi.IsSetupModeAsync(client));

        await using var connection = new NpgsqlConnection(app.ConnectionString);
        await connection.OpenAsync(Ct);
        var tenantId = (Guid)(await ScalarAsync(connection, "SELECT \"Id\" FROM tenancy.tenants"))!;
        Assert.Equal("admin@acme.test", await ScalarAsync(connection, "SELECT \"Email\" FROM tenancy.users WHERE \"TenantId\" = @tenant", tenantId));
        Assert.Equal("You are Acme's assistant.", await ScalarAsync(connection, "SELECT \"SystemPrompt\" FROM agents.assistants WHERE \"TenantId\" = @tenant", tenantId));
        Assert.Equal("llama3.2", await ScalarAsync(connection, "SELECT \"ModelName\" FROM agents.model_endpoints WHERE \"TenantId\" = @tenant", tenantId));

        var storedKey = (string)(await ScalarAsync(connection, "SELECT \"ProtectedApiKey\" FROM agents.model_endpoints"))!;
        Assert.DoesNotContain("sk-secret", storedKey, StringComparison.Ordinal);
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Quorum.Agents.ModelEndpoint.ApiKey");
        Assert.Equal("sk-secret", protector.Unprotect(storedKey));
    }

    [Fact] // AC-4b
    public async Task SetupCompletesEvenWhenTheModelIsUnreachable()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateSetupClient();

        using var response = await client.PostAsJsonAsync(SetupApi.Complete, SetupApi.CompleteRequest(baseUrl: "http://still-downloading.test/v1"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact] // AC-6
    public async Task AfterSetupTheSetupApiIsGone()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        var token = app.SetupToken;
        using var client = app.CreateSetupClient();
        using var completed = await client.PostAsJsonAsync(SetupApi.Complete, SetupApi.CompleteRequest(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        using var verify = await client.PostAsJsonAsync(SetupApi.VerifyToken, new { token }, Ct);
        using var test = await client.PostAsJsonAsync(SetupApi.TestModel, new { baseUrl = SetupApi.OllamaBaseUrl }, Ct);
        using var completeAgain = await client.PostAsJsonAsync(SetupApi.Complete, SetupApi.CompleteRequest(email: "other@acme.test"), Ct);

        Assert.Equal(HttpStatusCode.NotFound, verify.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, test.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, completeAgain.StatusCode);
    }

    [Fact]
    public async Task AfterARestartACompletedInstallationStaysOutOfSetupMode()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using (var client = app.CreateSetupClient())
        using (var completed = await client.PostAsJsonAsync(SetupApi.Complete, SetupApi.CompleteRequest(), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        }

        await using var restarted = app.Restart();
        using var restartedClient = restarted.CreateClient();

        Assert.False(await SetupApi.IsSetupModeAsync(restartedClient));
        Assert.DoesNotContain(restarted.Logs.Messages, message => message.Contains("setup token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] // AC-7
    public async Task ConcurrentCompletionsLetOnlyOneSucceed()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var first = app.CreateSetupClient();
        using var second = app.CreateSetupClient();

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync(SetupApi.Complete, SetupApi.CompleteRequest(email: "first@acme.test"), Ct),
            second.PostAsJsonAsync(SetupApi.Complete, SetupApi.CompleteRequest(email: "second@acme.test"), Ct));

        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);

            await using var connection = new NpgsqlConnection(app.ConnectionString);
            await connection.OpenAsync(Ct);
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM tenancy.tenants"));
            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM agents.assistants"));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact] // AC-8
    public async Task PasswordShorterThan12CharactersIsRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateSetupClient();

        using var response = await client.PostAsJsonAsync(SetupApi.Complete, SetupApi.CompleteRequest(password: "short-pass1"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await SetupApi.IsSetupModeAsync(client));
    }

    [Theory]
    [InlineData("not-an-email", SetupApi.OllamaBaseUrl, "llama3.2")]
    [InlineData("admin@acme.test", "ftp://ollama.test/v1", "llama3.2")]
    [InlineData("admin@acme.test", SetupApi.OllamaBaseUrl, "")]
    public async Task InvalidAdminOrModelDataIsRejected(string email, string baseUrl, string modelName)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateSetupClient();

        using var response = await client.PostAsJsonAsync(SetupApi.Complete, SetupApi.CompleteRequest(email: email, baseUrl: baseUrl, modelName: modelName), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(await SetupApi.IsSetupModeAsync(client));
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, Guid? tenant = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (tenant is { } id)
        {
            command.Parameters.AddWithValue("tenant", id);
        }

        return await command.ExecuteScalarAsync(Ct);
    }
}
