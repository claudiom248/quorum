using Microsoft.AspNetCore.DataProtection;

namespace Quorum.Modules.Agents.Secrets;

/// <summary>Encrypts a secret with the installation's master key. Each kind of secret has its own purpose, so one cannot be read as another.</summary>
internal abstract class SecretProtector(IDataProtectionProvider provider, string purpose)
{
    private readonly IDataProtector _protector = provider.CreateProtector(purpose);

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : _protector.Protect(secret);

    public string? Unprotect(string? protectedSecret) => protectedSecret is null ? null : _protector.Unprotect(protectedSecret);
}

internal sealed class ApiKeyProtector(IDataProtectionProvider provider)
    : SecretProtector(provider, "Quorum.Agents.ModelEndpoint.ApiKey");

/// <summary>Protects the static headers of an MCP server (D-21), stored as one JSON object.</summary>
internal sealed class McpHeadersProtector(IDataProtectionProvider provider)
    : SecretProtector(provider, "Quorum.Agents.McpServer.Headers");
