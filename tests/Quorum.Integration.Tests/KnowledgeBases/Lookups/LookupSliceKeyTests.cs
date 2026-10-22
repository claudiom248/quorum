using Quorum.Modules.KnowledgeBases.Domain;
using Quorum.Modules.KnowledgeBases.Features.Lookups;
using Quorum.Modules.KnowledgeBases.Features.Search;

namespace Quorum.Integration.Tests.KnowledgeBases.Lookups;

public sealed class LookupSliceKeyTests
{
    private static readonly FilterField Department = new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "department", DictionaryFieldType.Text);
    private static readonly FilterField Year = new(Guid.Parse("00000000-0000-0000-0000-000000000002"), "year", DictionaryFieldType.Number);
    private static readonly FilterField Published = new(Guid.Parse("00000000-0000-0000-0000-000000000003"), "published_on", DictionaryFieldType.Date);
    private static readonly FilterField Confidential = new(Guid.Parse("00000000-0000-0000-0000-000000000004"), "confidential", DictionaryFieldType.Boolean);

    [Fact] // AC-21
    public void FiltersInAnyOrderGiveTheSameKey()
    {
        var first = LookupSliceKey.From([new AnyOf(Department, ["hr"]), new NumberRange(Year, 2020, null), new IsBoolean(Confidential, true)]);
        var second = LookupSliceKey.From([new IsBoolean(Confidential, true), new AnyOf(Department, ["hr"]), new NumberRange(Year, 2020, null)]);

        Assert.Equal(first, second);
    }

    [Fact] // AC-21
    public void TheValuesOfATextFilterAreSortedAndCountedOnce()
    {
        var first = LookupSliceKey.From([new AnyOf(Department, ["legal", "hr", "hr"])]);
        var second = LookupSliceKey.From([new AnyOf(Department, ["hr", "legal"])]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ValuesAreComparedAsTheyAreWithoutFoldingCase()
    {
        Assert.NotEqual(LookupSliceKey.From([new AnyOf(Department, ["hr"])]).Hash, LookupSliceKey.From([new AnyOf(Department, ["HR"])]).Hash);
    }

    [Fact]
    public void FiltersThatMeanDifferentThingsGiveDifferentKeys()
    {
        var keys = new[]
        {
            LookupSliceKey.WholeKnowledgeBase,
            LookupSliceKey.From([new AnyOf(Department, ["hr"])]),
            LookupSliceKey.From([new AnyOf(Department, ["hr", "legal"])]),
            LookupSliceKey.From([new IsBoolean(Confidential, true)]),
            LookupSliceKey.From([new IsBoolean(Confidential, false)]),
            LookupSliceKey.From([new NumberRange(Year, 2020, null)]),
            LookupSliceKey.From([new NumberRange(Year, null, 2020)]),
        };

        Assert.Equal(keys.Length, keys.Select(k => k.Hash).Distinct().Count());
    }

    [Fact]
    public void TheSameFilterOnAnotherFieldIsAnotherSlice()
    {
        var other = new FilterField(Guid.Parse("00000000-0000-0000-0000-000000000009"), "team", DictionaryFieldType.Text);

        Assert.NotEqual(LookupSliceKey.From([new AnyOf(Department, ["hr"])]).Hash, LookupSliceKey.From([new AnyOf(other, ["hr"])]).Hash);
    }

    [Fact]
    public void NumbersAndInstantsHaveOneSpelling()
    {
        var plus = new DateTimeOffset(2024, 1, 31, 1, 0, 0, TimeSpan.FromHours(1));
        var utc = new DateTimeOffset(2024, 1, 31, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(LookupSliceKey.From([new NumberRange(Year, 2020.0, null)]), LookupSliceKey.From([new NumberRange(Year, 2020, null)]));
        Assert.Equal(LookupSliceKey.From([new DateRange(Published, plus, null)]), LookupSliceKey.From([new DateRange(Published, utc, null)]));
    }

    [Fact]
    public void NoFiltersIsTheWholeKnowledgeBaseAndHasAStableKey()
    {
        var key = LookupSliceKey.From([]);

        Assert.Equal(LookupSliceKey.WholeKnowledgeBase, key);
        Assert.Equal("[]", key.Filters);
        Assert.Equal(LookupSlice.FilterHashLength, key.Hash.Length);
        // The hash is stored: it must not change between releases.
        Assert.Equal("4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945", key.Hash);
    }

    [Fact]
    public void TheCanonicalFiltersAreReadableJsonByDictionaryFieldId()
    {
        var key = LookupSliceKey.From([new AnyOf(Department, ["legal", "hr"])]);

        Assert.Equal("""[{"field":"00000000-0000-0000-0000-000000000001","anyOf":["hr","legal"]}]""", key.Filters);
    }
}
