using Quorum.Modules.KnowledgeBase.Mapping;

namespace Quorum.Integration.Tests.KnowledgeBase.Mapping;

public sealed class Iso8601Tests
{
    [Theory]
    [InlineData("2024-01-15")]
    [InlineData("2024-01-15T10:30")]
    [InlineData("2024-01-15T10:30:45")]
    [InlineData("2024-01-15T10:30:45Z")]
    [InlineData("2024-01-15T10:30:45.123Z")]
    [InlineData("2024-01-15T10:30:45+02:00")]
    [InlineData("2024-01-15T10:30:45-0530")]
    [InlineData("2024-01-15 10:30:45")]
    [InlineData("2024-02-29")]
    public void AnIsoDateOrDateTimeIsAccepted(string value) => Assert.True(Iso8601.IsDateOrDateTime(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("15/01/2024")]
    [InlineData("January 15, 2024")]
    [InlineData("2024-1-5")]
    [InlineData("2024-13-01")]
    [InlineData("2023-02-29")]
    [InlineData("2024-01-15T25:00:00Z")]
    [InlineData("2024-01-15T")]
    [InlineData("1705312800")]
    [InlineData("2024-01-15\n")]
    public void AnythingElseIsRefused(string? value) => Assert.False(Iso8601.IsDateOrDateTime(value));
}
