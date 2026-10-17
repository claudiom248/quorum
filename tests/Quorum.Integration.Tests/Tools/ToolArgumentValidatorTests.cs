using Quorum.Modules.Agents.Tools;

namespace Quorum.Integration.Tests.Tools;

/// <summary>Arguments the model sends are checked against the tool's schema before anything runs (spec 004, AC-25).</summary>
public sealed class ToolArgumentValidatorTests
{
    private const string Schema = """
        {"type":"object",
         "properties":{
           "name":{"type":"string"},
           "count":{"type":"integer"},
           "ratio":{"type":"number"},
           "flag":{"type":"boolean"},
           "mode":{"type":"string","enum":["a","b"]},
           "tags":{"type":"array","items":{"type":"string"}},
           "nested":{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]},
           "either":{"type":["string","null"]}},
         "required":["name"]}
        """;

    [Theory]
    [InlineData("""{"name":"x"}""")]
    [InlineData("""{"name":"x","count":3,"ratio":1.5,"flag":true,"mode":"a","tags":["p","q"],"nested":{"id":1}}""")]
    [InlineData("""{"name":"x","count":3.0}""")]
    [InlineData("""{"name":"x","either":null}""")]
    [InlineData("""{"name":"x","either":"s"}""")]
    [InlineData("""{"name":"x","unlisted":"extra properties are fine unless the schema forbids them"}""")]
    public void AcceptsArgumentsThatMatch(string arguments) =>
        Assert.Null(ToolArgumentValidator.Validate(Schema, arguments));

    [Theory]
    [InlineData("""{}""", "name")]
    [InlineData("""{"name":1}""", "name")]
    [InlineData("""{"name":"x","count":"3"}""", "count")]
    [InlineData("""{"name":"x","count":3.5}""", "count")]
    [InlineData("""{"name":"x","ratio":"high"}""", "ratio")]
    [InlineData("""{"name":"x","flag":"yes"}""", "flag")]
    [InlineData("""{"name":"x","mode":"c"}""", "mode")]
    [InlineData("""{"name":"x","tags":"p"}""", "tags")]
    [InlineData("""{"name":"x","tags":["p",2]}""", "tags")]
    [InlineData("""{"name":"x","nested":{}}""", "id")]
    [InlineData("""{"name":"x","nested":{"id":"1"}}""", "id")]
    [InlineData("""{"name":"x","either":5}""", "either")]
    [InlineData("""["name"]""", "object")]
    [InlineData("""null""", "object")]
    [InlineData("""not json""", "JSON")]
    public void ExplainsWhatIsWrongInTermsTheModelCanFix(string arguments, string mentions)
    {
        var problem = ToolArgumentValidator.Validate(Schema, arguments);

        Assert.NotNull(problem);
        Assert.Contains(mentions, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedExtraPropertyIsNamed()
    {
        var problem = ToolArgumentValidator.Validate("""{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":false}""", """{"a":"1","b":2}""");

        Assert.NotNull(problem);
        Assert.Contains("b", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyArgumentsAreAnEmptyObject(string arguments)
    {
        Assert.Null(ToolArgumentValidator.Validate("""{"type":"object"}""", arguments));
        Assert.NotNull(ToolArgumentValidator.Validate("""{"type":"object","required":["a"]}""", arguments));
    }

    [Fact]
    public void KeywordsItDoesNotKnowAreIgnoredSoARichSchemaNeverBlocksAValidCall()
    {
        const string schema = """{"type":"object","properties":{"a":{"anyOf":[{"type":"string"},{"type":"integer"}]},"b":{"$ref":"#/$defs/b"}},"$defs":{"b":{"type":"string"}}}""";

        Assert.Null(ToolArgumentValidator.Validate(schema, """{"a":5,"b":"x"}"""));
    }
}
