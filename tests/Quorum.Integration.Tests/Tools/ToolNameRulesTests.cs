using Quorum.Modules.Agents.Domain;
using Quorum.Modules.Agents.Tools;

namespace Quorum.Integration.Tests.Tools;

/// <summary>The name of a tool made in Quorum is the one the model reads and the one people read (spec 015).</summary>
public sealed class ToolNameRulesTests
{
    [Theory] // AC-4, AC-5
    [InlineData("WebFetch", true)]
    [InlineData("search_contracts", true)]
    [InlineData("a", true)]
    [InlineData("A-b_c9", true)]
    [InlineData("9lives", true)]
    [InlineData("has space", false)]
    [InlineData("two__underscores", false)]
    [InlineData("-leading", false)]
    [InlineData("_leading", false)]
    [InlineData("accentà", false)]
    [InlineData("", false)]
    [InlineData("this-name-is-longer-than-forty-characters-for-sure", false)]
    public void ANameHasLettersOfAnyCaseDigitsDashAndUnderscoreAndNeverTwoUnderscores(string name, bool valid) =>
        Assert.Equal(valid, ToolNameRules.IsValid(name));

    [Fact]
    public void ANullNameIsNotValid() => Assert.False(ToolNameRules.IsValid(null));

    [Fact] // AC-1
    public void ToolsMadeInQuorumAreCalledByTheirOwnName()
    {
        Assert.Equal("calculator", ToolNaming.Builtin("calculator"));
        Assert.Equal("search_contracts", ToolNaming.Rag("search_contracts"));
        Assert.Equal("contracts", ToolNaming.Lookup("contracts"));
        Assert.Equal("WebSearch", ToolNaming.WebSearch("WebSearch"));
        Assert.Equal("WebFetch", ToolNaming.WebFetch("WebFetch"));
    }

    [Fact] // AC-2
    public void AnMcpToolCarriesItsServer() => Assert.Equal("files__read", ToolNaming.Mcp("files", "read"));

    [Fact] // AC-3
    public void TheOperationsOfALookupToolTakeTheOperationAsASuffix()
    {
        Assert.Equal(
            ["contracts_entries", "contracts_documents", "contracts_count", "contracts_describe", "contracts_query"],
            ToolNaming.FunctionsOf(ToolOrigin.Lookup, "contracts"));
        Assert.Equal(["contracts_count"], [ToolNaming.LookupFunction("contracts", "count")]);
        Assert.Equal(["contracts"], ToolNaming.FunctionsOf(ToolOrigin.Rag, "contracts"));
    }
}
