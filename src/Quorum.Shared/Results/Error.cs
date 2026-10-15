namespace Quorum.Shared.Results;

public enum ErrorType
{
    Validation,
    Unauthorized,
    NotFound,
    Conflict,
    Unprocessable,
    Locked,
}

/// <summary>
/// An expected failure of an operation. <see cref="Code"/> is stable and machine-readable; <see cref="Description"/> is for humans.
/// </summary>
public sealed record Error(string Code, string Description, ErrorType Type)
{
    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);

    public static Error Unauthorized(string code, string description) => new(code, description, ErrorType.Unauthorized);

    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    public static Error Unprocessable(string code, string description) => new(code, description, ErrorType.Unprocessable);

    public static Error Locked(string code, string description) => new(code, description, ErrorType.Locked);
}
