// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

public class ParserAndStorageTests
{
    [Fact]
    public async Task RoslynAstParser_ShouldExtractClassesAndMethods()
    {
        // Arrange
        var parser = new RoslynAstParser();
        var code = """
            using System;

            namespace TestNamespace;

            public class ServiceA : IService
            {
                public void ExecuteTask()
                {
                    Console.WriteLine("Hello World");
                }
            }

            public interface IService { }
            """;

        // Act
        var (nodes, edges) = await parser.ParseAsync("ServiceA.cs", code);

        // Assert
        Assert.NotEmpty(nodes);
        Assert.NotEmpty(edges);

        var classNode = nodes.FirstOrDefault(n => n.Type == "Class" && n.Name == "ServiceA");
        var interfaceNode = nodes.FirstOrDefault(n => n.Type == "Interface" && n.Name == "IService");
        var methodNode = nodes.FirstOrDefault(n => n.Type == "Method" && n.Name == "ExecuteTask");

        Assert.NotNull(classNode);
        Assert.NotNull(interfaceNode);
        Assert.NotNull(methodNode);

        // Check CONTAINS edge between Class and Method
        var containsEdge = edges.FirstOrDefault(e => e.SourceId == classNode.Id && e.TargetId == methodNode.Id && e.Relationship == "CONTAINS");
        Assert.NotNull(containsEdge);

        // Check IMPLEMENTS edge between Class and Interface
        var implementsEdge = edges.FirstOrDefault(e => e.SourceId == classNode.Id && e.TargetId == "IService" && e.Relationship == "IMPLEMENTS");
        Assert.NotNull(implementsEdge);
    }

