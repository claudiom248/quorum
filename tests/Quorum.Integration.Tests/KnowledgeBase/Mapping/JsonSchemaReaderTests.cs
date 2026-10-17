using Quorum.Modules.KnowledgeBase.Domain;
using Quorum.Modules.KnowledgeBase.Mapping;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBase.Mapping;

public sealed class JsonSchemaReaderTests
{
    private static Discovery Read(string schema)
    {
        var result = JsonSchemaReader.Read(schema);
        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Description);
        return result.Value;
    }

    [Fact]
    public void ThePastedFlatSchemaGivesTheTypesOfThePayload()
    {
        var discovery = Read(SampleSchemas.Flat);

        var types = discovery.Properties.ToDictionary(p => p.Path, p => p.Type);
        Assert.Equal(DictionaryFieldType.Text, types["text"]);
        Assert.Equal(DictionaryFieldType.Number, types["page"]);
        Assert.Equal(DictionaryFieldType.Number, types["year"]);
        Assert.Equal(DictionaryFieldType.Date, types["published_on"]);
        Assert.Equal(DictionaryFieldType.Boolean, types["confidential"]);
        Assert.Empty(discovery.Warnings);
    }

    [Fact]
    public void NestedPropertiesGetDotPaths()
    {
        var discovery = Read(SampleSchemas.Nested);

        Assert.Contains(discovery.Properties, p => p is { Path: "meta.year", Type: DictionaryFieldType.Number });
        Assert.Contains(discovery.Properties, p => p is { Path: "meta.published_on", Type: DictionaryFieldType.Date });
    }

    [Theory]
    [InlineData("date")]
    [InlineData("date-time")]
    public void ADateFormatMakesADateField(string format)
    {
        var discovery = Read("""{"type":"object","properties":{"d":{"type":"string","format":""" + $"\"{format}\"" + "}}}");

        Assert.Equal(DictionaryFieldType.Date, discovery.Properties.Single().Type);
    }

    [Fact]
    public void ATypeThatAllowsNullIsTheOtherType()
    {
        var discovery = Read("""{"type":"object","properties":{"year":{"type":["integer","null"]},"note":{"type":["null","string"]}}}""");

        Assert.Equal(DictionaryFieldType.Number, discovery.Properties.Single(p => p.Path == "year").Type);
        Assert.Equal(DictionaryFieldType.Text, discovery.Properties.Single(p => p.Path == "note").Type);
    }

    [Fact]
    public void AListOfStringsIsAListOfTexts()
    {
        var discovery = Read("""{"type":"object","properties":{"tags":{"type":"array","items":{"type":"string"}}}}""");

        Assert.Equal(DictionaryFieldType.TextList, discovery.Properties.Single().Type);
        Assert.Empty(discovery.Warnings);
    }

    [Fact]
    public void AListOfObjectsOrOfNumbersIsHandledAsInSampling()
    {
        var discovery = Read("""
            {"type":"object","properties":{
              "authors":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"}}}},
              "scores":{"type":"array","items":{"type":"number"}}}}
            """);

        Assert.Equal(["scores"], discovery.Properties.Select(p => p.Path));
        Assert.Contains(discovery.Warnings, w => w is { Code: WarningCodes.PropertyNotMapped, Path: "authors" });
        Assert.Contains(discovery.Warnings, w => w is { Code: WarningCodes.MixedTypes, Path: "scores" });
    }

    [Fact]
    public void ADraft07SchemaWorksToo()
    {
        var discovery = Read("""{"$schema":"http://json-schema.org/draft-07/schema#","type":"object","properties":{"title":{"type":"string"}},"definitions":{"x":{"type":"string"}}}""");

        Assert.Equal(["title"], discovery.Properties.Select(p => p.Path));
        Assert.Contains(discovery.Warnings, w => w is { Code: WarningCodes.UnsupportedSchemaKeyword } && w.Message.Contains("definitions", StringComparison.Ordinal));
    }

    [Fact]
    public void AReferenceIsNotResolvedAndTheWarningIsGivenOncePerKeyword()
    {
        var discovery = Read("""
            {"type":"object","properties":{
              "a":{"$ref":"#/$defs/year"},
              "b":{"$ref":"#/$defs/year"},
              "c":{"anyOf":[{"type":"string"},{"type":"number"}]},
              "text":{"type":"string"}}}
            """);

        Assert.Equal(["text"], discovery.Properties.Select(p => p.Path));
        Assert.Single(discovery.Warnings, w => w.Code == WarningCodes.UnsupportedSchemaKeyword && w.Message.Contains("$ref", StringComparison.Ordinal));
        Assert.Contains(discovery.Warnings, w => w is { Code: WarningCodes.PropertyNotMapped, Path: "a" });
        Assert.Contains(discovery.Warnings, w => w is { Code: WarningCodes.PropertyNotMapped, Path: "c" });
    }

    [Fact]
    public void ValidationKeywordsAreIgnoredWithoutAWarning()
    {
        var discovery = Read("""{"type":"object","required":["a"],"properties":{"a":{"type":"string","minLength":1,"description":"x","default":"y"}}}""");

        Assert.Empty(discovery.Warnings);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("""{"type":"object"}""")]
    [InlineData("""{"type":"object","properties":{}}""")]
    [InlineData("""{"type":"array","properties":{"a":{"type":"string"}}}""")]
    public void ATextThatIsNotAnObjectSchemaWithPropertiesIsRefused(string schema)
    {
        var result = JsonSchemaReader.Read(schema);

        Assert.False(result.IsSuccess);
        Assert.Equal("knowledge_base.invalid_schema", result.Error.Code);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"a":{"type":"strng"}}}""")]
    [InlineData("""{"type":"object","properties":{"a":5}}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","minLength":"x"}}}""")]
    [InlineData("""{"$schema":"http://json-schema.org/draft-07/schema#","type":"object","properties":{"a":{"type":"string","pattern":3}}}""")]
    public void ASchemaThatBreaksTheSpecificationOfItsDraftIsRefused(string schema)
    {
        var result = JsonSchemaReader.Read(schema);

        Assert.False(result.IsSuccess);
        Assert.Equal("knowledge_base.invalid_schema", result.Error.Code);
        Assert.Contains("not a valid JSON Schema", result.Error.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void ASchemaOfADraftWithoutAMetaSchemaIsReadAsItIs()
    {
        var discovery = Read("""{"$schema":"http://json-schema.org/draft-04/schema#","type":"object","properties":{"year":{"type":"integer","minimum":1,"exclusiveMinimum":true}}}""");

        Assert.Equal(["year"], discovery.Properties.Select(p => p.Path));
    }

    [Fact]
    public void ASchemaOverTheSizeLimitIsRefused()
    {
        var schema = "{\"type\":\"object\",\"description\":\"" + new string('x', JsonSchemaReader.MaxBytes) + "\",\"properties\":{\"a\":{\"type\":\"string\"}}}";

        var result = JsonSchemaReader.Read(schema);

        Assert.Equal("knowledge_base.invalid_schema", result.Error.Code);
    }

    [Fact]
    public void ADeepSchemaStopsAtTheDepthLimitInsteadOfFollowingItForever()
    {
        var inner = """{"type":"string"}""";
        for (var i = 0; i < 20; i++)
        {
            inner = """{"type":"object","properties":{"n":""" + inner + "}}";
        }

        var discovery = Read(inner);

        Assert.Empty(discovery.Properties);
        Assert.NotEmpty(discovery.Warnings);
    }
}
