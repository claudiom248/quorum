using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Quorum.Shared.Results;

public static class ErrorExtensions
{
    /// <summary>
    /// Maps an expected failure to a ProblemDetails response; the error code travels in the <c>code</c> extension.
    /// </summary>
    public static ProblemHttpResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var status = error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Unprocessable => StatusCodes.Status422UnprocessableEntity,
            ErrorType.Locked => StatusCodes.Status423Locked,
            ErrorType.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
            ErrorType.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
            _ => throw new ArgumentOutOfRangeException(nameof(error), error.Type, "Unknown error type."),
        };

        return TypedResults.Problem(
            detail: error.Description,
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
