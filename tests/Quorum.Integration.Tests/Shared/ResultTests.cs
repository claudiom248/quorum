using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Quorum.Shared.Results;

namespace Quorum.Integration.Tests.Shared;

public sealed class ResultTests
{
    [Fact]
    public void SuccessCarriesTheValue()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void FailureCarriesTheError()
    {
        Result<int> result = Error.NotFound("conversation.not_found", "Conversation not found.");

        Assert.False(result.IsSuccess);
        Assert.Equal("conversation.not_found", result.Error.Code);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Theory]
    [InlineData(ErrorType.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorType.Unauthorized, StatusCodes.Status401Unauthorized)]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorType.Unprocessable, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(ErrorType.Locked, StatusCodes.Status423Locked)]
    public void ErrorsMapToProblemDetailsStatusCodes(ErrorType type, int expectedStatus)
    {
        var problem = new Error("code", "description", type).ToProblem();

        var result = Assert.IsType<ProblemHttpResult>(problem);
        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Equal("code", result.ProblemDetails.Extensions["code"]);
    }
}
