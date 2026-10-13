using System.Text;

namespace Blazecoin.ServiceDefaults.Security;

/// <summary>
/// ASVS A2 fail-fast for the JWT signing key. HS256 requires at least a 256-bit (32-byte) key; a null,
/// empty, or short key is a security hole (guessable/forgeable tokens), so a service refuses to start
/// rather than sign or validate with a weak or default secret. A pure function so the fail-fast contract
/// is deterministically unit-testable (a host-boot test cannot reliably clear the process-wide
/// <c>Jwt__Key</c> environment variable that other tests depend on).
/// </summary>
public static class JwtKeyGuard
{
    /// <summary>Minimum key length in bytes (256 bits) for HS256.</summary>
    public const int MinBytes = 32;

    /// <summary>Returns the key when it is present and long enough; otherwise throws
    /// <see cref="InvalidOperationException"/> to abort startup.</summary>
    public static string Require(string? jwtKey)
    {
        if (string.IsNullOrWhiteSpace(jwtKey) || Encoding.UTF8.GetByteCount(jwtKey) < MinBytes)
        {
            throw new InvalidOperationException(
                "Jwt:Key is missing or too short. Configure a random secret of at least 32 bytes (256 bits) for HS256.");
        }
        return jwtKey;
    }
}
