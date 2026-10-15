using System.Security.Cryptography;
using System.Text;

namespace Quorum.Modules.Tenancy.Setup;

/// <summary>
/// Whether the installation still needs its first-run wizard, and the setup token that guards it.
/// The token lives only in memory: a restart issues a new one.
/// </summary>
internal sealed class SetupMode
{
    // No 0/O or 1/I, so a token copied by hand from the logs is not ambiguous.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int GroupCount = 5;
    private const int GroupLength = 4;

    private volatile string? _token;

    public bool IsActive => _token is not null;

    public string? Token => _token;

    public string Activate()
    {
        var characters = RandomNumberGenerator.GetItems<char>(Alphabet, GroupCount * GroupLength);
        _token = string.Join('-', characters.Chunk(GroupLength).Select(group => new string(group)));
        return _token;
    }

    public bool Verify(string? candidate)
    {
        var token = _token;
        if (token is null || candidate is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Normalize(token)),
            Encoding.ASCII.GetBytes(Normalize(candidate)));
    }

    public void Complete() => _token = null;

    private static string Normalize(string token) =>
        new(token.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());
}
