// Licensed to Shonkor under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Hashes API tokens for storage so the registry (<c>projects.json</c>) never holds plaintext secrets.
/// API tokens are high-entropy random strings, so a single SHA-256 (not a slow password hash) is the
/// appropriate, fast choice — comparison stays constant-time via <see cref="CryptographicOperations"/>.
/// <para>
/// Stored values are SELF-DESCRIBING: <c>sha256:&lt;64 lowercase hex&gt;</c>. The earlier scheme stored the
/// bare hex digest and shape-sniffed ("64 hex chars = already hashed"), which misclassified a
/// caller-supplied 64-hex-character plaintext token — the most common shape for a 32-byte token — as a
/// digest: stored in plaintext AND permanently failing verification. Never sniff; the prefix says what it is.
/// </para>
/// </summary>
public static class TokenHasher
{
    private const string Prefix = "sha256:";

    /// <summary>Returns the lowercase hex SHA-256 of <paramref name="token"/> (64 chars, no prefix).</summary>
    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Hashes a caller-supplied PLAINTEXT token into the self-describing storage format. Always hashes —
    /// use this at every write site for new/updated tokens, regardless of what the input looks like.
    /// </summary>
    public static string HashForStorage(string? token) =>
        string.IsNullOrEmpty(token) ? string.Empty : Prefix + Hash(token);

    /// <summary>True when <paramref name="value"/> is already in the self-describing stored format.</summary>
    public static bool IsStoredHash(string? value)
    {
        if (value is null || value.Length != Prefix.Length + 64) return false;
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;
        for (var i = Prefix.Length; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// One-time migration of a value ALREADY STORED in the registry to the current format:
    /// current-format values are normalized (lowercase hex); a bare 64-hex value is a digest written by
    /// the earlier scheme and gets the prefix; anything else is a legacy plaintext token and is hashed.
    /// Only for load-time migration of stored values — new input goes through <see cref="HashForStorage"/>.
    /// </summary>
    public static string MigrateStored(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (IsStoredHash(value)) return Prefix + value[Prefix.Length..].ToLowerInvariant();
        if (value is { Length: 64 } && value.All(Uri.IsHexDigit)) return Prefix + value.ToLowerInvariant();
        return HashForStorage(value);
    }

    /// <summary>
    /// Constant-time comparison of a presented plaintext token against a stored hash. Accepts the
    /// current <c>sha256:</c>-prefixed format and the legacy bare-hex format; hex casing is normalized.
    /// </summary>
    public static bool Verify(string presentedToken, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(presentedToken)) return false;
        var stored = storedHash.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? storedHash[Prefix.Length..]
            : storedHash;
        var presentedHash = Encoding.ASCII.GetBytes(Hash(presentedToken));
        var storedBytes = Encoding.ASCII.GetBytes(stored.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(presentedHash, storedBytes);
    }
}
