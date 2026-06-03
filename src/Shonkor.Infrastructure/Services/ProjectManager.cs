using System.Collections.Concurrent;
using System.Text.Json;
using Shonkor.Core.Interfaces;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Infrastructure.Services;

public class Project
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
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

public class ProjectManager
{
    private readonly string _projectsFilePath;
    // Lazy<> guarantees the provider factory (which initializes the schema) runs exactly once
    // per project even under concurrent access, avoiding duplicate connections/initializations.
    private readonly ConcurrentDictionary<string, Lazy<SqliteGraphStorageProvider>> _providers = new();
    // Tracks projects with an in-flight index scan so concurrent scans don't duplicate work or race.
    private readonly ConcurrentDictionary<string, byte> _activeScans = new();
    private readonly object _lock = new();
    private List<Project> _projects = new();
    private string _activeProjectName = string.Empty;
    private DateTime _lastLoadTime = DateTime.MinValue;

    public string WorkspacePath { get; private set; }

    public ProjectManager(string currentWorkspace)
    {
        WorkspacePath = currentWorkspace;
        _projectsFilePath = Path.Combine(currentWorkspace, "projects.json");
        LoadProjects(currentWorkspace);
        EnsureStandardPlugins();
    }

    private void EnsureUpToDate()
    {
        if (File.Exists(_projectsFilePath))
        {
            var currentWriteTime = File.GetLastWriteTimeUtc(_projectsFilePath);
            if (currentWriteTime > _lastLoadTime)
            {
                LoadProjects(WorkspacePath);
            }
        }
    }

    public List<Project> GetProjects()
    {
        lock (_lock)
        {
            EnsureUpToDate();
            return _projects.ToList();
        }
    }

    public string GetActiveProjectName()
    {
        lock (_lock)
        {
            EnsureUpToDate();
            return _activeProjectName;
        }
    }

    public Project? GetActiveProject()
    {
        lock (_lock)
        {
            EnsureUpToDate();
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

    public IGraphStorageProvider GetStorageProvider(string? projectName)
    {
        Project? project = null;
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            lock (_lock)
            {
                project = _projects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (project == null)
        {
            project = GetActiveProject();
        }

        if (project == null)
        {
            throw new InvalidOperationException("No active project configured.");
        }

        var key = project.Name.ToLowerInvariant();
        var lazy = _providers.GetOrAdd(key, _ => new Lazy<SqliteGraphStorageProvider>(() =>
        {
            var provider = new SqliteGraphStorageProvider(project.DatabasePath);
            provider.InitializeAsync().GetAwaiter().GetResult();
            return provider;
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    public IGraphStorageProvider GetActiveStorageProvider()
    {
        return GetStorageProvider(null);
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
            catch
            {
                // Fallback
            }
        }

        // Return a default config for the project
        return new WebConfig
        {
            DatabasePath = project.DatabasePath
        };
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
        if (File.Exists(_projectsFilePath))
        {
            try
            {
                var json = File.ReadAllText(_projectsFilePath);
                var registry = JsonSerializer.Deserialize<ProjectRegistryData>(json);
                if (registry != null)
                {
                    _projects = registry.Projects ?? new();
                    _activeProjectName = registry.ActiveProjectName ?? string.Empty;
                }
                _lastLoadTime = File.GetLastWriteTimeUtc(_projectsFilePath);
            }
            catch
            {
                // Fallback
            }
        }

        // If empty, auto-register current workspace as default
        if (_projects.Count == 0)
        {
            var folderName = new DirectoryInfo(currentWorkspace).Name;
            var defaultDb = Path.Combine(currentWorkspace, "shonkor.db");
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

    private void SaveProjects()
    {
        var data = new ProjectRegistryData
        {
            Projects = _projects,
            ActiveProjectName = _activeProjectName
        };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_projectsFilePath, json);
    }

    private class ProjectRegistryData
    {
        public List<Project> Projects { get; set; } = new();
        public string ActiveProjectName { get; set; } = string.Empty;
    }

    private void EnsureStandardPlugins()
    {
        try
        {
            var pluginsDir = Path.Combine(WorkspacePath, "plugins");
            if (!Directory.Exists(pluginsDir))
            {
                Directory.CreateDirectory(pluginsDir);
            }

            var coreAssembly = typeof(Shonkor.Core.Interfaces.IFileParser).Assembly;
            var resourceNames = coreAssembly.GetManifestResourceNames()
                .Where(n => n.Contains(".StandardPlugins.") && n.EndsWith(".cs"));

            foreach (var res in resourceNames)
            {
                // Extract filename like OptimizelyPlugin.cs from Shonkor.Core.StandardPlugins.OptimizelyPlugin.cs
                var parts = res.Split('.');
                var fileName = parts[^2] + "." + parts[^1];
                var destPath = Path.Combine(pluginsDir, fileName);

                if (!File.Exists(destPath))
                {
                    using var stream = coreAssembly.GetManifestResourceStream(res);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        File.WriteAllText(destPath, reader.ReadToEnd());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectManager] Failed to ensure standard plugins: {ex.Message}");
        }
    }
}
