using System.Text.Json.Nodes;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.SampleKnowledgeBase;

namespace Quorum.Integration.Tests.KnowledgeBases.Mapping;

public sealed class MappingConventionsTests
{
    private static DiscoveredProperty Prop(string path, DictionaryFieldType type = DictionaryFieldType.Text) => new(path, type);

    [Fact] // AC-7
    public void TheRolesAndTheNestedYearAreFoundInTheExamplePayload()
    {
        var draft = MappingConventions.Build(
        [
            Prop("chunk_text"),
            Prop("doc_id"),
            Prop("file_name"),
            Prop("page", DictionaryFieldType.Number),
            Prop("source_url"),
            Prop("meta.year", DictionaryFieldType.Number),
        ]);

        Assert.Equal(new MappingRoles("chunk_text", "doc_id", "file_name", "page", "source_url"), draft.Roles);
        var year = Assert.Single(draft.Fields);
        Assert.Equal("meta.year", year.Path);
        Assert.Equal("year", year.Name);
        Assert.Equal(DictionaryFieldType.Number, year.Type);
        Assert.Empty(year.Issues);
    }

    [Theory]
    [InlineData("TEXT")]
    [InlineData("Chunk-Text")]
    [InlineData("chunkText")]
    [InlineData("body")]
    [InlineData("content")]
    [InlineData("chunk")]
    public void RoleNamesIgnoreCaseHyphensAndUnderscores(string name)
    {
        var draft = MappingConventions.Build([Prop(name)]);

        Assert.Equal(name, draft.Roles.Text);
        Assert.Empty(draft.Fields);
    }

    [Fact]
    public void ARoleIsFoundInsideANestedObjectToo()
    {
        var draft = MappingConventions.Build([Prop("document.content"), Prop("document.link")]);

        Assert.Equal("document.content", draft.Roles.Text);
        Assert.Equal("document.link", draft.Roles.SourceUrl);
    }

    [Fact]
    public void TheShallowestMatchWinsAndTheOtherBecomesAMetadataField()
    {
        var draft = MappingConventions.Build([Prop("meta.title"), Prop("title")]);

        Assert.Equal("title", draft.Roles.Title);
        Assert.Equal(["meta.title"], draft.Fields.Select(f => f.Path));
    }

    [Fact]
    public void ThePreferredNameWinsAmongPropertiesAtTheSameDepth()
    {
        var draft = MappingConventions.Build([Prop("body"), Prop("text")]);

        Assert.Equal("text", draft.Roles.Text);
        Assert.Equal(["body"], draft.Fields.Select(f => f.Path));
    }

    [Fact]
    public void ARoleNeedsAValueOfAMatchingType()
    {
        var draft = MappingConventions.Build([Prop("text", DictionaryFieldType.Number), Prop("title", DictionaryFieldType.Boolean)]);

        Assert.Null(draft.Roles.Text);
        Assert.Null(draft.Roles.Title);
        Assert.Equal(["text", "title"], draft.Fields.Select(f => f.Path));
    }

    [Fact]
    public void ADocumentIdOrPositionMayBeANumber()
    {
        var draft = MappingConventions.Build([Prop("doc_id", DictionaryFieldType.Number), Prop("section", DictionaryFieldType.Text)]);

        Assert.Equal("doc_id", draft.Roles.DocumentId);
        Assert.Equal("section", draft.Roles.Position);
    }

    [Fact]
    public void APathNeverTakesTwoRoles()
    {
        var draft = MappingConventions.Build([Prop("name")]);

        Assert.Equal("name", draft.Roles.Title);
        Assert.Empty(draft.Fields);
    }

    [Fact] // AC-9
    public void TwoPathsWithTheSameLastSegmentAreBothFlagged()
    {
        var draft = MappingConventions.Build([Prop("meta.year", DictionaryFieldType.Number), Prop("info.year", DictionaryFieldType.Number), Prop("team")]);

        Assert.All(draft.Fields.Where(f => f.Name == "year"), f => Assert.Contains(MappingConventions.FieldNameCollision, f.Issues));
        Assert.Empty(draft.Fields.Single(f => f.Name == "team").Issues);
    }

    [Theory] // AC-9
    [InlineData("Release-Date", "knowledge_base.invalid_field_name")]
    [InlineData("2024", "knowledge_base.invalid_field_name")]
    [InlineData("has space", "knowledge_base.invalid_field_name")]
    [InlineData("Year", "knowledge_base.invalid_field_name")]
    [InlineData("query", "knowledge_base.reserved_field_name")]
    [InlineData("knowledge_base", "knowledge_base.reserved_field_name")]
    [InlineData("size_min", "knowledge_base.reserved_field_name")]
    [InlineData("created_to", "knowledge_base.reserved_field_name")]
    public void ANameTheDictionaryRefusesIsFlagged(string name, string issue)
    {
        var draft = MappingConventions.Build([Prop($"meta.{name}")]);

        var field = Assert.Single(draft.Fields);
        Assert.Equal(name, field.Name);
        Assert.Equal([issue], field.Issues);
    }

    [Fact] // AC-8
    public void TheSamePayloadGivesTheSameDraftWhetherItIsSampledOrDescribedByASchema()
    {
        var points = SampleCollections.CreateNested().Points.Select(p => p.Payload).ToList();
        var sampled = MappingConventions.Build(PayloadSampler.Sample(points).Properties);
        var described = MappingConventions.Build(JsonSchemaReader.Read(SampleSchemas.Nested).Value.Properties);

        Assert.Equal(sampled.Roles, described.Roles);
        Assert.Equal(sampled.Fields.Select(f => (f.Path, f.Name, f.Type)), described.Fields.Select(f => (f.Path, f.Name, f.Type)));
        Assert.Equal(new MappingRoles("chunk_text", "doc_id", "file_name", "page", "source_url"), sampled.Roles);
    }

    [Fact]
    public void AnEmptyPayloadHasNoRolesAndNoFields()
    {
        var draft = MappingConventions.Build(PayloadSampler.Sample([new JsonObject()]).Properties);

        Assert.Equal(DraftMapping.Empty.Roles, draft.Roles);
        Assert.Empty(draft.Fields);
    }
}
