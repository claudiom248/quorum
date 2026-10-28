namespace Quorum.Modules.Tenancy.Contracts;

/// <summary>Validates a service token and returns the owning tenant, or null if the token is not valid.</summary>
public interface ITenantServiceTokenValidator
{
    Task<Guid?> ValidateAsync(string token, CancellationToken cancellationToken = default);
}
