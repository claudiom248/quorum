using Microsoft.AspNetCore.DataProtection;

namespace Quorum.Modules.Agents.Secrets;

internal sealed class ApiKeyProtector(IDataProtectionProvider provider)
{
    private const string Purpose = "Quorum.Agents.ModelEndpoint.ApiKey";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string? Protect(string? apiKey) => string.IsNullOrEmpty(apiKey) ? null : _protector.Protect(apiKey);

    public string? Unprotect(string? protectedApiKey) => protectedApiKey is null ? null : _protector.Unprotect(protectedApiKey);
}
