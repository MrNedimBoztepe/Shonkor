// Licensed to Shonkor under the MIT License.

using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using Shonkor.Core.Models;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Seeds the first-party "standard" plugins into a workspace so a fresh checkout/deploy parses their file
/// types out of the box, with no manual install/activate step. Today that is the JS/TS base plugin
/// (<c>shonkor-typescript</c>, #292): its in-host parser was removed from the host wiring in #292, so without
/// seeding JS/TS parsing would silently vanish on a fresh deploy (#313).
/// </summary>
/// <remarks>
/// The plugin's installable ZIP (manifest + entry DLL + private Esprima fallback + the whole Node sidecar
/// incl. its pinned <c>typescript</c> under <c>node_modules</c>) is embedded in this assembly at build time
/// (see <c>Shonkor.Infrastructure.csproj</c>). Seeding reuses the existing registry primitives
/// (<see cref="PluginRegistry.InstallFromZip"/> then <see cref="PluginRegistry.Activate"/>) rather than a
/// bespoke mechanism, so the on-disk files and recorded <c>EntryAssemblySha256</c> are exactly what the
/// loader's tamper check expects.
/// </remarks>
public static class StandardPluginSeeder
{
    /// <summary>Manifest id of the first-party JS/TS plugin (must match its <c>plugin.json</c>).</summary>
    public const string TypeScriptPluginId = "shonkor-typescript";

    /// <summary>Stable logical name of the embedded ZIP (see the <c>EmbeddedResource</c> in the csproj).</summary>
    internal const string TypeScriptPluginResourceName = "Shonkor.Infrastructure.StandardPlugins.Shonkor.Plugin.TypeScript.zip";

    /// <summary>
    /// Ensures the standard plugins are installed + active in the given workspace, and that an already
    /// installed one still matches the artifact this host was built with.
    /// <para>
    /// Seeding used to fire ONLY when a plugin was entirely absent, which made the installed copy immortal:
    /// the embedded ZIP is rebuilt from source on every host build, but nothing ever re-applied it, so a
    /// workspace kept whatever binary it was first seeded with — across every later host build. That is how a
    /// plugin assembly can go on running against a contract the host has since changed (#401): the loader's
    /// <c>EntryAssemblySha256</c> check answers "is this the binary we installed?", never "was it built
    /// against the contract we now enforce?".
    /// </para>
    /// <para>
    /// So this now does two things, and only these two: it seeds an absent plugin, and it REFRESHES an
    /// installed one whose entry assembly differs from the embedded artifact. Everything else about the old
    /// contract is preserved — a refresh restores the previous <see cref="PluginState"/>, so a plugin the
    /// operator set <c>Disabled</c> stays disabled, and a <c>Failed</c> entry is left untouched entirely
    /// (re-installing under it would silently clear a failure the operator has not seen).
    /// </para>
    /// </summary>
    public static void EnsureSeeded(PluginRegistry registry)
    {
        var known = registry.List();
        SeedOrRefresh(registry, known, TypeScriptPluginId, TypeScriptPluginResourceName);
    }

