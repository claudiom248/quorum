using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Setup;

public sealed class OnboardingChecklistTests(PostgresFixture postgres)
{
    private static readonly Uri Checklist = new("/api/onboarding/checklist", UriKind.Relative);

    private static readonly Uri Dismissed = new("/api/onboarding/checklist/dismissed", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-13
    public async Task AfterSetupOnlyTheModelStepIsDone()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        var checklist = await GetAsync(admin);

        Assert.False(checklist.Dismissed);
        Assert.Equal(["model", "prompt", "tools", "knowledge"], checklist.Items.Select(i => i.Key));
        Assert.Equal(["model"], checklist.Items.Where(i => i.Done).Select(i => i.Key));
    }

    [Theory] // AC-13
    [InlineData("prompt")]
    [InlineData("tools")]
    [InlineData("knowledge")]
    public async Task MarkingAStepDoneIsIdempotent(string key)
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var first = await admin.PutAsync(new Uri($"/api/onboarding/checklist/{key}", UriKind.Relative), content: null, Ct);
        using var second = await admin.PutAsync(new Uri($"/api/onboarding/checklist/{key}", UriKind.Relative), content: null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        var checklist = await GetAsync(admin);
        Assert.Equal(["model", key], checklist.Items.Where(i => i.Done).Select(i => i.Key));
    }

    [Fact] // AC-13
    public async Task AnUnknownStepIsNotFound()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.PutAsync(new Uri("/api/onboarding/checklist/nope", UriKind.Relative), content: null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact] // AC-13
    public async Task DismissingHidesTheChecklistAndKeepsTheSteps()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();

        using var response = await admin.PutAsync(Dismissed, content: null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var checklist = await GetAsync(admin);
        Assert.True(checklist.Dismissed);
        Assert.Equal(["model"], checklist.Items.Where(i => i.Done).Select(i => i.Key));
    }

    [Fact] // AC-13
    public async Task NonAdminAndAnonymousCannotUseTheChecklist()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        using var anonymous = app.CreateUiClient();

        using var memberRead = await member.GetAsync(Checklist, Ct);
        using var memberWrite = await member.PutAsync(Dismissed, content: null, Ct);
        using var anonymousRead = await anonymous.GetAsync(Checklist, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, memberRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberWrite.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
    }

    private static async Task<ChecklistBody> GetAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<ChecklistBody>(Checklist, Ct))!;

    private sealed record ChecklistBody(ChecklistItem[] Items, bool Dismissed);

    private sealed record ChecklistItem(string Key, bool Done);
}
