using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Quorum.Host.Infrastructure.Telemetry;
using Quorum.Integration.Tests.Chat;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Telemetry;

public sealed class TelemetryFoundationTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Uri Agents = new("/api/agents", UriKind.Relative);

    [Fact] // AC-17
    public async Task AnAdministrativeRequestHasNoSpanWhenAdminRequestsAreOff()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true, adminRequests: false);
        using var client = await app.SetUpAsync(toolsActive: false);
        app.Spans.Clear();

        using var list = await client.GetAsync(Agents, Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        // The answer path is always traced: its span ends after the admin request's, so it proves the admin one ended too.
        var conversation = await ChatApi.CreateConversationAsync(client);
        await ChatApi.SendAsync(client, conversation, "Hello");
        await WaitForSpanAsync(app, s => s.Kind == ActivityKind.Server && s.DisplayName.Contains("messages", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(app.Spans.Snapshot(), s => ServerSpanFor(s, "agents"));
    }

    [Fact] // AC-17
    public async Task AnAdministrativeRequestHasItsOwnSpanWhenAdminRequestsAreOn()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true, adminRequests: true);
        using var client = await app.SetUpAsync(toolsActive: false);
        app.Spans.Clear();

        using var list = await client.GetAsync(Agents, Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var span = await WaitForSpanAsync(app, s => ServerSpanFor(s, "agents"));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
    }

    [Fact] // AC-19: an unreachable collector never fails an answer and is reported once.
    public async Task AnUnreachableCollectorDoesNotFailAnAnswerAndIsReportedOnce()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres, telemetry: true);
        using var client = await app.SetUpAsync(toolsActive: false);
        var conversation = await ChatApi.CreateConversationAsync(client);

        var events = await ChatApi.SendAsync(client, conversation, "Hello");
        Assert.DoesNotContain(events, e => e.Type == "error");

        // The answer's span has ended (so it is queued for the batch exporter) once it is in the capture.
        await WaitForSpanAsync(app, s => s.Kind == ActivityKind.Server && s.DisplayName.Contains("messages", StringComparison.OrdinalIgnoreCase));
        // The batch exporter sends on its own schedule, off the request path: wait for its first attempt to fail.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!app.Logs.Messages.Any(m => m.Contains("Telemetry export failed", StringComparison.Ordinal)) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, Ct);
        }

        Assert.Single(app.Logs.Messages, m => m.Contains("Telemetry export failed", StringComparison.Ordinal));
    }

    [Fact]
    public void OtlpHeadersAreParsedFromTheStandardFormat()
    {
        var headers = TelemetrySetup.ParseHeaders("api-key=abc%20def, x-tenant = acme ,broken");

        Assert.Equal("abc def", headers["api-key"]);
        Assert.Equal("acme", headers["x-tenant"]);
        Assert.False(headers.ContainsKey("broken"));
    }

    private static bool ServerSpanFor(Activity span, string segment) =>
        span.Kind == ActivityKind.Server && span.DisplayName.Contains(segment, StringComparison.OrdinalIgnoreCase);

    private static async Task<Activity> WaitForSpanAsync(QuorumApp app, Func<Activity, bool> match)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            var found = app.Spans.Snapshot().FirstOrDefault(match);
            if (found is not null)
            {
                return found;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "No span matched within 10 seconds.");
            await Task.Delay(20, Ct);
        }
    }
}
