using System.Collections.Concurrent;
using System.Text.Json;
using Shonkor.Core.Interfaces;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Infrastructure.Services;

public class Organization
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
}

public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string OrganizationId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string GitHubUsername { get; set; } = string.Empty; // For future GitHub SSO integration
}

public class Project
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    
    // Legacy field for backward compatibility during deserialization
    public string ApiKey { get; set; } = string.Empty;
}

public record ActiveProjectRequest(string Name);

public class WebConfig
{
    public string DatabasePath { get; set; } = "shonkor.db";
    public List<string> ExcludePatterns { get; set; } = new()
    {
        "**/bin/**",
        "**/obj/**",
        "**/.git/**",
        "**/.vs/**",
        "**/.idea/**",
        "**/node_modules/**",
        "**/*.db",
        "**/*.log",
        "**/*.log.*"
    };
}

/// <summary>
/// Owns the multi-project registry (<c>projects.json</c>): the list of projects, the active project,
/// per-project storage providers (cached + lifecycle), and index-scan locking. Organization/user
/// management lives in the <c>ProjectManager.Users.cs</c> partial; they share this type's registry
/// state and persistence.
/// </summary>
public partial class ProjectManager
{
    private readonly string _projectsFilePath;
    // Lazy<> guarantees the provider factory (which initializes the schema) runs exactly once
    // per project even under concurrent access, avoiding duplicate connections/initializations.
    private readonly ConcurrentDictionary<string, Lazy<SqliteGraphStorageProvider>> _providers = new();
    // Tracks projects with an in-flight index scan so concurrent scans don't duplicate work or race.
    private readonly ConcurrentDictionary<string, byte> _activeScans = new();
    private readonly object _lock = new();
    private List<Organization> _organizations = new();
    private List<User> _users = new();
    private List<Project> _projects = new();
    private string _activeProjectName = string.Empty;
    private DateTime _lastLoadTime = DateTime.MinValue;

    public string WorkspacePath { get; private set; }

    public ProjectManager(string currentWorkspace)
    {
        WorkspacePath = currentWorkspace;
        _projectsFilePath = Path.Combine(currentWorkspace, "projects.json");
        LoadProjects(currentWorkspace);
        StandardPluginsInstaller.Install(WorkspacePath);
    }

    private void EnsureUpToDate()
    {
        // Read the file timestamp OUTSIDE the lock — File.GetLastWriteTimeUtc is a cheap kernel
        // call but still I/O, and holding _lock during I/O serializes all concurrent readers.
        // We only enter the lock (and potentially do a full reload) when the timestamp has changed.
        if (!File.Exists(_projectsFilePath)) return;

        var currentWriteTime = File.GetLastWriteTimeUtc(_projectsFilePath);

        bool needsReload;
        lock (_lock)
        {
            needsReload = currentWriteTime > _lastLoadTime;
        }

        if (needsReload)
        {
            // LoadProjects acquires _lock internally for writes, so we don't hold it here.
            // A second concurrent caller might also enter here and call LoadProjects; that is safe
            // because LoadProjects is idempotent and the final _lastLoadTime write is under _lock.
            LoadProjects(WorkspacePath);
        }
    }

    public List<Project> GetProjects()
    {
        EnsureUpToDate(); // I/O outside the lock; acquires lock internally only for the swap
        lock (_lock)
        {
            return _projects.ToList();
        }
    }

    public string GetActiveProjectName()
    {
        EnsureUpToDate();
        lock (_lock)
        {
            return _activeProjectName;
        }
    }

