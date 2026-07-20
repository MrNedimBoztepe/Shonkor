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

    // ====================================================================================================
    // #293: symbol nodes (Class/Interface/Function/Method/Property/Enum/TypeAlias) + same-file heritage.
    // Every test below drives the REAL Node sidecar (the TS Compiler API), one file per fixture.
    // ====================================================================================================

    // ---- AC#1: a class with methods + a property yields Class/Method/Property nodes (not just JSComponent) ----

    [Fact]
    public async Task Class_WithMethodAndProperty_EmitsClassMethodPropertyNodes()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Calculator.ts");
        var code =
            "export class Calculator {\n" +
            "  value: number = 0;\n" +
            "  add(n: number): number { return this.value + n; }\n" +
            "}\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, edges) = await parser.ParseAsync(path, code);

        // The module node is still there ...
        Assert.Single(nodes, n => n.Type == "JSComponent");
        // ... and now the symbols exist as their OWN nodes, not folded into the opaque component.
        var cls = Assert.Single(nodes, n => n.Type == "Class");
        Assert.Equal("Calculator", cls.Name);
        var method = Assert.Single(nodes, n => n.Type == "Method");
        Assert.Equal("add", method.Name);
        var property = Assert.Single(nodes, n => n.Type == "Property");
        Assert.Equal("value", property.Name);

        // Members are contained by their declaring type (structural hierarchy).
        Assert.Contains(edges, e => e.Relationship == "CONTAINS" && e.SourceId == cls.Id && e.TargetId == method.Id);
        Assert.Contains(edges, e => e.Relationship == "CONTAINS" && e.SourceId == cls.Id && e.TargetId == property.Id);
    }

    // ---- AC#2: interface / function / enum / type alias each become one node with FilePath + line ----

    [Fact]
    public async Task InterfaceFunctionEnumTypeAlias_EachEmitOneNode_WithFilePathAndLine()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Shapes.ts");
        var code =
            "export interface Shape { area(): number; }\n" + // line 1
            "export function compute(): number { return 1; }\n" + // line 2
            "export enum Status { Active, Inactive }\n" + // line 3
            "export type Id = string;\n"; // line 4
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, _) = await parser.ParseAsync(path, code);

        var iface = Assert.Single(nodes, n => n.Type == "Interface");
        var func = Assert.Single(nodes, n => n.Type == "Function");
        var @enum = Assert.Single(nodes, n => n.Type == "Enum");
        var alias = Assert.Single(nodes, n => n.Type == "TypeAlias");

        Assert.Equal("Shape", iface.Name);
        Assert.Equal("compute", func.Name);
        Assert.Equal("Status", @enum.Name);
        Assert.Equal("Id", alias.Name);

        // FilePath + 1-based line provenance on every symbol node.
        foreach (var n in new[] { iface, func, @enum, alias })
        {
            Assert.Equal(path, n.FilePath);
        }
        Assert.Equal(1, iface.StartLine);
        Assert.Equal(2, func.StartLine);
        Assert.Equal(3, @enum.StartLine);
        Assert.Equal(4, alias.StartLine);
    }

    // ---- AC#3: same-file `extends` / `implements` become EXTENDS / IMPLEMENTS edges to the target node ----

    [Fact]
    public async Task ExtendsAndImplements_SameFile_EmitHeritageEdgesToTargetNodes()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Circle.ts");
        var code =
            "interface Drawable { draw(): void; }\n" +
            "class Shape {}\n" +
            "export class Circle extends Shape implements Drawable {\n" +
            "  draw(): void {}\n" +
            "}\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, edges) = await parser.ParseAsync(path, code);

        var circle = Assert.Single(nodes, n => n.Type == "Class" && n.Name == "Circle");
        var shape = Assert.Single(nodes, n => n.Type == "Class" && n.Name == "Shape");
        var drawable = Assert.Single(nodes, n => n.Type == "Interface" && n.Name == "Drawable");

        var ext = Assert.Single(edges, e => e.Relationship == "EXTENDS");
        Assert.Equal(circle.Id, ext.SourceId);
        Assert.Equal(shape.Id, ext.TargetId); // edge points at the actual same-file target node

        var impl = Assert.Single(edges, e => e.Relationship == "IMPLEMENTS");
        Assert.Equal(circle.Id, impl.SourceId);
        Assert.Equal(drawable.Id, impl.TargetId);
    }

    [Fact]
    public async Task Heritage_ToImportedBase_IsNotResolvedCrossFile()
    {
        // Ticket scope: purely syntactic, SAME-FILE only. A base declared elsewhere yields no heritage edge
        // here (cross-file resolution is #294) — proven by the absence of a dangling EXTENDS.
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Widget.tsx");
        var code =
            "import { Component } from 'react';\n" +
            "export class Widget extends Component {}\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, edges) = await parser.ParseAsync(path, code);

        Assert.Single(nodes, n => n.Type == "Class" && n.Name == "Widget");
        Assert.DoesNotContain(edges, e => e.Relationship == "EXTENDS");
    }

    // ---- AC#4: the Sitecore JSS / withDatasourceCheck signals survive on the JSComponent node ----

    [Fact]
    public async Task SitecoreSignals_ArePreservedOnComponentNode_AfterSymbolMigration()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Hero.tsx");
        var code =
            "import { withDatasourceCheck } from '@sitecore-jss/sitecore-jss-nextjs';\n" +
            "export class Hero {}\n" +
            "export default withDatasourceCheck()(Hero);\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, _) = await parser.ParseAsync(path, code);

        // Signals are deliberately kept on the module/component node, not migrated onto a symbol node.
        var component = Assert.Single(nodes, n => n.Type == "JSComponent");
        Assert.Equal("true", component.Properties["isSitecoreJSS"]);
        Assert.Equal("true", component.Properties["withDatasourceCheck"]);
        // And the symbol nodes coexist (migration did not drop the component).
        Assert.Contains(nodes, n => n.Type == "Class" && n.Name == "Hero");
    }

    // ---- AC#5: syntactic heritage edges are INFERRED (no false EXTRACTED claim; exact tiering is #295) ----

    [Fact]
    public async Task HeritageEdges_AreInferred_NotExtracted()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Model.ts");
        var code =
            "class Base {}\n" +
            "export class Derived extends Base {}\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (_, edges) = await parser.ParseAsync(path, code);

        // The parser's baseline tier is INFERRED: the host stamps every non-structural edge (EXTENDS /
        // IMPLEMENTS are not in the structural set) with this, so pure-syntax heritage never over-claims
        // EXTRACTED. (CONTAINS stays EXTRACTED by the deterministic-containment rule — TICKET-207.)
        Assert.Equal(Provenance.Inferred, parser.DefaultProvenance);
        Assert.Contains(edges, e => e.Relationship == "EXTENDS");
    }

    // ---- AC#6: node ids are case-sensitive and never collide with the module node (BUG-012 guard) ----

    [Fact]
    public async Task SymbolIds_AreCaseSensitive_AndNeverCollideWithComponentNode()
    {
        // The canonical BUG-012 trap: a class named exactly like its file (ubiquitous for React class
        // components). The module node is `{path}::Button`; the class must NOT reuse that id (which the
        // store's ON CONFLICT(Id) upsert would collapse, losing the signal-bearing component).
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Button.tsx");
        var code = "export class Button { onClick(): void {} }\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, _) = await parser.ParseAsync(path, code);

        var component = Assert.Single(nodes, n => n.Type == "JSComponent");
        var cls = Assert.Single(nodes, n => n.Type == "Class");
        Assert.Equal($"{path}::Button", component.Id);
        Assert.NotEqual(component.Id, cls.Id); // no collision -> component survives
        Assert.Equal($"{path}::Button::Button", cls.Id);

        // Every id is unique (no silent overwrite of any node).
        Assert.Equal(nodes.Count, nodes.Select(n => n.Id).Distinct().Count());

        // Ids are used VERBATIM — the uppercase 'B' is preserved, never lowercased (the BUG-012 defect).
        Assert.All(nodes, n => Assert.Contains("Button", n.Id, StringComparison.Ordinal));
        Assert.DoesNotContain(nodes, n => n.Id.Contains("button.tsx::button", StringComparison.Ordinal));
    }

    // ====================================================================================================
    // #317: member-id qualification — same-name get/set accessors and method overloads no longer collapse
    // onto one node under the store's ON CONFLICT(Id) upsert.
    // ====================================================================================================

    // ---- AC#1 + AC#2 (accessors): a get and set of the SAME name become TWO distinct kind-qualified nodes ----

    [Fact]
    public async Task GetterAndSetter_SameName_EmitTwoDistinctKindQualifiedNodes()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Box.ts");
        var code =
            "export class Box {\n" +
            "  private _v = 0;\n" +
            "  get value(): number { return this._v; }\n" +
            "  set value(v: number) { this._v = v; }\n" +
            "}\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, _) = await parser.ParseAsync(path, code);

        var cls = Assert.Single(nodes, n => n.Type == "Class" && n.Name == "Box");
        // Both accessors surface, both named "value" (verbatim), but as TWO distinct nodes ...
        var accessors = nodes.Where(n => n.Type == "Method" && n.Name == "value").ToList();
        Assert.Equal(2, accessors.Count);
        // ... discriminated by accessor kind in the id suffix (`:get` / `:set`).
        Assert.Contains(accessors, n => n.Id == $"{cls.Id}::value:get");
        Assert.Contains(accessors, n => n.Id == $"{cls.Id}::value:set");
        Assert.Equal(2, accessors.Select(n => n.Id).Distinct().Count());

        // AC#3: the distinct-id invariant is HARD here — this fixture has multiple members, so before #317
        // (a single `::Box::value` for both) it would fail; it is no longer false-passing on one member.
        Assert.Equal(nodes.Count, nodes.Select(n => n.Id).Distinct().Count());

        // BUG-012 guard: names are verbatim / case-sensitive, never lowercased.
        Assert.All(accessors, n => Assert.Contains("value:", n.Id, StringComparison.Ordinal));
    }

    // ---- AC#1 + AC#2 (overloads): an overload set dedups to ONE node anchored at the implementation ----

    [Fact]
    public async Task MethodOverloads_DedupToSingleImplementationNode()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Api.ts");
        var code =
            "export class Api {\n" +
            "  foo(x: number): void;\n" + // line 2: overload signature (no body)
            "  foo(x: string): void;\n" + // line 3: overload signature (no body)
            "  foo(x: any): void { }\n" + // line 4: implementation (body)
            "}\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, _) = await parser.ParseAsync(path, code);

        var cls = Assert.Single(nodes, n => n.Type == "Class" && n.Name == "Api");
        // Chosen semantics: one logical method = one node (the bodyless overload signatures are NOT emitted
        // as separate member nodes). See sidecar/nodeIds.js for the rationale.
        var foo = Assert.Single(nodes, n => n.Type == "Method" && n.Name == "foo");
        Assert.Equal($"{cls.Id}::foo", foo.Id);
        // The node is anchored at the implementation (line 4), not a bodyless signature.
        Assert.Equal(4, foo.StartLine);

        // Distinct-id invariant holds against the overload set (before #317 it emitted three `::Api::foo`).
        Assert.Equal(nodes.Count, nodes.Select(n => n.Id).Distinct().Count());
    }

    // ---- AC#3: the distinct-id invariant, hardened against a combined get/set + overload fixture ----

    [Fact]
    public async Task MemberIds_AreDistinct_AcrossAccessorsAndOverloads_NoSilentOverwrite()
    {
        var dir = NewProjectDir();
        var path = Path.Combine(dir, "Combined.ts");
        var code =
            "export class Combined {\n" +
            "  get value(): number { return 0; }\n" +
            "  set value(v: number) {}\n" +
            "  bar(x: number): void;\n" +
            "  bar(x: string): void;\n" +
            "  bar(x: any): void {}\n" +
            "}\n";
        await File.WriteAllTextAsync(path, code);

        var host = new TestHost();
        await using var parser = new TypeScriptParser();
        parser.Initialize(host);

        var (nodes, _) = await parser.ParseAsync(path, code);

        // value:get, value:set, bar -> three member nodes, all distinct; the overload signatures add none.
        var members = nodes.Where(n => n.Type == "Method").ToList();
        Assert.Equal(3, members.Count);
        // Every id is unique across the whole node set — no silent overwrite under ON CONFLICT(Id).
        Assert.Equal(nodes.Count, nodes.Select(n => n.Id).Distinct().Count());
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
