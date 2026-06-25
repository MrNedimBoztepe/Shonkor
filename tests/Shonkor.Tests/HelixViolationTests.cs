using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Plugin.Sitecore;
using Xunit;

namespace Shonkor.Tests;

public class HelixViolationTests
{
    private sealed class FakeGraphView : IGraphView
    {
        public Dictionary<string, List<GraphEdge>> EdgesByRel { get; } = new(StringComparer.Ordinal);

        public Task<GraphNode?> GetNodeAsync(string id, CancellationToken ct = default) => Task.FromResult<GraphNode?>(null);
        public Task<IReadOnlyList<GraphNode>> NodesByTypeAsync(string type, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GraphNode>>(new List<GraphNode>());
        public Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>> DefinitionsByNameAsync(IEnumerable<string> names, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>>(new Dictionary<string, IReadOnlyList<GraphNode>>());
        public Task<(IReadOnlyList<GraphEdge> Edges, IReadOnlyDictionary<string, GraphNode> Neighbours)> IncidentEdgesAsync(string nodeId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<GraphEdge>> EdgesByRelationshipAsync(string relationship, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GraphEdge>>(EdgesByRel.TryGetValue(relationship, out var l) ? l : new List<GraphEdge>());
    }

    // A C# type node id in a Helix module: {repo}/src/{Layer}/{Module}/code/{Type}.cs::{Type}
    private static string Node(string layer, string module, string type)
        => $"C:/repo/src/{layer}/{module}/code/{type}.cs::{type}";

    private static GraphEdge Ref(string sourceId, string targetId)
        => new() { SourceId = sourceId, TargetId = targetId, Relationship = "REFERENCES_TYPE" };

    private static async Task<IReadOnlyList<GraphDiagnostic>> Run(params GraphEdge[] edges)
    {
        var view = new FakeGraphView();
        view.EdgesByRel["REFERENCES_TYPE"] = edges.ToList();
        var enrichment = await new HelixViolationPostProcessor().ProcessAsync(view);
        Assert.Empty(enrichment.Nodes);
        Assert.Empty(enrichment.Edges);
        return enrichment.Diagnostics;
    }

    [Fact]
    public async Task FlagsUpwardDependency_FoundationOnFeature()
    {
        var diags = await Run(Ref(Node("Foundation", "Serialization", "Serializer"),
                                  Node("Feature", "Checkout", "CheckoutService")));

        var d = Assert.Single(diags);
        Assert.Equal("sitecore.helix-violation", d.Code);
        Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("upward", d.Message);
        Assert.Contains("Serialization", d.Message);
        Assert.Contains("Checkout", d.Message);
    }

    [Fact]
    public async Task FlagsSameLayerCrossModule_FeatureOnFeature()
    {
        var diags = await Run(Ref(Node("Feature", "Checkout", "CheckoutService"),
                                  Node("Feature", "Cart", "CartService")));

        var d = Assert.Single(diags);
        Assert.Contains("same-layer", d.Message);
        Assert.Contains("Checkout", d.Message);
        Assert.Contains("Cart", d.Message);
    }

    [Fact]
    public async Task AllowsDownwardDependency_FeatureOnFoundation()
    {
        var diags = await Run(Ref(Node("Feature", "Checkout", "CheckoutService"),
                                  Node("Foundation", "Serialization", "Serializer")));

        Assert.Empty(diags);
    }

    [Fact]
    public async Task AllowsIntraModuleDependency()
    {
        var diags = await Run(Ref(Node("Feature", "Checkout", "CheckoutService"),
                                  Node("Feature", "Checkout", "CheckoutModel")));

        Assert.Empty(diags);
    }

    [Fact]
    public async Task IgnoresNonHelixEndpoints()
    {
        var diags = await Run(Ref(Node("Feature", "Checkout", "CheckoutService"),
                                  "C:/repo/src/Shared/Util.cs::Util"));

        Assert.Empty(diags);
    }

    [Fact]
    public async Task DedupesToOneDiagnosticPerModulePair()
    {
        var diags = await Run(
            Ref(Node("Feature", "Checkout", "CheckoutService"), Node("Feature", "Cart", "CartService")),
            Ref(Node("Feature", "Checkout", "CheckoutController"), Node("Feature", "Cart", "CartModel")));

        Assert.Single(diags);
    }
}
