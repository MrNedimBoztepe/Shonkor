// Licensed to Shonkor under the MIT License.

using System.IO.Compression;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

using Shonkor.Core.Interfaces;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// End-to-end proof that a pre-built plugin assembly, installed from a ZIP and explicitly activated, loads
/// through the host's shared <see cref="IFileParser"/> contract and actually parses. Also confirms an
/// installed-but-not-activated plugin loads nothing.
/// </summary>
public class AssemblyPluginLoaderTests
{
    private const string PluginSource = """
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Shonkor.Core.Interfaces;
        using Shonkor.Core.Models;

        namespace DemoPlugin;

        public sealed class DemoParser : IFileParser
        {
            public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".demo" };
            public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new List<NodeTypeDescriptor>();

            public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
            {
                var nodes = new List<GraphNode>
                {
                    new GraphNode { Id = filePath + "::demo", Name = "DemoNode", Type = "DemoType", FilePath = filePath }
                };
                return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, new List<GraphEdge>()));
            }
        }
        """;

    /// <summary>Compiles a plugin source into a real .dll on disk (test setup only, not runtime).</summary>
    private static void CompilePluginDll(string dllPath, string source = PluginSource, string assemblyName = "DemoPlugin")
    {
        // File.Exists guard: a previously-run test may have loaded a plugin .dll from a temp dir that is
        // now deleted; that assembly can still linger in the AppDomain, and CreateFromFile would throw on
        // its missing path. Reference only assemblies whose backing file is still on disk.
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && System.IO.File.Exists(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = compilation.Emit(dllPath);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage())));
    }

    [Fact]
    public async Task ActivatedPlugin_LoadsFromAssembly_AndParses_WhileInstalledOnlyLoadsNothing()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_pluginload_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            // Build a plugin package: plugin.json + a compiled DemoPlugin.dll, zipped.
            var dll = Path.Combine(ws, "DemoPlugin.dll");
            CompilePluginDll(dll);

            var zip = Path.Combine(ws, "demo.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var m = archive.CreateEntry("plugin.json");
                using (var w = new StreamWriter(m.Open(), Encoding.UTF8))
                {
                    w.Write("""{ "id": "demo", "name": "Demo", "version": "1.0.0", "entryAssembly": "DemoPlugin.dll", "minHostApi": "1.0" }""");
                }
                archive.CreateEntryFromFile(dll, "DemoPlugin.dll");
            }

            var registry = new PluginRegistry(ws);
            Assert.True(registry.InstallFromZip(zip).Success);

            // Installed but not activated → the loader loads nothing.
            using (var inert = AssemblyPluginLoader.LoadActive(registry))
            {
                Assert.Empty(inert.Parsers);
            }

            // Activate → the assembly loads and the parser works through the host's IFileParser type.
            Assert.True(registry.Activate("demo").Success);
            using var loaded = AssemblyPluginLoader.LoadActive(registry);
            Assert.Single(loaded.Parsers);

            var parser = loaded.Parsers[0];
            Assert.Contains(".demo", parser.SupportedExtensions);

            var (nodes, _) = await parser.ParseAsync("C:/x/file.demo", "irrelevant");
            Assert.Single(nodes);
            Assert.Equal("DemoNode", nodes[0].Name);
            Assert.Equal("DemoType", nodes[0].Type);
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ActivatedPlugin_WhoseAssemblyIsTamperedAfterInstall_RefusesToLoad_AndIsMarkedFailed()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_plugintamper_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            var dll = Path.Combine(ws, "DemoPlugin.dll");
            CompilePluginDll(dll);
            var zip = Path.Combine(ws, "demo.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var m = archive.CreateEntry("plugin.json");
                using (var w = new StreamWriter(m.Open(), Encoding.UTF8))
                {
                    w.Write("""{ "id": "demo", "name": "Demo", "version": "1.0.0", "entryAssembly": "DemoPlugin.dll", "minHostApi": "1.0" }""");
                }
                archive.CreateEntryFromFile(dll, "DemoPlugin.dll");
            }

            var registry = new PluginRegistry(ws);
            Assert.True(registry.InstallFromZip(zip).Success);
            Assert.True(registry.Activate("demo").Success);

            // Tamper: swap the installed assembly for different bytes AFTER install recorded its hash.
            var installedDll = Path.Combine(ws, "plugins", "demo", "DemoPlugin.dll");
            File.WriteAllText(installedDll, "this is not the assembly that was installed");

            using var loaded = AssemblyPluginLoader.LoadActive(registry);
            Assert.Empty(loaded.Parsers); // refused to load the tampered file

            var entry = registry.List().Single(p => p.Manifest.Id == "demo");
            Assert.Equal(Shonkor.Core.Models.PluginState.Failed, entry.State);
            Assert.Contains("hash", entry.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    // ---- #306: component teardown before ALC unload + optional IPluginHost/IPluginInitializable hook ----

    /// <summary>
    /// A parser that implements <see cref="IAsyncDisposable"/> and, on disposal, writes a marker file whose
    /// path is baked in at compile time. Because the loader disposes components BEFORE unloading their
    /// collectible ALC — and after unload the plugin's code is gone and can no longer run — the marker's
    /// presence after <c>Dispose()</c> returns proves the disposal ran while the context was still loaded.
    /// </summary>
    private static string DisposableParserSource(string markerPath) => $$"""
        using System;
        using System.IO;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Shonkor.Core.Interfaces;
        using Shonkor.Core.Models;

        namespace DisposablePlugin;

        public sealed class DisposableParser : IFileParser, IAsyncDisposable
        {
            public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".disp" };
            public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new List<NodeTypeDescriptor>();

            public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
                => Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((new List<GraphNode>(), new List<GraphEdge>()));

            public ValueTask DisposeAsync()
            {
                File.WriteAllText(@"{{markerPath.Replace("\\", "/")}}", "disposed");
                return ValueTask.CompletedTask;
            }
        }
        """;

    /// <summary>
    /// A post-processor that writes a marker file on synchronous <see cref="IDisposable"/> disposal — proves
    /// the component-teardown path covers post-processors identically to parsers (both are constructed via
    /// <c>Activator.CreateInstance</c>).
    /// </summary>
    private static string DisposablePostProcessorSource(string markerPath) => $$"""
        using System;
        using System.IO;
        using System.Threading.Tasks;
        using Shonkor.Core.Interfaces;
        using Shonkor.Core.Models;

        namespace DisposablePpPlugin;

        public sealed class DisposablePostProcessor : IGraphPostProcessor, IDisposable
        {
            public string Name => "disposable.pp";
            public Task<GraphEnrichment> ProcessAsync(IGraphView graph) => Task.FromResult(GraphEnrichment.Empty);
            public void Dispose() => File.WriteAllText(@"{{markerPath.Replace("\\", "/")}}", "disposed");
        }
        """;

    /// <summary>
    /// An <see cref="IAsyncDisposable"/> parser whose <c>DisposeAsync</c> writes its marker only AFTER a real
    /// <c>await</c> continuation (<c>Task.Yield()</c>). Driven through the async teardown path the marker
    /// proves the continuation actually ran — i.e. the component was awaited, not merely started — which is
    /// exactly what the sync-over-async bridge is there to avoid. The marker's presence after teardown also
    /// proves (as in the sync test) that disposal ran while the collectible ALC was still loaded, BEFORE the
    /// unload — after unload the plugin's type is gone and its code can no longer run.
    /// </summary>
    private static string AsyncDisposableParserSource(string markerPath) => $$"""
        using System;
        using System.IO;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Shonkor.Core.Interfaces;
        using Shonkor.Core.Models;

        namespace AsyncDisposablePlugin;

        public sealed class AsyncDisposableParser : IFileParser, IAsyncDisposable
        {
            public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".adisp" };
            public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new List<NodeTypeDescriptor>();

            public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
                => Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((new List<GraphNode>(), new List<GraphEdge>()));

            public async ValueTask DisposeAsync()
            {
                await Task.Yield();
                File.WriteAllText(@"{{markerPath.Replace("\\", "/")}}", "disposed-async");
            }
        }
        """;

    /// <summary>
    /// Two <see cref="IAsyncDisposable"/> parsers in one plugin: <c>ThrowingParser</c> throws from
    /// <c>DisposeAsync</c>, <c>MarkerParser</c> writes a marker. Proves per-component exception isolation on
    /// the async path — the throwing teardown must not stop the other component's teardown, so the marker is
    /// written regardless of the order the loader disposes them in.
    /// </summary>
    private static string ThrowingAndMarkerAsyncParsersSource(string markerPath) => $$"""
        using System;
        using System.IO;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Shonkor.Core.Interfaces;
        using Shonkor.Core.Models;

        namespace ThrowingAsyncPlugin;

        public sealed class ThrowingParser : IFileParser, IAsyncDisposable
        {
            public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".throw" };
            public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new List<NodeTypeDescriptor>();
            public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
                => Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((new List<GraphNode>(), new List<GraphEdge>()));
            public async ValueTask DisposeAsync()
            {
                await Task.Yield();
                throw new InvalidOperationException("teardown boom");
            }
        }

        public sealed class MarkerParser : IFileParser, IAsyncDisposable
        {
            public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".mark" };
            public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new List<NodeTypeDescriptor>();
            public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
                => Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((new List<GraphNode>(), new List<GraphEdge>()));
            public async ValueTask DisposeAsync()
            {
                await Task.Yield();
                File.WriteAllText(@"{{markerPath.Replace("\\", "/")}}", "disposed-async");
            }
        }
        """;

    /// <summary>
    /// A parser that implements the optional <see cref="IPluginInitializable"/> hook. On
    /// <c>Initialize(host)</c> it writes a marker file (proving the hook ran) and logs through the host's
    /// <see cref="IPluginHost.Logger"/> (proving the supplied logger is usable). With no host the loader must
    /// not call it, so the marker stays absent.
    /// </summary>
    private static string InitializableParserSource(string markerPath) => $$"""
        using System;
        using System.IO;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Microsoft.Extensions.Logging;
        using Shonkor.Core.Interfaces;
        using Shonkor.Core.Models;

        namespace InitPlugin;

        public sealed class InitParser : IFileParser, IPluginInitializable
        {
            public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".init" };
            public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new List<NodeTypeDescriptor>();

            public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
                => Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((new List<GraphNode>(), new List<GraphEdge>()));

            public void Initialize(IPluginHost host)
            {
                File.WriteAllText(@"{{markerPath.Replace("\\", "/")}}", "initialized");
                host.Logger.LogInformation("init-parser-initialized");
            }
        }
        """;

    /// <summary>Captures log messages so a test can assert the host-supplied logger was actually usable.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private sealed class TestPluginHost : IPluginHost
    {
        public CapturingLogger Captured { get; } = new();
        public ILogger Logger => Captured;
    }

    /// <summary>Compiles the given plugin source, packages it as a ZIP, installs and activates it.</summary>
    private static PluginRegistry InstallAndActivate(string ws, string source, string assemblyName, string id)
    {
        var dll = Path.Combine(ws, $"{assemblyName}.dll");
        CompilePluginDll(dll, source, assemblyName);

        var zip = Path.Combine(ws, $"{id}.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var m = archive.CreateEntry("plugin.json");
            using (var w = new StreamWriter(m.Open(), Encoding.UTF8))
            {
                w.Write($$"""{ "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "entryAssembly": "{{assemblyName}}.dll", "minHostApi": "1.0" }""");
            }
            archive.CreateEntryFromFile(dll, $"{assemblyName}.dll");
        }

        var registry = new PluginRegistry(ws);
        Assert.True(registry.InstallFromZip(zip).Success);
        Assert.True(registry.Activate(id).Success);
        return registry;
    }

    [Fact]
    public void Dispose_TearsDownDisposableParser_BeforeUnloadingItsLoadContext()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_plugindispose_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var marker = Path.Combine(ws, "disposed.marker");
        try
        {
            var registry = InstallAndActivate(ws, DisposableParserSource(marker), "DisposablePlugin", "disp");

            var loaded = AssemblyPluginLoader.LoadActive(registry);
            Assert.Single(loaded.Parsers);
            Assert.False(File.Exists(marker)); // not disposed while still in use

            loaded.Dispose();

            // The marker exists only if DisposeAsync ran; it could not have run after the ALC was unloaded
            // (the type would be gone), so its presence proves teardown happened BEFORE the unload.
            Assert.True(File.Exists(marker));
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Dispose_TearsDownDisposablePostProcessor_Too()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_pluginppdispose_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var marker = Path.Combine(ws, "pp-disposed.marker");
        try
        {
            var registry = InstallAndActivate(ws, DisposablePostProcessorSource(marker), "DisposablePpPlugin", "disppp");

            var loaded = AssemblyPluginLoader.LoadActive(registry);
            Assert.Single(loaded.PostProcessors);
            Assert.False(File.Exists(marker));

            loaded.Dispose();

            Assert.True(File.Exists(marker));
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void InitializablePlugin_WithHost_IsInitializedOnce_WithUsableLogger()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_plugininit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var marker = Path.Combine(ws, "init.marker");
        try
        {
            var registry = InstallAndActivate(ws, InitializableParserSource(marker), "InitPlugin", "init");

            var host = new TestPluginHost();
            using var loaded = AssemblyPluginLoader.LoadActive(registry, host);

            Assert.Single(loaded.Parsers);
            Assert.True(File.Exists(marker)); // Initialize ran
            Assert.Equal("init-parser-initialized", Assert.Single(host.Captured.Messages)); // exactly once, usable logger
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void InitializablePlugin_WithoutHost_LoadsFine_AndIsNotInitialized()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_plugininit_nohost_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var marker = Path.Combine(ws, "init.marker");
        try
        {
            var registry = InstallAndActivate(ws, InitializableParserSource(marker), "InitPlugin", "init");

            // Default overload: no host supplied.
            using var loaded = AssemblyPluginLoader.LoadActive(registry);

            Assert.Single(loaded.Parsers);
            Assert.False(File.Exists(marker)); // Initialize was NOT called — no host, no init, no error
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SharedContractAssemblies_IncludeLoggingAbstractions_SoHostLoggerKeepsOneTypeAcrossTheAlcBoundary()
    {
        // IPluginHost.Logger (Microsoft.Extensions.Logging.ILogger) crosses the ALC boundary. If a plugin
        // ships its own Logging.Abstractions the resolver would load it privately and casting the host logger
        // would throw InvalidCastException. The assembly must therefore be served from the host, like
        // Shonkor.Core. This pins that intent so removing the entry is caught as a regression.
        var field = typeof(AssemblyPluginLoader).GetField(
            "SharedContractAssemblies",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        var shared = Assert.IsAssignableFrom<IEnumerable<string>>(field!.GetValue(null));

        Assert.Contains("Shonkor.Core", shared);
        Assert.Contains("Microsoft.Extensions.Logging.Abstractions", shared);
    }

    [Fact]
    public async Task PlainPlugin_WithoutNewInterfaces_LoadsParsesAndDisposes_Unchanged()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_pluginplain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            // The status-quo DemoParser implements neither IPluginInitializable nor IDisposable.
            var registry = InstallAndActivate(ws, PluginSource, "DemoPlugin", "demo");

            // Passing a host must not change behaviour for a plugin that ignores the hook.
            var host = new TestPluginHost();
            var loaded = AssemblyPluginLoader.LoadActive(registry, host);
            Assert.Single(loaded.Parsers);
            Assert.Empty(host.Captured.Messages); // nothing to initialize

            var (nodes, _) = await loaded.Parsers[0].ParseAsync("C:/x/file.demo", "irrelevant");
            Assert.Single(nodes);

            loaded.Dispose(); // no IDisposable to call; must complete cleanly, exactly as before
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisposeAsync_AwaitsAsyncDisposableComponent_BeforeUnloadingItsLoadContext()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_pluginadispose_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var marker = Path.Combine(ws, "adisposed.marker");
        try
        {
            var registry = InstallAndActivate(ws, AsyncDisposableParserSource(marker), "AsyncDisposablePlugin", "adisp");

            var loaded = AssemblyPluginLoader.LoadActive(registry);
            Assert.Single(loaded.Parsers);
            Assert.False(File.Exists(marker)); // not disposed while still in use

            // Async teardown path (#308): the component's DisposeAsync is awaited, not bridged via
            // sync-over-async. Its marker is written only after an inner await continuation, so the marker's
            // presence proves the continuation actually completed — and, as in the sync test, that teardown
            // ran while the ALC was still loaded (BEFORE the unload — after unload the type would be gone).
            await loaded.DisposeAsync();

            Assert.True(File.Exists(marker));
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisposeAsync_IsolatesAThrowingComponent_SoTheOthersStillTearDown()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_pluginadispiso_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var marker = Path.Combine(ws, "iso.marker");
        try
        {
            var registry = InstallAndActivate(ws, ThrowingAndMarkerAsyncParsersSource(marker), "ThrowingAsyncPlugin", "athrow");

            var loaded = AssemblyPluginLoader.LoadActive(registry);
            Assert.Equal(2, loaded.Parsers.Count);

            // One parser's DisposeAsync throws; the exception must be swallowed and must not abort the other
            // component's teardown, mirroring the sync path's per-component isolation.
            await loaded.DisposeAsync(); // must not throw

            Assert.True(File.Exists(marker)); // the non-throwing component was still torn down
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndSafeAfterSyncDispose()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_pluginadispidem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var marker = Path.Combine(ws, "idem.marker");
        try
        {
            var registry = InstallAndActivate(ws, AsyncDisposableParserSource(marker), "AsyncDisposablePlugin", "adisp");

            var loaded = AssemblyPluginLoader.LoadActive(registry);
            loaded.Dispose(); // sync path first
            Assert.True(File.Exists(marker));

            // A subsequent async teardown is a no-op (already unloaded) and must not throw — both paths guard
            // on the same _contexts sentinel, so mixing them across the dual API is safe.
            await loaded.DisposeAsync();
            await loaded.DisposeAsync();
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }
}
