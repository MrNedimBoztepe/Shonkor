// Licensed to Shonkor under the MIT License.

using System.IO.Compression;

using Microsoft.Extensions.Logging;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// Acceptance-criteria coverage for #313: the TypeScript plugin ships as an installable ZIP (incl. the Node
/// sidecar and its pinned <c>typescript</c> under <c>node_modules</c>), that ZIP installs + activates + loads
/// through the real registry/loader, and a fresh workspace is seeded default-active with no manual step — so
/// JS/TS parsing works out of the box after #292 removed the in-host parser.
/// </summary>
public sealed class StandardPluginPackagingTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    private string NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shonkor-seed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempPaths.Add(dir);
        return dir;
    }

    private string MaterializeEmbeddedZip()
    {
        using var stream = StandardPluginSeeder.OpenEmbeddedZip();
        Assert.NotNull(stream); // the packaged ZIP must be embedded for seeding to be possible
        var zipPath = Path.Combine(Path.GetTempPath(), "shonkor-seed-test-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var file = File.Create(zipPath)) stream!.CopyTo(file);
        _tempPaths.Add(zipPath);
        return zipPath;
    }

    public void Dispose()
    {
        foreach (var p in _tempPaths)
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                else if (File.Exists(p)) File.Delete(p);
            }
            catch { /* best effort */ }
        }
    }

    // ---- AC#1: the built package is an installable ZIP with the sidecar + node_modules, not just the DLL ----

    [Fact]
    public void Package_ZipRoot_ContainsManifestDllEsprimaAndFullSidecarWithNodeModules()
    {
        var zipPath = MaterializeEmbeddedZip();
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();

        // Root files the loader + adapter need.
        Assert.Contains("plugin.json", entries);
        Assert.Contains("Shonkor.Plugin.TypeScript.dll", entries);
        Assert.Contains("Esprima.dll", entries); // the private Esprima fallback, carried into the ALC folder

        // The whole sidecar tree — the plugin is NOT a lone DLL.
        Assert.Contains("sidecar/index.js", entries);
        Assert.Contains(entries, e => e.StartsWith("sidecar/package", StringComparison.Ordinal));

        // The pinned typescript actually present under node_modules (the crux of AC#1: not just the DLL).
        Assert.Contains("sidecar/node_modules/typescript/package.json", entries);
        Assert.Contains(entries, e => e.StartsWith("sidecar/node_modules/typescript/lib/", StringComparison.Ordinal));
    }

    // ---- AC#2: the ZIP installs + activates through the REAL registry, and the loader loads it (no hash fail) ----

    [Fact]
    public void Package_InstallsAndActivates_ThroughRealRegistry_AndLoaderLoadsParser()
    {
        var ws = NewWorkspace();
        var zipPath = MaterializeEmbeddedZip();

        var registry = new PluginRegistry(ws);
        Assert.True(registry.InstallFromZip(zipPath).Success);
        Assert.True(registry.Activate(StandardPluginSeeder.TypeScriptPluginId).Success);

        // Registry state: Active, with a recorded entry-assembly hash (the loader's tamper check depends on it).
        var entry = registry.List().Single(p => p.Manifest.Id == StandardPluginSeeder.TypeScriptPluginId);
        Assert.Equal(PluginState.Active, entry.State);
        Assert.False(string.IsNullOrEmpty(entry.EntryAssemblySha256));
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(entry.InstallPath, entry.Manifest.EntryAssembly)))).ToLowerInvariant(),
            entry.EntryAssemblySha256);

        // The loader loads its IFileParser without a hash / entry-missing failure. Drive the REGISTRY overload
        // here (no seeding) so this asserts purely on the install+activate result.
        using var loaded = AssemblyPluginLoader.LoadActive(registry);
        var parser = Assert.Single(loaded.Parsers);
        Assert.Contains(".ts", parser.SupportedExtensions);
        Assert.Contains(".tsx", parser.SupportedExtensions);
        Assert.Equal(PluginState.Active,
            registry.List().Single(p => p.Manifest.Id == StandardPluginSeeder.TypeScriptPluginId).State); // no MarkFailed
    }

    // ---- AC#3: a fresh workspace is seeded default-active with NO manual install/activate ----

    [Fact]
    public void FreshWorkspace_IsSeededActive_OnFirstLoad_WithSidecarAndNodeModulesOnDisk()
    {
        var ws = NewWorkspace();
        Assert.False(File.Exists(Path.Combine(ws, "plugins", "registry.json"))); // truly fresh

        // The single seam both hosts use. No manual InstallFromZip/Activate anywhere in this test.
        using var loaded = AssemblyPluginLoader.LoadActive(ws, new TestHost());

        var registry = new PluginRegistry(ws);
        var entry = registry.List().Single(p => p.Manifest.Id == StandardPluginSeeder.TypeScriptPluginId);
        Assert.Equal(PluginState.Active, entry.State);

        // Its sidecar + pinned typescript are materialised on disk at the install path.
        Assert.True(File.Exists(Path.Combine(entry.InstallPath, "sidecar", "index.js")));
        Assert.True(File.Exists(Path.Combine(entry.InstallPath, "sidecar", "node_modules", "typescript", "package.json")));

        // And it actually loaded (parser present).
        Assert.Contains(loaded.Parsers, p => p.SupportedExtensions.Contains(".tsx"));
    }

    // ---- AC#4: fresh-deploy round-trip through the SEEDED + LOADED plugin (real Node sidecar), not a direct parser ----

    [Fact]
    public async Task SeededPlugin_ParsesTsx_OverRealSidecar_ProducingJsComponentAndResolvedImport()
    {
        var ws = NewWorkspace();

        // Seed + load the plugin exactly as a host would on fresh deploy.
        using var loaded = AssemblyPluginLoader.LoadActive(ws, new TestHost());
        var parser = Assert.Single(loaded.Parsers, p => p.SupportedExtensions.Contains(".tsx"));

        // A real project on disk so the relative import resolves against a real file (id parity with #292).
        var project = NewWorkspace();
        var buttonPath = Path.Combine(project, "Button.tsx");
        var appPath = Path.Combine(project, "App.tsx");
        await File.WriteAllTextAsync(buttonPath, "export const Button = () => null;");
        var appCode = "import { Button } from './Button';\nexport const App = () => Button;\n";
        await File.WriteAllTextAsync(appPath, appCode);

        var (nodes, edges) = await parser.ParseAsync(appPath, appCode);

        var component = Assert.Single(nodes, n => n.Type == "JSComponent");
        Assert.Equal($"{appPath}::App", component.Id);
        var import = Assert.Single(edges, e => e.Relationship == "IMPORTS");
        Assert.Equal("./Button", import.Properties["rawSource"]);
        Assert.Equal(buttonPath, import.TargetId); // resolved by the real TS Compiler API in the seeded sidecar
    }

    // ---- AC#5: seeding is idempotent and never overwrites a user's explicit Disabled/Failed ----

    [Fact]
    public void Seeding_IsIdempotent_AndPreservesExplicitDisabledOrFailedState()
    {
        var ws = NewWorkspace();
        var registry = new PluginRegistry(ws);

        // First seed → exactly one Active entry.
        StandardPluginSeeder.EnsureSeeded(registry);
        Assert.Single(registry.List(), p => p.Manifest.Id == StandardPluginSeeder.TypeScriptPluginId);
        Assert.Equal(PluginState.Active,
            registry.List().Single(p => p.Manifest.Id == StandardPluginSeeder.TypeScriptPluginId).State);

        // Repeated seeding → still exactly one entry, still Active (no duplicate).
        StandardPluginSeeder.EnsureSeeded(registry);
        StandardPluginSeeder.EnsureSeeded(registry);
        Assert.Single(registry.List(), p => p.Manifest.Id == StandardPluginSeeder.TypeScriptPluginId);

        // The operator turns it OFF; a later deploy must NOT silently re-activate it.
        Assert.True(registry.Deactivate(StandardPluginSeeder.TypeScriptPluginId).Success);
        StandardPluginSeeder.EnsureSeeded(registry);
        Assert.Equal(PluginState.Disabled,
            registry.List().Single(p => p.Manifest.Id == StandardPluginSeeder.TypeScriptPluginId).State);

        // A Failed state is likewise preserved (seeding does not resurrect it).
        registry.MarkFailed(StandardPluginSeeder.TypeScriptPluginId, "simulated load failure");
        StandardPluginSeeder.EnsureSeeded(registry);
        Assert.Equal(PluginState.Failed,
            registry.List().Single(p => p.Manifest.Id == StandardPluginSeeder.TypeScriptPluginId).State);

        // Never duplicated across all of the above.
        Assert.Single(registry.List(), p => p.Manifest.Id == StandardPluginSeeder.TypeScriptPluginId);
    }

    // ---- AC#7 (runtime): an artifact WITHOUT node_modules still installs/activates/loads and the host stays
    //      functional — the file is not silently OK (an error diagnostic is surfaced), and nothing crashes.
    //      (Deps-less is prevented at RELEASE build time by the loud guard, pinned in the next test.) ----

    [Fact]
    public async Task ArtifactWithoutNodeModules_LoadsAndParsesWithoutCrashing_AndSurfacesADiagnostic()
    {
        var ws = NewWorkspace();

        // Build a degraded ZIP: the full package minus the sidecar node_modules (the AC#7 scenario — a machine
        // that could not run `npm ci`). Everything else (DLL, Esprima, sidecar/index.js) is kept.
        var fullZip = MaterializeEmbeddedZip();
        var strippedZip = Path.Combine(Path.GetTempPath(), "shonkor-seed-test-" + Guid.NewGuid().ToString("N") + ".zip");
        _tempPaths.Add(strippedZip);
        using (var src = ZipFile.OpenRead(fullZip))
        using (var dst = ZipFile.Open(strippedZip, ZipArchiveMode.Create))
        {
            foreach (var e in src.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (name.StartsWith("sidecar/node_modules/", StringComparison.Ordinal)) continue; // drop deps
                if (string.IsNullOrEmpty(e.Name)) continue; // skip pure directory entries
                var outEntry = dst.CreateEntry(e.FullName);
                using var input = e.Open();
                using var output = outEntry.Open();
                await input.CopyToAsync(output);
            }
        }

        var registry = new PluginRegistry(ws);
        Assert.True(registry.InstallFromZip(strippedZip).Success);
        Assert.True(registry.Activate(StandardPluginSeeder.TypeScriptPluginId).Success);

        var host = new TestHost();
        using var loaded = AssemblyPluginLoader.LoadActive(registry, host);
        var parser = Assert.Single(loaded.Parsers); // loads fine despite missing deps

        var project = NewWorkspace();
        var path = Path.Combine(project, "Plain.ts");
        var code = "export const foo = 1;\n";
        await File.WriteAllTextAsync(path, code);

        // Must not throw — a degraded artifact never crashes the scan.
        var ex = await Record.ExceptionAsync(() => parser.ParseAsync(path, code));
        Assert.Null(ex);

        // And it is NOT silently degraded: the sidecar's inability to load its TS deps surfaces as a diagnostic,
        // so the operator sees the artifact is broken rather than getting empty results with no signal.
        Assert.Contains(host.Logger.Messages, m => m.Contains("diagnostic", StringComparison.OrdinalIgnoreCase));
    }

    // ---- AC#7 (build-time loud guard): a RELEASE package missing node_modules must fail loudly, not ship a
    //      silently-degraded artifact — while a local Debug build still compiles. Pin the guard so a future
    //      csproj refactor cannot silently drop it (the loud check is what protects a release). ----

    [Fact]
    public void ReleasePackaging_HasLoudGuard_AgainstMissingSidecarNodeModules()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Shonkor.Plugin.TypeScript", "Shonkor.Plugin.TypeScript.csproj"));

        // A Release-only <Error> that fires when the pinned typescript is absent from the sidecar node_modules.
        Assert.Contains("VerifySidecarReleaseDeps", csproj);
        Assert.Contains("'$(Configuration)' == 'Release'", csproj);
        Assert.Contains("node_modules\\typescript\\package.json", csproj);
        Assert.Contains("<Error", csproj);
    }

    private static string RepoRoot()
    {
        // Walk up from the test bin dir to the repo root (the folder that holds Shonkor.slnx).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Shonkor.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    // ---- test double ----

    private sealed class TestHost : IPluginHost
    {
        public sealed class CapturingLogger : ILogger
        {
            private readonly object _gate = new();
            public List<string> Messages { get; } = new();
            IDisposable? ILogger.BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (_gate) { Messages.Add(formatter(state, exception)); }
            }
        }

        public CapturingLogger Logger { get; } = new();
        ILogger IPluginHost.Logger => Logger;
    }
}
