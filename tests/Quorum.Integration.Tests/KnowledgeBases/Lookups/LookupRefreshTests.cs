using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Quorum.Integration.Tests.Fixtures;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Persistence;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

/// <summary>Computing the lookups in the background and swapping generations (spec 007 D-10).</summary>
public sealed class LookupRefreshTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<(string Value, int Count)> Before = [("old-a", 10), ("old-b", 5)];

    private async Task<(QuorumApp App, FakeTimeProvider Clock, Guid TenantId)> StartAsync()
    {
        var clock = new FakeTimeProvider(Start);
        var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, clock);
        (await app.SetUpAsync()).Dispose();
        return (app, clock, await TenantScope.TenantIdAsync(app));
    }

    /// <summary>Signs the administrator in again: a client of the first app is not valid on a restarted one.</summary>
    private static async Task<HttpClient> AdminAsync(QuorumApp app)
    {
        var client = app.CreateUiClient();
        using var login = await client.PostAsJsonAsync(new Uri("/api/auth/login", UriKind.Relative), new { email = QuorumApp.AdminEmail, password = QuorumApp.AdminPassword }, Ct);
        login.EnsureSuccessStatusCode();
        return client;
    }

    private static LookupRefresher Refresher(QuorumApp app) => app.Services.GetRequiredService<LookupRefresher>();

    [Fact] // AC-13
    public async Task ASuccessfulComputationMakesTheNewGenerationCurrentAndDeletesTheOldOne()
    {
        var (app, clock, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before);
        app.LookupComputer.Handler = (_, _) => Task.FromResult(FakeLookupComputer.Of(("x", 5), ("y", 9), ("z", 1)));

        Assert.Equal(1, await Refresher(app).RunDueAsync(Ct));

        var slice = await LookupApi.SliceAsync(app, seeded.SliceId);
        Assert.Equal((2, 3, LookupStatus.Ready, false, null), (slice.CurrentGeneration, slice.ValueCount, slice.Status, slice.Stale, slice.LastError));
        Assert.Equal(clock.GetUtcNow(), slice.ComputedAt);
        Assert.Equal(clock.GetUtcNow().AddHours(24), slice.NextRefreshAt);
        Assert.Null(slice.ComputingSince);
        Assert.Equal([(2, "y", 9), (2, "x", 5), (2, "z", 1)], await LookupApi.StoredValuesAsync(app, seeded.SliceId));
    }

    [Fact] // AC-13
    public async Task AReaderSeesTheOldListUntilTheSwapAndTheNewOneAfterIt()
    {
        var (app, _, tenantId) = await StartAsync();
        await using var _ = app;
        using var admin = await AdminAsync(app);
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.LookupComputer.Handler = async (_, token) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return FakeLookupComputer.Of(("new-a", 3));
        };

        var run = Refresher(app).RunDueAsync(Ct);
        await started.Task.WaitAsync(Ct);
        var during = await LookupApi.ReadValuesOkAsync(admin, seeded.LookupId, null);
        release.SetResult();
        await run;
        var after = await LookupApi.ReadValuesOkAsync(admin, seeded.LookupId, null);

        Assert.Equal(["old-a", "old-b"], during.Values.Select(v => v.Value));
        Assert.Equal(["new-a"], after.Values.Select(v => v.Value));
    }

    [Fact] // AC-14
    public async Task AFailedComputationKeepsTheLastListMarksItStaleAndRetriesAtTheNextTurn()
    {
        var (app, clock, tenantId) = await StartAsync();
        await using var _ = app;
        using var admin = await AdminAsync(app);
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before);
        app.LookupComputer.Handler = (_, _) => throw new LookupComputationException("The field 'supplier' has no payload index.");

        await Refresher(app).RunDueAsync(Ct);

        var failed = await LookupApi.SliceAsync(app, seeded.SliceId);
        Assert.Equal((1, LookupStatus.Ready, true, "The field 'supplier' has no payload index."), (failed.CurrentGeneration, failed.Status, failed.Stale, failed.LastError));
        Assert.Equal(clock.GetUtcNow().AddHours(1), failed.NextRefreshAt);
        Assert.Null(failed.ComputingSince);
        var served = await LookupApi.ReadValuesOkAsync(admin, seeded.LookupId, null);
        Assert.True(served.Stale);
        Assert.Equal(["old-a", "old-b"], served.Values.Select(v => v.Value));

        // Not due yet: nothing is tried before the retry time.
        Assert.Equal(0, await Refresher(app).RunDueAsync(Ct));

        app.LookupComputer.Handler = (_, _) => Task.FromResult(FakeLookupComputer.Of(("fixed", 2)));
        // The app's own service ticks when the clock moves, so it may take the slice before the test does: wait for the outcome.
        clock.Advance(TimeSpan.FromHours(1));
        await Refresher(app).RunDueAsync(Ct);

        var retried = await LookupApi.UntilAsync(() => LookupApi.SliceAsync(app, seeded.SliceId), s => s.CurrentGeneration == 2 && s.ComputingSince is null);
        Assert.Equal((2, LookupStatus.Ready, false, null), (retried.CurrentGeneration, retried.Status, retried.Stale, retried.LastError));
    }

    [Fact] // AC-14
    public async Task AFirstComputationThatFailsLeavesTheSliceFailedWithNothingToServe()
    {
        var (app, _, tenantId) = await StartAsync();
        await using var _ = app;
        using var admin = await AdminAsync(app);
        var seeded = await LookupApi.SeedDueAsync(app, tenantId);
        app.LookupComputer.Handler = (_, _) => throw new LookupComputationException("The store is unreachable.");

        await Refresher(app).RunDueAsync(Ct);

        var slice = await LookupApi.SliceAsync(app, seeded.SliceId);
        Assert.Equal((0, LookupStatus.Failed, false, "The store is unreachable."), (slice.CurrentGeneration, slice.Status, slice.Stale, slice.LastError));
        Assert.Empty((await LookupApi.ReadValuesOkAsync(admin, seeded.LookupId, null)).Values);
    }

    [Fact] // AC-14
    public async Task ATimeoutFailsTheComputationWithoutLosingTheLastList()
    {
        var (app, clock, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.LookupComputer.Handler = async (_, token) =>
        {
            running.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return FakeLookupComputer.Of();
        };

        // The budget is measured by the app's clock, which the test moves past it.
        var run = Refresher(app).RunDueAsync(Ct);
        await running.Task.WaitAsync(Ct);
        clock.Advance(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LookupOptions>>().Value.ComputationTimeout + TimeSpan.FromSeconds(1));
        await run;

        var slice = await LookupApi.SliceAsync(app, seeded.SliceId);
        Assert.Equal((1, true), (slice.CurrentGeneration, slice.Stale));
        Assert.Contains("did not finish", slice.LastError, StringComparison.Ordinal);
        Assert.Equal([(1, "old-a", 10), (1, "old-b", 5)], await LookupApi.StoredValuesAsync(app, seeded.SliceId));
    }

    [Fact] // AC-13
    public async Task ARowsLeftByACrashedComputationAreRemovedBeforeTheNextOneWrites()
    {
        var (app, _, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            db.LookupValues.Add(LookupValue.Create(tenantId, seeded.SliceId, generation: 2, position: 0, value: "half-written", documentCount: 1));
            await db.SaveChangesAsync(Ct);
        }

        app.LookupComputer.Handler = (_, _) => Task.FromResult(FakeLookupComputer.Of(("whole", 4)));
        await Refresher(app).RunDueAsync(Ct);

        Assert.Equal([(2, "whole", 4)], await LookupApi.StoredValuesAsync(app, seeded.SliceId));
    }

    [Fact]
    public async Task TheListIsCutAtTheCeilingKeepingTheMostCountedAndValuesThatAreTooLongAreSkipped()
    {
        var (app, _, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedDueAsync(app, tenantId);
        var tooLong = new string('x', LookupValue.MaxValueLength + 1);
        app.LookupComputer.Handler = (_, _) => Task.FromResult(new LookupComputation([
            .. Enumerable.Range(0, LookupOptions.DefaultMaxValuesPerLookup + 10).Select(i => new ComputedValue($"v{i:D5}", i)),
            new ComputedValue(tooLong, 1_000_000),
        ]));

        await Refresher(app).RunDueAsync(Ct);

        var slice = await LookupApi.SliceAsync(app, seeded.SliceId);
        Assert.Equal((LookupOptions.DefaultMaxValuesPerLookup, 1, true), (slice.ValueCount, slice.SkippedValueCount, slice.Truncated));
        var stored = await LookupApi.StoredValuesAsync(app, seeded.SliceId);
        Assert.Equal(("v" + (LookupOptions.DefaultMaxValuesPerLookup + 9).ToString("D5", CultureInfo.InvariantCulture), LookupOptions.DefaultMaxValuesPerLookup + 9), (stored[0].Value, stored[0].Count));
        Assert.Equal(10, stored[^1].Count);
    }

    [Fact]
    public async Task AnEntityLookupKeepsTheOrderTheComputerGave()
    {
        var (app, _, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, kind: LookupKind.Entity);
        app.LookupComputer.Handler = (_, _) => Task.FromResult(new LookupComputation([new ComputedValue("Zed", Attributes: """{"age":30}"""), new ComputedValue("Ann", Attributes: """{"age":50}""")]));

        await Refresher(app).RunDueAsync(Ct);

        Assert.Equal(["Zed", "Ann"], (await LookupApi.StoredValuesAsync(app, seeded.SliceId)).Select(v => v.Value));
    }

    [Fact] // AC-15
    public async Task ASliceIsRecomputedWhenItsIntervalPassesWithoutAnyRequest()
    {
        var (app, clock, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before, nextRefreshAt: Start.AddHours(24));
        app.LookupComputer.Handler = (_, _) => Task.FromResult(FakeLookupComputer.Of(("scheduled", 1)));

        // Before the interval nothing is computed, however long the service waits.
        clock.Advance(TimeSpan.FromHours(23));
        await Task.Delay(300, Ct);
        Assert.Equal(0, app.LookupComputer.Calls);

        // The service may not have created its timer yet, so the clock keeps moving by a turn until it has run.
        var slice = await LookupApi.UntilAsync(
            () => LookupApi.SliceAsync(app, seeded.SliceId),
            s => s.CurrentGeneration == 2,
            between: () =>
            {
                clock.Advance(LookupRefreshService.Interval);
                return Task.CompletedTask;
            });

        Assert.True(slice.ComputedAt >= Start.AddHours(23));
        Assert.Equal([(2, "scheduled", 1)], await LookupApi.StoredValuesAsync(app, seeded.SliceId));
    }

    [Fact] // AC-16
    public async Task RefreshNowIsAcceptedAndComputedByTheServiceAndRefusedWhileOneRuns()
    {
        var (app, _, tenantId) = await StartAsync();
        await using var _ = app;
        using var admin = await AdminAsync(app);
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before, nextRefreshAt: Start.AddHours(24));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.LookupComputer.Handler = async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return FakeLookupComputer.Of(("now", 1));
        };

        using (var accepted = await LookupApi.PostAsync(admin, LookupApi.RefreshKnowledgeBase(seeded.KnowledgeBaseId)))
        {
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        await started.Task.WaitAsync(Ct);
        using (var byKnowledgeBase = await LookupApi.PostAsync(admin, LookupApi.RefreshKnowledgeBase(seeded.KnowledgeBaseId)))
        {
            Assert.Equal(HttpStatusCode.Conflict, byKnowledgeBase.StatusCode);
            Assert.Equal("lookup.refresh_running", await LookupApi.ProblemCodeAsync(byKnowledgeBase));
        }

        using (var byLookup = await LookupApi.PostAsync(admin, LookupApi.RefreshLookup(seeded.LookupId)))
        {
            Assert.Equal(HttpStatusCode.Conflict, byLookup.StatusCode);
            Assert.Equal("lookup.refresh_running", await LookupApi.ProblemCodeAsync(byLookup));
        }

        release.SetResult();
        await LookupApi.UntilAsync(() => LookupApi.SliceAsync(app, seeded.SliceId), s => s.CurrentGeneration == 2 && s.ComputingSince is null);

        using var again = await LookupApi.PostAsync(admin, LookupApi.RefreshLookup(seeded.LookupId));
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
    }

    [Fact] // AC-16
    public async Task RefreshOfAnUnknownKnowledgeBaseOrAManualLookupIsNotFound()
    {
        var (app, _, _) = await StartAsync();
        await using var _ = app;
        using var admin = await AdminAsync(app);
        var manual = await LookupApi.CreateManualOkAsync(admin);

        using var unknown = await LookupApi.PostAsync(admin, LookupApi.RefreshKnowledgeBase(Guid.NewGuid()));
        using var byManual = await LookupApi.PostAsync(admin, LookupApi.RefreshLookup(manual));
        using var byUnknown = await LookupApi.PostAsync(admin, LookupApi.RefreshLookup(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byManual.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byUnknown.StatusCode);
    }

    [Fact]
    public async Task OnlyOneComputationPerKnowledgeBaseRunsAtATimeAndTwoInTheInstallation()
    {
        var (app, _, tenantId) = await StartAsync();
        await using var _ = app;
        var a = await LookupApi.SeedDueAsync(app, tenantId);
        var b = await LookupApi.SeedDueAsync(app, tenantId);
        var c = await LookupApi.SeedDueAsync(app, tenantId);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            // A second slice of the first knowledge base: still only one of the two may run.
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            var second = Lookup.Computed(tenantId, a.KnowledgeBaseId, Guid.NewGuid(), LookupUnit.Chunks, Start);
            db.Lookups.Add(second);
            db.LookupSlices.Add(LookupSlice.Create(tenantId, second.Id, LookupSliceKey.WholeKnowledgeBase.Hash, LookupSliceKey.WholeKnowledgeBase.Filters, Start));
            await db.SaveChangesAsync(Ct);
        }

        var claimed = await Refresher(app).ClaimAsync(Ct);

        Assert.Equal(LookupRefresher.MaxConcurrent, claimed.Count);
        await using var read = app.Services.CreateAsyncScope();
        var db2 = read.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        var lookups = await db2.LookupSlices.IgnoreQueryFilters().Where(s => claimed.Contains(s.Id))
            .Join(db2.Lookups.IgnoreQueryFilters(), s => s.LookupId, l => l.Id, (s, l) => l.KnowledgeBaseId)
            .ToListAsync(Ct);
        Assert.Equal(lookups.Count, lookups.Distinct().Count());
    }

    [Fact]
    public async Task TwoInstancesOnOneDatabaseComputeADueSliceOnlyOnce()
    {
        var (first, _, tenantId) = await StartAsync();
        await using var _ = first;
        await using var second = QuorumApp.Start(first.ConnectionString, clock: new FakeTimeProvider(Start));
        var seeded = await LookupApi.SeedDueAsync(first, tenantId, Before);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var app in new[] { first, second })
        {
            app.LookupComputer.Handler = async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return FakeLookupComputer.Of(("once", 1));
            };
        }

        var runs = Task.WhenAll(Refresher(first).RunDueAsync(Ct), Refresher(second).RunDueAsync(Ct));
        await Task.Delay(300, Ct);
        release.SetResult();
        var claimed = await runs;

        Assert.Equal(1, claimed.Sum());
        Assert.Equal(1, first.LookupComputer.Calls + second.LookupComputer.Calls);
        Assert.Equal([(2, "once", 1)], await LookupApi.StoredValuesAsync(first, seeded.SliceId));
    }

    [Fact]
    public async Task ADeadComputationDoesNotBlockTheSliceForeverAndALiveOneIsNotTakenOver()
    {
        var (app, clock, tenantId) = await StartAsync();
        await using var _ = app;
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before);
        app.LookupComputer.Handler = (_, _) => Task.FromResult(FakeLookupComputer.Of(("taken-over", 1)));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
            await db.LookupSlices.IgnoreQueryFilters().Where(s => s.Id == seeded.SliceId)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.ComputingSince, clock.GetUtcNow()), Ct);
        }

        Assert.Equal(0, await Refresher(app).RunDueAsync(Ct));

        clock.Advance(app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LookupOptions>>().Value.Lease + TimeSpan.FromSeconds(1));
        await Refresher(app).RunDueAsync(Ct);
        var taken = await LookupApi.UntilAsync(() => LookupApi.SliceAsync(app, seeded.SliceId), s => s.CurrentGeneration == 2 && s.ComputingSince is null);
        Assert.Null(taken.LastError);
    }

    [Fact] // AC-17
    public async Task AfterARestartTheListsAreServedFromTheTablesAtOnceAndNothingIsRecomputedEarly()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        (await app.SetUpAsync()).Dispose();
        var tenantId = await TenantScope.TenantIdAsync(app);
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before, nextRefreshAt: DateTimeOffset.UtcNow.AddHours(24));

        await using var restarted = app.Restart();
        using var admin = await AdminAsync(restarted);
        var served = await LookupApi.ReadValuesOkAsync(admin, seeded.LookupId, null);

        Assert.Equal(["old-a", "old-b"], served.Values.Select(v => v.Value));
        Assert.Equal(0, restarted.LookupComputer.Calls);
        Assert.Equal(1, (await LookupApi.SliceAsync(restarted, seeded.SliceId)).CurrentGeneration);
    }

    [Fact] // AC-17
    public async Task TheCacheIsWarmedInTheBackgroundFromTheTables()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        (await app.SetUpAsync()).Dispose();
        var tenantId = await TenantScope.TenantIdAsync(app);
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before, nextRefreshAt: DateTimeOffset.UtcNow.AddHours(24));

        await using var restarted = app.Restart();

        // A miss would run the factory, which here fails: the entry must already be there.
        var cache = restarted.Services.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();
        var entry = await LookupApi.UntilAsync(
            async () =>
            {
                try
                {
                    return await cache.GetOrCreateAsync<CachedLookup>(LookupCache.KeyFor(seeded.SliceId, 1), _ => throw new InvalidOperationException("not warmed yet"), cancellationToken: Ct);
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            },
            e => e is not null);

        Assert.Equal(["old-a", "old-b"], entry!.ByPosition.Select(v => v.Value.Value));
    }

    [Fact] // AC-20
    public async Task DeletingAKnowledgeBaseRemovesItsLookupsSlicesAndValues()
    {
        var (app, _, tenantId) = await StartAsync();
        await using var _ = app;
        using var admin = await AdminAsync(app);
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before);
        await LookupApi.ReadValuesOkAsync(admin, seeded.LookupId, null);

        using var deleted = await admin.DeleteAsync(new Uri($"/api/knowledge-bases/{seeded.KnowledgeBaseId}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeBasesDbContext>();
        Assert.False(await db.Lookups.IgnoreQueryFilters().AnyAsync(l => l.Id == seeded.LookupId, Ct));
        Assert.False(await db.LookupSlices.IgnoreQueryFilters().AnyAsync(s => s.Id == seeded.SliceId, Ct));
        Assert.False(await db.LookupValues.IgnoreQueryFilters().AnyAsync(v => v.SliceId == seeded.SliceId, Ct));
        using var gone = await admin.GetAsync(LookupApi.Values(seeded.LookupId), Ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task TheLookupsOfAKnowledgeBaseAreListedWithTheStateOfEachSlice()
    {
        var (app, _, tenantId) = await StartAsync();
        await using var _ = app;
        using var admin = await AdminAsync(app);
        var seeded = await LookupApi.SeedDueAsync(app, tenantId, Before);
        app.LookupComputer.Handler = (_, _) => throw new LookupComputationException("No index.");
        await Refresher(app).RunDueAsync(Ct);

        var lookups = await admin.GetFromJsonAsync<List<LookupBody>>(LookupApi.KnowledgeBaseLookups(seeded.KnowledgeBaseId), Ct);

        var lookup = Assert.Single(lookups!);
        Assert.Equal((seeded.LookupId, "Computed", "documents"), (lookup.Id, lookup.Kind, lookup.Unit));
        var slice = Assert.Single(lookup.Slices);
        Assert.Equal((LookupSliceKey.WholeKnowledgeBase.Hash, "Ready", true, 2, false, "No index.", false), (slice.Hash, slice.Status, slice.Stale, slice.ValueCount, slice.Truncated, slice.Error, slice.Running));
        Assert.NotNull(slice.ComputedAt);
    }

    [Fact] // AC-41
    public async Task AnotherTenantsLookupsCannotBeListedRefreshedOrReadByTheAdmin()
    {
        var (app, _, _) = await StartAsync();
        await using var _ = app;
        using var admin = await AdminAsync(app);
        var other = await LookupApi.SeedDueAsync(app, Guid.CreateVersion7(), [("secret-supplier", 7)]);

        using var list = await admin.GetAsync(LookupApi.KnowledgeBaseLookups(other.KnowledgeBaseId), Ct);
        using var refreshKnowledgeBase = await LookupApi.PostAsync(admin, LookupApi.RefreshKnowledgeBase(other.KnowledgeBaseId));
        using var refreshLookup = await LookupApi.PostAsync(admin, LookupApi.RefreshLookup(other.LookupId));
        using var values = await admin.GetAsync(LookupApi.Values(other.LookupId), Ct);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refreshKnowledgeBase.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refreshLookup.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, values.StatusCode);
    }

    [Fact] // AC-42
    public async Task NoValueOrQueryReachesTheLogsWhateverTheComputationDoes()
    {
        var (app, _, tenantId) = await StartAsync();
        await using var _ = app;
        const string Secret = "confidential-supplier-name";
        var ok = await LookupApi.SeedDueAsync(app, tenantId);
        var failing = await LookupApi.SeedDueAsync(app, tenantId);
        app.LookupComputer.Handler = (request, _) => request.Slice.Id == ok.SliceId
            ? Task.FromResult(FakeLookupComputer.Of((Secret, 3)))
            : throw new InvalidOperationException($"the store said {Secret}");

        await Refresher(app).RunDueAsync(Ct);
        await Refresher(app).RunDueAsync(Ct);

        Assert.Equal(1, (await LookupApi.SliceAsync(app, ok.SliceId)).CurrentGeneration);
        Assert.Equal("The computation failed unexpectedly.", (await LookupApi.SliceAsync(app, failing.SliceId)).LastError);
        Assert.DoesNotContain(app.Logs.Messages, message => message.Contains(Secret, StringComparison.Ordinal));
        Assert.Contains(app.Logs.Messages, message => message.Contains("failed", StringComparison.Ordinal));
    }

    [Fact]
    public void TheOptionsAreRejectedOutsideTheirRange()
    {
        Assert.Null(LookupOptions.Validate(new LookupOptions()));
        Assert.NotNull(LookupOptions.Validate(new LookupOptions { ComputationTimeoutMinutes = 0 }));
        Assert.NotNull(LookupOptions.Validate(new LookupOptions { MaxValuesPerLookup = 0 }));
        Assert.NotNull(LookupOptions.Validate(new LookupOptions { MaxValuesPerLookup = LookupOptions.MaxValuesPerLookupLimit + 1 }));
        Assert.Null(LookupOptions.Validate(new LookupOptions { MaxValuesPerLookup = LookupOptions.MaxValuesPerLookupLimit }));
    }

    private sealed record LookupBody(Guid Id, string Name, string Kind, string? Field, string? Unit, List<SliceBody> Slices);

    private sealed record SliceBody(string Hash, string Status, bool Stale, int ValueCount, bool Truncated, DateTimeOffset? ComputedAt, string? Error, bool Running);
}
