using Microsoft.AspNetCore.DataProtection;

namespace Quorum.Modules.Models.Secrets;

/// <summary>
/// Encrypts the API key of a model. The purpose string is the one the model endpoints used before the Models module existed
/// (spec 019 D-17), so keys stored before the move still decrypt.
/// </summary>
internal sealed class ApiKeyProtector(IDataProtectionProvider provider)
{
    public const string Purpose = "Quorum.Agents.ModelEndpoint.ApiKey";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : _protector.Protect(secret);

    public string? Unprotect(string? protectedSecret) => protectedSecret is null ? null : _protector.Unprotect(protectedSecret);
}
