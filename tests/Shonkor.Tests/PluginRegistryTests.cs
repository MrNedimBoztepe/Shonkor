// Licensed to Shonkor under the MIT License.

using System.IO.Compression;
using System.Text;

using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// Covers the assembly-plugin lifecycle: a plugin installs from a ZIP as INERT (Installed), only becomes
/// Active on an explicit activate, and the registry never loads code. Also guards manifest validation and
/// zip-slip extraction safety.
/// </summary>
public class PluginRegistryTests
{
    private static string NewWorkspace()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_plugins_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        return ws;
    }

    /// <summary>Builds a plugin ZIP with the given manifest JSON and a (fake) entry assembly.</summary>
    private static string MakeZip(string dir, string manifestJson, string entryAssemblyName = "Plugin.dll", byte[]? dllBytes = null, string? extraUnsafeEntry = null)
    {
        var zipPath = Path.Combine(dir, $"pkg_{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var m = zip.CreateEntry("plugin.json");
        using (var w = new StreamWriter(m.Open(), Encoding.UTF8)) w.Write(manifestJson);

        if (entryAssemblyName.Length > 0)
        {
            var a = zip.CreateEntry(entryAssemblyName);
            using var s = a.Open();
            var bytes = dllBytes ?? Encoding.UTF8.GetBytes("not a real assembly, but a real file");
            s.Write(bytes, 0, bytes.Length);
        }

        if (extraUnsafeEntry != null)
        {
            var e = zip.CreateEntry(extraUnsafeEntry);
            using var s = e.Open();
            var bytes = Encoding.UTF8.GetBytes("escape attempt");
            s.Write(bytes, 0, bytes.Length);
        }
        return zipPath;
    }

    private const string ValidManifest =
        """{ "id": "demo-parser", "name": "Demo", "version": "1.0.0", "entryAssembly": "Plugin.dll", "minHostApi": "1.0", "targetExtensions": [".demo"] }""";

    [Fact]
    public void Install_FromZip_IsInertUntilActivated_ThenDeactivatesAndUninstalls()
    {
        var ws = NewWorkspace();
        try
        {
            var registry = new PluginRegistry(ws);
            var zip = MakeZip(ws, ValidManifest);

            // Install: extracted, recorded, but INERT (not active).
            var install = registry.InstallFromZip(zip);
            Assert.True(install.Success, install.Message);
            Assert.Equal(PluginState.Installed, install.Plugin!.State);
            Assert.True(File.Exists(Path.Combine(ws, "plugins", "demo-parser", "Plugin.dll")));
            Assert.True(File.Exists(Path.Combine(ws, "plugins", "registry.json")));
            Assert.Empty(registry.ActivePlugins()); // nothing runs after install

            // Activate: now it's active.
            var act = registry.Activate("demo-parser");
            Assert.True(act.Success);
            Assert.Single(registry.ActivePlugins());
            Assert.Equal("demo-parser", registry.ActivePlugins()[0].Manifest.Id);

            // Deactivate: inert again.
            Assert.True(registry.Deactivate("demo-parser").Success);
            Assert.Empty(registry.ActivePlugins());

            // Uninstall: folder gone, registry empty.
            Assert.True(registry.Uninstall("demo-parser").Success);
            Assert.False(Directory.Exists(Path.Combine(ws, "plugins", "demo-parser")));
            Assert.Empty(registry.List());
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Install_RejectsMissingManifestEntryAndIncompatibleHostApi()
    {
        var ws = NewWorkspace();
        try
        {
            var registry = new PluginRegistry(ws);

            // entryAssembly not present in the package.
            var badEntry = MakeZip(ws,
                """{ "id": "x", "name": "X", "version": "1.0.0", "entryAssembly": "Missing.dll", "minHostApi": "1.0" }""");
            var r1 = registry.InstallFromZip(badEntry);
            Assert.False(r1.Success);
            Assert.Contains("entryAssembly", r1.Message);
            Assert.False(Directory.Exists(Path.Combine(ws, "plugins", "x"))); // cleaned up

            // Incompatible host API (major 2 vs host 1).
            var badApi = MakeZip(ws,
                """{ "id": "y", "name": "Y", "version": "1.0.0", "entryAssembly": "Plugin.dll", "minHostApi": "2.0" }""");
            var r2 = registry.InstallFromZip(badApi);
            Assert.False(r2.Success);
            Assert.Contains("host API", r2.Message);

            Assert.Empty(registry.List());
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Install_WarnsButSucceeds_WhenPluginNeedsNewerMinorThanHost()
    {
        var ws = NewWorkspace();
        try
        {
            var registry = new PluginRegistry(ws);

            // Plugin needs a higher MINOR than the host (same major). The contract is additive, so this
            // installs — with a graceful warning — rather than failing like a major mismatch would.
            var hostMajor = PluginHostApi.Version.Split('.')[0];
            var newerMinor = MakeZip(ws,
                $$"""{ "id": "future", "name": "Future", "version": "1.0.0", "entryAssembly": "Plugin.dll", "minHostApi": "{{hostMajor}}.99" }""");
            var r = registry.InstallFromZip(newerMinor);

            Assert.True(r.Success, r.Message);                // not rejected
            Assert.Contains("installing anyway", r.Message);  // but warned
            Assert.Equal(PluginState.Installed, r.Plugin!.State);

            // A plugin targeting an OLDER minor installs cleanly, with no warning noise.
            var olderMinor = MakeZip(ws,
                """{ "id": "legacy", "name": "Legacy", "version": "1.0.0", "entryAssembly": "Plugin.dll", "minHostApi": "1.0" }""");
            var r2 = registry.InstallFromZip(olderMinor);
            Assert.True(r2.Success, r2.Message);
            Assert.DoesNotContain("installing anyway", r2.Message);
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Install_RejectsZipSlipEntries()
    {
        var ws = NewWorkspace();
        try
        {
            var registry = new PluginRegistry(ws);
            var zip = MakeZip(ws, ValidManifest, extraUnsafeEntry: "../escaped.txt");

            var r = registry.InstallFromZip(zip);
            Assert.False(r.Success);
            Assert.Contains("Unsafe", r.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(ws, "escaped.txt")));
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The <b>backslash</b> escape (#240). The test above only ever tried <c>../escaped.txt</c>, so the
    /// Windows-flavoured attack — a zip entry named <c>..\escaped.txt</c> — was never exercised in a
    /// <b>security</b> check.
    ///
    /// <para>
    /// It is not the same string on both platforms, and asserting one answer for both would be asserting that
    /// one of them is wrong:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Windows</b> — <c>\</c> is a path separator, so <c>..\escaped.txt</c> <b>escapes</b> the target
    ///   directory and must be rejected.</item>
    ///   <item><b>Linux</b> — <c>\</c> is an ordinary filename character, so <c>..\escaped.txt</c> is a single,
    ///   oddly-named file that is <b>contained</b>. Rejecting it would be wrong; extracting it is correct, and
    ///   it must land inside the target and nowhere else.</item>
    /// </list>
    /// <para>
    /// A containment check that merely stripped <c>/</c> would pass the original test on both platforms and
    /// still be exploitable on Windows. This pins that the real guard (<c>GetFullPath</c> + a separator
    /// boundary) gets both cases right.
    /// </para>
    /// </summary>
    [Fact]
    public void Install_HandlesTheBackslashEntry_AsThePlatformDefinesIt()
    {
        var ws = NewWorkspace();
        try
        {
            var registry = new PluginRegistry(ws);
            var zip = MakeZip(ws, ValidManifest, extraUnsafeEntry: @"..\escaped.txt");

            var r = registry.InstallFromZip(zip);

            // Wherever it lands, it must NEVER land outside the workspace.
            var parent = Directory.GetParent(ws)!.FullName;
            Assert.False(File.Exists(Path.Combine(parent, "escaped.txt")),
                @"a zip entry named '..\escaped.txt' escaped the target directory");

            if (OperatingSystem.IsWindows())
            {
                Assert.False(r.Success);
                Assert.Contains("Unsafe", r.Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // On Linux the backslash is just a character: the entry is contained, so install proceeds and
                // the file sits INSIDE the workspace under its literal, odd name.
                Assert.True(r.Success, $"expected the contained entry to install on this platform: {r.Message}");
            }
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }
}
