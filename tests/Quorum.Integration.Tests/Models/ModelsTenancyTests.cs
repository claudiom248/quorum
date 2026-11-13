using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Models.Contracts;
using Quorum.Modules.Models.Persistence;
using Quorum.Modules.Models.Secrets;
using Quorum.Modules.Tenancy.Contracts;

namespace Quorum.Integration.Tests.Models;

/// <summary>
/// Models across tenants: the key of a model is never sent for a tenant that does not own it, a non-owner sees a global model without its
/// address and cannot change it, and an embedding model must reach the consumer that uses it (spec 019, AC-11, AC-12, D-8, D-9).
/// </summary>
public sealed class ModelsTenancyTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    /// <summary>A tenant other than the one the setup creates: it owns the models the tests seed.</summary>
    private static readonly Guid OtherTenant = Guid.Parse("01a20000-0000-7000-8000-00000000000b");

    private static readonly Uri Models = new("/api/models", UriKind.Relative);

    private static readonly string[] ChatOnly = ["chat"];

    private static readonly string[] ChatAndTools = ["chat", "tools"];

    private static readonly string[] EmbeddingsOnly = ["embeddings"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact] // the key of a model the tenant does not own is never sent to an address the caller chooses
    public async Task TheKeyOfAModelTheTenantDoesNotOwnIsNotSentToAnAddress()
    {
        var id = await SeedAsync("shared", Quorum.Modules.Models.Domain.ModelScope.Global, [], Quorum.Modules.Models.Contracts.ModelCapabilities.Chat, "secret-of-b");

        using var response = await host.Admin.PostAsJsonAsync(new Uri("/api/models/test", UriKind.Relative), new { baseUrl = QuorumApp.ModelBaseUrl, modelId = id }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("Bearer secret-of-b", host.App.ModelServer.LastAuthorization);
    }

    [Fact] // AC-12: a global model of another tenant is listed without its address or key state, and cannot be changed or deleted
    public async Task ANonOwnerSeesAGlobalModelWithoutItsAddressAndCannotChangeIt()
    {
        var global = await SeedAsync("shared", Quorum.Modules.Models.Domain.ModelScope.Global, [], Quorum.Modules.Models.Contracts.ModelCapabilities.Chat, "k");
        var hidden = await SeedAsync("private", Quorum.Modules.Models.Domain.ModelScope.Tenant, [], Quorum.Modules.Models.Contracts.ModelCapabilities.Chat, "k2");
        var client = host.Admin;

        var list = await client.GetFromJsonAsync<JsonElement>(Models, Ct);
        var shared = list.EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == global);
        using var put = await client.PutAsJsonAsync(
            new Uri($"/api/models/{global}", UriKind.Relative),
            new { name = "renamed", baseUrl = QuorumApp.ModelBaseUrl, modelName = "m", capabilities = ChatOnly, contextTokenBudget = 4096, scope = "Global", scopeTenantIds = Array.Empty<Guid>() },
            Ct);
        using var delete = await client.DeleteAsync(new Uri($"/api/models/{global}", UriKind.Relative), Ct);
        using var hiddenGet = await client.GetAsync(new Uri($"/api/models/{hidden}", UriKind.Relative), Ct);

        Assert.False(shared.GetProperty("owned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, shared.GetProperty("baseUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, shared.GetProperty("hasApiKey").ValueKind);
        Assert.DoesNotContain(list.EnumerateArray(), m => m.GetProperty("id").GetGuid() == hidden);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, hiddenGet.StatusCode);
    }

    [Fact] // D-8: an embedding model that does not reach the consumer is refused, with its own code
    public async Task AnEmbeddingModelThatDoesNotReachTheConsumerIsRefused()
    {
        var id = await SeedAsync("private-embed", Quorum.Modules.Models.Domain.ModelScope.Tenant, [], Quorum.Modules.Models.Contracts.ModelCapabilities.Embeddings, "k");
        var admin = await AdminTenantAsync();
        await using var scope = TenantScope(host.App, admin);
        var embeddings = scope.ServiceProvider.GetRequiredService<IModelEmbeddings>();

        var result = await embeddings.EmbedQueryAsync("question", id, new ModelUseScope(admin, Global: true, []), Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal("embeddings.not_usable", result.Error.Code);
    }

    [Fact] // the capability filter of the list
    public async Task TheCapabilityFilterKeepsOnlyTheModelsWithThatCapability()
    {
        var client = host.Admin;
        using var chat = await client.PostAsJsonAsync(Models, new { name = "chat", baseUrl = QuorumApp.ModelBaseUrl, modelName = "c", capabilities = ChatAndTools, contextTokenBudget = 4096, scope = "Tenant", scopeTenantIds = Array.Empty<Guid>() }, Ct);
        using var embed = await client.PostAsJsonAsync(Models, new { name = "embed", baseUrl = QuorumApp.ModelBaseUrl, modelName = "e", capabilities = EmbeddingsOnly, scope = "Tenant", scopeTenantIds = Array.Empty<Guid>() }, Ct);

        var embeddings = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/models?capability=embeddings", UriKind.Relative), Ct);
        var tools = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/models?capability=tools", UriKind.Relative), Ct);

        Assert.Equal(["embed"], embeddings.EnumerateArray().Select(m => m.GetProperty("name").GetString()));
        Assert.Equal(["chat"], tools.EnumerateArray().Select(m => m.GetProperty("name").GetString()));
    }

    /// <summary>Stores a model owned by <see cref="OtherTenant"/>, as that tenant would, and returns its id.</summary>
    private async Task<Guid> SeedAsync(string name, Quorum.Modules.Models.Domain.ModelScope scope, IEnumerable<Guid> tenants, Quorum.Modules.Models.Contracts.ModelCapabilities capabilities, string key)
    {
        await using var scope2 = TenantScope(host.App, OtherTenant);
        var protector = scope2.ServiceProvider.GetRequiredService<ApiKeyProtector>();
        var db = scope2.ServiceProvider.GetRequiredService<ModelsDbContext>();
        var model = Quorum.Modules.Models.Domain.Model.Create(
            OtherTenant,
            name,
            new Uri(QuorumApp.ModelBaseUrl),
            "other-model",
            protector.Protect(key),
            capabilities,
            capabilities.HasFlag(Quorum.Modules.Models.Contracts.ModelCapabilities.Chat) ? 4096 : null,
            capabilities.HasFlag(Quorum.Modules.Models.Contracts.ModelCapabilities.Embeddings) ? "search_query: " : null,
            capabilities.HasFlag(Quorum.Modules.Models.Contracts.ModelCapabilities.Embeddings) ? 3 : null,
            scope,
            tenants,
            DateTimeOffset.UtcNow);
        db.Models.Add(model);
        await db.SaveChangesAsync(Ct);
        return model.Id;
    }

    private async Task<Guid> AdminTenantAsync()
    {
        await using var connection = new NpgsqlConnection(host.App.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT \"TenantId\" FROM agents.assistants LIMIT 1", connection);
        return (Guid)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>A service scope that looks like a request of the tenant, as the other tests do.</summary>
    private static AsyncServiceScope TenantScope(QuorumApp app, Guid tenantId)
    {
        var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(QuorumClaimTypes.TenantId, tenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                ],
                "test")),
        };
        return scope;
    }
}
