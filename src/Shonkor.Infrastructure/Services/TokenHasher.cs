// Licensed to Shonkor under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Hashes API tokens for storage so the registry (<c>projects.json</c>) never holds plaintext secrets.
/// API tokens are high-entropy random strings, so a single SHA-256 (not a slow password hash) is the
/// appropriate, fast choice — comparison stays constant-time via <see cref="CryptographicOperations"/>.
/// </summary>
public static class TokenHasher
{
    /// <summary>Returns the lowercase hex SHA-256 of <paramref name="token"/> (64 chars).</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>True when <paramref name="value"/> already looks like a SHA-256 hex digest.</summary>
    public static bool LooksHashed(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>Hashes <paramref name="value"/> unless it is empty or already hashed (idempotent migration helper).</summary>
    public static string EnsureHashed(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : (LooksHashed(value) ? value : Hash(value));

    /// <summary>Constant-time comparison of a presented plaintext token against a stored hash.</summary>
    public static bool Verify(string presentedToken, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(presentedToken)) return false;
        var presentedHash = Encoding.ASCII.GetBytes(Hash(presentedToken));
        var stored = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(presentedHash, stored);
    }
}