    private static void SeedOrRefresh(PluginRegistry registry, IReadOnlyList<InstalledPlugin> known, string id, string resourceName)
    {
        var existing = known.FirstOrDefault(p => p.Manifest.Id == id);

        // A Failed entry carries an error the operator may not have seen yet; refreshing it would clear that
        // signal without anyone reading it. Leave it exactly as it is — the same reasoning as before.
        if (existing is { State: PluginState.Failed }) return;

        // A seeding hiccup (missing resource, transient IO) must never stop the host from loading the plugins
        // that ARE already active; on the next startup the still-absent (or still-stale) plugin is retried.
        try
        {
            var zipPath = TryMaterializeEmbeddedZip(resourceName);
            if (zipPath == null) return; // no embedded artifact in this build variant — nothing to seed

            try
            {
                if (existing != null)
                {
                    // Compare the artifact we would install against the one that IS installed. The embedded
                    // entry assembly is hashed straight out of the ZIP, which is byte-identical to what
                    // extraction writes, so it compares directly against the recorded EntryAssemblySha256 —
                    // no new registry field, and no reliance on version strings a plugin author may forget
                    // to bump.
                    var embeddedHash = TryHashEntryAssemblyInZip(zipPath);
                    if (embeddedHash == null) return;                       // unreadable package — leave the install alone
                    if (string.Equals(embeddedHash, existing.EntryAssemblySha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return;                                             // already current
                    }
                }

                // InstallFromZip is a full reinstall (it drops the prior copy and re-records the hash) and
                // always lands in Installed, so the previous state has to be re-applied explicitly — for
                // BOTH directions. Restoring only Active would quietly promote a Disabled plugin to
                // Installed, which is neither what the operator chose nor what it was.
                var install = registry.InstallFromZip(zipPath);
                if (!install.Success) return;

                switch (existing?.State)
                {
                    case null:                     // first seed: standard plugins are active by default
                    case PluginState.Active:
                        registry.Activate(id);
                        break;
                    case PluginState.Disabled:
                        registry.Deactivate(id);
                        break;
                    // Installed (never activated) is what InstallFromZip already left behind; Failed never
                    // reaches here (returned above).
                }
            }
            finally
            {
                try { File.Delete(zipPath); } catch { /* best-effort temp cleanup */ }
            }
        }
        catch
        {
            // Intentionally swallowed: seeding is a convenience over an inert workspace, never a hard
            // precondition for loading. A genuinely broken registry surfaces through the loader itself.
        }
    }

    /// <summary>
    /// SHA256 of the entry assembly inside a plugin ZIP, read from the archive without extracting it.
    /// Mirrors <see cref="PluginRegistry.InstallFromZip"/>'s manifest lookup (root <c>plugin.json</c>, then a
    /// case-insensitive fallback) so the two agree on which package this is. Returns <c>null</c> when the
    /// archive has no readable manifest or the manifest's entry assembly is missing from it — in which case
    /// the caller must not touch a working install on the strength of a package it cannot read.
    ///
    /// <para>
    /// Public because the same comparison serves a second purpose (#416): pointed at a freshly BUILT package
    /// instead of the embedded one, it answers "is the plugin this workspace has installed the plugin these
    /// sources produce?". That is a precondition of any scan whose output someone will rely on — the
    /// verification scan for the provenance freeze most of all, since this workspace held four stale plugin
    /// binaries as recently as 2026-08-17 and nothing reported it.
    /// </para>
    /// </summary>
    public static string? TryHashEntryAssemblyInZip(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifestEntry = zip.GetEntry("plugin.json")
                ?? zip.Entries.FirstOrDefault(e => string.Equals(e.Name, "plugin.json", StringComparison.OrdinalIgnoreCase));
            if (manifestEntry == null) return null;

            PluginManifest? manifest;
            using (var manifestStream = manifestEntry.Open())
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(manifestStream, ManifestJsonOptions);
            }
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.EntryAssembly)) return null;

            var entry = zip.GetEntry(manifest.EntryAssembly)
                ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, manifest.EntryAssembly, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;

            using var assemblyStream = entry.Open();
            return Convert.ToHexString(SHA256.HashData(assemblyStream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Manifest deserialization settings, matching <see cref="PluginRegistry"/>'s.</summary>
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Copies the embedded plugin ZIP to a temp file (InstallFromZip takes a path). Returns null when the
    /// resource is not present in this assembly.
    /// </summary>
    private static string? TryMaterializeEmbeddedZip(string resourceName)
    {
        var assembly = typeof(StandardPluginSeeder).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        var tempPath = Path.Combine(Path.GetTempPath(), $"shonkor-seed-{Guid.NewGuid():N}.zip");
        using (var file = File.Create(tempPath))
        {
            stream.CopyTo(file);
        }
        return tempPath;
    }

    /// <summary>
    /// Opens the embedded standard-plugin ZIP stream, or null if absent. Used by the packaging tests and by
    /// `plugin verify` (#416), which needs the artifact this host ships in order to compare it against what
    /// the workspace actually installed.
    /// </summary>
    public static Stream? OpenEmbeddedZip(string resourceName = TypeScriptPluginResourceName)
        => typeof(StandardPluginSeeder).Assembly.GetManifestResourceStream(resourceName);
}
