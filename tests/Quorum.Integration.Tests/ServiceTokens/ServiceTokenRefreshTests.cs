using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Integration.Tests.KnowledgeBases.Lookups;

namespace Quorum.Integration.Tests.ServiceTokens;

/// <summary>Refresh and status endpoints with the service token (spec 011 AC-4..AC-10, AC-13).</summary>
public sealed class ServiceTokenRefreshTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri TokenEndpoint = new("/api/tenants/me/service-tokens", UriKind.Relative);

    [Fact] // AC-4
    public async Task RevokedTokenIsRejectedWith401()
    {
        var (app, admin, knowledgeBaseId, token) = await SeedAsync();
        await using var _ = app;

        await admin.DeleteAsync(new Uri($"{TokenEndpoint}/{(await GetTokenIdAsync(admin, token)).Id}", UriKind.Relative), Ct);

        using var refresh = await CallRefreshAsync(app, token, knowledgeBaseId);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact] // AC-5
    public async Task RefreshWithValidBearerTokenReturns202()
    {
        var (app, admin, knowledgeBaseId, token) = await SeedAsync();
        await using var _ = app;

        using var refresh = await CallRefreshAsync(app, token, knowledgeBaseId);

        Assert.Equal(HttpStatusCode.Accepted, refresh.StatusCode);
    }

    [Fact] // AC-6
    public async Task RefreshOnAnotherTenantKnowledgeBaseReturns404()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var admin = await app.SetUpAsync();
        var token = await CreateTokenAsync(admin);
        var otherKb = await LookupApi.SeedDueAsync(app, Guid.CreateVersion7());

        using var refresh = await CallRefreshAsync(app, token, otherKb.KnowledgeBaseId);

        Assert.Equal(HttpStatusCode.NotFound, refresh.StatusCode);
    }

    [Fact] // AC-7
    public async Task RefreshWhileOneIsRunningReturns409()
    {
        var (app, admin, knowledgeBaseId, token) = await SeedAsync();
        await using var _ = app;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.LookupComputer.Handler = async (_, cancellation) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            return FakeLookupComputer.Of(("x", 1));
        };

        try
        {
            using var first = await CallRefreshAsync(app, token, knowledgeBaseId);
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

            await started.Task.WaitAsync(Ct);
            using var second = await CallRefreshAsync(app, token, knowledgeBaseId);

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact] // AC-8
    public async Task StatusWithValidBearerTokenReturnsSummary()
    {
        var (app, admin, knowledgeBaseId, token) = await SeedAsync();
        await using var _ = app;

        using var response = await CallStatusAsync(app, token, knowledgeBaseId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RefreshStatus>(Ct);
        Assert.NotNull(body);
        Assert.Equal("idle", body.Status);
        Assert.True(body.TotalSlices >= 0);
    }

    [Fact] // AC-9
    public async Task StatusOnKnowledgeBaseWithoutLookupsReturnsIdleZero()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var admin = await app.SetUpAsync();
        var token = await CreateTokenAsync(admin);
        var tenantId = await TenantScope.TenantIdAsync(app);
        var kb = Quorum.Integration.Tests.KnowledgeBases.KnowledgeBaseFixtures.Create(
            tenantId, "Empty KB", "http://qdrant.test:6333", "col",
            Quorum.Modules.KnowledgeBases.Domain.KnowledgeBaseScope.Tenant, []);

        await using (var scope = TenantScope.Open(app, tenantId))
        {
            var db = scope.ServiceProvider.GetRequiredService<Quorum.Modules.KnowledgeBases.Persistence.KnowledgeBasesDbContext>();
            db.KnowledgeBases.Add(kb);
            await db.SaveChangesAsync(Ct);
        }

        using var response = await CallStatusAsync(app, token, kb.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RefreshStatus>(Ct);
        Assert.Equal(("idle", 0, 0), (body!.Status, body.RunningSlices, body.TotalSlices));
    }

    [Fact] // AC-10
    public async Task StatusWithCookieAdminReturnsSameAsBearer()
    {
        var (app, admin, knowledgeBaseId, token) = await SeedAsync();
        await using var _ = app;

        using var bearerResponse = await CallStatusAsync(app, token, knowledgeBaseId);
        using var cookieResponse = await admin.GetAsync(new Uri($"/api/knowledge-bases/{knowledgeBaseId}/refresh-status", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, bearerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cookieResponse.StatusCode);
        var bearer = await bearerResponse.Content.ReadFromJsonAsync<RefreshStatus>(Ct);
        var cookie = await cookieResponse.Content.ReadFromJsonAsync<RefreshStatus>(Ct);
        Assert.Equal(bearer!.Status, cookie!.Status);
        Assert.Equal(bearer.TotalSlices, cookie.TotalSlices);
    }

    [Fact] // AC-13
    public async Task ServiceTokenOnNonRefreshEndpointReturns403()
    {
        var (app, _, _, token) = await SeedAsync();
        await using var _ = app;

        using var response = await CallWithBearerAsync(app, token, "/api/knowledge-bases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Helpers ---

    private async Task<(QuorumApp App, HttpClient Admin, Guid KnowledgeBaseId, string Token)> SeedAsync()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        var admin = await app.SetUpAsync();
        var token = await CreateTokenAsync(admin);
        // Not due: a due slice would be computed by the background service on its own, racing with what the test asserts.
        var seeded = await LookupApi.SeedDueAsync(
            app,
            await TenantScope.TenantIdAsync(app),
            published: [("x", 1)],
            nextRefreshAt: clock.GetUtcNow().AddDays(30));
        return (app, admin, seeded.KnowledgeBaseId, token);
    }

    private static async Task<string> CreateTokenAsync(HttpClient admin)
    {
        var response = await admin.PostAsync(TokenEndpoint, content: null, Ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CreateTokenResponse>(Ct);
        return body!.Token;
    }

    private static async Task<TaskInfo> GetTokenIdAsync(HttpClient admin, string _)
    {
        var list = await admin.GetFromJsonAsync<List<TaskInfo>>(TokenEndpoint, Ct);
        return list!.First();
    }

    private static async Task<HttpResponseMessage> CallRefreshAsync(QuorumApp app, string token, Guid kbId)
    {
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.PostAsync(new Uri($"/api/knowledge-bases/{kbId}/lookups/refresh", UriKind.Relative), content: null, Ct);
    }

    private static async Task<HttpResponseMessage> CallStatusAsync(QuorumApp app, string token, Guid kbId)
    {
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync(new Uri($"/api/knowledge-bases/{kbId}/refresh-status", UriKind.Relative), Ct);
    }

    private static async Task<HttpResponseMessage> CallWithBearerAsync(QuorumApp app, string token, string path)
    {
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync(new Uri(path, UriKind.Relative), Ct);
    }

    private sealed record CreateTokenResponse(Guid Id, string Token, DateTimeOffset CreatedAt);
    private sealed record TaskInfo(Guid Id, DateTimeOffset CreatedAt);
    private sealed record RefreshStatus(string Status, DateTimeOffset? LastCompletedAt, int RunningSlices, int TotalSlices);
}
