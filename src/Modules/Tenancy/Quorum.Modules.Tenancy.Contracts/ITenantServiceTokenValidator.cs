namespace Quorum.Modules.Tenancy.Contracts;

/// <summary>Validates a service token's SHA-256 hash against the stored tokens and returns the owning tenant.</summary>
public interface ITenantServiceTokenValidator
{
    Task<Guid?> ValidateAsync(string tokenHash, CancellationToken cancellationToken = default);
}
