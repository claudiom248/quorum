using System.Reflection;
using Microsoft.AspNetCore.Http.HttpResults;
using Quorum.Shared.Endpoints;

namespace Quorum.Host.Features.System;

internal sealed class VersionEndpoints : IEndpointGroup
{
    // Informational version is "<semver>+<commit>"; the commit is build metadata, not part of the version.
    private static readonly string Version =
        (typeof(VersionEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/version", GetVersion)
            .WithName("GetVersion")
            .WithTags("System");

    private static Ok<VersionResponse> GetVersion() => TypedResults.Ok(new VersionResponse(Version));
}

internal sealed record VersionResponse(string Version);
