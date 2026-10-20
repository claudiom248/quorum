using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Search;
using Quorum.Modules.KnowledgeBases.Mapping;
using Quorum.Modules.KnowledgeBases.Storage.Qdrant;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

public sealed class FilterTranslatorTests
{
    private static readonly FilterField Department = new(Guid.NewGuid(), "department", DictionaryFieldType.Text);
    private static readonly FilterField Year = new(Guid.NewGuid(), "year", DictionaryFieldType.Number);
    private static readonly FilterField Published = new(Guid.NewGuid(), "published_on", DictionaryFieldType.Date);

    private static KnowledgeBaseMapping Mapping(params (string Path, FilterField Field)[] fields) =>
        new(new MappingRoles("text", null, null, null, null), [.. fields.Select(f => new MappedField(f.Path, f.Field.Id))]);

    [Fact] // AC-25
    public void EachFilterBecomesAConditionOnThePathTheKnowledgeBaseMappedForItsField()
    {
        var filters = new FieldFilter[]
        {
            new AnyOf(Department, ["hr", "it"]),
            new NumberRange(Year, 2022, null),
        };

        var translation = FilterTranslator.Translate(filters, Mapping(("meta.dept", Department), ("meta.year", Year)));

        Assert.Empty(translation.MissingFields);
        Assert.Equal(2, translation.Conditions.Count);
        Assert.Contains(translation.Conditions, c => c is MatchAnyCondition { Path: "meta.dept" } m && m.Values.SequenceEqual(["hr", "it"]));
        Assert.Contains(translation.Conditions, c => c is NumberRangeCondition { Path: "meta.year", Min: 2022, Max: null });
    }

    [Fact] // AC-25
    public void ABooleanAndADateRangeTranslateToo()
    {
        var confidential = new FilterField(Guid.NewGuid(), "confidential", DictionaryFieldType.Boolean);
        var from = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.FromHours(2));
        var filters = new FieldFilter[] { new IsBoolean(confidential, true), new DateRange(Published, from, null) };

        var translation = FilterTranslator.Translate(filters, Mapping(("confidential", confidential), ("published_on", Published)));

        Assert.Contains(translation.Conditions, c => c is MatchBooleanCondition { Path: "confidential", Value: true });
        var range = Assert.Single(translation.Conditions.OfType<DateRangeCondition>());
        Assert.Equal(new DateTime(2024, 1, 15, 8, 0, 0, DateTimeKind.Utc), range.From);
        Assert.Null(range.To);
    }

    [Fact] // AC-28
    public void AFieldTheKnowledgeBaseDidNotMapIsReportedByNameAndNoConditionsAreOffered()
    {
        var filters = new FieldFilter[] { new AnyOf(Department, ["hr"]), new NumberRange(Year, 2022, 2024) };

        var translation = FilterTranslator.Translate(filters, Mapping(("department", Department)));

        Assert.Equal(["year"], translation.MissingFields);
        Assert.Empty(translation.Conditions);
    }

    [Fact]
    public void WithoutFiltersThereAreNoConditionsAndNothingMissing()
    {
        var translation = FilterTranslator.Translate([], Mapping());

        Assert.Empty(translation.Conditions);
        Assert.Empty(translation.MissingFields);
    }

    [Fact] // AC-26
    public void TwoFiltersOnTheSameFieldBothApply()
    {
        var filters = new FieldFilter[] { new AnyOf(Department, ["hr"]), new AnyOf(Department, ["it"]) };

        var translation = FilterTranslator.Translate(filters, Mapping(("department", Department)));

        Assert.Equal(2, translation.Conditions.Count);
    }
}
