using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Search;

namespace Quorum.Integration.Tests.KnowledgeBases.Search;

/// <summary>
/// Which field a fixed filter lands on (spec 003 A1-4). A fixed filter carves the slice of data a tool may return, so a name must
/// never resolve to a field the administrator did not mean.
/// </summary>
public sealed class FixedFilterNamesTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static DictionaryField Field(string name, string? cluster = null, DictionaryFieldType type = DictionaryFieldType.Text) =>
        DictionaryField.Create(Tenant, name, type, description: null, DictionaryFieldScope.Tenant, DateTimeOffset.UtcNow, cluster);

    [Fact]
    public void AnAliasIsWhatTheFilterIsKnownByAndTheFieldsOwnNameIsNot()
    {
        var hr = Field("year", "hr");

        var names = FixedFilterNames.Build(new Dictionary<string, DictionaryField> { ["year_hr"] = hr }, []);

        Assert.Equal(hr.Id, names.Fields["year_hr"].Id);
        Assert.DoesNotContain("year", names.Fields.Keys);
    }

    [Fact]
    public void AnAliasWinsOverAnotherFieldThatHappensToBeCalledTheSame()
    {
        var aliased = Field("region", "sales");
        var other = Field("year", "finance");

        var names = FixedFilterNames.Build(new Dictionary<string, DictionaryField> { ["year"] = aliased }, [other]);

        Assert.Equal(aliased.Id, names.Fields["year"].Id);
        Assert.Null(names.AmbiguityProblem(["year"]));
    }

    [Fact]
    public void AFieldNoOneElseIsCalledLikeIsKnownByItsOwnName()
    {
        var department = Field("department", "hr");

        var names = FixedFilterNames.Build(new Dictionary<string, DictionaryField>(), [department]);

        Assert.Equal(department.Id, names.Fields["department"].Id);
    }

    [Fact]
    public void ANameSeveralClustersHaveResolvesToNoneAndIsReportedWithTheirClusters()
    {
        var names = FixedFilterNames.Build(new Dictionary<string, DictionaryField>(), [Field("year", "hr"), Field("year", "finance"), Field("department", "hr")]);

        Assert.DoesNotContain("year", names.Fields.Keys);
        Assert.Contains("department", names.Fields.Keys);
        var problem = names.AmbiguityProblem(["year"]);
        Assert.Contains("several clusters (finance, hr)", problem, StringComparison.Ordinal);
        Assert.Contains("alias", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("year_min")]
    [InlineData("year_max")]
    [InlineData("year_from")]
    [InlineData("year_to")]
    public void TheSuffixedNamesOfAnAmbiguousFieldAreAmbiguousToo(string filter)
    {
        var names = FixedFilterNames.Build(new Dictionary<string, DictionaryField>(), [Field("year", "hr", DictionaryFieldType.Number), Field("year", "finance", DictionaryFieldType.Number)]);

        Assert.Contains("'year'", names.AmbiguityProblem([filter]), StringComparison.Ordinal);
    }

    [Fact]
    public void ExposingOneOfTheTwoUnderAnAliasLeavesTheOtherUnambiguous()
    {
        var hr = Field("year", "hr");
        var finance = Field("year", "finance", DictionaryFieldType.Number);

        var names = FixedFilterNames.Build(new Dictionary<string, DictionaryField> { ["year_hr"] = hr }, [hr, finance]);

        Assert.Equal(finance.Id, names.Fields["year"].Id);
        Assert.Equal(hr.Id, names.Fields["year_hr"].Id);
        Assert.Null(names.AmbiguityProblem(["year_min", "year_hr"]));
    }

    [Fact]
    public void AFilterThatNamesNothingAmbiguousIsNotReported()
    {
        var names = FixedFilterNames.Build(new Dictionary<string, DictionaryField>(), [Field("year", "hr"), Field("year", "finance")]);

        Assert.Null(names.AmbiguityProblem(["department", "unknown_min"]));
    }

    [Fact] // the one place the test-search filter names are decided
    public void TheFilterNameOfAMappedFieldIsItsNameUnlessTwoMappedFieldsShareOne()
    {
        var hrYear = Field("year", "hr");
        var financeYear = Field("year", "finance", DictionaryFieldType.Number);
        var department = Field("department", "hr");
        var plainYear = Field("year");

        var names = TestSearchNames.ForMapped([hrYear, financeYear, department]);
        var withNoCluster = TestSearchNames.ForMapped([plainYear, financeYear]);

        Assert.Equal("hr_year", names[hrYear.Id]);
        Assert.Equal("finance_year", names[financeYear.Id]);
        Assert.Equal("department", names[department.Id]);
        Assert.Equal("year", withNoCluster[plainYear.Id]);
        Assert.Equal("finance_year", withNoCluster[financeYear.Id]);
    }
}
