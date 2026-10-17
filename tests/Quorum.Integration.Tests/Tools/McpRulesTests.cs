using System.Net;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Mcp;

namespace Quorum.Integration.Tests.Tools;

public sealed class McpRulesTests
{
    [Theory]
    [InlineData("files", "read", "files__read")]
    [InlineData("files", "read-file_2", "files__read-file_2")]
    public void APlainToolNameIsOnlyPrefixed(string server, string tool, string expected) =>
        Assert.Equal(expected, ToolNaming.Mcp(server, tool));

    [Theory]
    [InlineData("files", "read.file")]
    [InlineData("files", "lee archivo")]
    [InlineData("files", "búsqueda")]
    [InlineData("files", "this-tool-name-is-far-too-long-for-any-model-api-to-accept-as-a-function-name")]
    public void AToolNameTheModelApiWouldRejectIsSanitizedAndStaysWithinTheLimit(string server, string tool)
    {
        var name = ToolNaming.Mcp(server, tool);

        Assert.StartsWith(server + "__", name, StringComparison.Ordinal);
        Assert.InRange(name.Length, 1, ToolNaming.MaxLength);
        Assert.Matches("^[a-zA-Z0-9_-]+$", name);
    }

    [Fact]
    public void NamesThatSanitizeToTheSameTextStayDifferent()
    {
        var dotted = ToolNaming.Mcp("files", "a.b");
        var underscored = ToolNaming.Mcp("files", "a_b");
        var spaced = ToolNaming.Mcp("files", "a b");

        Assert.Equal(3, new[] { dotted, underscored, spaced }.Distinct().Count());
        Assert.Equal("files__a_b", underscored);
    }

    [Fact]
    public void TwoLongNamesWithTheSamePrefixStayDifferent()
    {
        var prefix = new string('x', 80);

        Assert.NotEqual(ToolNaming.Mcp("files", prefix + "1"), ToolNaming.Mcp("files", prefix + "2"));
    }

    [Theory]
    [InlineData("files", true)]
    [InlineData("my-server_2", true)]
    [InlineData("0day", true)]
    [InlineData("", false)]
    [InlineData("Files", false)]
    [InlineData("has space", false)]
    [InlineData("dotted.name", false)]
    [InlineData("-leading", false)]
    [InlineData("a__b", false)]
    [InlineData("builtin", false)]
    [InlineData("this-name-is-longer-than-thirty-two-characters", false)]
    public void ServerNamesAreRestrictedSoTheModelFacingNameIsValid(string name, bool valid) =>
        Assert.Equal(valid, McpServerRules.IsValidName(name));

    [Theory]
    [InlineData("http://tools.internal:8080/mcp", true)]
    [InlineData("https://mcp.example.com", true)]
    [InlineData("ftp://tools.internal/mcp", false)]
    [InlineData("/relative/path", false)]
    [InlineData("https://user:secret@mcp.example.com", false)]
    [InlineData("", false)]
    public void OnlyAbsoluteHttpUrlsWithoutCredentialsAreAccepted(string url, bool valid) =>
        Assert.Equal(valid, McpServerRules.TryParseUrl(url, out _));

    [Theory]
    [InlineData("Authorization", "Bearer abc", true)]
    [InlineData("X-Api-Key", "abc", true)]
    [InlineData("Host", "evil.example", false)]
    [InlineData("content-type", "text/plain", false)]
    [InlineData("Mcp-Session-Id", "1", false)]
    [InlineData("Bad Header", "x", false)]
    [InlineData("X-Injected", "a\r\nX-Other: b", false)]
    public void HeadersTheConnectionOwnsOrThatCouldInjectOthersAreRejected(string name, string value, bool valid) =>
        Assert.Equal(valid, McpServerRules.ValidateHeaders(new Dictionary<string, string> { [name] = value }) is null);

    [Fact]
    public void TheDefinitionHashIgnoresKeyOrderButNotContent()
    {
        var original = ToolDefinitionHash.Compute("t", "d", """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}},"required":["a"]}""");
        var reordered = ToolDefinitionHash.Compute("t", "d", """{"required":["a"],"properties":{"b":{"type":"number"},"a":{"type":"string"}},"type":"object"}""");
        var otherDescription = ToolDefinitionHash.Compute("t", "d2", """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}},"required":["a"]}""");
        var otherName = ToolDefinitionHash.Compute("t2", "d", """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}},"required":["a"]}""");
        var otherSchema = ToolDefinitionHash.Compute("t", "d", """{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""");
        var otherOrderInArray = ToolDefinitionHash.Compute("t", "d", """{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"number"}},"required":["b","a"]}""");

        Assert.Equal(original, reordered);
        Assert.Equal(64, original.Length);
        Assert.Equal(4, new[] { original, otherDescription, otherName, otherSchema }.Distinct().Count());
        Assert.NotEqual(original, otherOrderInArray);
    }

    [Theory]
    [InlineData("169.254.169.254", true)]
    [InlineData("169.254.0.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("100.100.100.200", true)]
    [InlineData("fd00:ec2::254", true)]
    [InlineData("168.63.129.16", true)]
    [InlineData("192.0.0.192", true)]
    [InlineData("fd20:ce::254", true)]
    [InlineData("::ffff:168.63.129.16", true)]
    [InlineData("168.63.129.17", false)]
    [InlineData("0.0.0.0", true)]
    [InlineData("0.1.2.3", true)]
    [InlineData("::", true)]
    [InlineData("::ffff:169.254.169.254", true)]
    [InlineData("::ffff:100.100.100.200", true)]
    [InlineData("64:ff9b::a9fe:a9fe", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("172.16.0.5", false)]
    [InlineData("100.100.100.201", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("64:ff9b::5db8:d822", false)]
    public void OnlyMetadataAndLinkLocalAddressesAreBlocked(string address, bool blocked) =>
        Assert.Equal(blocked, McpUrlGuard.IsBlocked(IPAddress.Parse(address)));
}
