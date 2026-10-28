using System.Security.Cryptography;
using System.Text;

namespace Quorum.Modules.Tenancy.Domain;

internal sealed class ServiceToken
{
    private const int TokenByteLength = 32;

    private ServiceToken()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Creates a token for the tenant; the plaintext is returned once and never stored.</summary>
    public static (ServiceToken Token, string PlainText) Create(Guid tenantId, DateTimeOffset now)
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        var encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var plainText = $"svc_{encoded}";

        return (new ServiceToken
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainText))),
            CreatedAt = now,
        }, plainText);
    }

    public static string HashToken(string plainText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainText)));

    public bool Matches(string plainText) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(TokenHash),
            SHA256.HashData(Encoding.UTF8.GetBytes(plainText)));
}
