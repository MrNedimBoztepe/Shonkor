// Licensed to Shonkor under the MIT License.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shonkor.Core.Models;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Outcome of a registry operation: success with a message, or failure with a reason. Keeps the
/// install/activate flow free of exceptions for the expected validation paths.
/// </summary>
public sealed record PluginOperationResult(bool Success, string Message, InstalledPlugin? Plugin = null)
{
    public static PluginOperationResult Ok(string message, InstalledPlugin? plugin = null) => new(true, message, plugin);
    public static PluginOperationResult Fail(string message) => new(false, message);
}

/// <summary>
/// Manages the workspace plugin registry: installing plugins from ZIP packages (extracted but inert),
/// then explicitly activating/deactivating them. State lives in <c>{workspace}/plugins/registry.json</c>;
/// each plugin's assemblies live in <c>{workspace}/plugins/{id}/</c>.
/// </summary>
/// <remarks>
/// This class only manages on-disk state and metadata — it never loads or runs plugin code. Loading the
/// assemblies of <see cref="PluginState.Active"/> plugins is the job of <c>AssemblyPluginLoader</c>, so
/// installation is provably inert: a freshly installed plugin runs nothing until it is activated.
/// </remarks>
public sealed class PluginRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Plugin authors write the manifest in camelCase ("id", "entryAssembly"); map it to the record.
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _pluginsDir;
    private readonly string _registryPath;
    private readonly object _lock = new();

    public PluginRegistry(string workspacePath)
    {
        _pluginsDir = Path.Combine(workspacePath, "plugins");
        _registryPath = Path.Combine(_pluginsDir, "registry.json");
    }

    /// <summary>All registered plugins (any state).</summary>
    public IReadOnlyList<InstalledPlugin> List()
    {
        lock (_lock) return Load();
    }

    /// <summary>The plugins that are currently <see cref="PluginState.Active"/>.</summary>
    public IReadOnlyList<InstalledPlugin> ActivePlugins()
    {
        lock (_lock) return Load().Where(p => p.State == PluginState.Active).ToList();
    }

    /// <summary>
    /// Installs a plugin from a ZIP package: validates the <c>plugin.json</c> manifest and host-API
    /// compatibility, extracts the package into <c>{workspace}/plugins/{id}/</c>, and records it as
    /// <see cref="PluginState.Installed"/> — INERT. The plugin runs nothing until <see cref="Activate"/>.
    /// </summary>
    public PluginOperationResult InstallFromZip(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            return PluginOperationResult.Fail($"ZIP not found: {zipPath}");
        }

        PluginManifest manifest;
        try
        {
            using var probe = ZipFile.OpenRead(zipPath);
            var manifestEntry = probe.GetEntry("plugin.json")
                ?? probe.Entries.FirstOrDefault(e => string.Equals(e.Name, "plugin.json", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry == null)
            {
                return PluginOperationResult.Fail("The ZIP has no 'plugin.json' manifest at its root.");
            }
            using var ms = manifestEntry.Open();
            manifest = JsonSerializer.Deserialize<PluginManifest>(ms, JsonOptions)
                       ?? throw new InvalidDataException("manifest deserialized to null");
        }
        catch (Exception ex)
        {
            return PluginOperationResult.Fail($"Could not read the plugin manifest: {ex.Message}");
        }

        var (validationError, compatWarning) = ValidateManifest(manifest);
        if (validationError != null)
        {
            return PluginOperationResult.Fail(validationError);
        }

        lock (_lock)
        {
            var plugins = Load();
            var target = Path.Combine(_pluginsDir, manifest.Id);

            // Reinstall/upgrade: drop any prior copy of the same id first (and unload happens on next reload).
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(target);
                ExtractZipSafely(zipPath, target);
            }
            catch (Exception ex)
            {
                return PluginOperationResult.Fail($"Extraction failed: {ex.Message}");
            }

            var entryPath = Path.Combine(target, manifest.EntryAssembly);
            if (!File.Exists(entryPath))
            {
                try { Directory.Delete(target, recursive: true); } catch { /* best effort */ }
                return PluginOperationResult.Fail($"Manifest entryAssembly '{manifest.EntryAssembly}' is not in the package.");
            }

            var entry = new InstalledPlugin
            {
                Manifest = manifest,
                State = PluginState.Installed,
                InstallPath = target,
                EntryAssemblySha256 = Sha256OfFile(entryPath),
                InstalledAtUtc = DateTime.UtcNow.ToString("o")
            };

            var updated = plugins.Where(p => p.Manifest.Id != manifest.Id).Append(entry).ToList();
            Save(updated);
            var warningPrefix = compatWarning != null ? compatWarning + " " : string.Empty;
            return PluginOperationResult.Ok($"{warningPrefix}Installed '{manifest.Id}' v{manifest.Version} (inactive — run 'activate' to enable it).", entry);
        }
    }

    /// <summary>Marks a plugin <see cref="PluginState.Active"/> so the loader picks it up. Does not load here.</summary>
    public PluginOperationResult Activate(string id) => Transition(id, PluginState.Active, "activated");

    /// <summary>Marks a plugin <see cref="PluginState.Disabled"/> so it is no longer loaded.</summary>
    public PluginOperationResult Deactivate(string id) => Transition(id, PluginState.Disabled, "deactivated");

    /// <summary>Records an activation failure (used by the loader when an active plugin won't load).</summary>
    public void MarkFailed(string id, string error)
    {
        lock (_lock)
        {
            var plugins = Load();
            var p = plugins.FirstOrDefault(x => x.Manifest.Id == id);
            if (p == null) return;
            Save(plugins.Select(x => x.Manifest.Id == id ? x with { State = PluginState.Failed, Error = error } : x).ToList());
        }
    }

    /// <summary>Removes a plugin from the registry and deletes its folder.</summary>
    public PluginOperationResult Uninstall(string id)
    {
        lock (_lock)
        {
            var plugins = Load();
            var p = plugins.FirstOrDefault(x => x.Manifest.Id == id);
            if (p == null) return PluginOperationResult.Fail($"No plugin '{id}' is installed.");
            try
            {
                if (Directory.Exists(p.InstallPath)) Directory.Delete(p.InstallPath, recursive: true);
            }
            catch (Exception ex)
            {
                return PluginOperationResult.Fail($"Could not delete plugin files: {ex.Message}");
            }
            Save(plugins.Where(x => x.Manifest.Id != id).ToList());
            return PluginOperationResult.Ok($"Uninstalled '{id}'.");
        }
    }

    private PluginOperationResult Transition(string id, PluginState to, string verb)
    {
        lock (_lock)
        {
            var plugins = Load();
            var p = plugins.FirstOrDefault(x => x.Manifest.Id == id);
            if (p == null) return PluginOperationResult.Fail($"No plugin '{id}' is installed.");
            var updated = p with { State = to, Error = null };
            Save(plugins.Select(x => x.Manifest.Id == id ? updated : x).ToList());
            return PluginOperationResult.Ok($"Plugin '{id}' {verb}.", updated);
        }
    }

    /// <summary>
    /// Validates a manifest. Returns a fatal <c>Error</c> (install is refused) and/or a non-fatal
    /// <c>Warning</c> (install proceeds, message surfaced to the user). A major host-API mismatch is fatal;
    /// a plugin needing a higher minor than this host is a graceful warning (the contract is additive, so
    /// the plugin still loads — any feature this older host lacks simply isn't invoked).
    /// </summary>
    private static (string? Error, string? Warning) ValidateManifest(PluginManifest m)
    {
        if (string.IsNullOrWhiteSpace(m.Id)) return ("Manifest 'id' is required.", null);
        if (m.Id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) return ("Manifest 'id' may only contain letters, digits, '-' and '_'.", null);
        if (string.IsNullOrWhiteSpace(m.Version)) return ("Manifest 'version' is required.", null);
        if (string.IsNullOrWhiteSpace(m.EntryAssembly)) return ("Manifest 'entryAssembly' is required.", null);
        if (!m.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return ("Manifest 'entryAssembly' must be a .dll.", null);

        var (pluginMajor, pluginMinor) = ParseApiVersion(m.MinHostApi);
        var (hostMajor, hostMinor) = ParseApiVersion(PluginHostApi.Version);

        // A differing major is a breaking-contract mismatch — never load it.
        if (pluginMajor != hostMajor)
        {
            return ($"Plugin targets host API {m.MinHostApi}, but this host is {PluginHostApi.Version} — incompatible.", null);
        }

        // Same major, but the plugin needs a newer minor than this host provides: the contract is additive,
        // so install anyway and warn that any feature this host predates won't be available to the plugin.
        if (pluginMinor > hostMinor)
        {
            return (null, $"Plugin targets host API {m.MinHostApi}, but this host is {PluginHostApi.Version} — installing anyway; features newer than {PluginHostApi.Version} won't be available.");
        }

        return (null, null);
    }

    /// <summary>Parses a "major.minor" API version, tolerating a missing or malformed minor (treated as 0).</summary>
    private static (int Major, int Minor) ParseApiVersion(string version)
    {
        var parts = (version ?? string.Empty).Split('.');
        _ = int.TryParse(parts.ElementAtOrDefault(0), out var major);
        _ = int.TryParse(parts.ElementAtOrDefault(1), out var minor);
        return (major, minor);
    }

    /// <summary>Extracts a ZIP, rejecting any entry whose path escapes the target directory (zip-slip).</summary>
    private static void ExtractZipSafely(string zipPath, string targetDir)
    {
        var fullTarget = Path.GetFullPath(targetDir);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            // Directory entries have empty Name.
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var destPath = Path.GetFullPath(Path.Combine(fullTarget, entry.FullName));
            if (!destPath.StartsWith(fullTarget + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(destPath, fullTarget, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsafe ZIP entry path (escapes target): '{entry.FullName}'.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private List<InstalledPlugin> Load()
    {
        if (!File.Exists(_registryPath)) return new List<InstalledPlugin>();
        try
        {
            var json = File.ReadAllText(_registryPath);
            return JsonSerializer.Deserialize<RegistryFile>(json, JsonOptions)?.Plugins ?? new List<InstalledPlugin>();
        }
        catch (Exception ex)
        {
            // A registry that EXISTS but can't be read (locked by AV/backup/another instance, or corrupt)
            // must fail the current operation. Swallowing this and returning an empty list meant the next
            // load-modify-Save cycle persisted the empty state — erasing every installed plugin.
            throw new InvalidOperationException(
                $"The plugin registry '{_registryPath}' exists but could not be read; refusing to continue (a write now would wipe it).", ex);
        }
    }

    private void Save(List<InstalledPlugin> plugins)
    {
        Directory.CreateDirectory(_pluginsDir);
        // Atomic write: a crash mid-write must never leave a torn registry.json behind (which the next
        // Load would reject, blocking every plugin operation until manually repaired).
        var tempPath = _registryPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(new RegistryFile { Plugins = plugins }, JsonOptions));
        if (File.Exists(_registryPath))
        {
            File.Replace(tempPath, _registryPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _registryPath);
        }
    }

    private sealed class RegistryFile
    {
        public List<InstalledPlugin> Plugins { get; set; } = new();
    }
}
