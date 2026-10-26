using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Quorum.Shared.Endpoints;

namespace Quorum.Integration.Tests.Shared;

public sealed class ListQueryTests
{
    private static QueryCollection Query(params (string Key, string Value)[] pairs) =>
        new(pairs.ToDictionary(p => p.Key, p => new StringValues(p.Value)));

    [Fact]
    public void NothingGivenMeansNoFilterAndTheDefaultLimit()
    {
        var result = ListQuery.Parse(Query());

        Assert.True(result.IsSuccess);
        Assert.Equal(ListQuery.DefaultLimit, result.Value.Limit);
        Assert.Empty(result.Value.Scope);
        Assert.Null(result.Value.Q);
        Assert.Null(result.Value.Cursor);
    }

    [Fact]
    public void ValuesMayBeCommaSeparatedAndAreDeduplicated()
    {
        var tenant = Guid.NewGuid();

        var result = ListQuery.Parse(Query(("scope", "global, Tenant,global"), ("tenant", tenant.ToString()), ("q", "  year ")));

        Assert.Equal(["global", "Tenant"], result.Value.Scope);
        Assert.Equal([tenant], result.Value.Tenant);
        Assert.Equal("year", result.Value.Q);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("-3")]
    [InlineData("ten")]
    public void ALimitOutOfRangeFails(string limit)
    {
        var result = ListQuery.Parse(Query(("limit", limit)));

        Assert.Equal("list.invalid_limit", result.Error.Code);
    }

    [Fact]
    public void ASearchTextLongerThanTheBoundFails()
    {
        var result = ListQuery.Parse(Query(("q", new string('a', ListQuery.MaxSearchLength + 1))));

        Assert.Equal("list.invalid_filter", result.Error.Code);
        Assert.True(ListQuery.Parse(Query(("q", new string('a', ListQuery.MaxSearchLength)))).IsSuccess);
    }

    [Fact]
    public void TheLimitBoundsAreAccepted()
    {
        Assert.Equal(1, ListQuery.Parse(Query(("limit", "1"))).Value.Limit);
        Assert.Equal(ListQuery.MaxLimit, ListQuery.Parse(Query(("limit", "200"))).Value.Limit);
    }

    [Fact]
    public void ATenantThatIsNotAnIdFails()
    {
        Assert.Equal("list.invalid_filter", ListQuery.Parse(Query(("tenant", "acme"))).Error.Code);
        Assert.Equal("list.invalid_filter", ListQuery.Parse(Query(("assistant", "acme"))).Error.Code);
    }

    [Fact]
    public void ACursorRoundTripsForItsOwnList()
    {
        var id = Guid.NewGuid();

        var cursor = ListCursor.Encode("dictionary", "year", id);
        var decoded = ListCursor.Decode("dictionary", cursor);

        Assert.Equal(("year", id), decoded.Value);
    }

    [Fact]
    public void NoCursorIsTheFirstPage()
    {
        Assert.Null(ListCursor.Decode("dictionary", null).Value);
    }

    [Fact]
    public void ACursorOfAnotherListIsRejected()
    {
        var cursor = ListCursor.Encode("knowledge-bases", "year", Guid.NewGuid());

        Assert.Equal("list.invalid_cursor", ListCursor.Decode("dictionary", cursor).Error.Code);
    }

    [Theory]
    [InlineData("not a cursor")]
    [InlineData("e30")]
    [InlineData("bm90LWpzb24")]
    public void AMalformedCursorIsRejected(string cursor)
    {
        Assert.Equal("list.invalid_cursor", ListCursor.Decode("dictionary", cursor).Error.Code);
    }
}
