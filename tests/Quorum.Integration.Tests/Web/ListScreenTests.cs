using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>What a list does when the server's answer cannot be read (spec 010).</summary>
public sealed class ListScreenTests : BunitContext
{
    [Fact]
    public async Task AnAnswerThatCannotBeReadEndsTheWaitWithAProblem()
    {
        var screen = new ListScreen<string>(
            Services.GetRequiredService<NavigationManager>(),
            "/tools/mcp",
            (_, _) => throw new JsonException("truncated"),
            () => { });

        await screen.ReloadAsync();

        Assert.False(screen.Loading);
        Assert.True(screen.Loaded);
        Assert.NotNull(screen.Problem);
    }
}
