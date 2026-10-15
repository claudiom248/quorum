using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Setup;

public sealed class ModelConnectionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-3
    public async Task ReachableEndpointReturnsItsModels()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        app.ModelServer.Serve(SetupApi.OllamaBaseUrl, "llama3.2", "qwen2.5:7b");
        using var client = app.CreateSetupClient();

        using var response = await client.PostAsJsonAsync(SetupApi.TestModel, new { baseUrl = SetupApi.OllamaBaseUrl }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<SetupApi.ModelTestBody>(Ct);
        Assert.True(body!.Reachable);
        Assert.Equal(["llama3.2", "qwen2.5:7b"], body.Models);
    }

    [Fact]
    public async Task ApiKeyIsSentAsBearerToken()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        app.ModelServer.Serve(SetupApi.OllamaBaseUrl, "llama3.2");
        using var client = app.CreateSetupClient();

        using var response = await client.PostAsJsonAsync(SetupApi.TestModel, new { baseUrl = SetupApi.OllamaBaseUrl, apiKey = "sk-test" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer sk-test", app.ModelServer.LastAuthorization);
    }

    [Fact] // AC-4
    public async Task UnreachableEndpointReturns422WithAReadableMessage()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateSetupClient();

        using var response = await client.PostAsJsonAsync(SetupApi.TestModel, new { baseUrl = "http://nowhere.test:11434/v1" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.Contains("nowhere.test", problem!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidBaseUrlIsAValidationError()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = app.CreateSetupClient();

        using var response = await client.PostAsJsonAsync(SetupApi.TestModel, new { baseUrl = "not a url" }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
