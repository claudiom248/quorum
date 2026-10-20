using System.Globalization;
using System.Text.Json;
using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Search;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

public sealed class SearchFilterParserTests
{
    private const string Code = "knowledge_base.invalid_filter";

    private static readonly Dictionary<string, FilterField> Fields = new[]
    {
        new FilterField(Guid.NewGuid(), "department", DictionaryFieldType.Text),
        new FilterField(Guid.NewGuid(), "year", DictionaryFieldType.Number),
        new FilterField(Guid.NewGuid(), "published_on", DictionaryFieldType.Date),
        new FilterField(Guid.NewGuid(), "confidential", DictionaryFieldType.Boolean),
        new FilterField(Guid.NewGuid(), "tags", DictionaryFieldType.TextList),
    }.ToDictionary(f => f.Name, StringComparer.Ordinal);

    /// <summary>Filters as the model sends them: a JSON object of arguments, given here as <c>name = json</c> pairs.</summary>
    private static Dictionary<string, JsonElement> Arguments(params (string Name, string Json)[] values) =>
        values.ToDictionary(v => v.Name, v => JsonDocument.Parse(v.Json).RootElement.Clone(), StringComparer.Ordinal);

    private static FieldFilter Single(params (string Name, string Json)[] values)
    {
        var result = SearchFilterParser.Parse(Arguments(values), Fields, Code, ignoreEmpty: true);
        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Description);
        return Assert.Single(result.Value);
    }

    private static string Rejected(params (string Name, string Json)[] values)
    {
        var result = SearchFilterParser.Parse(Arguments(values), Fields, Code, ignoreEmpty: true);
        Assert.False(result.IsSuccess);
        Assert.Equal(Code, result.Error.Code);
        return result.Error.Description;
    }

    [Fact] // AC-25
    public void ATextFilterMatchesAnyOfItsValues()
    {
        var filter = Assert.IsType<AnyOf>(Single(("department", """["hr", "it"]""")));

        Assert.Equal("department", filter.Field.Name);
        Assert.Equal(["hr", "it"], filter.Values);
    }

    [Fact]
    public void ABareStringIsOneValue()
    {
        Assert.Equal(["hr"], Assert.IsType<AnyOf>(Single(("department", "\"hr\""))).Values);
    }

    [Fact] // AC-25
    public void AListFieldMatchesAnyOfItsValuesToo()
    {
        var filter = Assert.IsType<AnyOf>(Single(("tags", """["travel"]""")));

        Assert.Equal(DictionaryFieldType.TextList, filter.Field.Type);
        Assert.Equal(["travel"], filter.Values);
    }

    [Fact] // AC-25
    public void ANumberRangeCanBeOpenAtEitherEnd()
    {
        var both = Assert.IsType<NumberRange>(Single(("year_min", "2022"), ("year_max", "2024.5")));
        var onlyMin = Assert.IsType<NumberRange>(Single(("year_min", "2022")));

        Assert.Equal((2022d, 2024.5d), (both.Min, both.Max));
        Assert.Equal((2022d, (double?)null), (onlyMin.Min, onlyMin.Max));
    }

    [Fact] // AC-25
    public void ABooleanFilterNeedsARealBoolean()
    {
        Assert.True(Assert.IsType<IsBoolean>(Single(("confidential", "true"))).Value);
        Assert.False(Assert.IsType<IsBoolean>(Single(("confidential", "false"))).Value);
    }

    [Fact] // AC-25
    public void ADateRangeIsReadAsInstantsAndADateOnlyEndIncludesThatWholeDay()
    {
        var range = Assert.IsType<DateRange>(Single(("published_on_from", "\"2024-01-15\""), ("published_on_to", "\"2024-03-10\"")));

        Assert.Equal(new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero), range.From);
        Assert.Equal(new DateTimeOffset(2024, 3, 11, 0, 0, 0, TimeSpan.Zero).AddTicks(-1), range.To);
    }

    [Fact]
    public void ADateWithATimeAndAnOffsetIsKeptAsThatInstant()
    {
        var range = Assert.IsType<DateRange>(Single(("published_on_from", "\"2024-01-15T10:30:00+02:00\"")));

        Assert.Equal(DateTimeOffset.Parse("2024-01-15T08:30:00Z", CultureInfo.InvariantCulture), range.From);
        Assert.Null(range.To);
    }

    [Fact]
    public void AllTheFiltersOfOneCallComeBackTogether()
    {
        var result = SearchFilterParser.Parse(
            Arguments(("department", """["hr"]"""), ("year_min", "2023"), ("confidential", "false")),
            Fields,
            Code,
            ignoreEmpty: true);

        Assert.Equal(["confidential", "department", "year"], result.Value.Select(f => f.Field.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"\"")]
    public void ANullOrEmptyValueIsNoFilterAtAll(string json)
    {
        var result = SearchFilterParser.Parse(Arguments(("department", json), ("year_min", "null")), Fields, Code, ignoreEmpty: true);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Theory] // AC-31
    [InlineData("year_min", "\"2022\"")]
    [InlineData("year_min", "true")]
    [InlineData("year_max", "[1]")]
    [InlineData("confidential", "\"true\"")]
    [InlineData("confidential", "1")]
    [InlineData("department", "5")]
    [InlineData("department", "[1, 2]")]
    [InlineData("department", """["hr", ""]""")]
    [InlineData("department", """{"any": ["hr"]}""")]
    [InlineData("published_on_from", "20240115")]
    [InlineData("published_on_from", "\"15/01/2024\"")]
    [InlineData("published_on_to", "\"2024-13-45\"")]
    [InlineData("published_on_from", "\"yesterday\"")]
    public void AValueThatDoesNotFitTheFieldTypeIsRefused(string name, string json)
    {
        Assert.False(string.IsNullOrWhiteSpace(Rejected((name, json))));
    }

    [Fact] // AC-31
    public void ARangeWhoseEndsAreTheWrongWayRoundIsRefused()
    {
        Rejected(("year_min", "2025"), ("year_max", "2020"));
        Rejected(("published_on_from", "\"2024-05-01\""), ("published_on_to", "\"2024-04-01\""));
    }

    [Fact] // AC-31
    public void AListOfMoreThanTwentyValuesOrAValueOfMoreThanTwoHundredCharactersIsRefused()
    {
        var tooMany = JsonSerializer.Serialize(Enumerable.Range(1, 21).Select(i => $"value{i}"));
        var tooLong = JsonSerializer.Serialize(new[] { new string('x', 201) });
        var longest = JsonSerializer.Serialize(new[] { new string('x', 200) });

        Rejected(("department", tooMany));
        Rejected(("tags", tooLong));
        Assert.Single(SearchFilterParser.Parse(Arguments(("department", longest)), Fields, Code, ignoreEmpty: true).Value);
    }

    [Theory] // AC-31
    [InlineData("unknown_field")]
    [InlineData("query")]
    [InlineData("department_min")]
    [InlineData("year_from")]
    [InlineData("published_on_min")]
    [InlineData("confidential_max")]
    public void AParameterThatIsNotAFilterOfTheFieldsIsRefused(string name)
    {
        Rejected((name, "1"));
    }

    [Fact] // AC-31
    public void ANumberOrDateFieldGivenByItsBareNameTellsWhichParametersToUse()
    {
        Assert.Contains("year_min", Rejected(("year", "2024")), StringComparison.Ordinal);
        Assert.Contains("published_on_from", Rejected(("published_on", "\"2024-01-01\"")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("department", "[]")]
    [InlineData("department", "null")]
    [InlineData("department", "\"\"")]
    [InlineData("year_min", "null")]
    public void AFixedFilterThatIsEmptyIsAnErrorBecauseItWouldRestrictNothing(string name, string json)
    {
        var result = SearchFilterParser.Parse(Arguments((name, json)), Fields, Code, ignoreEmpty: false);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void TheCallerChoosesTheCodeOfTheError()
    {
        var result = SearchFilterParser.Parse(Arguments(("year_min", "\"x\"")), Fields, "knowledge_base.invalid_fixed_filter", ignoreEmpty: true);

        Assert.Equal("knowledge_base.invalid_fixed_filter", result.Error.Code);
    }
}
