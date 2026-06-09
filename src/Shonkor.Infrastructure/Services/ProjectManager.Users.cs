// Licensed to Shonkor under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Organization and user management for the multi-tenant SaaS mode. Implemented as a partial of
/// <see cref="ProjectManager"/> because organizations, users and projects are persisted together in
/// the single <c>projects.json</c> registry and share the same in-memory state and lock.
/// </summary>
public partial class ProjectManager
{
    public List<Organization> GetOrganizations()
    {
        lock (_lock)
        {
            return _organizations.ToList();
        }
    }

    public Organization? GetOrganization(string id)
    {
        lock (_lock)
        {
            return _organizations.FirstOrDefault(o => o.Id == id);
        }
    }

    public void AddOrganization(Organization org)
    {
        lock (_lock)
        {
            _organizations.Add(org);
            SaveProjects();
        }
    }

    public List<User> GetUsersByOrganization(string orgId)
    {
        lock (_lock)
        {
            return _users.Where(u => u.OrganizationId == orgId).ToList();
        }
    }

    /// <summary>
    /// Looks up a user by their (plaintext) API token. Tokens are stored hashed, so the presented
    /// token is hashed and compared constant-time. Use <see cref="GetUserByTokenConstantTime"/> in
    /// auth-critical paths (identical here, kept for call-site clarity).
    /// </summary>
    public User? GetUserByToken(string token) => GetUserByTokenConstantTime(token);

    /// <summary>
    /// Looks up a user by their presented (plaintext) API token using a constant-time hash comparison.
    /// Use this for every authentication code path.
    /// </summary>
    public User? GetUserByTokenConstantTime(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        lock (_lock)
        {
            foreach (var user in _users)
            {
                if (TokenHasher.Verify(token, user.ApiToken))
                {
                    return user;
                }
            }
        }

        return null;
    }

    public void AddUser(User user)
    {
        lock (_lock)
        {
            // Never store the plaintext token — persist only its hash.
            user.ApiToken = TokenHasher.EnsureHashed(user.ApiToken);
            _users.Add(user);
            SaveProjects();
        }
    }

    public bool DeleteUser(string id)
    {
        lock (_lock)
        {
            var user = _users.FirstOrDefault(u => u.Id == id);
            if (user != null)
            {
                _users.Remove(user);
                SaveProjects();
                return true;
            }
            return false;
        }
    }
}
