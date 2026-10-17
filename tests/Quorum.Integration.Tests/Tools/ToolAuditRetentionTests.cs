using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Persistence;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Integration.Tests.Tools;

/// <summary>The purge of old audit entries (spec 004, AC-45, D-13).</summary>
public sealed class ToolAuditRetentionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AddEntryAsync(QuorumApp app, DateTimeOffset startedAt, string tool, Guid? tenantId = null)
    {
        var tenant = tenantId ?? await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"TenantId\" FROM agents.assistants LIMIT 1");
        var assistant = await ToolFixtures.ScalarAsync<Guid>(app, "SELECT \"Id\" FROM agents.assistants LIMIT 1");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        var entry = ToolAuditEntry.Start(tenant, Guid.NewGuid(), assistant, Guid.NewGuid(), Guid.NewGuid(), null, tool, ToolDecision.Auto, "{}", false, startedAt);
        entry.Complete(ToolOutcome.Succeeded, 1, null, startedAt.AddMilliseconds(5), TimeSpan.FromMilliseconds(5));
        db.ToolAuditEntries.Add(entry);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<int> PurgeAsync(QuorumApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditRetention>().PurgeAsync(Ct);
    }

    private static async Task<string[]> RemainingAsync(QuorumApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
        return db.ToolAuditEntries.IgnoreQueryFilters().OrderBy(e => e.ToolName).Select(e => e.ToolName).ToArray();
    }

    [Fact] // AC-45
    public async Task EntriesOlderThanTheRetentionAreDeletedAndRecentOnesAreKept()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();
        var now = clock.GetUtcNow();
        await AddEntryAsync(app, now.AddDays(-91), "ancient");
        await AddEntryAsync(app, now.AddDays(-90).AddMinutes(-1), "just-over");
        await AddEntryAsync(app, now.AddDays(-89), "recent");
        await AddEntryAsync(app, now, "today");

        var deleted = await PurgeAsync(app);

        Assert.Equal(2, deleted);
        Assert.Equal(["recent", "today"], await RemainingAsync(app));
    }

    [Fact] // AC-45
    public async Task AnEntryExactlyAtTheLimitIsKept()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();
        await AddEntryAsync(app, clock.GetUtcNow().AddDays(-90), "at-the-limit");

        var deleted = await PurgeAsync(app);

        Assert.Equal(0, deleted);
        Assert.Equal(["at-the-limit"], await RemainingAsync(app));
    }

    [Fact] // D-13
    public async Task ThePurgeFollowsTheRetentionTheAdministratorSet()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();
        await ToolFixtures.SetLimitsAsync(client);
        using var change = await client.PutAsJsonAsync(
            ToolsApi.ToolSettings,
            new { maxRounds = 8, totalTimeoutSeconds = 300, callTimeoutSeconds = 30, confirmationTimeoutSeconds = 300, maxResultCharacters = 20_000, auditRetentionDays = 10 },
            Ct);
        change.EnsureSuccessStatusCode();
        await AddEntryAsync(app, clock.GetUtcNow().AddDays(-20), "twenty-days");
        await AddEntryAsync(app, clock.GetUtcNow().AddDays(-5), "five-days");

        await PurgeAsync(app);

        Assert.Equal(["five-days"], await RemainingAsync(app));
    }

    [Fact] // AC-45
    public async Task EachTenantIsPurgedByItsOwnRetention()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();
        var other = Guid.CreateVersion7();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentsDbContext>();
            var settings = ToolSettings.CreateDefault(other);
            settings.Change(8, 300, 30, 300, 20_000, auditRetentionDays: 365);
            db.ToolSettings.Add(settings);
            await db.SaveChangesAsync(Ct);
        }

        var sixtyDays = clock.GetUtcNow().AddDays(-60);
        await ToolFixtures.ExecuteAsync(app, "UPDATE agents.tool_settings SET \"AuditRetentionDays\" = 30 WHERE \"TenantId\" <> '" + other + "'");
        await AddEntryAsync(app, sixtyDays, "mine-old");
        await AddEntryAsync(app, sixtyDays, "theirs-old", tenantId: other);

        await PurgeAsync(app);

        Assert.Equal(["theirs-old"], await RemainingAsync(app));
    }

    [Fact] // AC-45
    public async Task ATenantWithoutSettingsRowIsPurgedWithTheDefaultRetention()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();
        var orphan = Guid.CreateVersion7();
        await AddEntryAsync(app, clock.GetUtcNow().AddDays(-120), "orphan-old", tenantId: orphan);
        await AddEntryAsync(app, clock.GetUtcNow().AddDays(-10), "orphan-recent", tenantId: orphan);

        await PurgeAsync(app);

        Assert.Equal(["orphan-recent"], await RemainingAsync(app));
    }

    [Fact] // AC-45
    public async Task TheJobPurgesOnItsOwnOnceADay()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();
        await AddEntryAsync(app, clock.GetUtcNow().AddDays(-89), "about-to-expire");
        Assert.Equal(["about-to-expire"], await RemainingAsync(app));

        // Two days later the entry is past 90 days: the daily tick must have removed it without anyone asking.
        for (var attempt = 0; attempt < 100 && (await RemainingAsync(app)).Length > 0; attempt++)
        {
            clock.Advance(TimeSpan.FromDays(2));
            await Task.Delay(100, Ct);
        }

        Assert.Empty(await RemainingAsync(app));
    }

    [Fact] // AC-45
    public async Task EntriesThatExpiredWhileTheApplicationWasStoppedAreDeletedAtStartup()
    {
        await using var first = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var client = await first.SetUpAsync();
        await AddEntryAsync(first, DateTimeOffset.UtcNow.AddDays(-200), "expired-while-down");
        await AddEntryAsync(first, DateTimeOffset.UtcNow.AddDays(-1), "yesterday");

        await using var restarted = first.Restart();
        using var again = restarted.CreateUiClient();

        Assert.Equal(["yesterday"], await RemainingAsync(restarted));
    }

    [Fact]
    public async Task ANewDatabaseWithoutEntriesPurgesNothingAndKeepsRunning()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        using var client = await app.SetUpAsync();

        var deleted = await PurgeAsync(app);

        Assert.Equal(0, deleted);
        Assert.Empty(app.Logs.ErrorsAndAbove);
    }
}
