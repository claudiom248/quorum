using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client.Api;
using Quorum.Web.Client.Components;

namespace Quorum.Integration.Tests.Web;

/// <summary>The tools of an agent: grouped as the menu groups them, searched, switched on and off, and offered full or lean.</summary>
public sealed class AgentToolPickerTests : BunitContext
{
    private static readonly Guid Calculator = Guid.Parse("01a14130-0000-7208-890d-b78925ebb291");
    private static readonly Guid Clock = Guid.Parse("01a14131-0000-7208-890d-b78925ebb291");
    private static readonly Guid Lookup = Guid.Parse("01a14132-0000-7208-890d-b78925ebb291");

    private readonly HashSet<Guid> _active = [Calculator];
    private readonly Dictionary<Guid, string> _manifests = new() { [Lookup] = "Full" };
    private int _changes;

    public AgentToolPickerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
    }

    private static PoolTool Tool(string name, Guid? id = null, string? manifest = null) => new(id ?? Guid.NewGuid(), name, false, manifest);

    private IRenderedComponent<AgentToolPicker> Draw(params PoolTool[] pool) =>
        Render<AgentToolPicker>(parameters => parameters
            .Add(picker => picker.Pool, pool)
            .Add(picker => picker.Active, _active)
            .Add(picker => picker.Manifests, _manifests)
            .Add(picker => picker.Changed, () => _changes++));

    [Fact]
    public void TheToolsMadeInQuorumAreGroupedAsTheMenuDoesAndEachServerKeepsItsOwnGroup()
    {
        var page = Draw(
            Tool("files__read"),
            Tool("rag__search_contracts"),
            Tool("lookup__contracts", Lookup, "Full"),
            Tool("web_fetch__fetch"),
            Tool("web_search__web"),
            Tool("builtin__calculator", Calculator));

        Assert.Equal(["builtin", "search tools", "web tools", "files"], page.FindAll("[data-source]").Select(s => s.GetAttribute("data-source")).ToArray());
        var search = page.Find("[data-source='search tools']").ParentElement!;
        Assert.Equal(["lookup · contracts", "rag · search_contracts"], search.QuerySelectorAll(".mud-checkbox").Select(c => c.TextContent.Trim()).Order().ToArray());
    }

    [Fact]
    public void ToolsAreListedWithFriendlyNamesAndTheActiveOnesAreChecked()
    {
        var page = Draw(Tool("builtin__calculator", Calculator), Tool("builtin__current_datetime", Clock));

        Assert.Equal(["calculator", "current_datetime"], page.FindAll(".mud-checkbox").Select(c => c.TextContent.Trim()).ToArray());
        var boxes = page.FindAll(".mud-checkbox input");
        Assert.True(boxes[0].HasAttribute("checked"));
        Assert.False(boxes[1].HasAttribute("checked"));
    }

    [Fact]
    public void SearchingNarrowsTheTools()
    {
        var page = Draw(Tool("builtin__calculator", Calculator), Tool("builtin__current_datetime", Clock));

        page.Find("input[type=text]").Input("calc");

        page.WaitForAssertion(() => Assert.Equal(["calculator"], page.FindAll(".mud-checkbox").Select(c => c.TextContent.Trim()).ToArray()));
    }

    [Fact]
    public void SwitchingAToolChangesTheSetAndSaysSo()
    {
        var page = Draw(Tool("builtin__calculator", Calculator), Tool("builtin__current_datetime", Clock));

        page.FindAll(".mud-checkbox input")[0].Change(false);
        page.FindAll(".mud-checkbox input")[1].Change(true);

        Assert.Equal([Clock], _active);
        Assert.Equal(2, _changes);
    }

    [Fact]
    public void ALookupToolIsOfferedLeanOnlyWhileItIsOn()
    {
        var page = Draw(Tool("lookup__contracts", Lookup, "Full"));

        Assert.True(page.Find(".mud-switch input").HasAttribute("disabled"));
        page.Find(".mud-checkbox input").Change(true);
        Assert.False(page.Find(".mud-switch input").HasAttribute("disabled"));
        page.Find(".mud-switch input").Change(true);

        Assert.Equal("Lean", _manifests[Lookup]);
    }
}
