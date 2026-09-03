// Licensed to Shonkor under the MIT License.

using System.Text.Json;

using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Regression tests for #462: a bare type name resolved to the type's own CONSTRUCTOR, whose node carries
/// the type's name, so every impact tool answered about a node with no edges. `edit_plan GraphIndexScanner`
/// reported "No reference sites — safe to change in isolation" for a class with 71 incident edges that
/// ripgrep found in 32 files. The fixture reproduces the shape that caused it: the Class node sits behind
/// a constructor, same-file members and a doc section in the search ranking, outside the window the
/// resolver used to look at.
/// </summary>
public class BareNameResolutionTests
{
    private const string Ns = "Acme.Widgets";

    private static GraphNode Node(string id, string name, string type, string file, string? content = null, int? line = null) => new()
    {
        Id = id,
        Name = name,
        Type = type,
        FilePath = file,
        Content = content ?? string.Empty,
        StartLine = line
    };

    private static async Task<(McpRequestHandler Handler, string Workspace)> SetupAsync(bool secondWidgetClass = false)
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_barename_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(ws, "src"));
        Directory.CreateDirectory(Path.Combine(ws, "docs"));
        var dbPath = Path.Combine(ws, "g.db");

        string F(string rel) => Path.Combine(ws, rel.Replace('/', Path.DirectorySeparatorChar));

        var widgetFile = F("src/Widget.cs");
        var widgetClass = $"{widgetFile}::{Ns}.Widget";
        var widgetCtor = $"{widgetClass}::Constructor#2";

        // A real Class node carries the WHOLE class body as its FTS content. Search ranks by
        // bm25 ascending, which punishes a long document, so the class scores *worse* than its own
        // short members — that is why the Class node was not even in the top SymbolSearchLimit hits.
        var classBody = "public sealed class Widget\n{\n"
            + string.Join("\n", Enumerable.Range(0, 400).Select(i => $"    private readonly int _field{i} = {i}; // housekeeping line {i}"))
            + "\n    public void Assemble() { }\n}\n";

        var nodes = new List<GraphNode>
        {
            Node(widgetFile, "Widget.cs", "File", widgetFile, "// Widget.cs"),
            Node(widgetClass, "Widget", "Class", widgetFile, classBody, 12),
            // The node that used to win: a constructor named after its own type.
            Node(widgetCtor, "Widget", "Constructor", widgetFile, "public Widget(string name, int size) { }", 20),
            // Same-file neighbours, as a real type file has them — these crowd the search window.
            Node($"{widgetFile}::{Ns}.Widget::IsReady", "IsReady", "Property", widgetFile, "public bool IsReady => true; // Widget", 30),
            Node($"{widgetFile}::{Ns}.WidgetResult", "WidgetResult", "Record", widgetFile, "public record WidgetResult(Widget Widget);", 40),
            Node($"{widgetFile}::{Ns}.WidgetState", "WidgetState", "Enum", widgetFile, "public enum WidgetState { Idle } // Widget", 50),
            Node($"{widgetFile}::{Ns}.Widget::Rebuild#0", "Rebuild", "Method", widgetFile, "void Rebuild() { /* Widget */ }", 60),
            // A doc section that mentions the type, like the capsule samples in bench/.
            Node(F("docs/widgets.md") + "::intro", "Widget overview", "MarkdownSection", F("docs/widgets.md"),
                 "`Class`: **Widget** — the Widget type and its Widget lifecycle."),
        };

        var edges = new List<GraphEdge>
        {
            new() { SourceId = widgetFile, TargetId = widgetClass, Relationship = "CONTAINS" },
            new() { SourceId = widgetClass, TargetId = widgetCtor, Relationship = "CONTAINS" },
        };

        // Three real reference sites, on the CLASS node — the ones the tools failed to report.
        foreach (var caller in new[] { "Assembler", "Packer", "Shipper" })
        {
            var file = F($"src/{caller}.cs");
            var id = $"{file}::{Ns}.{caller}";
            nodes.Add(Node(file, $"{caller}.cs", "File", file, $"// {caller}.cs"));
            nodes.Add(Node(id, caller, "Class", file, $"public class {caller} {{ Widget _w; }}", 5));
            edges.Add(new GraphEdge { SourceId = file, TargetId = id, Relationship = "CONTAINS" });
            edges.Add(new GraphEdge { SourceId = id, TargetId = widgetClass, Relationship = "REFERENCES_TYPE", Provenance = Provenance.Extracted });
        }

        if (secondWidgetClass)
        {
            var other = F("src/legacy/Widget.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(other)!);
            nodes.Add(Node(other, "Widget.cs", "File", other, "// legacy Widget.cs"));
            nodes.Add(Node($"{other}::Acme.Legacy.Widget", "Widget", "Class", other, "public class Widget { }", 3));
            edges.Add(new GraphEdge { SourceId = other, TargetId = $"{other}::Acme.Legacy.Widget", Relationship = "CONTAINS" });
        }

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(nodes);
            await storage.UpsertEdgesAsync(edges);
        }

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
            ActiveProjectName = "P"
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

        return (new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true), ws);
    }

    private static string ToolCall(string tool, object args) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args } });

    private static string TextOf(string? response)
    {
        using var doc = JsonDocument.Parse(response!);
        return doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task VerifyExists_BareTypeName_ReportsTheType_NotItsConstructor()
    {
        var (handler, _) = await SetupAsync();

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("verify_exists", new { symbol = "Widget" })));

        Assert.Contains("(Class)", text);
        Assert.DoesNotContain("(Constructor)", text);
    }

    [Fact]
    public async Task EditPlan_BareTypeName_ListsReferenceSites_InsteadOfAFalseAllClear()
    {
        var (handler, _) = await SetupAsync();

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("edit_plan", new { symbol = "Widget" })));

        // The defect: a class with three reference sites was reported as safe to change in isolation.
        Assert.DoesNotContain("safe to change in isolation", text);
        Assert.Contains("3 reference site(s)", text);
        foreach (var caller in new[] { "Assembler", "Packer", "Shipper" })
        {
            Assert.Contains(caller, text);
        }
    }

    [Fact]
    public async Task FindUsages_BareTypeName_FindsTheClassUsages()
    {
        var (handler, _) = await SetupAsync();

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("find_usages", new { symbol = "Widget" })));

        Assert.DoesNotContain("No usages", text);
        Assert.Contains("3 usage(s)", text);
        Assert.Contains("REFERENCES_TYPE", text);
    }

    [Fact]
    public async Task BlastRadius_BareTypeName_ReachesTheReferencingTypes()
    {
        var (handler, _) = await SetupAsync();

        var json = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("blast_radius", new { nodeOrFile = "Widget" })));

        foreach (var caller in new[] { "Assembler", "Packer", "Shipper" })
        {
            Assert.Contains(caller, json);
        }
    }

    [Fact]
    public async Task AmbiguousTypeName_SaysWhichDeclarationItAnswersAbout()
    {
        var (handler, _) = await SetupAsync(secondWidgetClass: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("edit_plan", new { symbol = "Widget" })));

        // Two declarations share the name: the pick must be stated, not made silently.
        Assert.Contains("2 declarations are named 'Widget'", text);
        Assert.Contains("legacy", text);
    }

    [Fact]
    public async Task UnambiguousTypeName_AddsNoAmbiguityNoise()
    {
        var (handler, _) = await SetupAsync();

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("edit_plan", new { symbol = "Widget" })));

        Assert.DoesNotContain("declarations are named", text);
    }
}
