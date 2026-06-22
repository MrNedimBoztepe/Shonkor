// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Models;

/// <summary>
/// The lifecycle state of an installed plugin. Installation never loads code — a plugin only runs once
/// it is explicitly <see cref="Active"/>.
/// </summary>
public enum PluginState
{
    /// <summary>Present on disk but NOT loaded. The default after install — inert until activated.</summary>
    Installed,

    /// <summary>Activated: its assembly is loaded and its parsers participate in indexing.</summary>
    Active,

    /// <summary>Was active, has been deactivated: present on disk, not loaded.</summary>
    Disabled,

    /// <summary>Activation failed (bad assembly, missing entry, incompatible host API). See the error.</summary>
    Failed
}

/// <summary>
/// The manifest (<c>plugin.json</c>) shipped inside a plugin ZIP, describing what to load and the host
/// contract it targets. Declarative metadata only — no code.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>Stable, unique id (kebab-case, e.g. "optimizely-oxid"). Also the install folder name.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Semantic version of the plugin (e.g. "1.0.0").</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Optional short description shown in listings.</summary>
    public string? Description { get; init; }

    /// <summary>The plugin's entry assembly file name within the package (e.g. "Shonkor.Plugin.Optimizely.dll").</summary>
    public string EntryAssembly { get; init; } = string.Empty;

    /// <summary>
    /// The host plugin-API version this plugin targets (e.g. "1.0"). The host rejects activation when its
    /// own <see cref="PluginHostApi.Version"/> major differs, so an incompatible plugin never loads.
    /// </summary>
    public string MinHostApi { get; init; } = "1.0";

    /// <summary>Optional: the file extensions the plugin's parsers handle (informational, for listings).</summary>
    public IReadOnlyList<string> TargetExtensions { get; init; } = Array.Empty<string>();
}

/// <summary>The host's plugin contract version. Plugins declare a compatible <see cref="PluginManifest.MinHostApi"/>.</summary>
public static class PluginHostApi
{
    /// <summary>Current host plugin-API version. Bump the major on a breaking contract change.</summary>
    public const string Version = "1.0";
}

/// <summary>
/// A plugin recorded in the workspace registry: its manifest plus install/runtime bookkeeping. Persisted
/// in <c>{workspace}/plugins/registry.json</c>; the on-disk assemblies live in <c>{workspace}/plugins/{Id}/</c>.
/// </summary>
public sealed record InstalledPlugin
{
    public PluginManifest Manifest { get; init; } = new();

    /// <summary>Current lifecycle state. Install yields <see cref="PluginState.Installed"/> (inert).</summary>
    public PluginState State { get; init; } = PluginState.Installed;

    /// <summary>Absolute path to the plugin's extracted folder.</summary>
    public string InstallPath { get; init; } = string.Empty;

    /// <summary>SHA-256 of the installed entry assembly, for tamper/identity checks.</summary>
    public string EntryAssemblySha256 { get; init; } = string.Empty;

    /// <summary>UTC timestamp (round-trip "o") of installation.</summary>
    public string InstalledAtUtc { get; init; } = string.Empty;

    /// <summary>The last activation error, when <see cref="State"/> is <see cref="PluginState.Failed"/>.</summary>
    public string? Error { get; init; }
}
