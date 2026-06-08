// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Materializes the shipped sample plugins (embedded as resources in Shonkor.Core under
/// <c>StandardPlugins/</c>) into the workspace's <c>plugins</c> directory on first run, so users have
/// editable examples. This is a filesystem-bootstrap concern, intentionally separate from the
/// project registry (<see cref="ProjectManager"/>).
/// </summary>
public static class StandardPluginsInstaller
{
    /// <summary>
    /// Copies any not-yet-present standard plugin source files into <c>{workspacePath}/plugins</c>.
    /// Best-effort: failures are logged and swallowed so a plugin-copy problem never blocks startup.
    /// </summary>
    public static void Install(string workspacePath)
    {
        try
        {
            var pluginsDir = Path.Combine(workspacePath, "plugins");
            if (!Directory.Exists(pluginsDir))
            {
                Directory.CreateDirectory(pluginsDir);
            }

            var coreAssembly = typeof(IFileParser).Assembly;
            var resourceNames = coreAssembly.GetManifestResourceNames()
                .Where(n => n.Contains(".StandardPlugins.") && n.EndsWith(".cs"));

            foreach (var res in resourceNames)
            {
                // Extract filename like OptimizelyPlugin.cs from Shonkor.Core.StandardPlugins.OptimizelyPlugin.cs
                var parts = res.Split('.');
                var fileName = parts[^2] + "." + parts[^1];
                var destPath = Path.Combine(pluginsDir, fileName);

                if (File.Exists(destPath)) continue;

                using var stream = coreAssembly.GetManifestResourceStream(res);
                if (stream == null) continue;

                using var reader = new StreamReader(stream);
                File.WriteAllText(destPath, reader.ReadToEnd());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StandardPluginsInstaller] Failed to install standard plugins: {ex.Message}");
        }
    }
}