    public Project? GetActiveProject()
    {
        EnsureUpToDate();
        lock (_lock)
        {
            return _projects.FirstOrDefault(p => p.Name.Equals(_activeProjectName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void SetActiveProject(string name)
    {
        lock (_lock)
        {
            var project = _projects.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (project != null)
            {
                _activeProjectName = project.Name;
                SaveProjects();
            }
            else
            {
                throw new ArgumentException($"Project '{name}' not found.");
            }
        }
    }

    public void AddProject(string name, string path, string dbPath, string apiKey = "")
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Name and Path are required.");
            }

            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"Directory does not exist: {path}");
            }

            var resolvedDbPath = string.IsNullOrWhiteSpace(dbPath) 
                ? Path.Combine(path, "shonkor.db") 
                : (Path.IsPathRooted(dbPath) ? dbPath : Path.GetFullPath(Path.Combine(path, dbPath)));

            // Remove existing project with same name
            _projects.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            _projects.Add(new Project
            {
                Name = name,
                Path = path,
                DatabasePath = resolvedDbPath,
                ApiKey = string.IsNullOrWhiteSpace(apiKey) ? "sk-" + Guid.NewGuid().ToString("N") : apiKey
            });

            if (string.IsNullOrEmpty(_activeProjectName))
            {
                _activeProjectName = name;
            }

            SaveProjects();
        }
    }

    public void DeleteProject(string name)
    {
        lock (_lock)
        {
            _projects.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            
            if (_activeProjectName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                _activeProjectName = _projects.FirstOrDefault()?.Name ?? string.Empty;
            }

            if (_providers.TryRemove(name.ToLowerInvariant(), out var provider) && provider.IsValueCreated)
            {
                provider.Value.Dispose();
            }

            SaveProjects();
        }
    }

    /// <summary>
    /// Returns an initialized storage provider for the given project (or the active project when
    /// <paramref name="projectName"/> is null/empty), without blocking the calling thread during
    /// first-time schema initialization. Providers are cached per project, so only the first call
    /// per project performs I/O. Used in all contexts (ASP.NET Core endpoints, MCP handlers).
    /// </summary>
    public async Task<IGraphStorageProvider> GetStorageProviderAsync(string? projectName, CancellationToken cancellationToken = default)
    {
        var project = ResolveProject(projectName);
        var key = project.Name.ToLowerInvariant();

        // Fast path: provider already initialized — return immediately.
        if (_providers.TryGetValue(key, out var existing) && existing.IsValueCreated)
        {
            return existing.Value;
        }

        // Slow path: initialize the provider asynchronously, then register it.
        // GetOrAdd with a Lazy ensures only one initialization runs even under concurrency.
        var provider = new SqliteGraphStorageProvider(project.DatabasePath);
        await provider.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var newLazy = new Lazy<SqliteGraphStorageProvider>(() => provider, LazyThreadSafetyMode.ExecutionAndPublication);

        // Only keep our provider if no other thread won the race.
        var winner = _providers.GetOrAdd(key, newLazy);
        if (!ReferenceEquals(winner, newLazy))
        {
            // We lost the race — dispose ours and return the winner's value.
            provider.Dispose();
        }

        return winner.Value;
    }

    public Task<IGraphStorageProvider> GetActiveStorageProviderAsync(CancellationToken cancellationToken = default)
    {
        return GetStorageProviderAsync(null, cancellationToken);
    }

    /// <summary>Resolves the project for a given name, or falls back to the active project.</summary>
    private Project ResolveProject(string? projectName)
    {
        Project? project = null;
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            lock (_lock)
            {
                project = _projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            }
        }

        project ??= GetActiveProject();

        return project ?? throw new InvalidOperationException("No active project configured.");
    }

    /// <summary>
    /// Resolves the registered project that contains the given directory (deepest/longest path match),
    /// or null if none matches. Used to derive context purely from the caller's working directory,
    /// independent of the shared, mutable <c>ActiveProjectName</c>.
    /// </summary>
    public Project? FindProjectByPath(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        string normalized;
        try { normalized = Path.GetFullPath(directory).TrimEnd('\\', '/'); }
        catch { return null; }

        lock (_lock)
        {
            return _projects
                .Where(p => !string.IsNullOrWhiteSpace(p.Path))
                .Select(p =>
                {
                    string full;
                    try { full = Path.GetFullPath(p.Path).TrimEnd('\\', '/'); }
                    catch { full = p.Path.TrimEnd('\\', '/'); }
                    return (project: p, full);
                })
                .Where(x => normalized.Equals(x.full, StringComparison.OrdinalIgnoreCase)
                         || normalized.StartsWith(x.full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.full.Length)
                .Select(x => x.project)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Attempts to claim an exclusive index-scan slot for the given project.
    /// Returns false if a scan for that project is already running.
    /// Callers MUST call <see cref="EndScan"/> in a finally block when they obtained the slot.
    /// </summary>
    public bool TryBeginScan(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        return _activeScans.TryAdd(projectName.ToLowerInvariant(), 0);
    }

    /// <summary>Releases an index-scan slot previously claimed via <see cref="TryBeginScan"/>.</summary>
    public void EndScan(string projectName)
    {
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            _activeScans.TryRemove(projectName.ToLowerInvariant(), out _);
        }
    }

    public void RefreshStorageProvider(string projectName)
    {
        var key = projectName.ToLowerInvariant();
        if (_providers.TryRemove(key, out var provider) && provider.IsValueCreated)
        {
            provider.Value.Dispose();
        }
    }

    public WebConfig GetProjectConfig(string projectName)
    {
        Project? project;
        lock (_lock)
        {
            project = _projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        }

        if (project == null)
        {
            throw new ArgumentException($"Project '{projectName}' not found.");
        }

        var configPath = Path.Combine(project.Path, "shonkor.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<WebConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config != null)
                {
                    if (!Path.IsPathRooted(config.DatabasePath))
                    {
                        config.DatabasePath = Path.GetFullPath(Path.Combine(project.Path, config.DatabasePath));
                    }
                    return config;
                }
            }
            catch (Exception ex)
            {
                // Malformed shonkor.json — fall back to the default config but surface the reason.
                Console.Error.WriteLine($"[ProjectManager] Failed to read config for '{projectName}': {ex.Message}");
            }
        }

        // Return a default config for the project
        return new WebConfig
        {
            DatabasePath = project.DatabasePath
        };
    }

    public Project? FindProjectByRepoUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // Normalize URL by removing .git suffix, trailing slashes, and maybe credentials
        var normalized = NormalizeGitUrl(url);

        lock (_lock)
        {
            return _projects.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.RepositoryUrl) &&
                NormalizeGitUrl(p.RepositoryUrl).Equals(normalized, StringComparison.OrdinalIgnoreCase)
            );
        }
    }

    private static string NormalizeGitUrl(string url)
    {
        url = url.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            url = url.Substring(0, url.Length - 4);
        }
        return url.TrimEnd('/');
    }

    public void SaveProjectConfig(string projectName, WebConfig newConfig)
    {
        Project? project;
        lock (_lock)
        {
            project = _projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        }

        if (project == null)
        {
            throw new ArgumentException($"Project '{projectName}' not found.");
        }

        var configPath = Path.Combine(project.Path, "shonkor.json");
        
        // Keep DB path relative or clean in the saved json file
        var dbPathToSave = newConfig.DatabasePath;
        if (Path.IsPathRooted(dbPathToSave) && dbPathToSave.StartsWith(project.Path, StringComparison.OrdinalIgnoreCase))
        {
            dbPathToSave = Path.GetRelativePath(project.Path, dbPathToSave);
        }

        var configToSave = new WebConfig
        {
            DatabasePath = dbPathToSave,
            ExcludePatterns = newConfig.ExcludePatterns
        };

        var json = JsonSerializer.Serialize(configToSave, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);

        // Update database path in project registry if changed
        var absoluteDbPath = Path.IsPathRooted(newConfig.DatabasePath) 
            ? newConfig.DatabasePath 
            : Path.GetFullPath(Path.Combine(project.Path, newConfig.DatabasePath));

        if (project.DatabasePath != absoluteDbPath)
        {
            lock (_lock)
            {
                project.DatabasePath = absoluteDbPath;
                SaveProjects();
            }
            RefreshStorageProvider(projectName);
        }
    }

    private void LoadProjects(string currentWorkspace)
    {
        // Phase 1: read + parse the file WITHOUT the lock (I/O can be slow).
        List<Organization>? orgs = null;
        List<User>? users = null;
        List<Project>? projects = null;
        string? activeName = null;
        DateTime fileTime = DateTime.MinValue;
        bool needsSave = false;

        if (File.Exists(_projectsFilePath))
        {
            try
            {
                var json = File.ReadAllText(_projectsFilePath);
                fileTime = File.GetLastWriteTimeUtc(_projectsFilePath);
                var registry = JsonSerializer.Deserialize<ProjectRegistryData>(json);
                if (registry != null)
                {
                    orgs = registry.Organizations ?? new();
                    users = registry.Users ?? new();
                    projects = registry.Projects ?? new();
                    activeName = registry.ActiveProjectName ?? string.Empty;

                    // Migration: projects that have ApiKey but no OrganizationId are moved to a
                    // "Legacy Agency" org so that ApiKeyMiddleware can find them via User.ApiToken.
                    foreach (var proj in projects.Where(p => string.IsNullOrEmpty(p.OrganizationId) && !string.IsNullOrEmpty(p.ApiKey)))
                    {
                        var defaultOrg = orgs.FirstOrDefault(o => o.Name == "Legacy Agency");
                        if (defaultOrg == null)
                        {
                            defaultOrg = new Organization { Name = "Legacy Agency" };
                            orgs.Add(defaultOrg);
                        }

                        var defaultUser = users.FirstOrDefault(u => u.ApiToken == proj.ApiKey);
                        if (defaultUser == null)
                        {
                            defaultUser = new User
                            {
                                OrganizationId = defaultOrg.Id,
                                Username = "LegacyAdmin",
                                ApiToken = proj.ApiKey
                            };
                            users.Add(defaultUser);
                        }

                        proj.OrganizationId = defaultOrg.Id;
                        proj.ApiKey = string.Empty; // clear legacy field
                        needsSave = true;
                    }
                }
            }
            catch (Exception ex)
            {
                // Corrupt/unreadable projects.json — keep whatever is already in memory, but log it
                // so silent data loss doesn't go unnoticed.
                Console.Error.WriteLine($"[ProjectManager] Failed to load projects.json: {ex.Message}");
            }
        }

        // Phase 2: swap the parsed data in under the lock (fast — no I/O).
        lock (_lock)
        {
            if (orgs != null)    _organizations = orgs;
            if (users != null)   _users = users;
            if (projects != null) _projects = projects;
            if (activeName != null) _activeProjectName = activeName;
            if (fileTime > _lastLoadTime) _lastLoadTime = fileTime;
        }

        // Phase 3: if migration produced changes, persist them (I/O again, outside lock).
        if (needsSave)
        {
            lock (_lock) { SaveProjects(); }
        }

        // If still empty after load, auto-register current workspace as default.
        bool isEmpty;
        lock (_lock) { isEmpty = _projects.Count == 0; }

        if (isEmpty)
        {
            var folderName = new DirectoryInfo(currentWorkspace).Name;
            var defaultDb = Path.Combine(currentWorkspace, "shonkor.db");
            lock (_lock)
            {
                _projects.Add(new Project
                {
                    Name = folderName,
                    Path = currentWorkspace,
                    DatabasePath = defaultDb
                });
                _activeProjectName = folderName;
                SaveProjects();
            }
        }
    }

    private void SaveProjects()
    {
        var data = new ProjectRegistryData
        {
            Organizations = _organizations,
            Users = _users,
            Projects = _projects,
            ActiveProjectName = _activeProjectName
        };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_projectsFilePath, json);
    }

    private class ProjectRegistryData
    {
        public List<Organization> Organizations { get; set; } = new();
        public List<User> Users { get; set; } = new();
        public List<Project> Projects { get; set; } = new();
        public string ActiveProjectName { get; set; } = string.Empty;
    }

}
