using Microsoft.Extensions.DependencyInjection;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Contracts;

namespace Quorum.Integration.Tests.Embeddings;

public sealed class EmbeddingsTests(PostgresFixture postgres)
{
    private const string Model = "nomic-embed-text";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATextIsEmbeddedWithTheNamedModelOnTheTenantsEndpoint()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        app.ModelServer.ServeEmbeddings(QuorumApp.ModelBaseUrl, Model, text => [text.Length, 0.5f, -1.25f]);
        await using var scope = TenantScope.Open(app, await TenantScope.TenantIdAsync(app));

        var result = await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync("search_query: leave policy", Model, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([26f, 0.5f, -1.25f], result.Value);
        var request = Assert.Single(app.ModelServer.EmbeddingRequests);
        Assert.Equal(Model, request.Model);
        Assert.Equal("search_query: leave policy", request.Input);
    }

    [Fact]
    public async Task TheRequestAsksForPlainVectorsAndSendsNoCredentialsWhenThereIsNoKey()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        app.ModelServer.ServeEmbeddings(QuorumApp.ModelBaseUrl, Model, text => [text.Length]);
        await using var scope = TenantScope.Open(app, await TenantScope.TenantIdAsync(app));

        await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync("text", Model, Ct);

        var request = Assert.Single(app.ModelServer.EmbeddingRequests);
        Assert.Equal("float", request.EncodingFormat);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task AModelTheEndpointDoesNotServeIsReportedByName()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        await using var scope = TenantScope.Open(app, await TenantScope.TenantIdAsync(app));

        var result = await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync("text", "missing-model", Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal("embeddings.model_not_found", result.Error.Code);
        Assert.Contains("missing-model", result.Error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndpointWithoutTheEmbeddingsApiIsReported()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        app.ModelServer.RemoveEmbeddingsApi(QuorumApp.ModelBaseUrl);
        await using var scope = TenantScope.Open(app, await TenantScope.TenantIdAsync(app));

        var result = await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync("text", Model, Ct);

        Assert.Equal("embeddings.not_supported", result.Error.Code);
    }

    [Fact]
    public async Task AnEndpointErrorIsReportedWithItsStatus()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        app.ModelServer.FailEmbeddings(QuorumApp.ModelBaseUrl, 503);
        await using var scope = TenantScope.Open(app, await TenantScope.TenantIdAsync(app));

        var result = await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync("text", Model, Ct);

        Assert.Equal("embeddings.failed", result.Error.Code);
        Assert.Contains("503", result.Error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableEndpointIsReportedWithoutItsAddress()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        app.ModelServer.Stop(QuorumApp.ModelBaseUrl);
        await using var scope = TenantScope.Open(app, await TenantScope.TenantIdAsync(app));

        var result = await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync("text", Model, Ct);

        Assert.Equal("embeddings.unreachable", result.Error.Code);
        Assert.DoesNotContain("ollama.test", result.Error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyVectorIsNotAnEmbedding()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        app.ModelServer.ServeEmbeddings(QuorumApp.ModelBaseUrl, Model, _ => []);
        await using var scope = TenantScope.Open(app, await TenantScope.TenantIdAsync(app));

        var result = await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync("text", Model, Ct);

        Assert.Equal("embeddings.invalid_response", result.Error.Code);
    }

    [Fact]
    public async Task AnEmbeddingFailureLeavesNoTextInTheLogs()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        app.ModelServer.FailEmbeddings(QuorumApp.ModelBaseUrl, 500);
        await using var scope = TenantScope.Open(app, await TenantScope.TenantIdAsync(app));

        await scope.ServiceProvider.GetRequiredService<IEmbeddings>().EmbedAsync("confidential merger plans", Model, Ct);

        Assert.DoesNotContain(app.Logs.Messages, message => message.Contains("merger", StringComparison.OrdinalIgnoreCase));
    }
}
