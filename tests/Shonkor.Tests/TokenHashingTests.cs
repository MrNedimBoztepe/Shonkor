// Licensed to Shonkor under the MIT License.

using System.Linq;

using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// Verifies that API tokens are stored hashed (never plaintext, self-describing sha256: format) and
/// that authentication hashes the presented token before a constant-time comparison. Includes the
/// BUG-010 regression: a 64-hex-character plaintext token must be hashed, not shape-sniffed as a digest.
/// </summary>
public class TokenHashingTests
{
    private static ProjectManager NewProjectManager()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_tok_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        return new ProjectManager(ws);
    }

    [Fact]
    public void AddUser_StoresHash_NotPlaintext()
    {
        var pm = NewProjectManager();
        var org = new Organization { Name = "Acme" };
        pm.AddOrganization(org);

        const string token = "sk_supersecrettoken123456";
        pm.AddUser(new User { OrganizationId = org.Id, Username = "jdoe", ApiToken = token });

        var stored = pm.GetUsersByOrganization(org.Id).Single();
        Assert.NotEqual(token, stored.ApiToken);                           // not stored in plaintext
        Assert.True(TokenHasher.IsStoredHash(stored.ApiToken));            // self-describing format
        Assert.Equal(TokenHasher.HashForStorage(token), stored.ApiToken);  // exactly the hash
    }

    [Fact]
    public void GetUserByTokenConstantTime_MatchesPlaintext_RejectsWrongAndHash()
    {
        var pm = NewProjectManager();
        var org = new Organization { Name = "Acme" };
        pm.AddOrganization(org);

        const string token = "sk_correcttoken_abcdef";
        pm.AddUser(new User { OrganizationId = org.Id, Username = "jdoe", ApiToken = token });

        Assert.NotNull(pm.GetUserByTokenConstantTime(token));               // correct plaintext resolves
        Assert.Null(pm.GetUserByTokenConstantTime("sk_wrong"));             // wrong token rejected
        // Presenting the stored hash itself must NOT authenticate (a leaked hash is not a usable token).
        Assert.Null(pm.GetUserByTokenConstantTime(TokenHasher.Hash(token)));
        Assert.Null(pm.GetUserByTokenConstantTime(TokenHasher.HashForStorage(token)));
    }

    [Fact]
    public void TokenHasher_RoundTrip_Format_And_LegacyVerify()
    {
        const string plaintext = "sk_abc123";
        var stored = TokenHasher.HashForStorage(plaintext);

        Assert.StartsWith("sha256:", stored);
        Assert.True(TokenHasher.IsStoredHash(stored));
        Assert.False(TokenHasher.IsStoredHash(plaintext));
        Assert.True(TokenHasher.Verify(plaintext, stored));
        Assert.False(TokenHasher.Verify("other", stored));
        Assert.False(TokenHasher.Verify(plaintext, null));
        Assert.Equal(string.Empty, TokenHasher.HashForStorage(""));

        // Legacy bare-hex digests (old scheme) must keep verifying — including uppercase ones,
        // which the old byte-exact comparison never matched.
        var bare = TokenHasher.Hash(plaintext);
        Assert.True(TokenHasher.Verify(plaintext, bare));
        Assert.True(TokenHasher.Verify(plaintext, bare.ToUpperInvariant()));
    }

    [Fact]
    public void HashForStorage_HashesA64HexPlaintextToken_InsteadOfSniffingItAsDigest()
    {
        // BUG-010: the most common shape for a 32-byte API token — exactly 64 hex chars. The old
        // EnsureHashed passed it through as "already hashed": stored in PLAINTEXT and never verifying
        // (Verify hashes the presented token, and SHA256(token) != token).
        var hexPlaintext = new string('a', 64);

        var stored = TokenHasher.HashForStorage(hexPlaintext);

        Assert.DoesNotContain(hexPlaintext, stored);        // no plaintext at rest
        Assert.True(TokenHasher.Verify(hexPlaintext, stored)); // and the owner can authenticate
    }

    [Fact]
    public void MigrateStored_PrefixesLegacyDigests_HashesLegacyPlaintext_NormalizesCase()
    {
        var digest = TokenHasher.Hash("sk_x");

        // Legacy bare-hex digest → prefixed, still verifies.
        var migrated = TokenHasher.MigrateStored(digest);
        Assert.Equal("sha256:" + digest, migrated);
        Assert.True(TokenHasher.Verify("sk_x", migrated));

        // Uppercase stored digest → normalized so byte-exact storage comparisons keep working.
        Assert.Equal("sha256:" + digest, TokenHasher.MigrateStored(digest.ToUpperInvariant()));
        Assert.Equal("sha256:" + digest, TokenHasher.MigrateStored("SHA256:" + digest.ToUpperInvariant()));

        // Legacy plaintext (not 64-hex) → hashed.
        Assert.Equal(TokenHasher.HashForStorage("sk_plain"), TokenHasher.MigrateStored("sk_plain"));

        // Already-migrated values are stable (idempotent).
        Assert.Equal(migrated, TokenHasher.MigrateStored(migrated));
    }
}
