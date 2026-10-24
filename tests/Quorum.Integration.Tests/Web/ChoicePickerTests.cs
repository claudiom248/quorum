using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Quorum.Web.Client.Components;

namespace Quorum.Integration.Tests.Web;

/// <summary>Choosing among many: the picker of lookups and knowledge bases in the tool forms (spec 010).</summary>
public sealed class ChoicePickerTests : BunitContext
{
    private sealed record Item(int Number, string Source)
    {
        public string Name => $"list{Number:00}";
    }

    private static readonly IReadOnlyList<Item> Many = [.. Enumerable.Range(1, 35).Select(i => new Item(i, i % 2 == 0 ? "contracts" : "company"))];

    public ChoicePickerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Render<MudBlazor.MudPopoverProvider>();
    }

    private IRenderedComponent<ChoicePicker<Item>> RenderPicker(HashSet<int> selected, List<(int Number, bool On)> toggles) =>
        Render<ChoicePicker<Item>>(p => p
            .Add(c => c.Items, Many)
            .Add(c => c.IsSelected, i => selected.Contains(i.Number))
            .Add(c => c.Label, i => $"{i.Name} · {i.Source}")
            .Add(c => c.Group, i => i.Source)
            .Add(c => c.OnToggle, t => toggles.Add((t.Item.Number, t.On))));

    [Fact]
    public void OnlyTheFirstFewChoicesShowAndShowMoreAddsTheNext()
    {
        var picker = RenderPicker([], []);

        Assert.Equal(10, picker.FindAll("[data-picker-available] .mud-checkbox").Count);

        picker.FindAll("button").First(b => b.TextContent.Contains("Show more", StringComparison.Ordinal)).Click();

        Assert.Equal(20, picker.FindAll("[data-picker-available] .mud-checkbox").Count);
    }

    [Fact]
    public void SearchingNarrowsTheListToWhatMatches()
    {
        var picker = RenderPicker([], []);

        picker.Find(".picker-search input").Input("list3");

        var labels = picker.FindAll("[data-picker-available] .mud-checkbox").Select(c => c.TextContent.Trim()).ToArray();
        Assert.Equal(6, labels.Length);
        Assert.All(labels, l => Assert.StartsWith("list3", l, StringComparison.Ordinal));
    }

    [Fact]
    public void WhatIsChosenStaysOnTopAndOutOfTheAvailableChoices()
    {
        var picker = RenderPicker([34], []);

        Assert.Single(picker.FindAll("[data-picker-selected] .mud-checkbox"));
        Assert.Contains("list34", picker.Find("[data-picker-selected]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(picker.FindAll("[data-picker-available] .mud-checkbox"), c => c.TextContent.Contains("list34", StringComparison.Ordinal));
    }

    [Fact]
    public void TickingAChoiceTellsThePageAndUntickingTheChosenOneToo()
    {
        var toggles = new List<(int Number, bool On)>();
        var picker = RenderPicker([7], toggles);

        picker.FindAll("[data-picker-available] .mud-checkbox input")[0].Change(true);
        picker.Find("[data-picker-selected] .mud-checkbox input").Change(false);

        Assert.Equal([(1, true), (7, false)], toggles);
    }

    [Fact]
    public void AFewChoicesNeedNoSearch()
    {
        var picker = Render<ChoicePicker<Item>>(p => p
            .Add(c => c.Items, Many.Take(4).ToList())
            .Add(c => c.IsSelected, _ => false)
            .Add(c => c.Label, i => i.Name));

        Assert.Empty(picker.FindAll(".picker-search"));
        Assert.Equal(4, picker.FindAll("[data-picker-available] .mud-checkbox").Count);
    }
}
