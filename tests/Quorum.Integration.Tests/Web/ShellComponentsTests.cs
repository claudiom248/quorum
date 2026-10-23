using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;
using Quorum.Web.Client.Components;

namespace Quorum.Integration.Tests.Web;

/// <summary>The shared pieces of the lists: filter bar, page scaffold, drawer, scope picker (spec 010, D-3 to D-9; AC-3, AC-4, AC-10, AC-11, AC-16).</summary>
public sealed class ShellComponentsTests : BunitContext
{
    private static readonly Guid TenantA = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4001");
    private static readonly Guid TenantB = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4002");
    private static readonly Guid AssistantId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b4011");

    private static readonly TenantSummary[] TwoTenants = [new(TenantA, "Acme", Current: true), new(TenantB, "Globex")];
    private static readonly AssistantSummary[] Assistants = [new(AssistantId, "Support")];

    public ShellComponentsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
    }

    private IRenderedComponent<MudPopoverProvider> Popovers() => Render<MudPopoverProvider>();

    private IRenderedComponent<MudDialogProvider> Dialogs() => Render<MudDialogProvider>();

    [Fact]
    public void TheTenantFilterShowsOnlyWhenTheInstallationHasMoreThanOneTenant()
    {
        Popovers();
        var single = Render<FilterBar>(p => p.Add(c => c.Query, ListQuery.Empty).Add(c => c.Tenants, [new TenantSummary(TenantA, "Acme", true)]));
        var several = Render<FilterBar>(p => p.Add(c => c.Query, ListQuery.Empty).Add(c => c.Tenants, TwoTenants));

        Assert.DoesNotContain(single.FindAll("label"), l => l.TextContent == "Tenant");
        Assert.Contains(several.FindAll("label"), l => l.TextContent == "Tenant");
    }

    [Fact]
    public void TheAgentFilterNeedsAnAssistantAndTheTypeFilterAListOfTypes()
    {
        Popovers();
        var bare = Render<FilterBar>(p => p.Add(c => c.Query, ListQuery.Empty));
        var full = Render<FilterBar>(p => p
            .Add(c => c.Query, ListQuery.Empty)
            .Add(c => c.Assistants, Assistants)
            .Add(c => c.TypeLabel, "Tool type")
            .Add(c => c.TypeOptions, [new FilterOption("mcp", "MCP"), new FilterOption("rag", "RAG")]));

        Assert.DoesNotContain(bare.FindAll("label"), l => l.TextContent is "Agent" or "Tool type");
        Assert.Contains(full.FindAll("label"), l => l.TextContent == "Agent");
        Assert.Contains(full.FindAll("label"), l => l.TextContent == "Tool type");
    }

    [Fact]
    public void ActiveFiltersShowAsChipsThatRemoveThemselves()
    {
        Popovers();
        ListQuery? changed = null;
        var query = new ListQuery { Scope = ["assistant"], Assistant = [AssistantId], Q = "search" };
        var bar = Render<FilterBar>(p => p
            .Add(c => c.Query, query)
            .Add(c => c.Assistants, Assistants)
            .Add(c => c.QueryChanged, q => changed = q));

        Assert.Equal("Scope: Assistant", bar.Find("[data-filter=scope]").TextContent.Trim());
        Assert.Equal("Agent: Support", bar.Find("[data-filter=assistant]").TextContent.Trim());

        bar.Find("[data-filter=scope] .mud-chip-close-button").Click();

        Assert.NotNull(changed);
        Assert.Empty(changed.Scope);
        Assert.Equal([AssistantId], changed.Assistant);
        Assert.Equal("search", changed.Q);
    }

    [Fact]
    public void ClearFiltersDropsEveryFilterAndTheSearch()
    {
        Popovers();
        ListQuery? changed = null;
        var bar = Render<FilterBar>(p => p
            .Add(c => c.Query, new ListQuery { Scope = ["global"], Status = ["enabled"], Q = "x" })
            .Add(c => c.QueryChanged, q => changed = q));

        bar.FindAll("button").First(b => b.TextContent.Contains("Clear filters", StringComparison.Ordinal)).Click();

        Assert.False(changed!.HasFilters);
    }

    [Fact]
    public void TypingInTheSearchAppliesItAfterThePause()
    {
        Popovers();
        ListQuery? changed = null;
        var bar = Render<FilterBar>(p => p
            .Add(c => c.Query, ListQuery.Empty)
            .Add(c => c.SearchDebounce, 0)
            .Add(c => c.QueryChanged, q => changed = q));

        bar.Find("input").Input("github");

        Assert.Equal("github", changed?.Q);
    }

    [Fact]
    public void AnEmptyListWithoutFiltersInvitesToAddTheFirstItem()
    {
        var added = false;
        var page = Render<ListPage>(p => p
            .Add(c => c.Title, "MCP servers")
            .Add(c => c.AddLabel, "Add server")
            .Add(c => c.IsEmpty, true)
            .Add(c => c.EmptyTitle, "Register your first server")
            .Add(c => c.OnAdd, () => added = true));

        var empty = page.Find("[data-empty=none]");
        Assert.Contains("Register your first server", empty.TextContent, StringComparison.Ordinal);
        empty.QuerySelector("button")!.Click();
        Assert.True(added);
    }

    [Fact]
    public void AnEmptyListUnderFiltersSaysNothingMatchesAndClearsThem()
    {
        var cleared = false;
        var page = Render<ListPage>(p => p
            .Add(c => c.Title, "MCP servers")
            .Add(c => c.AddLabel, "Add server")
            .Add(c => c.IsEmpty, true)
            .Add(c => c.HasFilters, true)
            .Add(c => c.OnClearFilters, () => cleared = true));

        var empty = page.Find("[data-empty=filtered]");
        Assert.Contains("Nothing matches", empty.TextContent, StringComparison.Ordinal);
        empty.QuerySelector("button")!.Click();
        Assert.True(cleared);
        Assert.Empty(page.FindAll("[data-empty=none]"));
    }

    [Fact]
    public void TheGridStaysMountedWhenTheListIsEmpty()
    {
        var page = Render<ListPage>(p => p
            .Add(c => c.Title, "Lookups")
            .Add(c => c.IsEmpty, true)
            .Add(c => c.ChildContent, b => b.AddMarkupContent(0, "<table id=grid></table>")));

        Assert.NotNull(page.Find("#grid"));
        Assert.True(page.Find("#grid").ParentElement!.HasAttribute("hidden"));
    }

    [Fact]
    public void AListThatCannotLoadShowsTheProblemInPlaceOfTheListAndRetries()
    {
        var retried = false;
        var page = Render<ListPage>(p => p
            .Add(c => c.Title, "Lookups")
            .Add(c => c.Problem, new ApiProblem(500, null, "The server is down.", new Dictionary<string, string[]>()))
            .Add(c => c.OnRetry, () => retried = true)
            .Add(c => c.ChildContent, b => b.AddMarkupContent(0, "<table id=grid></table>")));

        Assert.Contains("The server is down.", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("#grid"));
        page.FindAll("button").First(b => b.TextContent.Contains("Try again", StringComparison.Ordinal)).Click();
        Assert.True(retried);
    }

    [Fact]
    public void TheBadgeNamesTheScopeAndSaysWhenTheItemIsShared()
    {
        var badge = Render<ScopeBadge>(p => p
            .Add(c => c.Scope, new ToolScope(ScopeKinds.Tenant, [TenantB], []))
            .Add(c => c.Shared, true));

        Assert.Contains("Tenant +1", badge.Markup, StringComparison.Ordinal);
        Assert.Contains("Shared with you", badge.Markup, StringComparison.Ordinal);
        Assert.Equal("tenant", badge.Find("[data-scope]").GetAttribute("data-scope"));
    }

    [Fact]
    public void TheScopePickerListsOtherTenantsByNameAndTypesIdsOnlyWhenItHasNoList()
    {
        Popovers();
        var form = new ScopeForm { Kind = ScopeKinds.Tenant };

        var typed = Render<ScopePicker>(p => p.Add(c => c.Form, form));
        var named = Render<ScopePicker>(p => p.Add(c => c.Form, form).Add(c => c.Tenants, TwoTenants));
        var alone = Render<ScopePicker>(p => p.Add(c => c.Form, form).Add(c => c.Tenants, [new TenantSummary(TenantA, "Acme", true)]));

        Assert.Contains(typed.FindAll("label"), l => l.TextContent.Contains("Other tenants (ids", StringComparison.Ordinal));
        Assert.Contains(named.FindAll("label"), l => l.TextContent == "Other tenants");
        Assert.DoesNotContain(alone.FindAll("label"), l => l.TextContent.Contains("Other tenants", StringComparison.Ordinal));
    }

    [Fact]
    public void ClosingACleanDrawerClosesItAtOnce()
    {
        Popovers();
        Dialogs();
        var closed = 0;
        var drawer = RenderDrawer(dirty: false, () => closed++);

        drawer.Find(".list-drawer-body").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Equal(1, closed);
    }

    [Fact]
    public void ClosingAModifiedDrawerAsksFirstAndKeepsItOpenWhenTheAdminKeepsEditing()
    {
        Popovers();
        var dialogs = Dialogs();
        var closed = 0;
        var drawer = RenderDrawer(dirty: true, () => closed++);

        drawer.Find(".list-drawer-body").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        dialogs.WaitForAssertion(() => Assert.Contains("Discard changes?", dialogs.Markup, StringComparison.Ordinal));
        Assert.Equal(0, closed);

        dialogs.FindAll("button").First(b => b.TextContent.Contains("Keep editing", StringComparison.Ordinal)).Click();

        Assert.Equal(0, closed);
    }

    [Fact]
    public void ClosingAModifiedDrawerClosesItOnceTheAdminAgreesToDiscard()
    {
        Popovers();
        var dialogs = Dialogs();
        var closed = 0;
        var drawer = RenderDrawer(dirty: true, () => closed++);

        drawer.Find("button[aria-label=Close]").Click();
        dialogs.WaitForAssertion(() => Assert.Contains("Discard changes?", dialogs.Markup, StringComparison.Ordinal));
        dialogs.FindAll("button").First(b => b.TextContent.Contains("Discard", StringComparison.Ordinal) && !b.TextContent.Contains("Discard changes", StringComparison.Ordinal)).Click();

        dialogs.WaitForAssertion(() => Assert.Equal(1, closed));
    }

    [Fact]
    public void LeavingThePageWithAModifiedDrawerOpenAsksBeforeNavigating()
    {
        Popovers();
        var dialogs = Dialogs();
        RenderDrawer(dirty: true, () => { });
        var navigation = Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo("settings");

        dialogs.WaitForAssertion(() => Assert.Contains("Discard changes?", dialogs.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("settings", navigation.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void ACleanDrawerDoesNotStopThePageFromChanging()
    {
        Popovers();
        Dialogs();
        RenderDrawer(dirty: false, () => { });
        var navigation = Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo("settings");

        Assert.EndsWith("/settings", navigation.Uri, StringComparison.Ordinal);
    }

    private IRenderedComponent<ListDrawer> RenderDrawer(bool dirty, Action onClose) =>
        Render<ListDrawer>(p => p
            .Add(c => c.Open, true)
            .Add(c => c.Title, "Edit server")
            .Add(c => c.Dirty, dirty)
            .Add(c => c.OnClose, onClose)
            .Add(c => c.ChildContent, b => b.AddMarkupContent(0, "<p>form</p>")));
}
