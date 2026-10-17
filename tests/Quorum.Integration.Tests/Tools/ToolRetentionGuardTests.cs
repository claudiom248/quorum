using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Tools;

/// <summary>
/// Lowering the audit retention deletes entries for good at the next purge, so the API refuses to do it silently (spec 004, D-13).
/// </summary>
public sealed class ToolRetentionGuardTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> PutRetentionAsync(HttpClient client, int days, bool confirm = false) =>
        client.PutAsJsonAsync(
            new Uri(confirm ? "/api/settings/tools?confirmDeletion=true" : "/api/settings/tools", UriKind.Relative),
            new { maxRounds = 8, totalTimeoutSeconds = 300, callTimeoutSeconds = 30, confirmationTimeoutSeconds = 300, maxResultCharacters = 20_000, auditRetentionDays = days },
            Ct);

    private static async Task<int> StoredRetentionAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ToolsApi.ToolSettingsBody>(ToolsApi.ToolSettings, Ct))!.AuditRetentionDays;

    private async Task<(QuorumApp App, HttpClient Client, FakeTimeProvider Clock)> StartAsync()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        return (app, await app.SetUpAsync(), clock);
    }

    [Fact]
    public async Task LoweringTheRetentionBelowTheAgeOfSomeEntriesIsRefusedWithTheirCountAndNothingChanges()
    {
        var (app, client, clock) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        await ToolFixtures.AddAuditEntryAsync(app, clock.GetUtcNow().AddDays(-40), "forty");
        await ToolFixtures.AddAuditEntryAsync(app, clock.GetUtcNow().AddDays(-20), "twenty");
        await ToolFixtures.AddAuditEntryAsync(app, clock.GetUtcNow().AddDays(-5), "five");

        using var response = await PutRetentionAsync(client, days: 10);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tool.retention_would_delete", await ToolsApi.ProblemCodeAsync(response));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("2 audit entries", body.RootElement.GetProperty("detail").GetString()!, StringComparison.Ordinal);
        Assert.Equal(90, await StoredRetentionAsync(client));
    }

    [Fact]
    public async Task ConfirmingTheDeletionSavesTheLowerRetention()
    {
        var (app, client, clock) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        await ToolFixtures.AddAuditEntryAsync(app, clock.GetUtcNow().AddDays(-40), "forty");

        using var response = await PutRetentionAsync(client, days: 10, confirm: true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(10, await StoredRetentionAsync(client));
    }

    [Fact]
    public async Task LoweringTheRetentionWhenNothingWouldBeDeletedNeedsNoConfirmation()
    {
        var (app, client, clock) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        await ToolFixtures.AddAuditEntryAsync(app, clock.GetUtcNow().AddDays(-5), "five");

        using var response = await PutRetentionAsync(client, days: 10);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(10, await StoredRetentionAsync(client));
    }

    [Fact]
    public async Task RaisingTheRetentionNeedsNoConfirmation()
    {
        var (app, client, clock) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        await ToolFixtures.AddAuditEntryAsync(app, clock.GetUtcNow().AddDays(-40), "forty");

        using var response = await PutRetentionAsync(client, days: 365);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(365, await StoredRetentionAsync(client));
    }

    [Fact]
    public async Task AnEntryExactlyAtTheNewLimitIsNotCountedBecauseThePurgeKeepsIt()
    {
        var (app, client, clock) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        await ToolFixtures.AddAuditEntryAsync(app, clock.GetUtcNow().AddDays(-10), "at-the-limit");

        using var response = await PutRetentionAsync(client, days: 10);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task OnlyTheTenantsOwnEntriesAreCounted()
    {
        var (app, client, clock) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        await ToolFixtures.AddAuditEntryAsync(app, clock.GetUtcNow().AddDays(-40), "foreign", tenantId: Guid.CreateVersion7());

        using var response = await PutRetentionAsync(client, days: 10);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmingDoesNotExcuseAnInvalidValue()
    {
        var (app, client, _) = await StartAsync();
        await using var __ = app;
        using var ___ = client;

        using var response = await PutRetentionAsync(client, days: 0, confirm: true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(90, await StoredRetentionAsync(client));
    }

    [Fact]
    public async Task ARefusedChangeDoesNotLetThePurgeDeleteAnything()
    {
        var (app, client, clock) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        await ToolFixtures.AddAuditEntryAsync(app, clock.GetUtcNow().AddDays(-40), "forty");

        using var response = await PutRetentionAsync(client, days: 10);
        await using var scope = app.Services.CreateAsyncScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<Quorum.Modules.Agents.Tools.AuditRetention>().PurgeAsync(Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, deleted);
    }
}
