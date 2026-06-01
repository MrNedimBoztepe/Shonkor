// Licensed to Shonkor under the MIT License.

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
    public async Task CrossTechLinker_ShouldEstablishDynamicMappings()
    {
        // Arrange
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        var nodes = new List<GraphNode>
        {
            // NextJS JSComponent
            new() { Id = "src/components/blogbox.tsx", Type = "JSComponent", Name = "BlogBox", Properties = new Dictionary<string, string> { ["filePath"] = "src/components/blogbox.tsx" } },
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
            new() { Id = "csharp::blogcontroller", Type = "Class", Name = "BlogController", Properties = new Dictionary<string, string> { ["filePath"] = "src/Feature/Blog/code/Controllers/BlogController.cs" } },
            // GraphQL Query referencing BlogPost
            new() {
                Id = "query::getblogpost",
                Type = "GraphQLQuery",
                Name = "GetBlogPost",
                Properties = new Dictionary<string, string> {
                    ["filePath"] = "src/Feature/Blog/graphql/GetBlogPost.graphql",
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
