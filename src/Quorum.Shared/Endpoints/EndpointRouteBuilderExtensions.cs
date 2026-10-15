using System.Reflection;
using Microsoft.AspNetCore.Routing;

namespace Quorum.Shared.Endpoints;

public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps every <see cref="IEndpointGroup"/> found in the given assemblies.
    /// </summary>
    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder app, params IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(app);

        var groups = assemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(IEndpointGroup)))
            .Select(type => (IEndpointGroup)Activator.CreateInstance(type, nonPublic: true)!);

        foreach (var group in groups)
        {
            group.Map(app);
        }

        return app;
    }
}
