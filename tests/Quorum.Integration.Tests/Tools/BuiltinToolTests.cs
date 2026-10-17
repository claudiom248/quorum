using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Quorum.Modules.Agents.Tools;
using Quorum.Modules.Agents.Tools.Builtin;

namespace Quorum.Integration.Tests.Tools;

/// <summary>The built-in tools, without a database or a model (spec 004, D-15, AC-19).</summary>
public sealed class BuiltinToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<string> Calculate(string expression) =>
        new CalculatorTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { expression }), Ct);

    [Theory]
    [InlineData("1 + 2", "3")]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("(2 + 3) * 4", "20")]
    [InlineData("10 / 4", "2.5")]
    [InlineData("10 % 4", "2")]
    [InlineData("-7 % 4", "-3")]
    [InlineData("2 ^ 10", "1024")]
    [InlineData("2 ^ 3 ^ 2", "512")]
    [InlineData("-2 ^ 2", "-4")]
    [InlineData("2 ^ -1", "0.5")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("1 / 3", "0.3333333333333333333333333333")]
    [InlineData("100 * 1.1", "110")]
    [InlineData("--5", "5")]
    [InlineData("+5", "5")]
    [InlineData("sqrt(16)", "4")]
    [InlineData("pow(2, 8)", "256")]
    [InlineData("abs(-3.5)", "3.5")]
    [InlineData("round(2.5)", "3")]
    [InlineData("round(3.14159, 2)", "3.14")]
    [InlineData("min(4, 2, 9)", "2")]
    [InlineData("max(4, 2, 9)", "9")]
    [InlineData("max(1, sqrt(81)) + min(2, 3)", "11")]
    [InlineData("SQRT(9)", "3")]
    [InlineData("1234567890123 * 1000", "1234567890123000")]
    [InlineData(".5 + .5", "1")]
    public async Task EvaluatesArithmetic(string expression, string expected)
    {
        Assert.Equal(expected, await Calculate(expression));
    }

    [Fact]
    public async Task RootsKeepTheFullDecimalPrecisionAndFractionalPowersAreFloatingPoint()
    {
        Assert.StartsWith("1.41421356237309504880", await Calculate("sqrt(2)"), StringComparison.Ordinal);
        Assert.Equal("2", await Calculate("pow(4, 0.5)"));
    }

    [Theory]
    [InlineData("1 / 0", "zero")]
    [InlineData("1 % 0", "zero")]
    [InlineData("sqrt(-1)", "negative")]
    [InlineData("(1 + 2", "parenthes")]
    [InlineData("1 + 2)", "unexpected")]
    [InlineData("1 +", "expected")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("foo(1)", "foo")]
    [InlineData("1 + x", "x")]
    [InlineData("min()", "argument")]
    [InlineData("sqrt(1, 2)", "argument")]
    [InlineData("1e3", "unexpected")]
    [InlineData("1 $ 2", "unexpected")]
    [InlineData("79228162514264337593543950335 * 10", "range")]
    [InlineData("pow(10, 400)", "range")]
    [InlineData("System.IO.File.ReadAllText('secrets')", "System")]
    [InlineData("1..2", "number")]
    public async Task ReportsAnErrorTheModelCanReadForInvalidInput(string expression, string expectedWord)
    {
        var error = await Assert.ThrowsAsync<ToolFailedException>(() => Calculate(expression));

        Assert.Contains(expectedWord, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesAnExpressionLongerThanTheLimit()
    {
        var expression = string.Join('+', Enumerable.Repeat("1", CalculatorTool.MaxExpressionLength));

        var error = await Assert.ThrowsAsync<ToolFailedException>(() => Calculate(expression));

        Assert.Contains("too long", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesDeepNestingInsteadOfOverflowingTheStack()
    {
        var depth = CalculatorTool.MaxDepth + 1;
        var expression = new string('(', depth) + "1" + new string(')', depth);

        var error = await Assert.ThrowsAsync<ToolFailedException>(() => Calculate(expression));

        Assert.Contains("deeply", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesLongChainsOfUnaryOperatorsAndPowers()
    {
        var unary = new string('-', CalculatorTool.MaxDepth + 1) + "1";
        var powers = string.Join('^', Enumerable.Repeat("1", CalculatorTool.MaxDepth + 2));

        await Assert.ThrowsAsync<ToolFailedException>(() => Calculate(unary));
        await Assert.ThrowsAsync<ToolFailedException>(() => Calculate(powers));
    }

    [Fact]
    public async Task ARequestWithoutAnExpressionIsAnError()
    {
        var tool = new CalculatorTool();

        await Assert.ThrowsAsync<ToolFailedException>(() => tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { }), Ct));
    }

    [Fact]
    public async Task TheCurrentDateAndTimeAreInUtcByDefault()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 30, 5, TimeSpan.Zero));

        var text = await new CurrentDateTimeTool(clock).ExecuteAsync(JsonSerializer.SerializeToElement(new { }), Ct);

        Assert.Contains("2026-10-17T12:30:05+00:00", text, StringComparison.Ordinal);
        Assert.Contains("Saturday", text, StringComparison.Ordinal);
        Assert.Contains("UTC", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Europe/Rome", "2026-10-17T14:30:05+02:00")]
    [InlineData("America/New_York", "2026-10-17T08:30:05-04:00")]
    [InlineData("Asia/Kolkata", "2026-10-17T18:00:05+05:30")]
    public async Task TheTimeIsConvertedToTheRequestedZone(string zone, string expected)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 30, 5, TimeSpan.Zero));

        var text = await new CurrentDateTimeTool(clock).ExecuteAsync(JsonSerializer.SerializeToElement(new { timezone = zone }), Ct);

        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.Contains(zone, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("../../etc/passwd")]
    public async Task AnUnknownZoneIsAnErrorThatSuggestsTheFormat(string zone)
    {
        var tool = new CurrentDateTimeTool(new FakeTimeProvider());

        var error = await Assert.ThrowsAsync<ToolFailedException>(
            () => tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { timezone = zone }), Ct));

        Assert.Contains("Europe/Rome", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheToolsKnowTheirCatalogNames()
    {
        Assert.Equal(BuiltinToolCatalog.Calculator, new CalculatorTool().Name);
        Assert.Equal(BuiltinToolCatalog.CurrentDateTime, new CurrentDateTimeTool(TimeProvider.System).Name);
    }
}
