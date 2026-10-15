using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Quorum.Modules.Agents.Contracts;
using Quorum.Modules.Tenancy.Setup;
using Quorum.Shared.Endpoints;
using Quorum.Shared.Results;

namespace Quorum.Modules.Tenancy.Features.Setup;

internal sealed class SetupEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var setup = app.MapGroup("/api/setup").WithTags("Setup");

        setup.MapGet("/status", GetStatus).WithName("GetSetupStatus");
        setup.MapPost("/verify-token", VerifyToken).WithName("VerifySetupToken");
        setup.MapPost("/model/test", TestModelConnection.HandleAsync)
            .WithName("TestSetupModelConnection")
            .AddEndpointFilter(new SetupTokenFilter(conflictWhenCompleted: false));
        setup.MapPost("/complete", CompleteSetup.HandleAsync)
            .WithName("CompleteSetup")
            .AddEndpointFilter(new SetupTokenFilter(conflictWhenCompleted: true));
    }

    // The wizard shows the default system prompt and context budget so the administrator can start from them (spec 001, AC-1b).
    private static Ok<SetupStatusResponse> GetStatus(SetupMode setupMode) =>
        TypedResults.Ok(new SetupStatusResponse(
            setupMode.IsActive,
            setupMode.IsActive ? AssistantDefaults.SystemPrompt : null,
            setupMode.IsActive ? AssistantDefaults.ContextTokenBudget : null));

    private static Results<NoContent, ProblemHttpResult> VerifyToken(VerifyTokenRequest request, SetupMode setupMode)
    {
        if (!setupMode.IsActive)
        {
            return SetupErrors.Completed.ToProblem();
        }

        return setupMode.Verify(request.Token) ? TypedResults.NoContent() : SetupErrors.InvalidToken.ToProblem();
    }
}

internal sealed record SetupStatusResponse(bool IsSetupMode, string? DefaultSystemPrompt, int? DefaultContextTokenBudget);

internal sealed record VerifyTokenRequest(string? Token);

internal static class SetupErrors
{
    public static Error InvalidToken { get; } =
        Error.Unauthorized("setup.invalid_token", "The setup token is missing or wrong. Copy it from the application logs.");

    public static Error Completed { get; } =
        Error.NotFound("setup.completed", "This installation is already set up.");

    public static Error AlreadyCompleted { get; } =
        Error.Conflict("setup.already_completed", "This installation is already set up.");
}

/// <summary>Setup endpoints exist only in setup mode and require the token in the <c>X-Setup-Token</c> header.</summary>
internal sealed class SetupTokenFilter(bool conflictWhenCompleted) : IEndpointFilter
{
    public const string HeaderName = "X-Setup-Token";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var setupMode = context.HttpContext.RequestServices.GetRequiredService<SetupMode>();

        if (!setupMode.IsActive)
        {
            return (conflictWhenCompleted ? SetupErrors.AlreadyCompleted : SetupErrors.Completed).ToProblem();
        }

        if (!setupMode.Verify(context.HttpContext.Request.Headers[HeaderName]))
        {
            return SetupErrors.InvalidToken.ToProblem();
        }

        return await next(context);
    }
}
