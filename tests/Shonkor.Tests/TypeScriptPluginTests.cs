// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Logging;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Plugin.TypeScript;

namespace Shonkor.Tests;

/// <summary>
/// Acceptance-criteria coverage for the #292 TypeScript base plugin: the real Node sidecar round-trip, the
/// surfacing (not swallowing) of parse diagnostics, clean degradation when Node is unavailable, tsconfig
/// loading, and the bounded per-request timeout. These drive the actual <see cref="TypeScriptParser"/>
/// against a real <c>node</c> process (Node is available in the test environment).
/// </summary>
public sealed class TypeScriptPluginTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string NewProjectDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shonkor-ts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- AC#1: real process round-trip yields the JSComponent/IMPORTS shape ----

    [Fact]
    public async Task RoundTrip_OverRealSidecar_ProducesJsComponentAndResolvedImport()
    {
        var dir = NewProjectDir();
        var buttonPath = Path.Combine(dir, "Button.tsx");
        var appPath = Path.Combine(dir, "App.tsx");
        await File.WriteAllTextAsync(buttonPath, "export const Button = () => null;");
        var appCode = "import { Button } from './Button';\nexport const App = () => Button;\n";
        await File.WriteAllTextAsync(appPath, appCode);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, edges) = await parser.ParseAsync(appPath, appCode);

        var component = Assert.Single(nodes, n => n.Type == "JSComponent");
        Assert.Equal($"{appPath}::App", component.Id);
        Assert.Equal("App", component.Name);

        Assert.Contains(edges, e => e.Relationship == "CONTAINS" && e.SourceId == appPath && e.TargetId == component.Id);

        var import = Assert.Single(edges, e => e.Relationship == "IMPORTS");
        Assert.Equal(component.Id, import.SourceId);
        Assert.Equal("./Button", import.Properties["rawSource"]);
        // The relative import resolves to the real Button.tsx on disk (id parity with the former parser).
        Assert.Equal(buttonPath, import.TargetId);
    }

    // ---- AC#2: advanced TS is NOT silently dropped; genuine syntax errors surface as diagnostics ----

    [Fact]
    public async Task AdvancedTypeScript_IsNotDropped_AndProducesNoFalseError()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Advanced.ts");
        var code = """
            import { Service } from './service';
            enum Color { Red, Green, Blue }
            function identity<T>(value: T): T { return value; }
            @sealed
            class Widget<T extends object> {
                constructor(private readonly value: T) {}
            }
            export { Widget, Color, identity };
            """;
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, edges) = await parser.ParseAsync(path, code);

        // Unlike the former Esprima tolerant parse (which threw on this and dropped the file), the component
        // is produced and its import is captured.
        Assert.Contains(nodes, n => n.Type == "JSComponent");
        Assert.Contains(edges, e => e.Relationship == "IMPORTS" && e.Properties["rawSource"] == "./service");
        // Valid advanced syntax must not be reported as a parse error.
        Assert.DoesNotContain(host.Logger.Messages, m => m.Contains("parse diagnostic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SyntaxError_SurfacesAsDiagnostic_NotSwallowed()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Broken.ts");
        var code = "const x: = ;\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, _) = await parser.ParseAsync(path, code);

        // The file is still represented (not silently dropped) ...
        Assert.Contains(nodes, n => n.Type == "JSComponent");
        // ... and the parse error is surfaced through the host diagnostics channel.
        Assert.Contains(host.Logger.Messages, m => m.Contains("parse diagnostic", StringComparison.OrdinalIgnoreCase));
    }

    // ---- AC#3: Node unavailable -> visible degradation to the Esprima fallback, no crash ----

    [Fact]
    public async Task NodeUnavailable_DegradesToEsprimaFallback_WithDiagnostic_NoCrash()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Plain.js");
        var code = "import a from './a';\nexport const foo = 1;\n";
        await File.WriteAllTextAsync(path, code);

        // An explicit-but-bogus NodePath is authoritative: discovery does not fall through to a real node.
        var settings = new SidecarSettings { NodePath = Path.Combine(dir, "no-such-node.exe") };
        var host = new TestHost();
        await using var parser = new TypeScriptParser(settings);
        parser.Initialize(host);

        var (nodes, edges) = await parser.ParseAsync(path, code); // must not throw

        Assert.Contains(nodes, n => n.Type == "JSComponent");
        Assert.Contains(edges, e => e.Relationship == "IMPORTS"); // Esprima still extracted the import
        Assert.Contains(host.Logger.Messages, m => m.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
    }

    // ---- AC#4: a tsconfig in the project is loaded ----

    [Fact]
    public async Task Tsconfig_IsLoaded_AndAnnouncedAsDiagnostic()
    {
        var dir = NewProjectDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "tsconfig.json"),
            """{ "compilerOptions": { "target": "ES2022", "strict": true } }""");
        var path = Path.Combine(dir, "index.ts");
        var code = "export const n = 1;\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        await parser.ParseAsync(path, code);

        Assert.Contains(host.Logger.Messages, m => m.Contains("tsconfig", StringComparison.OrdinalIgnoreCase));
    }

    // ---- AC#5: a hanging parse hits the bounded timeout (diagnostic), never a deadlock ----

    [Fact]
    public async Task Timeout_FiresDiagnostic_AndFallsBack_NoDeadlock()
    {
        // Enable the sidecar's deterministic hang hook for any process spawned from here on (harmless to
        // other tests: it only triggers on the sentinel below).
        Environment.SetEnvironmentVariable("SHONKOR_SIDECAR_TEST_HOOKS", "1");

        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Hang.ts");
        var code = "/* __SHONKOR_TEST_HANG__ */ export const x = 1;\n";
        await File.WriteAllTextAsync(path, code);

        var settings = new SidecarSettings { TimeoutSeconds = 1 };
        var host = new TestHost();
        await using var parser = new TypeScriptParser(settings);
        parser.Initialize(host);

        // The overall call must complete well within a generous bound — proof there is no deadlock.
        var parseTask = parser.ParseAsync(path, code);
        var completed = await Task.WhenAny(parseTask, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(parseTask, completed);

        var (nodes, _) = await parseTask;
        Assert.Contains(nodes, n => n.Type == "JSComponent"); // fell back to Esprima
        Assert.Contains(host.Logger.Messages, m => m.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    // ---- test doubles ----

    private sealed class TestHost : IPluginHost
    {
        public CapturingLogger Logger { get; } = new();
        ILogger IPluginHost.Logger => Logger;
    }

    private sealed class CapturingLogger : ILogger
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
}
