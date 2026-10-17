using System.Net;
using System.Net.Http.Json;
using Quorum.Integration.Tests.Fixtures;

namespace Quorum.Integration.Tests.Tools;

public sealed class ToolPermissionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact] // AC-10
    public async Task ManagingToolsNeedsThePermissionAndAnonymousCallersAreRejected()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        using var anonymous = app.CreateUiClient();
        var assistantId = await ToolsApi.DefaultAssistantIdAsync(admin);
        var tool = (await ToolsApi.ListToolsAsync(admin))[0];

        var requests = new List<Func<HttpClient, Task<HttpResponseMessage>>>
        {
            client => client.GetAsync(ToolsApi.Tools, Ct),
            client => client.PutAsJsonAsync(ToolsApi.Tool(tool.Id), new { state = "Enabled", policy = "Auto" }, Ct),
            client => client.GetAsync(ToolsApi.ToolSettings, Ct),
            client => client.PutAsJsonAsync(
                ToolsApi.ToolSettings,
                new { maxRounds = 8, totalTimeoutSeconds = 300, callTimeoutSeconds = 30, confirmationTimeoutSeconds = 300, maxResultCharacters = 20000, auditRetentionDays = 90 },
                Ct),
            client => client.GetAsync(ToolsApi.AssistantTools(assistantId), Ct),
            client => client.PutAsJsonAsync(ToolsApi.AssistantTools(assistantId), new { toolIds = Array.Empty<Guid>() }, Ct),
        };

        foreach (var send in requests)
        {
            using var asMember = await send(member);
            using var asAnonymous = await send(anonymous);
            using var asAdmin = await send(admin);

            Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
            Assert.True(asAdmin.IsSuccessStatusCode, $"The administrator was refused: {(int)asAdmin.StatusCode}");
        }
    }

    [Fact]
    public async Task AnySignedInUserCanListTheAssistants()
    {
        await using var app = await QuorumApp.StartOnNewDatabaseAsync(postgres);
        using var admin = await app.SetUpAsync();
        using var member = await app.CreateMemberClientAsync();
        using var anonymous = app.CreateUiClient();

        using var asMember = await member.GetAsync(ToolsApi.Assistants, Ct);
        using var asAnonymous = await anonymous.GetAsync(ToolsApi.Assistants, Ct);

        Assert.Equal(HttpStatusCode.OK, asMember.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);
    }
}
