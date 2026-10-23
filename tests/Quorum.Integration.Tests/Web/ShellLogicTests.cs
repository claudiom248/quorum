using Quorum.Web.Client;
using Quorum.Web.Client.Api;

namespace Quorum.Integration.Tests.Web;

/// <summary>The plain logic behind the shell and the lists: the filters in the URL, who may open what and how a scope reads (spec 010, D-4 to D-6, D-19).</summary>
public sealed class ShellLogicTests
{
    private static readonly Guid TenantA = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b3001");
    private static readonly Guid TenantB = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b3002");
    private static readonly Guid AssistantId = Guid.Parse("01a14797-6817-7d25-b071-1da93d0b3011");

    private static readonly AssistantSummary[] Assistants = [new(AssistantId, "Support"), new(Guid.Parse("01a14797-6817-7d25-b071-1da93d0b3012"), "Sales")];

    [Fact]
    public void FiltersSurviveARoundTripThroughTheQueryString()
    {
        var query = new ListQuery
        {
            Scope = ["assistant", "global"],
            Tenant = [TenantA, TenantB],
            Assistant = [AssistantId],
            Type = ["mcp"],
            Status = ["to-review"],
            Q = "search code & more",
        };

        var parsed = ListQuery.Parse("http://quorum.test/tools/mcp" + query.ToQueryString());

        Assert.Equal(query, parsed with { }, new ListQueryComparer());
    }

    [Fact]
    public void AnUnfilteredListHasNoQueryStringAndKeepsItsPath()
    {
        Assert.Equal(string.Empty, ListQuery.Empty.ToQueryString());
        Assert.Equal("tools/mcp", ListQuery.Empty.ApplyTo("tools/mcp?scope=global#x"));
        Assert.Equal("tools/mcp?scope=global", new ListQuery { Scope = ["global"] }.ApplyTo("tools/mcp?q=old"));
    }

    [Fact]
    public void IdsThatAreNotIdsAndUnknownKeysAreIgnored()
    {
        var parsed = ListQuery.Parse($"?tenant={TenantA},not-an-id&drawer=open&q=%20%20");

        Assert.Equal([TenantA], parsed.Tenant);
        Assert.Null(parsed.Q);
        Assert.True(parsed.HasFilters);
        Assert.False(ListQuery.Parse("?drawer=open").HasFilters);
    }

    [Fact]
    public void ARepeatedKeyAddsUpWithoutDuplicates()
    {
        var parsed = ListQuery.Parse("?scope=global&scope=tenant,global");

        Assert.Equal(["global", "tenant"], parsed.Scope);
    }

    [Theory]
    [InlineData("tools", true)]
    [InlineData("tools/mcp/new", true)]
    [InlineData("Knowledge/lookups?scope=global", true)]
    [InlineData("knowledge/bases/new", true)]
    [InlineData("", false)]
    [InlineData("settings", false)]
    [InlineData("lab/editor", false)]
    [InlineData("toolshed", false)]
    public void ToolsAndKnowledgeAreTheAdminSections(string path, bool adminOnly)
    {
        Assert.Equal(adminOnly, AccessRules.IsAdminOnly(path));
    }

    [Fact]
    public void OnlyAnAdminOpensAnAdminSection()
    {
        Assert.True(AccessRules.CanOpen("Admin", "tools/mcp"));
        Assert.False(AccessRules.CanOpen("Member", "tools/mcp"));
        Assert.False(AccessRules.CanOpen(null, "knowledge"));
        Assert.True(AccessRules.CanOpen("Member", "settings"));
    }

    [Fact]
    public void AScopeReadsTheSameEverywhere()
    {
        Assert.Equal("Global", ScopeView.Label(new ToolScope(ScopeKinds.Global, [], []), Assistants));
        Assert.Equal("Tenant", ScopeView.Label(new ToolScope(ScopeKinds.Tenant, [], []), Assistants));
        Assert.Equal("Tenant +2", ScopeView.Label(new ToolScope(ScopeKinds.Tenant, [TenantA, TenantB], []), Assistants));
        Assert.Equal("Assistants: Support", ScopeView.Label(new ToolScope(ScopeKinds.Assistant, [], [AssistantId]), Assistants));
        Assert.Equal("Assistants: an assistant you cannot see", ScopeView.Label(new ToolScope(ScopeKinds.Assistant, [], [Guid.NewGuid()]), Assistants));
    }

    [Fact]
    public void ManyAssistantsCollapseToACount()
    {
        var scope = new ToolScope(ScopeKinds.Assistant, [], [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);

        Assert.Equal("3 assistants", ScopeView.Label(scope, Assistants));
    }

    private sealed class ListQueryComparer : IEqualityComparer<ListQuery>
    {
        public bool Equals(ListQuery? x, ListQuery? y) =>
            x is not null && y is not null
            && x.Scope.SequenceEqual(y.Scope) && x.Tenant.SequenceEqual(y.Tenant) && x.Assistant.SequenceEqual(y.Assistant)
            && x.Type.SequenceEqual(y.Type) && x.Status.SequenceEqual(y.Status) && x.Q == y.Q;

        public int GetHashCode(ListQuery obj) => obj.ToQueryString().GetHashCode(StringComparison.Ordinal);
    }
}
