// Licensed to LLMBrain under the MIT License.

using LLMBrain.Core.Models;
using LLMBrain.Core.Services;
using LLMBrain.Infrastructure.Storage;

namespace LLMBrain.Tests;

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
        Assert.Contains(importEdges, e => e.TargetId.Contains("UserService"));
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
}
