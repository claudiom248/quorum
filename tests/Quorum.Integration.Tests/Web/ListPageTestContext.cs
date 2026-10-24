using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>What the tests of the list pages share: the fake API, the Mud services and the providers a drawer and a dialog need.</summary>
public abstract class ListPageTestContext : BunitContext
{
    protected ListPageTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(new QuorumApi(Api.CreateClient()));
    }

    protected FakeApiHandler Api { get; } = new();

    protected IRenderedComponent<MudDialogProvider> Dialogs { get; private set; } = null!;

    /// <summary>Opens the page at an address, as a link or a reload would, and waits until it stops loading.</summary>
    protected IRenderedComponent<TPage> Visit<TPage>(string address, Action<ComponentParameterCollectionBuilder<TPage>>? parameters = null)
        where TPage : IComponent
    {
        Render<MudPopoverProvider>();
        Dialogs = Render<MudDialogProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo(address);
        var page = Render<TPage>(parameters ?? (_ => { }));
        page.WaitForAssertion(() => Assert.DoesNotContain("mud-progress-linear", page.Find(".list-page, .settings, body").InnerHtml, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        return page;
    }

    protected static void ClickButton(IRenderedComponent<MudDialogProvider> dialogs, string text) =>
        dialogs.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    protected static void ClickButton<TComponent>(IRenderedComponent<TComponent> page, string text)
        where TComponent : IComponent =>
        page.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal)).Click();

    protected static JsonElement Body(FakeApiHandler api, HttpMethod method, string path) =>
        JsonDocument.Parse(api.LastBody(method, path)!).RootElement.Clone();

    protected static object NoScopeIds(string kind) => new { kind, tenantIds = Array.Empty<Guid>(), assistantIds = Array.Empty<Guid>() };

    protected static HttpResponseMessage Page(params object[] items) => FakeApiHandler.Json(new { items, cursor = (string?)null });

    protected static HttpResponseMessage Page(string? cursor, params object[] items) => FakeApiHandler.Json(new { items, cursor });
}
