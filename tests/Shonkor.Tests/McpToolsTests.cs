// Licensed to Shonkor under the MIT License.

using System.Text.Json;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Tests the AI-facing MCP tools: impact analysis (incoming references), reusable short node handles
/// that round-trip as seeds, AI summaries in the subgraph output, and the maxChars token budget.
/// </summary>
public class McpToolsTests
{
    private static async Task<(ProjectManager Pm, ContextCapsuleSynthesizer Synth, string Workspace)> SetupAsync()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_mcp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");

        var widgetId = Path.Combine(ws, "Widget.cs") + "::Widget";
        var gadgetId = Path.Combine(ws, "Gadget.cs") + "::Gadget";

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = widgetId, Name = "Widget", Type = "Class", FilePath = Path.Combine(ws, "Widget.cs"), StartLine = 1, Content = "class Widget {}", Summary = "A reusable widget.", Embedding = new[] { 1f, 0f, 0f } },
                new GraphNode { Id = gadgetId, Name = "Gadget", Type = "Class", FilePath = Path.Combine(ws, "Gadget.cs"), StartLine = 1, Content = "class Gadget { Widget w; }", Summary = "Depends on Widget.", Embedding = new[] { 0f, 1f, 0f } },
                // Interaction nodes for get_open_threads (one open task, one done task, one open question).
                new GraphNode { Id = "task::open", Name = "Implement caching", Type = "Task", Content = "todo", Properties = new() { ["status"] = "Todo" } },
                new GraphNode { Id = "task::done", Name = "Old finished task", Type = "Task", Content = "done", Properties = new() { ["status"] = "Done" } },
                new GraphNode { Id = "task::donespace", Name = "Trimmed done task", Type = "Task", Content = "done", Properties = new() { ["status"] = "Done " } },
                new GraphNode { Id = "question::open", Name = "Why is X slow", Type = "Question", Content = "q", Properties = new() { ["status"] = "Open" } },
                // Tier-2 fixtures (unconnected to Widget/Gadget): an interface + impl, and a test referencing Widget.
                new GraphNode { Id = Path.Combine(ws, "IThing.cs") + "::IThing", Name = "IThing", Type = "Interface", FilePath = Path.Combine(ws, "IThing.cs"), StartLine = 1, Content = "interface IThing" },
                new GraphNode { Id = Path.Combine(ws, "Impl.cs") + "::Impl", Name = "Impl", Type = "Class", FilePath = Path.Combine(ws, "Impl.cs"), StartLine = 1, Content = "class Impl : IThing", Summary = "Implements IThing." },
                new GraphNode { Id = Path.Combine(ws, "WidgetTests.cs") + "::WidgetTests", Name = "WidgetTests", Type = "Class", FilePath = Path.Combine(ws, "WidgetTests.cs"), StartLine = 7, Content = "class WidgetTests { Widget sut; }" },
                // Tier-3 fixtures (unconnected): a file with a class + method, for signature/outline.
                new GraphNode { Id = Path.Combine(ws, "Calc.cs"), Name = "Calc.cs", Type = "File", FilePath = Path.Combine(ws, "Calc.cs") },
                new GraphNode { Id = Path.Combine(ws, "Calc.cs") + "::Calc", Name = "Calc", Type = "Class", FilePath = Path.Combine(ws, "Calc.cs"), StartLine = 1, Content = "class Calc" },
                new GraphNode { Id = Path.Combine(ws, "Calc.cs") + "::Calc::Add", Name = "Add", Type = "Method", FilePath = Path.Combine(ws, "Calc.cs"), StartLine = 3, Content = "int Add(int a, int b) => a + b;", Properties = new() { ["returnType"] = "int", ["modifiers"] = "public", ["parameters"] = "int a, int b" } },
                // call_hierarchy fixture: Caller --calls--> Add.
                new GraphNode { Id = Path.Combine(ws, "Calc.cs") + "::Calc::Caller", Name = "Caller", Type = "Method", FilePath = Path.Combine(ws, "Calc.cs"), StartLine = 5, Content = "void Caller() => Add(1, 2);" },
                // Transitive-test fixture: ConsumerTests (a test) -> Consumer -> Widget (Widget reached at 2 hops).
                new GraphNode { Id = Path.Combine(ws, "Consumer.cs") + "::Consumer", Name = "Consumer", Type = "Class", FilePath = Path.Combine(ws, "Consumer.cs"), StartLine = 1, Content = "class Consumer { Widget w; }" },
                new GraphNode { Id = Path.Combine(ws, "ConsumerTests.cs") + "::ConsumerTests", Name = "ConsumerTests", Type = "Class", FilePath = Path.Combine(ws, "ConsumerTests.cs"), StartLine = 1, Content = "class ConsumerTests { Consumer sut; }" }
            });
            await storage.UpsertEdgesAsync(new[]
            {
                new GraphEdge { SourceId = gadgetId, TargetId = widgetId, Relationship = "REFERENCES_TYPE" },
                // IMPLEMENTS targets the base type by NAME (as the parser emits it).
                new GraphEdge { SourceId = Path.Combine(ws, "Impl.cs") + "::Impl", TargetId = "IThing", Relationship = "IMPLEMENTS" },
                // A test in a *Tests.cs file references Widget.
                new GraphEdge { SourceId = Path.Combine(ws, "WidgetTests.cs") + "::WidgetTests", TargetId = widgetId, Relationship = "REFERENCES_TYPE" },
                // Calc.cs CONTAINS Calc CONTAINS Add (for outline).
                new GraphEdge { SourceId = Path.Combine(ws, "Calc.cs"), TargetId = Path.Combine(ws, "Calc.cs") + "::Calc", Relationship = "CONTAINS" },
                new GraphEdge { SourceId = Path.Combine(ws, "Calc.cs") + "::Calc", TargetId = Path.Combine(ws, "Calc.cs") + "::Calc::Add", Relationship = "CONTAINS" },
                new GraphEdge { SourceId = Path.Combine(ws, "Calc.cs") + "::Calc", TargetId = Path.Combine(ws, "Calc.cs") + "::Calc::Caller", Relationship = "CONTAINS" },
                new GraphEdge { SourceId = Path.Combine(ws, "Calc.cs") + "::Calc::Caller", TargetId = Path.Combine(ws, "Calc.cs") + "::Calc::Add", Relationship = "CALLS" },
                // ConsumerTests -> Consumer -> Widget: a test reaching Widget transitively (2 hops).
                new GraphEdge { SourceId = Path.Combine(ws, "Consumer.cs") + "::Consumer", TargetId = widgetId, Relationship = "REFERENCES_TYPE" },
                new GraphEdge { SourceId = Path.Combine(ws, "ConsumerTests.cs") + "::ConsumerTests", TargetId = Path.Combine(ws, "Consumer.cs") + "::Consumer", Relationship = "REFERENCES_TYPE" }
            });
        }

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
            ActiveProjectName = "P"
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

        return (new ProjectManager(ws), new ContextCapsuleSynthesizer(), ws);
    }

    private static string ToolCall(string tool, object args) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args } });

    private static string TextOf(string? response)
    {
        using var doc = JsonDocument.Parse(response!);
        return doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task ImpactOf_ListsDependentsWithRelationAndSummary()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Widget" })));

        Assert.Contains("Gadget", text);             // the dependent is listed
        Assert.Contains("REFERENCES_TYPE", text);    // grouped by relation
        Assert.Contains("Depends on Widget.", text); // the dependent's AI summary is included
        Assert.Contains("[extracted]", text);        // 0.1d: each edge is tagged with its provenance tier
    }

    [Fact]
    public async Task Hotspots_RanksCentralNodesByBetweenness()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("hotspots", new { })));

        // Widget is referenced by several types (and reached transitively via Consumer), so it brokers the
        // most shortest paths — it must rank as a hotspot, and each line carries the centrality metrics.
        Assert.Contains("hotspot", text);
        Assert.Contains("Widget", text);
        Assert.Contains("betweenness=", text);
    }

    [Fact]
    public async Task SurprisingConnections_FindsSimilarButUnlinkedNodes()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_surprise_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");
        try
        {
            using (var storage = new SqliteGraphStorageProvider(dbPath))
            {
                await storage.InitializeAsync();
                // Two nodes with identical embeddings and NO edge between them → a surprising connection.
                await storage.UpsertNodesAsync(new[]
                {
                    new GraphNode { Id = Path.Combine(ws, "Alpha.cs") + "::Alpha", Name = "Alpha", Type = "Class", FilePath = Path.Combine(ws, "Alpha.cs"), StartLine = 1, Content = "class Alpha {}", Embedding = new[] { 1f, 0f, 0f } },
                    new GraphNode { Id = Path.Combine(ws, "Beta.cs") + "::Beta", Name = "Beta", Type = "Class", FilePath = Path.Combine(ws, "Beta.cs"), StartLine = 1, Content = "class Beta {}", Embedding = new[] { 1f, 0f, 0f } }
                });
            }

            var registry = new
            {
                Organizations = Array.Empty<object>(),
                Users = Array.Empty<object>(),
                Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
                ActiveProjectName = "P"
            };
            File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

            var handler = new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true);
            var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("surprising_connections", new { })));

            Assert.Contains("Alpha", text);
            Assert.Contains("Beta", text);
            Assert.Contains("similarity=", text);
            Assert.Contains("INFERRED", text); // must be labelled as inferred, never presented as a proven edge
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(ws, true);
        }
    }

    [Fact]
    public async Task Clusters_ReportsModularityCommunities_AndComponents()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Default mode = modularity communities.
        var modularity = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("clusters", new { })));
        Assert.Contains("modularity community", modularity);

        // Explicit components mode: the fixture has several disconnected pieces (Widget graph, Impl/IThing,
        // Calc, isolated tasks) → more than one connected cluster.
        var comps = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("clusters", new { mode = "components" })));
        Assert.Contains("connected cluster", comps);
    }

    [Fact]
    public async Task Audit_ProducesBriefingWithTrustMixAndHotspots()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("audit", new { })));

        Assert.Contains("# Graph Audit", text);
        Assert.Contains("Trust mix", text);
        Assert.Contains("EXTRACTED", text);
        Assert.Contains("god nodes", text);
        Assert.Contains("Suggested starting points", text);
        Assert.Contains("Widget", text); // the central node shows up as a hotspot / suggested reference
    }

    [Fact]
    public async Task References_ProvenanceFilter_ExcludesInferredWhenExtractedOnly()
    {
        // 0.1d: a caller can demand hard-extracted-only impact, and every edge shows its tier.
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_prov_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");
        var target = Path.Combine(ws, "Target.cs") + "::Target";
        var hard = Path.Combine(ws, "HardDep.cs") + "::HardDep";
        var soft = Path.Combine(ws, "SoftDep.cs") + "::SoftDep";
        try
        {
            using (var storage = new SqliteGraphStorageProvider(dbPath))
            {
                await storage.InitializeAsync();
                await storage.UpsertNodesAsync(new[]
                {
                    new GraphNode { Id = target, Name = "Target", Type = "Class", FilePath = Path.Combine(ws, "Target.cs"), StartLine = 1, Content = "class Target {}" },
                    new GraphNode { Id = hard, Name = "HardDep", Type = "Class", FilePath = Path.Combine(ws, "HardDep.cs"), StartLine = 1, Content = "class HardDep { Target t; }" },
                    new GraphNode { Id = soft, Name = "SoftDep", Type = "Class", FilePath = Path.Combine(ws, "SoftDep.cs"), StartLine = 1, Content = "class SoftDep { Target t; }" }
                });
                await storage.UpsertEdgesAsync(new[]
                {
                    new GraphEdge { SourceId = hard, TargetId = target, Relationship = "REFERENCES_TYPE", Provenance = Provenance.Extracted },
                    new GraphEdge { SourceId = soft, TargetId = target, Relationship = "REFERENCES_TYPE", Provenance = Provenance.Inferred }
                });
            }

            var registry = new
            {
                Organizations = Array.Empty<object>(),
                Users = Array.Empty<object>(),
                Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
                ActiveProjectName = "P"
            };
            File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

            var handler = new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true);

            // No filter → both dependents, each tagged with its tier.
            var all = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Target" })));
            Assert.Contains("HardDep", all);
            Assert.Contains("SoftDep", all);
            Assert.Contains("[extracted]", all);
            Assert.Contains("[inferred]", all);

            // provenance=extracted → the inferred dependent is excluded.
            var hardOnly = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Target", provenance = "extracted" })));
            Assert.Contains("HardDep", hardOnly);
            Assert.DoesNotContain("SoftDep", hardOnly);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(ws, true);
        }
    }

    [Fact]
    public async Task ToolsList_EveryTool_ExposesProjectName_ExceptSetProject()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P");

        var listMsg = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        using var doc = JsonDocument.Parse((await handler.ProcessJsonRpcMessageAsync(listMsg))!);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");

        // set_project switches the active project itself, so it is intentionally project-agnostic.
        var exempt = new HashSet<string> { "set_project" };

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString()!;
            if (exempt.Contains(name)) continue;
            var props = tool.GetProperty("inputSchema").GetProperty("properties");
            Assert.True(props.TryGetProperty("projectName", out _), $"Tool '{name}' is missing the 'projectName' argument.");
        }
    }

    [Fact]
    public async Task Initialize_EchoesClientProtocol_AndReportsAssemblyVersion()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P");

        var initMsg = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { protocolVersion = "2025-03-26" }
        });
        using var doc = JsonDocument.Parse((await handler.ProcessJsonRpcMessageAsync(initMsg))!);
        var result = doc.RootElement.GetProperty("result");

        // The client's requested protocol revision is echoed back (negotiation).
        Assert.Equal("2025-03-26", result.GetProperty("protocolVersion").GetString());
        // The version is read from the running assembly (not a hardcoded string) so it's always populated.
        // The exact value is the test host's here; the SSOT (csproj/Directory.Build.props) is checked via the binary.
        var version = result.GetProperty("serverInfo").GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public async Task CallHierarchy_ResolvesCallersAndCallees_OverCallsEdges()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // callers of Add: Caller calls it.
        var callers = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("call_hierarchy", new { symbol = "Add", direction = "callers" })));
        Assert.Contains("Caller", callers);
        Assert.Contains("<--calls--", callers);

        // callees of Caller: it calls Add.
        var callees = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("call_hierarchy", new { symbol = "Caller", direction = "callees" })));
        Assert.Contains("Add", callees);
        Assert.Contains("--calls-->", callees);

        // A method with no callers reports the empty hint (and mentions semantic indexing).
        var none = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("call_hierarchy", new { symbol = "Caller", direction = "callers" })));
        Assert.Contains("no callers", none);
    }

    [Fact]
    public async Task ImpactOf_OnMethod_ShowsCallers_NotStructuralContainment()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Add is contained by Calc (CONTAINS) and called by Caller (CALLS). Method-level impact = the caller,
        // not the enclosing type: the structural CONTAINS edge is filtered out.
        var impact = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Add" })));
        Assert.Contains("CALLS", impact);
        Assert.Contains("Caller", impact);
        Assert.DoesNotContain("CONTAINS", impact);

        var usages = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("find_usages", new { symbol = "Add" })));
        Assert.Contains("Caller", usages);
        Assert.DoesNotContain("CONTAINS", usages);
    }

    [Fact]
    public async Task BlastRadius_ListsTransitiveImpact_FlagsTests()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Widget is referenced by Gadget and by WidgetTests (a test). Blast radius shows both, flags the
        // test, reports a test count, and excludes structural containment.
        var blast = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Widget", direction = "used_by", depth = 3 })));
        Assert.Contains("Gadget", blast);
        Assert.Contains("WidgetTests", blast);
        Assert.Contains("REFERENCES_TYPE", blast);
        Assert.Contains("[test]", blast);
        Assert.Contains("test(s)", blast);
        Assert.DoesNotContain("CONTAINS", blast);

        // For a method, the radius is its callers (CALLS).
        var methodBlast = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Add", direction = "used_by", depth = 3 })));
        Assert.Contains("Caller", methodBlast);
        Assert.Contains("CALLS", methodBlast);
    }

    [Fact]
    public async Task RenamePlan_ListsDeclarationAndCallerSites_OverloadPrecise()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Add is called by Caller (CALLS) and contained by Calc (CONTAINS). The rename plan lists the
        // declaration + the caller site, notes overload precision, shows the new name, and excludes the
        // structural CONTAINS edge (the parent type isn't a rename site).
        var plan = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("rename_plan", new { symbol = "Add", new_name = "Sum" })));

        Assert.Contains("Rename plan", plan);
        Assert.Contains("'Sum'", plan);            // new name shown
        Assert.Contains("declaration", plan);
        Assert.Contains("CALLS", plan);
        Assert.Contains("Caller", plan);
        Assert.Contains("overload-precise", plan);
        Assert.DoesNotContain("CONTAINS", plan);
    }

    [Fact]
    public async Task Review_BriefsCompile_Impact_AndTests_ForChangedFiles()
    {
        var (pm, synth, ws) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // "Change" Widget.cs: the review aggregates its impact (Gadget/Consumer reference Widget) and the
        // tests reaching it (WidgetTests direct, ConsumerTests via 2 hops).
        var review = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("review", new { paths = new[] { Path.Combine(ws, "Widget.cs") } })));

        Assert.Contains("Review of 1 changed file", review);
        Assert.Contains("COMPILE:", review);
        Assert.Contains("IMPACT:", review);
        Assert.Contains("TESTS TO RUN", review);
        Assert.Contains("WidgetTests", review);
        Assert.Contains("ConsumerTests", review);
        Assert.Contains("RISK:", review);
    }

    [Fact]
    public async Task Architecture_ListsModules_AndCrossModuleDependencyDiagram()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_arch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");
        try
        {
            var coreId = Path.Combine(ws, "src", "Core", "A.cs") + "::CoreType";
            var infraId = Path.Combine(ws, "src", "Infra", "B.cs") + "::InfraType";
            using (var storage = new SqliteGraphStorageProvider(dbPath))
            {
                await storage.InitializeAsync();
                await storage.UpsertNodesAsync(new[]
                {
                    new GraphNode { Id = coreId, Name = "CoreType", Type = "Class", FilePath = Path.Combine(ws, "src", "Core", "A.cs"), StartLine = 1 },
                    new GraphNode { Id = infraId, Name = "InfraType", Type = "Class", FilePath = Path.Combine(ws, "src", "Infra", "B.cs"), StartLine = 1 }
                });
                // Infra depends on Core.
                await storage.UpsertEdgesAsync(new[] { new GraphEdge { SourceId = infraId, TargetId = coreId, Relationship = "REFERENCES_TYPE" } });
            }

            var registry = new
            {
                Organizations = Array.Empty<object>(),
                Users = Array.Empty<object>(),
                Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
                ActiveProjectName = "P"
            };
            File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));
            var handler = new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true);

            var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("architecture", new { })));

            Assert.Contains("Core", text);
            Assert.Contains("Infra", text);
            Assert.Contains("graph LR", text);            // Mermaid module diagram
            Assert.Contains("m_Infra -->", text);         // Infra -> Core cross-module edge
            Assert.Contains("m_Core", text);
        }
        finally
        {
            try { if (Directory.Exists(ws)) Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Tools_AnnotateStaleness_WhenFileEditedSinceIndexing()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_stale_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");
        var file = Path.Combine(ws, "Foo.cs");
        try
        {
            await File.WriteAllTextAsync(file, "namespace N { public class Foo { } }");
            using (var storage = new SqliteGraphStorageProvider(dbPath))
            {
                await storage.InitializeAsync();
                var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
                await scanner.ScanDirectoryAsync(ws, Array.Empty<string>());
            }

            var registry = new
            {
                Organizations = Array.Empty<object>(),
                Users = Array.Empty<object>(),
                Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
                ActiveProjectName = "P"
            };
            File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

            // fileParsers enabled -> the handler can hash the file and detect staleness.
            var handler = new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P",
                lockToContextProject: true, fileParsers: new IFileParser[] { new RoslynAstParser() });

            var fresh = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Foo" })));
            Assert.DoesNotContain("stale", fresh, StringComparison.OrdinalIgnoreCase);

            // Edit the file on disk WITHOUT reindexing -> analysis must warn it may be stale.
            await File.WriteAllTextAsync(file, "namespace N { public class Foo { public void Added() { } } }");
            var stale = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Foo" })));
            Assert.Contains("EDITED since indexing", stale);
        }
        finally
        {
            // Best-effort cleanup: the project's SQLite db may still be open (like SetupAsync's temp dirs).
            try { if (Directory.Exists(ws)) Directory.Delete(ws, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SetProject_ListsSwitchesAndRejects_AndIsBlockedWhenTenantLocked()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: false);

        // No name -> lists the projects with the active one marked.
        var list = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("set_project", new { })));
        Assert.Contains("Active project", list);
        Assert.Contains("P", list);

        // Switch to an existing project.
        var sw = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("set_project", new { name = "P" })));
        Assert.Contains("is now 'P'", sw);

        // Unknown project is rejected.
        var no = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("set_project", new { name = "Nope" })));
        Assert.Contains("No project named 'Nope'", no);

        // A tenant-locked server refuses switching (SaaS safety).
        var locked = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);
        var lockedResp = TextOf(await locked.ProcessJsonRpcMessageAsync(ToolCall("set_project", new { name = "P" })));
        Assert.Contains("locked to a single tenant", lockedResp);
    }

    [Fact]
    public async Task Orient_ReturnsGraphSize_ToolPalette_AndEditLoop()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("orient", new { })));

        Assert.Contains("START HERE", text);
        Assert.Contains("nodes", text);                 // live graph size
        Assert.Contains("references", text);            // impact palette
        Assert.Contains("check_edit", text);            // edit loop
        Assert.Contains("related_tests", text);
        Assert.Contains("OPEN THREADS", text);          // composes the thread count
    }

    [Fact]
    public async Task RelatedTests_FindsTransitiveCoverage_RankedByHops()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // WidgetTests references Widget directly; ConsumerTests reaches Widget via Consumer (2 hops).
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("related_tests", new { symbol = "Widget", depth = 3 })));
        Assert.Contains("WidgetTests", text);
        Assert.Contains("direct", text);
        Assert.Contains("ConsumerTests", text);
        Assert.Contains("via 2 hops", text);
    }

    [Fact]
    public async Task ImpactOf_NoDependents_ReportsSafe()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Gadget is referenced by nothing -> safe to change.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Gadget" })));
        Assert.Contains("Nothing references", text);
    }

    [Fact]
    public async Task GetSubgraph_EmitsHandlesAndSummaries_AndHandleRoundTripsAsSeed()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Seed using a short "@/…" handle instead of the absolute id.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_subgraph", new { seeds = new[] { "@/Widget.cs::Widget" }, hops = 1 })));

        Assert.Contains("@/Widget.cs::Widget", text);  // handle emitted (and was accepted as a seed)
        Assert.Contains("@/Gadget.cs::Gadget", text);  // neighbour handle
        Assert.Contains("A reusable widget.", text);   // node summary included
        Assert.Contains("--REFERENCES_TYPE-->", text); // edge using handles
    }

    [Fact]
    public async Task GetSubgraph_MaxChars_TruncatesOutput()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("get_subgraph", new { seeds = new[] { "@/Widget.cs::Widget" }, hops = 1, maxChars = 40 })));

        Assert.Contains("truncated", text);
        Assert.True(text.Length < 120);
    }

    [Fact]
    public async Task Record_RoutesByType_AndRejectsUnknownOrMissingRequired()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // type=task records a Task; it then shows up as an open thread.
        var rec = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("record", new { type = "task", name = "Wire the importer" })));
        Assert.Contains("recorded task", rec);

        var threads = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_open_threads", new { })));
        Assert.Contains("Wire the importer", threads);

        // type=decision requires content (returned as a JSON-RPC error, so assert on the raw response;
        // the JSON encoder escapes apostrophes, so match on the unquoted words).
        var noContent = await handler.ProcessJsonRpcMessageAsync(
            ToolCall("record", new { type = "decision", name = "Pick a store" }));
        Assert.Contains("requires", noContent!);
        Assert.Contains("content", noContent!);

        // An unknown type is rejected with the allowed set.
        var bad = await handler.ProcessJsonRpcMessageAsync(
            ToolCall("record", new { type = "note", name = "x" }));
        Assert.Contains("decision, milestone, task, question", bad!);
    }

    [Fact]
    public async Task ToolsList_AdvertisesReferences()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var res = await handler.ProcessJsonRpcMessageAsync(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/list" }));

        Assert.Contains("references", res);
    }

    [Fact]
    public async Task VerifyExists_ConfirmsAndDenies()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var yes = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("verify_exists", new { symbol = "Widget" })));
        Assert.StartsWith("YES", yes);
        Assert.Contains("Widget", yes);

        var no = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("verify_exists", new { symbol = "TotallyMadeUpType" })));
        Assert.StartsWith("NO", no);
    }

    [Fact]
    public async Task GetSource_ReturnsExactBody_WithLocation()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_source", new { symbol = "Widget" })));

        Assert.Contains("Widget (Class)", text);     // header with name + type
        Assert.Contains("Widget.cs:1", text);         // file:line location
        Assert.Contains("class Widget {}", text);     // the exact stored body
    }

    [Fact]
    public async Task FindUsages_ListsCallSites_WithSnippet()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Gadget --REFERENCES_TYPE--> Widget, and Gadget's body mentions Widget.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("find_usages", new { symbol = "Widget" })));

        Assert.Contains("usage(s) of 'Widget'", text);
        Assert.Contains("REFERENCES_TYPE", text);
        Assert.Contains("Gadget", text);
        Assert.Contains("class Gadget { Widget w; }", text); // the grep-like usage snippet

        // Widget references nothing, so it has no usages reported for Gadget.
        var none = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("find_usages", new { symbol = "Gadget" })));
        Assert.Contains("No usages", none);
    }

    [Fact]
    public async Task ReindexFile_RefreshesGraph_AndDegradesWithoutParsers()
    {
        var (pm, synth, ws) = await SetupAsync();

        // Without parsers (e.g. SaaS relay) the tool degrades gracefully.
        var noParsers = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);
        var unavailable = TextOf(await noParsers.ProcessJsonRpcMessageAsync(
            ToolCall("reindex_file", new { path = "Foo.cs" })));
        Assert.Contains("unavailable", unavailable);

        // With parsers + a real file, the file is indexed and immediately queryable via get_source.
        await File.WriteAllTextAsync(Path.Combine(ws, "Foo.cs"), "namespace D; public class Foo { }");
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true,
            fileParsers: new IFileParser[] { new RoslynAstParser() });

        var reindexed = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("reindex_file", new { path = "@/Foo.cs" })));
        Assert.Contains("Reindexed", reindexed);

        var src = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_source", new { symbol = "Foo" })));
        Assert.Contains("Foo", src);
    }

    [Fact]
    public async Task Tier3_Signature_Outline_DependencyTree()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // signature: a method rebuilt from stored properties, no body.
        var sig = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("signature", new { symbol = "Add" })));
        Assert.Contains("public int Add(int a, int b)", sig);

        // outline: the file's CONTAINS hierarchy (Class -> Method).
        var outline = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("outline", new { path = "@/Calc.cs" })));
        Assert.Contains("Calc", outline);
        Assert.Contains("Add", outline);
        Assert.Contains("Method", outline);

        // references (uses, depth>1) = dependency tree: Gadget --REFERENCES_TYPE--> Widget.
        var tree = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("references", new { symbol = "Gadget", direction = "uses", depth = 2 })));
        Assert.Contains("--REFERENCES_TYPE--> Widget", tree);
    }

    [Fact]
    public async Task Tier2_ImplementationsOf_RelatedTests_EditPlan()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // implementations_of: Impl IMPLEMENTS IThing (the edge targets the base type by name).
        var impls = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("implementations_of", new { symbol = "IThing" })));
        Assert.Contains("Impl", impls);
        Assert.Contains("IMPLEMENTS", impls);

        // related_tests: WidgetTests (in *Tests.cs) references Widget; Gadget (non-test) is excluded.
        var tests = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("related_tests", new { symbol = "Widget" })));
        Assert.Contains("WidgetTests", tests);
        Assert.DoesNotContain("Gadget", tests);

        // edit_plan: the definition + every reference site as a checklist, with the verify footer.
        var plan = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("edit_plan", new { symbol = "Widget" })));
        Assert.Contains("Edit plan", plan);
        Assert.Contains("[ ]", plan);
        Assert.Contains("WidgetTests", plan);
        Assert.Contains("reindex_file", plan);
    }

    [Fact]
    public async Task References_Uses_ListsOutgoingReferences()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Gadget --REFERENCES_TYPE--> Widget, so Gadget (uses) depends on Widget.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Gadget", direction = "uses" })));
        Assert.Contains("Widget", text);
        Assert.Contains("REFERENCES_TYPE", text);
        Assert.Contains("A reusable widget.", text); // dependency's AI summary

        // Widget points at nothing -> self-contained.
        var leaf = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("references", new { symbol = "Widget", direction = "uses" })));
        Assert.Contains("self-contained", leaf);
    }

    [Fact]
    public async Task FindPath_ReturnsChainWithRealEdgeDirection()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // Edge is Gadget --REFERENCES_TYPE--> Widget.
        var forward = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("find_path", new { from = "Gadget", to = "Widget" })));
        Assert.Contains("Gadget --REFERENCES_TYPE--> Widget", forward);

        // Reverse direction must render the arrow the other way (traversed against the edge).
        var backward = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("find_path", new { from = "Widget", to = "Gadget" })));
        Assert.Contains("Widget <--REFERENCES_TYPE-- Gadget", backward);
    }

    [Fact]
    public async Task FindPath_NoConnection_ReportsClearly()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // The open Task node shares no edge with Widget.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("find_path", new { from = "Widget", to = "Implement caching" })));
        Assert.Contains("No path", text);
    }

    [Fact]
    public async Task FindPath_MultiHop_ReconstructsFullChainInOrder()
    {
        // Cog --REFERENCES_TYPE--> Gadget --REFERENCES_TYPE--> Widget (an isolated 3-node chain,
        // so the shared SetupAsync's assertions stay untouched).
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_chain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");
        string Nid(string n) => Path.Combine(ws, $"{n}.cs") + $"::{n}";

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = Nid("Widget"), Name = "Widget", Type = "Class", FilePath = Path.Combine(ws, "Widget.cs") },
                new GraphNode { Id = Nid("Gadget"), Name = "Gadget", Type = "Class", FilePath = Path.Combine(ws, "Gadget.cs") },
                new GraphNode { Id = Nid("Cog"),    Name = "Cog",    Type = "Class", FilePath = Path.Combine(ws, "Cog.cs") }
            });
            await storage.UpsertEdgesAsync(new[]
            {
                new GraphEdge { SourceId = Nid("Cog"),    TargetId = Nid("Gadget"), Relationship = "REFERENCES_TYPE" },
                new GraphEdge { SourceId = Nid("Gadget"), TargetId = Nid("Widget"), Relationship = "REFERENCES_TYPE" }
            });
        }

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
            ActiveProjectName = "P"
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

        var handler = new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("find_path", new { from = "Cog", to = "Widget" })));

        // The full two-hop chain, in source->target order, each arrow forward.
        Assert.Contains("Cog --REFERENCES_TYPE--> Gadget --REFERENCES_TYPE--> Widget", text);
        Assert.Contains("2 hop(s)", text);
    }

    /// <summary>Returns a fixed embedding regardless of input — lets the test pin which seeded node ranks first.</summary>
    private sealed class StubEmbeddingService : IEmbeddingService
    {
        private readonly float[] _vector;
        public StubEmbeddingService(float[] vector) => _vector = vector;
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default) => Task.FromResult(_vector);
    }

    [Fact]
    public async Task SearchSemantic_RanksByVectorSimilarity_WithHandleAndSummary()
    {
        var (pm, synth, _) = await SetupAsync();
        // Query vector aligned with Widget's embedding [1,0,0] -> Widget must rank first.
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true,
            embeddingService: new StubEmbeddingService(new[] { 1f, 0f, 0f }));

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("search_semantic", new { query = "a reusable component" })));

        Assert.Contains("Widget", text);
        Assert.Contains("A reusable widget.", text);          // AI summary included
        Assert.Contains("@/Widget.cs::Widget", text);          // short handle, not the absolute id
        Assert.True(text.IndexOf("Widget", StringComparison.Ordinal)
                  < text.IndexOf("Gadget", StringComparison.Ordinal)); // ranked above the orthogonal node
    }

    [Fact]
    public async Task SearchSemantic_WithoutEmbeddingBackend_DegradesGracefully()
    {
        var (pm, synth, _) = await SetupAsync();
        // No embedding service (the stdio/CLI case) -> must not throw, returns a clear message.
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("search_semantic", new { query = "anything" })));

        Assert.Contains("unavailable", text);
        Assert.Contains("search_graph", text); // points the caller at the keyword fallback
    }

    [Fact]
    public async Task SearchHybrid_FusesFtsAndVector_RanksExpectedNodeFirst()
    {
        var (pm, synth, _) = await SetupAsync();
        // Query vector aligned with Widget [1,0,0]; the text "Widget" also matches FTS — both retrievers
        // surface Widget, so the RRF fusion must rank it first.
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true,
            embeddingService: new StubEmbeddingService(new[] { 1f, 0f, 0f }));

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("search_hybrid", new { query = "Widget" })));

        Assert.Contains("Widget", text);
        Assert.True(text.IndexOf("Widget", StringComparison.Ordinal)
                  < text.IndexOf("Gadget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchHybrid_WithoutEmbeddingBackend_IsNotListed()
    {
        var (pm, synth, _) = await SetupAsync();
        // No embedding service (stdio/CLI case) -> search_hybrid is capability-gated out of tools/list.
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var listed = await handler.ProcessJsonRpcMessageAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");

        Assert.NotNull(listed);
        Assert.DoesNotContain("search_hybrid", listed);
    }

    [Fact]
    public async Task GetOpenThreads_ListsOpen_ExcludesClosed()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(ToolCall("get_open_threads", new { })));

        Assert.Contains("Implement caching", text);   // open task
        Assert.Contains("Why is X slow", text);        // open question
        Assert.DoesNotContain("Old finished task", text); // Done -> excluded
        Assert.DoesNotContain("Trimmed done task", text); // "Done " (trailing space) -> trimmed, still excluded
    }

    [Fact]
    public async Task FindPath_TolerantOfNumericStringArg()
    {
        var (pm, synth, _) = await SetupAsync();
        var handler = new McpRequestHandler(pm, synth, "P", lockToContextProject: true);

        // maxHops as a JSON string "1" must not blow up with an internal error — ReadInt parses it.
        var text = TextOf(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("find_path", new { from = "Gadget", to = "Widget", maxHops = "1" })));

        Assert.Contains("Gadget --REFERENCES_TYPE--> Widget", text);
    }
}
