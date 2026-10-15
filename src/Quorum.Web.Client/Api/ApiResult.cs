using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Quorum.Web.Client.Api;

/// <summary>A ProblemDetails response, reduced to what the UI shows.</summary>
public sealed record ApiProblem(int Status, string? Code, string Message, IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsUnauthorized => Status == (int)HttpStatusCode.Unauthorized;

    /// <summary>The field errors of a validation problem, or the general message.</summary>
    public IEnumerable<string> Messages => Errors.Count > 0 ? Errors.Values.SelectMany(m => m) : [Message];

    public static ApiProblem Unreachable { get; } =
        new(0, "client.unreachable", "Quorum is not reachable. Check your connection and try again.", new Dictionary<string, string[]>());

    public static async Task<ApiProblem> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ProblemDocument? document = null;
        try
        {
            document = await response.Content.ReadFromJsonAsync<ProblemDocument>(cancellationToken);
        }
        catch (JsonException)
        {
            // Not a ProblemDetails body (e.g. a proxy error page): fall back to the status code.
        }

        var errors = document?.Errors ?? [];
        var message = document?.Detail ?? document?.Title ?? $"The request failed ({(int)response.StatusCode} {response.ReasonPhrase}).";
        return new ApiProblem((int)response.StatusCode, document?.Code, message, errors);
    }

    private sealed record ProblemDocument(string? Title, string? Detail, string? Code, Dictionary<string, string[]>? Errors);
}

public class ApiResult
{
    protected ApiResult(ApiProblem? problem) => Problem = problem;

    public ApiProblem? Problem { get; }

    public bool IsSuccess => Problem is null;

    public static ApiResult Success { get; } = new(null);

    public static ApiResult Failure(ApiProblem problem) => new(problem);

    public static ApiResult<T> Ok<T>(T value) => new(value, null);

    public static ApiResult<T> Fail<T>(ApiProblem problem) => new(default, problem);
}

public sealed class ApiResult<T> : ApiResult
{
    private readonly T? _value;

    internal ApiResult(T? value, ApiProblem? problem)
        : base(problem) => _value = value;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("The request failed: there is no value.");
}
