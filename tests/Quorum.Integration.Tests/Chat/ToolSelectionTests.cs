using Quorum.Modules.Agents.Chat;
using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Integration.Tests.Chat;

/// <summary>The rule that turns the root agent's tools into the ones a turn offers (spec 018 D-4, D-5, D-7, D-17).</summary>
public sealed class ToolSelectionTests
{
    [Fact] // AC-6, AC-7 (runtime): a consumer turns off optional tools only
    public void AConsumerTurnsOffOptionalToolsAndKeepsRequiredOnes()
    {
        var tools = new[] { Tool("calculator", ToolMode.Optional), Tool("lookup", ToolMode.Required) };

        var effective = ToolSelection.Effective(tools, ["calculator", "lookup"], debug: false);

        Assert.Equal(["lookup"], effective.Select(t => t.ExposedName));
    }

    [Fact] // AC-11 (rule): a debug conversation turns off any tool, required ones included
    public void ADebugConversationTurnsOffRequiredToolsToo()
    {
        var tools = new[] { Tool("calculator", ToolMode.Required), Tool("lookup", ToolMode.Optional) };

        var effective = ToolSelection.Effective(tools, ["calculator"], debug: true);

        Assert.Equal(["lookup"], effective.Select(t => t.ExposedName));
    }

    [Fact] // D-17: a name that is not attached is ignored at run time
    public void UnknownNamesAreIgnored()
    {
        var tools = new[] { Tool("calculator", ToolMode.Optional) };

        var effective = ToolSelection.Effective(tools, ["gone"], debug: false);

        Assert.Equal(["calculator"], effective.Select(t => t.ExposedName));
    }

    [Fact]
    public void NothingDisabledKeepsEverything()
    {
        var tools = new[] { Tool("calculator", ToolMode.Optional) };

        Assert.Same(tools, ToolSelection.Effective(tools, [], debug: false));
    }

    private static ActiveTool Tool(string name, ToolMode mode) =>
        new(Guid.NewGuid(), ToolOrigin.Builtin, name, name, null, "Tool.", "{}", Mode: mode);
}
