using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Quorum.Modules.KnowledgeBases.Persistence;

/// <summary>
/// Encrypts the API key of a knowledge base's store with the installation's master key. It has its own purpose, so a key stored
/// by another module cannot be read as one of these (D-6).
/// </summary>
internal sealed class KnowledgeBaseSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Quorum.KnowledgeBase.Connection");

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : _protector.Protect(secret);

    /// <summary>Whether the stored key could be read: it cannot when the installation's key ring was replaced or lost.</summary>
    public bool TryUnprotect(string? protectedSecret, out string? secret)
    {
        secret = null;
        if (protectedSecret is null)
        {
            return true;
        }

        try
        {
            secret = _protector.Unprotect(protectedSecret);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
