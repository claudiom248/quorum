using Microsoft.AspNetCore.Routing;

namespace Quorum.Shared.Endpoints;

/// <summary>
/// A feature's endpoints. Implementations need a parameterless constructor and are discovered by
/// <see cref="EndpointRouteBuilderExtensions.MapEndpoints"/>.
/// </summary>
public interface IEndpointGroup
{
    void Map(IEndpointRouteBuilder app);
}
