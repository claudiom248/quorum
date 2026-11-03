using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Setup;

public sealed class OnboardingChecklistTests(SharedQuorumApp host) : IClassFixture<SharedQuorumApp>, IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(host.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly Uri Checklist = new("/api/onboarding/checklist", UriKind.Relative);

    private static readonly Uri Dismissed = new("/api/onboarding/checklist/dismissed", UriKind.Relative);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-13
    public async Task AfterSetupOnlyTheModelStepIsDone()
    {
        var admin = host.Admin;

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
        var admin = host.Admin;

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
        var admin = host.Admin;

        using var response = await admin.PutAsync(new Uri("/api/onboarding/checklist/nope", UriKind.Relative), content: null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact] // AC-13
    public async Task DismissingHidesTheChecklistAndKeepsTheSteps()
    {
        var admin = host.Admin;

        using var response = await admin.PutAsync(Dismissed, content: null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var checklist = await GetAsync(admin);
        Assert.True(checklist.Dismissed);
        Assert.Equal(["model"], checklist.Items.Where(i => i.Done).Select(i => i.Key));
    }

    [Fact] // AC-13
    public async Task NonAdminAndAnonymousCannotUseTheChecklist()
    {
        var app = host.App;
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
