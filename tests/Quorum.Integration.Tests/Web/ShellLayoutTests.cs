using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;
using Quorum.Web.Client.Layout;

namespace Quorum.Integration.Tests.Web;

/// <summary>The sidebar shell and who sees what in it (spec 010, D-1, D-2, D-19; AC-1, AC-2).</summary>
public sealed class ShellLayoutTests : BunitContext
{
    private static readonly Guid ServerId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b5001");

    private readonly FakeApiHandler _api = new();
    private string _role = "Admin";
    private int _pending = 3;

    public ShellLayoutTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        var client = new QuorumApi(_api.CreateClient());
        Services.AddSingleton(client);
        Services.AddSingleton(new Session(client));
        Services.AddScoped<ThemeState>();

        _api.Respond = (method, path) =>
        {
            if (method != HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return path switch
            {
                "/api/setup/status" => FakeApiHandler.Json(new { isSetupMode = false }),
                "/api/auth/me" => FakeApiHandler.Json(new { email = "ada@example.com", displayName = "Ada", role = _role }),
                "/api/tools/servers" => FakeApiHandler.Json(new[]
                {
                    new
                    {
                        id = ServerId, name = "github", url = "http://mcp/", hasHeaders = false, status = "Available", lastError = (string?)null,
                        lastRefreshedAt = (DateTimeOffset?)null, scope = new { kind = "Tenant", tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() },
                        toolCount = 12, pendingReviewCount = _pending, shared = false,
                    },
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        };
    }

    private IRenderedComponent<MainLayout> RenderShell(string path = "")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(path);
        Render<MudPopoverProvider>();
        var shell = Render<MainLayout>(p => p.Add(c => c.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=page>the page</p>"))));
        shell.WaitForAssertion(() => Assert.NotEmpty(shell.FindAll("nav.nav-menu")));
        return shell;
    }

    private static IReadOnlyList<string> Links(IRenderedComponent<MainLayout> shell) =>
        [.. shell.FindAll("nav.nav-menu a.mud-nav-link").Select(a => a.TextContent.Trim())];

    [Fact]
    public void AnAdminSeesEveryGroupOfTheSidebarAndTheTopBarHasNoNavigation()
    {
        var shell = RenderShell();

        var links = Links(shell);
        foreach (var expected in new[] { "Chat", "MCP servers", "RAG tools", "Lookup tools", "Built-in", "Knowledge bases", "Lookups", "Dictionary", "Settings", "Editor lab" })
        {
            Assert.Contains(links, l => l.StartsWith(expected, StringComparison.Ordinal));
        }

        Assert.Empty(shell.FindAll(".app-bar a"));
        Assert.Contains("Ada", shell.Find(".app-bar").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServerEntryShowsHowManyToolsWaitForReview()
    {
        var shell = RenderShell();

        var mcp = shell.FindAll("nav.nav-menu a.mud-nav-link").First(a => a.TextContent.Contains("MCP servers", StringComparison.Ordinal));
        shell.WaitForAssertion(() => Assert.Contains("3", mcp.TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherRoleDoesNotSeeTheAdminGroups()
    {
        _role = "Member";

        var shell = RenderShell();

        var links = Links(shell);
        Assert.DoesNotContain(links, l => l.StartsWith("MCP servers", StringComparison.Ordinal) || l.StartsWith("Knowledge bases", StringComparison.Ordinal));
        Assert.Contains(links, l => l.StartsWith("Settings", StringComparison.Ordinal));
        Assert.Contains(links, l => l.StartsWith("Chat", StringComparison.Ordinal));
        Assert.Equal(0, _api.Count(HttpMethod.Get, "/api/tools/servers"));
    }

    [Fact]
    public void AnAdminPageOpenedByAddressSaysThereIsNoAccessToAnyoneElse()
    {
        _role = "Member";

        var shell = RenderShell("tools/mcp");

        Assert.Contains("You don't have access", shell.Markup, StringComparison.Ordinal);
        Assert.Empty(shell.FindAll("#page"));
    }

    [Fact]
    public void AnAdminOpensTheSamePage()
    {
        var shell = RenderShell("tools/mcp");

        Assert.NotNull(shell.Find("#page"));
        Assert.DoesNotContain("You don't have access", shell.Markup, StringComparison.Ordinal);
    }
}
