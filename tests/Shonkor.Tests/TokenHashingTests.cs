// Licensed to Shonkor under the MIT License.

using System.Linq;

using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// Verifies that API tokens are stored hashed (never plaintext) and that authentication hashes the
/// presented token before a constant-time comparison.
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
        Assert.NotEqual(token, stored.ApiToken);                 // not stored in plaintext
        Assert.Equal(64, stored.ApiToken.Length);                // SHA-256 hex digest
        Assert.Equal(TokenHasher.Hash(token), stored.ApiToken);  // exactly the hash
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
    }

    [Fact]
    public void TokenHasher_RoundTrip_And_Idempotency()
    {
        const string plaintext = "sk_abc123";
        var hash = TokenHasher.Hash(plaintext);

        Assert.True(TokenHasher.LooksHashed(hash));
        Assert.False(TokenHasher.LooksHashed(plaintext));
        Assert.True(TokenHasher.Verify(plaintext, hash));
        Assert.False(TokenHasher.Verify("other", hash));
        Assert.False(TokenHasher.Verify(plaintext, null));

        Assert.Equal(hash, TokenHasher.EnsureHashed(plaintext)); // hashes plaintext
        Assert.Equal(hash, TokenHasher.EnsureHashed(hash));      // idempotent on an existing hash
        Assert.Equal(string.Empty, TokenHasher.EnsureHashed(""));
    }
}