    [Fact]
    public async Task JavaScriptParser_ShouldExtractImports()
    {
        // Arrange
        var parser = new JavaScriptParser();
        var jsCode = """
            import { useState, useEffect } from 'react';
            import UserService from './UserService';

            export default function UserCard() {
                return "User";
            }
            """;

        // Act
        var (nodes, edges) = await parser.ParseAsync("UserCard.js", jsCode);

        // Assert
        Assert.NotEmpty(nodes);
        var componentNode = nodes.FirstOrDefault(n => n.Type == "JSComponent");
        Assert.NotNull(componentNode);

        // Check imports
        var importEdges = edges.Where(e => e.Relationship == "IMPORTS").ToList();
        Assert.NotEmpty(importEdges);
        Assert.Contains(importEdges, e => e.TargetId.Contains("react"));
        Assert.Contains(importEdges, e => e.TargetId.Contains("userservice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PhpModuleParser_ShouldExtractOxidModuleExtensions()
    {
        // Arrange
        var parser = new PhpModuleParser();
        var phpCode = """
            <?pragma xml version="1.0" encoding="utf-8"?>
            class MyCustomOrder extends MyCustomOrder_parent
            {
                public void finalizeOrder() { }
            }
            """;

        // Act
        var (nodes, edges) = await parser.ParseAsync("MyCustomOrder.php", phpCode);

        // Assert
        Assert.NotEmpty(nodes);
        var moduleNode = nodes.FirstOrDefault(n => n.Type == "OxidModule" && n.Name == "MyCustomOrder");
        Assert.NotNull(moduleNode);
        Assert.Equal("MyCustomOrder.php::MyCustomOrder", moduleNode.Id);

        // Check inheritance
        var extendsEdge = edges.FirstOrDefault(e => e.Relationship == "EXTENDS");
        Assert.NotNull(extendsEdge);
        Assert.Equal("MyCustomOrder.php::MyCustomOrder", extendsEdge.SourceId);
        Assert.Equal("MyCustomOrder_parent", extendsEdge.TargetId);
    }

    [Fact]
    public async Task SqliteGraphStorage_ShouldPerformFts5AndCteSubgraphTraversal()
    {
        // Arrange
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        var nodes = new List<GraphNode>
        {
            new() { Id = "file1.cs", Type = "File", Name = "file1.cs", Content = "Some content about compiler structures." },
            new() { Id = "classA", Type = "Class", Name = "ParserClass", Content = "public class ParserClass { }" },
            new() { Id = "methodM", Type = "Method", Name = "ParseTokens", Content = "public void ParseTokens() { }" }
        };

        var edges = new List<GraphEdge>
        {
            new() { SourceId = "file1.cs", TargetId = "classA", Relationship = "CONTAINS" },
            new() { SourceId = "classA", TargetId = "methodM", Relationship = "CONTAINS" }
        };

        // Act
        await storage.UpsertNodesAsync(nodes);
        await storage.UpsertEdgesAsync(edges);

        // Verify Search (FTS5 Match)
        var searchResults = await storage.SearchAsync("compiler");
        Assert.Single(searchResults);
        Assert.Equal("file1.cs", searchResults[0].Node.Id);

        // Verify Subgraph CTE (Traverse 2 hops from file1.cs seed node)
        var (subgraphNodes, subgraphEdges) = await storage.GetSubgraphAsync(new[] { "file1.cs" }, 2);

        // Assert
        Assert.Equal(3, subgraphNodes.Count); // Should include file1, classA, methodM
        Assert.Equal(2, subgraphEdges.Count); // Should include both CONTAINS edges

        Assert.Contains(subgraphNodes, n => n.Id == "file1.cs");
        Assert.Contains(subgraphNodes, n => n.Id == "classA");
        Assert.Contains(subgraphNodes, n => n.Id == "methodM");
    }



    [Fact]
    public async Task SqliteGraphStorage_ShouldHandleConcurrentOperationsWithoutCorruption()
    {
        // Regression test for A1: a single provider instance must tolerate concurrent
        // reads/writes (ASP.NET serves requests in parallel). With a shared, non-thread-safe
        // connection this throws or corrupts; with connection-per-operation it succeeds.
        var dbPath = Path.Combine(Path.GetTempPath(), $"shonkor_concurrency_{Guid.NewGuid():N}.db");
        var storage = new SqliteGraphStorageProvider(dbPath);
        try
        {
            await storage.InitializeAsync();

            // Seed a node so searches have something to find.
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = "seed", Type = "File", Name = "seed.cs", Content = "concurrent indexing alpha beta" }
            });

            // Fire many overlapping operations in parallel.
            var tasks = new List<Task>();
            for (var i = 0; i < 50; i++)
            {
                var n = i;
                tasks.Add(Task.Run(async () =>
                {
                    await storage.UpsertNodesAsync(new[]
                    {
                        new GraphNode { Id = $"node{n}", Type = "Class", Name = $"Class{n}", Content = $"alpha content {n}" }
                    });
                    await storage.UpsertEdgesAsync(new[]
                    {
                        new GraphEdge { SourceId = "seed", TargetId = $"node{n}", Relationship = "CONTAINS" }
                    });
                    _ = await storage.SearchAsync("alpha", 20);
                    _ = await storage.GetStatisticsAsync();
                }));
            }

            await Task.WhenAll(tasks);

            // All 50 nodes plus the seed must be present and consistently retrievable.
            var stats = await storage.GetStatisticsAsync();
            Assert.Equal(51, stats.TotalNodes);
            Assert.Equal(50, stats.TotalEdges);

            // The seed must report all 50 related edges via the batch edge loader.
            var seedHit = (await storage.SearchAsync("concurrent", 5)).Single(r => r.Node.Id == "seed");
            Assert.Equal(50, seedHit.RelatedEdges.Count);
        }
        finally
        {
            storage.Dispose();
            // Connection pooling keeps file handles open; clear the pool before deleting.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task RoslynAstParser_AndLinker_ShouldResolveTypeUsageEdges()
    {
        // Regression/feature test for type-reference impact edges:
        // a class that uses a type defined in another file must be linked to that type's
        // definition node via a REFERENCES_TYPE edge after the post-scan linker runs.
        var defParser = new RoslynAstParser();
        var defCode = """
            namespace Demo;
            public record GraphNode { public string Id { get; init; } = ""; }
            """;
        var (defNodes, defEdges) = await defParser.ParseAsync("GraphNode.cs", defCode);

        var useParser = new RoslynAstParser();
        var useCode = """
            namespace Demo;
            public class Repository
            {
                private GraphNode _cached;
                public GraphNode Get(GraphNode seed) => seed;
            }
            """;
        var (useNodes, useEdges) = await useParser.ParseAsync("Repository.cs", useCode);

        // The using type must carry the referenced type name for the linker to resolve.
        var repoType = useNodes.First(n => n.Type == "Class" && n.Name == "Repository");
        Assert.Contains("GraphNode", repoType.Properties["referencedTypes"].Split(','));

        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(defNodes.Concat(useNodes));
        await storage.UpsertEdgesAsync(defEdges.Concat(useEdges));

        // Act: run the post-scan linker that resolves type references into edges.
        await CrossTechLinker.EstablishCrossTechnologyConnectionsAsync(storage, "C:\\mock");

        // Assert: traversing out from the definition reaches its user via REFERENCES_TYPE.
        var (_, edges) = await storage.GetSubgraphAsync(new[] { "GraphNode.cs::GraphNode" }, 1);
        Assert.Contains(edges, e =>
            e.Relationship == "REFERENCES_TYPE" &&
            e.SourceId == "Repository.cs::Repository" &&
            e.TargetId == "GraphNode.cs::GraphNode");
    }

    [Fact]
    public async Task GraphQLParser_ShouldExtractQueriesAndTemplateReferences()
    {
        // Arrange
        var parser = new GraphQLParser();
        var gqlContent = """
            query GetBlogPost($datasource: String!) {
              item(path: $datasource) {
                ... on BlogPost {
                  title { value }
                  text { value }
                }
              }
            }
            """;

        // Act
        var (nodes, edges) = await parser.ParseAsync("GetBlogPost.graphql", gqlContent);

        // Assert
        Assert.Single(nodes);
        var queryNode = nodes[0];
        Assert.Equal("GraphQLQuery", queryNode.Type);
        Assert.Equal("GetBlogPost", queryNode.Name);
        Assert.Equal("BlogPost", queryNode.Properties["referencedTemplates"]);

        Assert.Single(edges);
        Assert.Equal("DEFINED_IN", edges[0].Relationship);
    }

    [Fact]
    public async Task ScanFile_IndexesEditsAndDeletions()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_scanfile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Sample.cs");
        var fullPath = Path.GetFullPath(file);

        try
        {
            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });

            // 1. Initial index.
            await File.WriteAllTextAsync(file, "namespace D; public class Foo { public void Bar() {} }");
            var r1 = await scanner.ScanFileAsync(file);
            Assert.True(r1.NodesCreated > 0);
            var (nodes1, _) = await storage.GetSubgraphAsync(new[] { fullPath }, 1);
            Assert.Contains(nodes1, n => n.Type == "Class" && n.Name == "Foo");

            // 2. Edit: rename the class -> old node gone, new node present.
            await File.WriteAllTextAsync(file, "namespace D; public class Renamed { public void Bar() {} }");
            await scanner.ScanFileAsync(file);
            var (nodes2, _) = await storage.GetSubgraphAsync(new[] { fullPath }, 1);
            Assert.Contains(nodes2, n => n.Name == "Renamed");
            Assert.DoesNotContain(nodes2, n => n.Name == "Foo");

            // 3. Delete the file -> its graph is cleared.
            File.Delete(file);
            var r3 = await scanner.ScanFileAsync(file);
            Assert.Equal(0, r3.NodesCreated);
            Assert.Null(await storage.GetNodeByIdAsync(fullPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Scanner_DiagnosticsNeverGoToStdout()
    {
        // Scan diagnostics must go to stderr/logger, never stdout — stdout is the JSON-RPC channel of
        // the stdio MCP server, and a stray line there would corrupt the protocol.
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_stdout_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // A >5 MB file triggers the "Skipping large file" diagnostic.
            await File.WriteAllTextAsync(Path.Combine(dir, "Big.cs"), new string('a', 6 * 1024 * 1024));

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() }); // no logger

            var original = Console.Out;
            await using var captured = new StringWriter();
            Console.SetOut(captured);
            try
            {
                await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.Equal(string.Empty, captured.ToString());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ScanDirectory_SemanticCsharp_ResolvesReferenceToCorrectFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_sem_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "AThing.cs"), "namespace A { public class Thing { } }");
            await File.WriteAllTextAsync(Path.Combine(dir, "BThing.cs"), "namespace B { public class Thing { } }");
            await File.WriteAllTextAsync(Path.Combine(dir, "User.cs"),   "using A; namespace U { public class User { public Thing Field; } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            // semanticCsharp: true -> the name-based C# resolution is skipped and the SemanticCsharpLinker runs.
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() }, logger: null, semanticCsharp: true);
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var userId = Path.GetFullPath(Path.Combine(dir, "User.cs")) + "::User";

