using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBase.Domain;
using Quorum.Modules.KnowledgeBase.Mapping;

namespace Quorum.Integration.Tests.KnowledgeBase.Mapping;

public sealed class PayloadSamplerTests
{
    private static JsonObject Payload(string json) => JsonNode.Parse(json)!.AsObject();

    private static DictionaryFieldType TypeOf(Discovery discovery, string path) =>
        discovery.Properties.Single(p => p.Path == path).Type;

    [Fact]
    public void ScalarsAndListsOfScalarsAreFoundWithTheirTypes()
    {
        var discovery = PayloadSampler.Sample(
        [
            Payload("""{"text":"a","page":1,"score":0.5,"public":true,"tags":["x","y"],"published":"2024-01-15"}"""),
            Payload("""{"text":"b","page":2,"score":0.7,"public":false,"tags":["z"],"published":"2024-02-01T10:00:00Z"}"""),
        ]);

        Assert.Equal(DictionaryFieldType.Text, TypeOf(discovery, "text"));
        Assert.Equal(DictionaryFieldType.Number, TypeOf(discovery, "page"));
        Assert.Equal(DictionaryFieldType.Number, TypeOf(discovery, "score"));
        Assert.Equal(DictionaryFieldType.Boolean, TypeOf(discovery, "public"));
        Assert.Equal(DictionaryFieldType.TextList, TypeOf(discovery, "tags"));
        Assert.Equal(DictionaryFieldType.Date, TypeOf(discovery, "published"));
        Assert.Empty(discovery.Warnings);
    }

    [Fact]
    public void NestedObjectsGetDotPaths()
    {
        var discovery = PayloadSampler.Sample([Payload("""{"meta":{"year":2024,"author":{"team":"hr"}}}""")]);

        Assert.Equal(["meta.year", "meta.author.team"], discovery.Properties.Select(p => p.Path));
    }

    [Fact]
    public void AStringThatIsNotAlwaysADateStaysText()
    {
        var discovery = PayloadSampler.Sample([Payload("""{"when":"2024-01-15"}"""), Payload("""{"when":"next week"}""")]);

        Assert.Equal(DictionaryFieldType.Text, TypeOf(discovery, "when"));
        Assert.Empty(discovery.Warnings);
    }

    [Fact]
    public void ADateThatDoesNotExistIsNotADate()
    {
        var discovery = PayloadSampler.Sample([Payload("""{"when":"2024-13-45"}""")]);

        Assert.Equal(DictionaryFieldType.Text, TypeOf(discovery, "when"));
    }

    [Fact]
    public void MixedTypesAreProposedAsTextWithAWarning()
    {
        var discovery = PayloadSampler.Sample([Payload("""{"year":2024}"""), Payload("""{"year":"2023"}""")]);

        Assert.Equal(DictionaryFieldType.Text, TypeOf(discovery, "year"));
        var warning = Assert.Single(discovery.Warnings);
        Assert.Equal(WarningCodes.MixedTypes, warning.Code);
        Assert.Equal("year", warning.Path);
    }

    [Fact]
    public void APropertyThatIsOnlyNullHasNoKnownTypeAndIsReported()
    {
        var discovery = PayloadSampler.Sample([Payload("""{"text":"a","note":null}""")]);

        Assert.DoesNotContain(discovery.Properties, p => p.Path == "note");
        Assert.Equal(WarningCodes.NoValues, Assert.Single(discovery.Warnings).Code);
    }

    [Fact]
    public void ANullInOnePointDoesNotHideTheTypeSeenInTheOthers()
    {
        var discovery = PayloadSampler.Sample([Payload("""{"year":null}"""), Payload("""{"year":2024}""")]);

        Assert.Equal(DictionaryFieldType.Number, TypeOf(discovery, "year"));
        Assert.Empty(discovery.Warnings);
    }

    [Fact]
    public void ListsOfObjectsAndNestedListsAreNotMappedAndSaySo()
    {
        var discovery = PayloadSampler.Sample([Payload("""{"text":"a","authors":[{"name":"x"}],"matrix":[[1,2],[3]]}""")]);

        Assert.Equal(["text"], discovery.Properties.Select(p => p.Path));
        Assert.Equal(["authors", "matrix"], discovery.Warnings.Where(w => w.Code == WarningCodes.PropertyNotMapped).Select(w => w.Path));
    }

    [Fact]
    public void AListOfNumbersIsAListOfTextsWithAWarning()
    {
        var discovery = PayloadSampler.Sample([Payload("""{"scores":[1,2,3]}""")]);

        Assert.Equal(DictionaryFieldType.TextList, TypeOf(discovery, "scores"));
        Assert.Equal(WarningCodes.MixedTypes, Assert.Single(discovery.Warnings).Code);
    }

    [Fact]
    public void AnEmptyListInOnePointStillLeavesAListOfTexts()
    {
        var discovery = PayloadSampler.Sample([Payload("""{"tags":[]}"""), Payload("""{"tags":["a"]}""")]);

        Assert.Equal(DictionaryFieldType.TextList, TypeOf(discovery, "tags"));
        Assert.Empty(discovery.Warnings);
    }

    [Fact]
    public void ADottedPropertyNameIsLeftOutBecauseAFilterCouldNotAddressIt()
    {
        var discovery = PayloadSampler.Sample([Payload("""{"text":"a","a.b":1}""")]);

        Assert.Equal(["text"], discovery.Properties.Select(p => p.Path));
        Assert.Equal(WarningCodes.UnsupportedPropertyName, Assert.Single(discovery.Warnings).Code);
    }

    [Fact]
    public void AVeryWidePayloadIsCutAndSaysSo()
    {
        var wide = new JsonObject();
        for (var i = 0; i < 600; i++)
        {
            wide[$"p{i}"] = i;
        }

        var discovery = PayloadSampler.Sample([wide]);

        Assert.Equal(500, discovery.Properties.Count);
        Assert.Contains(discovery.Warnings, w => w.Code == WarningCodes.TooManyProperties);
    }

    [Fact]
    public void NoPointsMeansNothingFound()
    {
        var discovery = PayloadSampler.Sample([]);

        Assert.Empty(discovery.Properties);
        Assert.Empty(discovery.Warnings);
    }
}
