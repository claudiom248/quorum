using System.Text.Json;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Integration.Tests.Tools;

public sealed class ToolDefinitionTests
{
    private const string Schema = """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""";

    [Fact]
    public void RendersTheToolsInTheOpenAiFunctionFormat()
    {
        var json = ToolDefinitionRenderer.Render([new ToolDefinition("builtin__calculator", "Adds numbers.", Schema)]);

        using var document = JsonDocument.Parse(json);
        var tool = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        var function = tool.GetProperty("function");
        Assert.Equal("builtin__calculator", function.GetProperty("name").GetString());
        Assert.Equal("Adds numbers.", function.GetProperty("description").GetString());
        Assert.Equal("object", function.GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public void RendersInAStableOrderWhateverTheInputOrder()
    {
        var a = new ToolDefinition("builtin__a", "A.", Schema);
        var b = new ToolDefinition("builtin__b", "B.", Schema);

        Assert.Equal(ToolDefinitionRenderer.Render([a, b]), ToolDefinitionRenderer.Render([b, a]));
    }

    [Fact]
    public void LengthIsTheLengthOfWhatTheModelReceivesAndZeroWithoutTools()
    {
        var tools = new[] { new ToolDefinition("builtin__a", "A.", Schema) };

        Assert.Equal(ToolDefinitionRenderer.Render(tools).Length, ToolDefinitionRenderer.Length(tools));
        Assert.Equal(0, ToolDefinitionRenderer.Length([]));
    }

    [Fact]
    public void BuiltinToolsAreExposedWithTheirPrefix() =>
        Assert.Equal("builtin__calculator", ToolNaming.Builtin("calculator"));

    [Theory]
    [InlineData(1, 0, 1000)]
    [InlineData(15, 200, 1000)]
    [InlineData(0, 0, 0)]
    public void NoWarningWithinTheLimits(int activeTools, int definitionCharacters, int usableCharacters) =>
        Assert.Empty(ToolWarnings.Evaluate(activeTools, definitionCharacters, usableCharacters));

    [Fact]
    public void MoreThanFifteenActiveToolsWarn()
    {
        var warning = Assert.Single(ToolWarnings.Evaluate(16, 100, 100_000));

        Assert.Equal("tools.too_many_active", warning.Code);
    }

    [Fact]
    public void DefinitionsAboveAFifthOfTheUsableContextWarn()
    {
        var warning = Assert.Single(ToolWarnings.Evaluate(2, 201, 1000));

        Assert.Equal("tools.definitions_large", warning.Code);
    }

    [Fact]
    public void BothWarningsCanApplyTogether() =>
        Assert.Equal(
            ["tools.too_many_active", "tools.definitions_large"],
            ToolWarnings.Evaluate(20, 900, 1000).Select(w => w.Code).ToArray());
}
