using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client.Components;
using Quorum.Web.Client.Pages;
using SkillsPage = Quorum.Web.Client.Pages.Skills;

namespace Quorum.Integration.Tests.Web;

/// <summary>The Skills page (list, settings, delete refused while assigned) and the locked load_skill row of the tool picker (spec 017 D-4, AC-33).</summary>
public sealed class SkillsPageTests : ListPageTestContext
{
    private static readonly Guid Review = Guid.Parse("01a14140-0000-7208-890d-b78925ebb291");

    // A route this test does not answer: a problem, as the server would send one, so the page reads it without failing.
    private static HttpResponseMessage NotFound() =>
        FakeApiHandler.Json(new { title = "Not Found", status = 404 }, HttpStatusCode.NotFound);

    private static object SkillItem(Guid id, string slug, int agents = 0) =>
        new { id, slug, displayName = slug, description = $"Use {slug} when asked.", scope = "Tenant", agentCount = agents };

    [Fact]
    public void TheSkillsAreListedWithTheAgentsThatUseThemAndTheStyleIsShown()
    {
        Api.Respond = (method, path) => path switch
        {
            "/api/skills" when method == HttpMethod.Get => FakeApiHandler.Json(new[] { SkillItem(Review, "contract-review", agents: 2) }),
            "/api/skills/settings" => FakeApiHandler.Json(new { displayNameStyle = "Slug" }),
            _ => NotFound(),
        };

        var page = Visit<SkillsPage>("/skills");

        var row = page.Find("[data-grid='skills']").TextContent;
        Assert.Contains("contract-review", row, StringComparison.Ordinal);
        Assert.Contains("Use contract-review when asked.", row, StringComparison.Ordinal);
        Assert.Contains("2", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingASkillStillAssignedIsRefusedAndSaysWhy()
    {
        var refusal = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = FakeApiHandler.Json(new { title = "Conflict", status = 409, detail = "The skill is assigned to agents. Remove it from them first." }).Content,
        };
        Api.Respond = (method, path) =>
        {
            if (method == HttpMethod.Delete && path == $"/api/skills/{Review}")
            {
                return refusal;
            }

            return path switch
            {
                "/api/skills" => FakeApiHandler.Json(new[] { SkillItem(Review, "contract-review", agents: 1) }),
                "/api/skills/settings" => FakeApiHandler.Json(new { displayNameStyle = "Slug" }),
                _ => NotFound(),
            };
        };
        var page = Visit<SkillsPage>("/skills");

        page.Find("[aria-label='Delete skill']").Click();
        await page.WaitForStateAsync(() => page.FindAll("[data-problem]").Count > 0);

        Assert.Contains("assigned to agents", page.Find("[data-problem]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoadSkillRowIsLockedAndSaysWhetherTheAgentHasSkills()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        var none = Render<AgentToolPicker>(parameters => parameters
            .Add(picker => picker.Pool, [])
            .Add(picker => picker.Active, new HashSet<Guid>())
            .Add(picker => picker.Manifests, new Dictionary<Guid, string>())
            .Add(picker => picker.HasSkills, false));

        var locked = none.Find("[data-locked-tool='load_skill']");
        Assert.Contains("Offered once a skill is assigned", locked.TextContent, StringComparison.Ordinal);
        Assert.Equal("false", locked.GetAttribute("data-selected"));

        var withSkills = Render<AgentToolPicker>(parameters => parameters
            .Add(picker => picker.Pool, [])
            .Add(picker => picker.Active, new HashSet<Guid>())
            .Add(picker => picker.Manifests, new Dictionary<Guid, string>())
            .Add(picker => picker.HasSkills, true));

        var lockedWithSkills = withSkills.Find("[data-locked-tool='load_skill']");
        Assert.Contains("Offered because this agent has skills", lockedWithSkills.TextContent, StringComparison.Ordinal);
        Assert.Equal("true", lockedWithSkills.GetAttribute("data-selected"));
    }
}
