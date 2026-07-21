// Licensed to Shonkor under the MIT License.

using System.Reflection;

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
    /// Ensures the standard plugins are installed + active in the given workspace. Idempotent and
    /// intent-preserving: a standard plugin is seeded ONLY when it is entirely absent from the registry. Once
    /// it is registered — whether the seed left it <c>Active</c>, or the user later set it <c>Disabled</c>, or
    /// the loader marked it <c>Failed</c> — this call leaves it exactly as it is (no duplicate entry, and no
    /// silent re-activation of a plugin the operator deliberately turned off).
    /// </summary>
    public static void EnsureSeeded(PluginRegistry registry)
    {
        var known = registry.List();
        SeedIfAbsent(registry, known, TypeScriptPluginId, TypeScriptPluginResourceName);
    }

    private static void SeedIfAbsent(PluginRegistry registry, IReadOnlyList<InstalledPlugin> known, string id, string resourceName)
    {
        // Present in ANY state → respect the existing entry (idempotency + do not overwrite Disabled/Failed).
        if (known.Any(p => p.Manifest.Id == id)) return;

        // A seeding hiccup (missing resource, transient IO) must never stop the host from loading the plugins
        // that ARE already active; on the next startup the still-absent plugin is retried.
        try
        {
            var zipPath = TryMaterializeEmbeddedZip(resourceName);
            if (zipPath == null) return; // no embedded artifact in this build variant — nothing to seed

            try
            {
                var install = registry.InstallFromZip(zipPath);
                if (install.Success)
                {
                    registry.Activate(id);
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

    /// <summary>Test hook: opens the embedded standard-plugin ZIP stream, or null if absent.</summary>
    internal static Stream? OpenEmbeddedZip(string resourceName = TypeScriptPluginResourceName)
        => typeof(StandardPluginSeeder).Assembly.GetManifestResourceStream(resourceName);
}