            // User references A.Thing exactly (the imported one) — name matching would have linked BOTH.
            var (aEdges, _) = await storage.GetIncidentEdgesAsync(Path.GetFullPath(Path.Combine(dir, "AThing.cs")) + "::Thing");
            Assert.Contains(aEdges, e => e.SourceId == userId && e.Relationship == "REFERENCES_TYPE");

            var (bEdges, _) = await storage.GetIncidentEdgesAsync(Path.GetFullPath(Path.Combine(dir, "BThing.cs")) + "::Thing");
            Assert.DoesNotContain(bEdges, e => e.SourceId == userId); // the ambiguous name-based edge must NOT exist
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReindexFile_RelinksOutgoingReferenceTypeEdges()
    {
        // Drift Layer 1: after editing a file, reindex_file must recompute that file's outgoing
        // REFERENCES_TYPE edges (otherwise impact/depends_on go stale until the next full scan).
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_relink_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var userFile = Path.Combine(dir, "User.cs");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "Widget.cs"), "namespace N { public class Widget { } }");
            await File.WriteAllTextAsync(Path.Combine(dir, "Gadget.cs"), "namespace N { public class Gadget { } }");
            await File.WriteAllTextAsync(userFile, "namespace N { public class User { public Widget W; } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var userId = Path.GetFullPath(userFile) + "::User";
            var widgetId = Path.GetFullPath(Path.Combine(dir, "Widget.cs")) + "::Widget";
            var gadgetId = Path.GetFullPath(Path.Combine(dir, "Gadget.cs")) + "::Gadget";

            // Baseline: the full scan linked User -> Widget.
            var (e0, _) = await storage.GetIncidentEdgesAsync(widgetId);
            Assert.Contains(e0, e => e.SourceId == userId && e.Relationship == "REFERENCES_TYPE");

            // Edit User to reference Gadget instead, then reindex ONLY that file.
            await File.WriteAllTextAsync(userFile, "namespace N { public class User { public Gadget G; } }");
            await scanner.ScanFileAsync(userFile);

            // The scoped relink recreated the outgoing edge to the NEW target...
            var (eg, _) = await storage.GetIncidentEdgesAsync(gadgetId);
            Assert.Contains(eg, e => e.SourceId == userId && e.Relationship == "REFERENCES_TYPE");
            // ...and the stale edge to Widget is gone.
            var (ew, _) = await storage.GetIncidentEdgesAsync(widgetId);
            Assert.DoesNotContain(ew, e => e.SourceId == userId);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcilePaths_Semantic_RefreshesCallsEdge_WithoutFullScan()
    {
        // Drift P3 (incremental semantic relink): editing a file to add a call refreshes its CALLS edge via
        // a single per-batch compilation, without a whole-graph rescan.
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_semreconcile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var aFile = Path.Combine(dir, "Svc.cs");
        try
        {
            await File.WriteAllTextAsync(aFile, "namespace N { public class Svc { public void Run() { } public void Helper() { } } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() }, logger: null, semanticCsharp: true);
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var helperId = Path.GetFullPath(aFile) + "::Svc::Helper#0";
            var runId = Path.GetFullPath(aFile) + "::Svc::Run#0";

            // Baseline: Run does not call Helper yet.
            var (e0, _) = await storage.GetIncidentEdgesAsync(helperId);
            Assert.DoesNotContain(e0, e => e.Relationship == "CALLS");

            // Edit: Run now calls Helper. Reconcile only this file.
            await File.WriteAllTextAsync(aFile, "namespace N { public class Svc { public void Run() { Helper(); } public void Helper() { } } }");
            await scanner.ReconcilePathsAsync(dir, new[] { "Svc.cs" });

            var (e1, _) = await storage.GetIncidentEdgesAsync(helperId);
            Assert.Contains(e1, e => e.SourceId == runId && e.Relationship == "CALLS");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcilePaths_Semantic_RemovesDanglingCallerEdges_OnTypeRename()
    {
        // Drift P3: renaming a type in A (reindexing ONLY A) refreshes the referencer B's semantic edges too,
        // removing B's now-dangling REFERENCES_TYPE/CALLS into the old type — via the reverse index.
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_semrename_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var aFile = Path.Combine(dir, "Widget.cs");
        try
        {
            await File.WriteAllTextAsync(aFile, "namespace N { public class Widget { public void Do() { } } }");
            await File.WriteAllTextAsync(Path.Combine(dir, "User.cs"), "namespace N { public class User { public void Use() { new Widget().Do(); } } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() }, logger: null, semanticCsharp: true);
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var userId = Path.GetFullPath(Path.Combine(dir, "User.cs")) + "::User";

            // Baseline: User references/calls into Widget.
            var (e0, _) = await storage.GetIncidentEdgesAsync(userId);
            Assert.Contains(e0, e => e.SourceId == userId && (e.Relationship == "REFERENCES_TYPE" || e.Relationship == "CALLS"));

            // Rename Widget -> Gadget in A, reconcile ONLY A.
            await File.WriteAllTextAsync(aFile, "namespace N { public class Gadget { public void Do() { } } }");
            await scanner.ReconcilePathsAsync(dir, new[] { "Widget.cs" });

            // User's edges into the now-gone Widget are cleared (User's source still says Widget -> unresolved).
            var (e1, _) = await storage.GetIncidentEdgesAsync(userId);
            Assert.DoesNotContain(e1, e => e.SourceId == userId && (e.Relationship == "REFERENCES_TYPE" || e.Relationship == "CALLS"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcilePaths_HandlesAddModifyDelete_Surgically()
    {
        // Drift Layer 4: given an explicit changed-file set, reconcile re-indexes only those (add/modify/
        // delete) without a whole-tree rescan.
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_reconcilepaths_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var aFile = Path.Combine(dir, "A.cs");
        var bFile = Path.Combine(dir, "B.cs");
        var cFile = Path.Combine(dir, "C.cs");
        try
        {
            await File.WriteAllTextAsync(aFile, "namespace N { public class Alpha { } }");
            await File.WriteAllTextAsync(bFile, "namespace N { public class Beta { } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            // Out-of-band: rename Alpha in A, add C, delete B.
            await File.WriteAllTextAsync(aFile, "namespace N { public class AlphaRenamed { } }");
            await File.WriteAllTextAsync(cFile, "namespace N { public class Gamma { } }");
            File.Delete(bFile);

            await scanner.ReconcilePathsAsync(dir, new[] { "A.cs", "C.cs", "B.cs" });

            Assert.NotNull(await storage.GetNodeByIdAsync(Path.GetFullPath(aFile) + "::AlphaRenamed"));
            Assert.Null(await storage.GetNodeByIdAsync(Path.GetFullPath(aFile) + "::Alpha"));
            Assert.NotNull(await storage.GetNodeByIdAsync(Path.GetFullPath(cFile) + "::Gamma"));
            Assert.Null(await storage.GetNodeByIdAsync(Path.GetFullPath(bFile) + "::Beta"));
            Assert.Null(await storage.GetNodeByIdAsync(Path.GetFullPath(bFile)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcileDrift_ReindexesOutOfBandChanges_AndLeavesGraphClean()
    {
        // Drift Layer 3: detect drift vs the working tree and surgically re-index the drifted files,
        // catching an out-of-band edit; afterwards the graph reports no drift.
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_reconciledrift_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var aFile = Path.Combine(dir, "A.cs");
        try
        {
            await File.WriteAllTextAsync(aFile, "namespace N { public class Alpha { } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            // Out-of-band edit: add a method, bypassing reindex_file.
            await File.WriteAllTextAsync(aFile, "namespace N { public class Alpha { public void Added() { } } }");

            var driftBefore = await scanner.DetectDriftAsync(dir, Array.Empty<string>());
            Assert.False(driftBefore.IsClean);

            var result = await scanner.ReconcileDriftAsync(dir, Array.Empty<string>());
            Assert.True(result.FilesScanned > 0);

            // The new method is now in the graph and no drift remains.
            Assert.NotNull(await storage.GetNodeByIdAsync(Path.GetFullPath(aFile) + "::Alpha::Added#0"));
            Assert.True((await scanner.DetectDriftAsync(dir, Array.Empty<string>())).IsClean);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReindexFile_RemovesDanglingIncomingEdges_OnRename()
    {
        // Drift Layer 2: A defines Widget, B references it. Renaming Widget away in A (and reindexing ONLY A)
        // must remove B's now-dangling REFERENCES_TYPE edge, via the reverse index — without reindexing B.
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_rename_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var defFile = Path.Combine(dir, "Def.cs");
        try
        {
            await File.WriteAllTextAsync(defFile, "namespace N { public class Widget { } }");
            await File.WriteAllTextAsync(Path.Combine(dir, "User.cs"), "namespace N { public class User { public Widget W; } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var userId = Path.GetFullPath(Path.Combine(dir, "User.cs")) + "::User";
            var widgetId = Path.GetFullPath(defFile) + "::Widget";

            // Baseline: User -> Widget exists.
            var (e0, _) = await storage.GetIncidentEdgesAsync(widgetId);
            Assert.Contains(e0, e => e.SourceId == userId && e.Relationship == "REFERENCES_TYPE");

            // Rename Widget -> Gadget in Def.cs, then reindex ONLY Def.cs (NOT User.cs).
            await File.WriteAllTextAsync(defFile, "namespace N { public class Gadget { } }");
            await scanner.ScanFileAsync(defFile);

            // The dangling User -> Widget edge is gone (Widget no longer defined; User wasn't re-parsed).
            var (eu, _) = await storage.GetIncidentEdgesAsync(userId);
            Assert.DoesNotContain(eu, e => e.SourceId == userId && e.Relationship == "REFERENCES_TYPE");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReindexFile_CreatesNewlyResolvableIncomingEdges_OnAddedDefinition()
    {
        // Drift Layer 2 (dual): B references Widget before it's defined (no edge). Adding Widget in A and
        // reindexing ONLY A must create B -> Widget via the reverse index — without reindexing B.
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_added_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var defFile = Path.Combine(dir, "Def.cs");
        try
        {
            await File.WriteAllTextAsync(defFile, "namespace N { public class Placeholder { } }");
            await File.WriteAllTextAsync(Path.Combine(dir, "User.cs"), "namespace N { public class User { public Widget W; } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            var userId = Path.GetFullPath(Path.Combine(dir, "User.cs")) + "::User";
            var widgetId = Path.GetFullPath(defFile) + "::Widget";

            // Baseline: Widget undefined -> no edge.
            var (e0, _) = await storage.GetIncidentEdgesAsync(userId);
            Assert.DoesNotContain(e0, e => e.SourceId == userId && e.Relationship == "REFERENCES_TYPE");

            // Add Widget to Def.cs, then reindex ONLY Def.cs.
            await File.WriteAllTextAsync(defFile, "namespace N { public class Placeholder { } public class Widget { } }");
            await scanner.ScanFileAsync(defFile);

            // User -> Widget became resolvable and was created.
            var (eu, _) = await storage.GetIncidentEdgesAsync(widgetId);
            Assert.Contains(eu, e => e.SourceId == userId && e.Relationship == "REFERENCES_TYPE");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DetectDrift_ReportsChangedNewAndDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_drift_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var changedFile = Path.Combine(dir, "Changed.cs");
        var oldFile = Path.Combine(dir, "Old.cs");
        var newFile = Path.Combine(dir, "New.cs");
        try
        {
            await File.WriteAllTextAsync(changedFile, "namespace N { public class Changed { } }");
            await File.WriteAllTextAsync(oldFile, "namespace N { public class Old { } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            // Mutate the working tree: edit one, add one, delete one.
            await File.WriteAllTextAsync(changedFile, "namespace N { public class Changed { public int X; } }");
            await File.WriteAllTextAsync(newFile, "namespace N { public class New { } }");
            File.Delete(oldFile);

            var drift = await scanner.DetectDriftAsync(dir, Array.Empty<string>());

            Assert.False(drift.IsClean);
            Assert.Contains(Path.GetFullPath(changedFile), drift.Changed);
            Assert.Contains(Path.GetFullPath(newFile), drift.New);
            Assert.Contains(Path.GetFullPath(oldFile), drift.Deleted);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task CheckFreshness_DetectsFreshStaleUntrackedDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_fresh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "F.cs");
        var untracked = Path.Combine(dir, "U.cs");
        try
        {
            await File.WriteAllTextAsync(file, "namespace N { public class F { } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            Assert.Equal(GraphIndexScanner.FreshnessState.Fresh, await scanner.CheckFreshnessAsync(file));

            await File.WriteAllTextAsync(file, "namespace N { public class F { public int X; } }");
            Assert.Equal(GraphIndexScanner.FreshnessState.Stale, await scanner.CheckFreshnessAsync(file));

            await File.WriteAllTextAsync(untracked, "namespace N { public class U { } }");
            Assert.Equal(GraphIndexScanner.FreshnessState.Untracked, await scanner.CheckFreshnessAsync(untracked));

            File.Delete(file);
            Assert.Equal(GraphIndexScanner.FreshnessState.Deleted, await scanner.CheckFreshnessAsync(file));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Scanner_StampsCurrentSchemeVersion_AfterScan()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_schemev_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "S.cs"), "namespace N { public class S { public void M() {} } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });
            await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());

            // The whole tree was just built under the current scheme -> stamped, no re-index recommended.
            Assert.Equal(CsharpNodeId.SchemeVersion, await storage.GetNodeIdSchemeVersionAsync());
            var stats = await storage.GetStatisticsAsync();
            Assert.False(stats.ReindexRecommended);
            Assert.Equal(CsharpNodeId.SchemeVersion, stats.CurrentSchemeVersion);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetStatistics_ReportsReindexRecommended_WhenSchemeStale()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        // A non-empty graph stamped with an older scheme -> the mismatch must surface as a re-index hint.
        await storage.UpsertNodesAsync(new[] { new GraphNode { Id = "x", Name = "x", Type = "Class" } });
        await storage.SetNodeIdSchemeVersionAsync(1);

        var stats = await storage.GetStatisticsAsync();
        Assert.Equal(1, stats.SchemeVersion);
        Assert.True(stats.ReindexRecommended);
    }

    [Fact]
    public async Task Scanner_ForcesReparse_WhenSchemeStale_DespiteUnchangedFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_forcereparse_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "S.cs"), "namespace N { public class S { public void M() {} } }");

            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });

            var first = await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());
            Assert.True(first.NodesCreated > 0);

            // An unchanged rescan normally skips every file (hash match) -> nothing re-created.
            var unchanged = await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());
            Assert.Equal(0, unchanged.NodesCreated);

            // Simulate a legacy graph: roll the stored scheme back. Despite the files being unchanged,
            // the next scan must force a full reparse to migrate the ids, then re-stamp the version.
            await storage.SetNodeIdSchemeVersionAsync(1);
            var migrated = await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());
            Assert.True(migrated.NodesCreated > 0); // forced reparse, not skipped
            Assert.Equal(CsharpNodeId.SchemeVersion, await storage.GetNodeIdSchemeVersionAsync());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ScanFile_PreservesIncomingReferences_OnEdit()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_reidx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Target.cs");
        var fullPath = Path.GetFullPath(file);
        var targetId = fullPath + "::Target";

        try
        {
            using var storage = new SqliteGraphStorageProvider(":memory:");
            await storage.InitializeAsync();
            var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() });

            // Index Target.cs, then add an incoming reference owned by ANOTHER file (Caller -> Target).
            await File.WriteAllTextAsync(file, "namespace D; public class Target { }");
            await scanner.ScanFileAsync(file);
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = "caller::Caller", Type = "Class", Name = "Caller", FilePath = Path.Combine(dir, "Caller.cs"), Content = "uses Target" }
            });
            await storage.UpsertEdgesAsync(new[]
            {
                new GraphEdge { SourceId = "caller::Caller", TargetId = targetId, Relationship = "REFERENCES_TYPE" }
            });

            // Edit Target.cs (add a method — same class name, so the node id is stable) and re-index it.
            await File.WriteAllTextAsync(file, "namespace D; public class Target { public void M() {} }");
            await scanner.ScanFileAsync(file);

            // The incoming reference from Caller must survive the single-file re-index.
            var (edges, _) = await storage.GetIncidentEdgesAsync(targetId);
            Assert.Contains(edges, e => e.SourceId == "caller::Caller" && e.TargetId == targetId && e.Relationship == "REFERENCES_TYPE");
            // And the edit landed (the new method node exists under the re-parsed type).
            var (nodes, _) = await storage.GetSubgraphAsync(new[] { targetId }, 1);
            Assert.Contains(nodes, n => n.Type == "Method" && n.Name == "M");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteByFilePaths_RemovesNodesAndOrphanEdges_InOneTransaction()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        const string fileA = "/repo/A.cs";
        const string fileC = "/repo/C.cs";
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "a1", Type = "Class",  Name = "A1", FilePath = fileA, Content = "a1" },
            new GraphNode { Id = "a2", Type = "Method", Name = "A2", FilePath = fileA, Content = "a2" },
            new GraphNode { Id = "c1", Type = "Class",  Name = "C1", FilePath = fileC, Content = "c1" },
            new GraphNode { Id = "c2", Type = "Method", Name = "C2", FilePath = fileC, Content = "c2" }
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "a1", TargetId = "a2", Relationship = "CONTAINS" }, // within A
            new GraphEdge { SourceId = "a2", TargetId = "c1", Relationship = "CALLS" },    // A -> C (orphaned by delete)
            new GraphEdge { SourceId = "c1", TargetId = "c2", Relationship = "CONTAINS" }  // within C (must survive)
        });

        await storage.DeleteByFilePathsAsync(new[] { fileA });

        // A's nodes are gone; C's survive.
        Assert.Null(await storage.GetNodeByIdAsync("a1"));
        Assert.Null(await storage.GetNodeByIdAsync("a2"));
        Assert.NotNull(await storage.GetNodeByIdAsync("c1"));

        // Only the intra-C edge survives; edges touching a deleted node are cleaned up.
        var stats = await storage.GetStatisticsAsync();
        Assert.Equal(2, stats.TotalNodes);
        Assert.Equal(1, stats.TotalEdges);
    }

    [Fact]
    public async Task GraphPathFinder_FindsShortestChain_WithRealDirections()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        // A --CALLS--> B --CALLS--> C  (and an isolated D)
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "A", Type = "Method", Name = "Alpha", Content = "a" },
            new GraphNode { Id = "B", Type = "Method", Name = "Beta", Content = "b" },
            new GraphNode { Id = "C", Type = "Method", Name = "Gamma", Content = "c" },
            new GraphNode { Id = "D", Type = "Method", Name = "Delta", Content = "d" }
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "A", TargetId = "B", Relationship = "CALLS" },
            new GraphEdge { SourceId = "B", TargetId = "C", Relationship = "CALLS" }
        });

        var path = await Shonkor.Core.Services.GraphPathFinder.FindPathAsync(storage, "A", "C", maxHops: 5);

        Assert.NotNull(path);
        Assert.Equal(3, path!.Count);                       // A, B, C
        Assert.Equal("A", path[0].Node.Id);
        Assert.Null(path[0].Relation);                       // source has no incoming relation
        Assert.Equal("B", path[1].Node.Id);
        Assert.Equal("CALLS", path[1].Relation);
        Assert.True(path[1].Forward);                        // edge points A->B (with the path direction)
        Assert.Equal("C", path[2].Node.Id);
        Assert.True(path[2].Forward);

        // No path to the isolated node.
        var none = await Shonkor.Core.Services.GraphPathFinder.FindPathAsync(storage, "A", "D", maxHops: 5);
        Assert.Null(none);
    }

    [Fact]
    public async Task GetIncidentEdges_ReturnsBothDirections_WithEndpointNodes()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "A", Type = "Class", Name = "Alpha", Content = "a" },
            new GraphNode { Id = "B", Type = "Class", Name = "Beta", Content = "b" },
            new GraphNode { Id = "C", Type = "Class", Name = "Gamma", Content = "c" },
            new GraphNode { Id = "X", Type = "Class", Name = "Xenon", Content = "x" } // unrelated, must not appear
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "A", TargetId = "B", Relationship = "CALLS" },        // A is source
            new GraphEdge { SourceId = "C", TargetId = "A", Relationship = "REFERENCES" },   // A is target
            new GraphEdge { SourceId = "B", TargetId = "C", Relationship = "CALLS" }          // touches neither A end
        });

        var (edges, neighbours) = await storage.GetIncidentEdgesAsync("A");

        // Both edges incident to A, in either direction; the B->C edge is excluded.
        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.SourceId == "A" && e.TargetId == "B" && e.Relationship == "CALLS");
        Assert.Contains(edges, e => e.SourceId == "C" && e.TargetId == "A" && e.Relationship == "REFERENCES");

        // Endpoint nodes are returned for rendering; the unrelated node X is not.
        Assert.Equal("Beta", neighbours["B"].Name);
        Assert.Equal("Gamma", neighbours["C"].Name);
        Assert.True(neighbours.ContainsKey("A"));
        Assert.False(neighbours.ContainsKey("X"));
    }

    [Fact]
    public async Task MarkdownHierarchyParser_ShouldExtractSectionsAndRelativeLinks()
    {
        // Arrange
        var parser = new MarkdownHierarchyParser();
        var md = """
            # Guide

            See [setup](./setup.md) and the [API docs](https://example.com/api).

            ## Installation

            Repeat the [setup](./setup.md) link — must be de-duplicated.
            """;

        // Act
        var (nodes, edges) = await parser.ParseAsync("docs/guide.md", md);

        // Assert: one section node per header, carrying level + index.
        var h1 = nodes.FirstOrDefault(n => n.Type == "MarkdownSection" && n.Name == "Guide");
        var h2 = nodes.FirstOrDefault(n => n.Type == "MarkdownSection" && n.Name == "Installation");
        Assert.NotNull(h1);
        Assert.NotNull(h2);
        Assert.Equal("1", h1!.Properties["level"]);
        Assert.Equal("2", h2!.Properties["level"]);

        // Each section is CONTAINED by the file.
        Assert.Contains(edges, e => e.Relationship == "CONTAINS" && e.SourceId == "docs/guide.md" && e.TargetId == h1.Id);

        // Relative links become REFERENCES edges; the http link is excluded; the duplicate is collapsed.
        var refs = edges.Where(e => e.Relationship == "REFERENCES").ToList();
        Assert.Single(refs);
        Assert.Equal("./setup.md", refs[0].Properties["rawTarget"]);
        Assert.DoesNotContain(edges, e => e.Relationship == "REFERENCES" && e.Properties.GetValueOrDefault("rawTarget", "").Contains("example.com"));
    }

    [Fact]
    public async Task CrossTechLinker_ShouldEstablishDynamicMappings()
    {
        // Arrange
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        var nodes = new List<GraphNode>
        {
            // NextJS JSComponent
            new() { Id = "src/components/blogbox.tsx", Type = "JSComponent", Name = "BlogBox", FilePath = "src/components/blogbox.tsx" },
            // Sitecore Rendering with explicit controller & componentName
            new() {
                Id = "dfbb0822-f08f-4e1f-a4a7-20b5cde22893",
                Type = "SitecoreRendering",
                Name = "BlogBox",
                Properties = new Dictionary<string, string> {
                    ["sitecorePath"] = "/sitecore/layout/Renderings/Feature/Blog/BlogBox",
                    ["controller"] = "MuM.Feature.Blog.Controllers.BlogController, MuM.Feature.Blog",
                    ["componentName"] = "BlogBox"
                }
            },
            // C# AST Controller Class
            new() { Id = "csharp::blogcontroller", Type = "Class", Name = "BlogController", FilePath = "src/Feature/Blog/code/Controllers/BlogController.cs" },
            // GraphQL Query referencing BlogPost
            new() {
                Id = "query::getblogpost",
                Type = "GraphQLQuery",
                Name = "GetBlogPost",
                FilePath = "src/Feature/Blog/graphql/GetBlogPost.graphql",
                Properties = new Dictionary<string, string> {
                    ["referencedTemplates"] = "BlogPost"
                }
            },
            // Sitecore Template matching GQL query target
            new() {
                Id = "a2c33f3f-86c5-43a5-aeb4-5598cec45116",
                Type = "SitecoreTemplate",
                Name = "BlogPost",
                Properties = new Dictionary<string, string> {
                    ["sitecorePath"] = "/sitecore/templates/Feature/Blog/BlogPost"
                }
            }
        };

        await storage.UpsertNodesAsync(nodes);

        // Act - Run post processing connections
        await CrossTechLinker.EstablishCrossTechnologyConnectionsAsync(storage, "C:\\mock");

        // Fetch back and assert new edges
        var (subgraphNodes, subgraphEdges) = await storage.GetSubgraphAsync(new[] { "dfbb0822-f08f-4e1f-a4a7-20b5cde22893" }, 1);

        // Assert Bindings
        // 1. Next.js component to Sitecore Rendering (BINDS_TO)
        Assert.Contains(subgraphEdges, e => e.Relationship == "BINDS_TO" && e.SourceId == "src/components/blogbox.tsx" && e.TargetId == "dfbb0822-f08f-4e1f-a4a7-20b5cde22893");

        // 2. C# Controller to Sitecore Rendering (CONTROLLER_OF)
        Assert.Contains(subgraphEdges, e => e.Relationship == "CONTROLLER_OF" && e.SourceId == "csharp::blogcontroller" && e.TargetId == "dfbb0822-f08f-4e1f-a4a7-20b5cde22893");

        // 3. GraphQL Query to Sitecore Template (QUERIES_TEMPLATE)
        var (gqlNodes, gqlEdges) = await storage.GetSubgraphAsync(new[] { "query::getblogpost" }, 1);
        Assert.Contains(gqlEdges, e => e.Relationship == "QUERIES_TEMPLATE" && e.SourceId == "query::getblogpost" && e.TargetId == "a2c33f3f-86c5-43a5-aeb4-5598cec45116");

        // 4. Helix Module mappings (BELONGS_TO_MODULE)
        Assert.Contains(gqlEdges, e => e.Relationship == "BELONGS_TO_MODULE" && e.TargetId == "helix::feature::blog");
        Assert.Contains(subgraphEdges, e => e.Relationship == "BELONGS_TO_MODULE" && e.TargetId == "helix::feature::blog");
    }
}
