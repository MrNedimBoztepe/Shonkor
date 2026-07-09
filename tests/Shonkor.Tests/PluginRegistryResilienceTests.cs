// Licensed to Shonkor under the MIT License.

using System.IO.Compression;
using System.Text;

using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// Regression tests for BUG-008: a transiently unreadable or corrupt registry.json must fail the
/// current operation loudly — the old code swallowed the read failure, returned an empty list, and the
/// next save persisted the empty state, erasing every installed plugin.
/// </summary>
public class PluginRegistryResilienceTests
{
    private static string NewWorkspace()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_plugres_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        return ws;
    }

    private static string MakeZip(string dir, string id)
    {
        var zipPath = Path.Combine(dir, $"pkg_{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var m = zip.CreateEntry("plugin.json");
        using (var w = new StreamWriter(m.Open(), Encoding.UTF8))
        {
            w.Write($$"""{ "id": "{{id}}", "name": "P", "version": "1.0.0", "entryAssembly": "Plugin.dll", "minHostApi": "1.0", "targetExtensions": [".demo"] }""");
        }
        var a = zip.CreateEntry("Plugin.dll");
        using var s = a.Open();
        var bytes = Encoding.UTF8.GetBytes("fake assembly bytes");
        s.Write(bytes, 0, bytes.Length);
        return zipPath;
    }

    [Fact]
    public void Install_WhileRegistryFileIsLocked_Fails_WithoutWipingExistingPlugins()
    {
        var ws = NewWorkspace();
        try
        {
            var registry = new PluginRegistry(ws);
            Assert.True(registry.InstallFromZip(MakeZip(ws, "first")).Success);

            var registryPath = Path.Combine(ws, "plugins", "registry.json");
            // Simulate AV/backup/another instance holding the file: reads must fail, not read-as-empty.
            using (File.Open(registryPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<InvalidOperationException>(() => registry.InstallFromZip(MakeZip(ws, "second")));
            }

            // The failed operation must NOT have persisted an empty (or partial) registry.
            var survivors = registry.List();
            Assert.Contains(survivors, p => p.Manifest.Id == "first");
        }
        finally
        {
            if (Directory.Exists(ws)) Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void Read_OfCorruptRegistry_Throws_InsteadOfReturningEmpty()
    {
        var ws = NewWorkspace();
        try
        {
            var registry = new PluginRegistry(ws);
            Assert.True(registry.InstallFromZip(MakeZip(ws, "first")).Success);

            // Torn/corrupt file on disk (e.g. crash mid-write under the OLD non-atomic save).
            File.WriteAllText(Path.Combine(ws, "plugins", "registry.json"), "{ \"plugins\": [ TRUNC");

            Assert.ThrowsAny<InvalidOperationException>(() => registry.List());
        }
        finally
        {
            if (Directory.Exists(ws)) Directory.Delete(ws, recursive: true);
        }
    }
}
