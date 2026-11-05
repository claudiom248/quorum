using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Quorum.Modules.Agents.Features;

/// <summary>An entity that blocks a delete or an edit, listed in the <c>references</c> extension of the 409 response (spec 013 D-16).</summary>
internal sealed record BlockingReference(string Kind, Guid Id, string Name)
{
    public const string AgentKind = "agent";

    public const string AssistantKind = "assistant";

    public const string SkillKind = "skill";

    /// <summary>The <c>409 Conflict</c> that names what is in the way, with a stable <paramref name="code"/> for the client.</summary>
    public static ProblemHttpResult Conflict(string code, string detail, IReadOnlyList<BlockingReference> references) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["references"] = references });
}
