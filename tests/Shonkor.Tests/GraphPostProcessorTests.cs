using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;
using Shonkor.Plugins;
using Xunit;

namespace Shonkor.Tests;

public class GraphPostProcessorTests
{
    /// <summary>Minimal in-memory <see cref="IGraphView"/> for resolver unit tests.</summary>
    private sealed class FakeGraphView : IGraphView
    {
        public List<GraphNode> ClrTypeNodes { get; } = new();
        public Dictionary<string, IReadOnlyList<GraphNode>> Definitions { get; } = new(StringComparer.Ordinal);

        public Task<GraphNode?> GetNodeAsync(string id, CancellationToken ct = default) => Task.FromResult<GraphNode?>(null);

        public Task<IReadOnlyList<GraphNode>> NodesByTypeAsync(string type, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GraphNode>>(type == "ClrType" ? ClrTypeNodes : new List<GraphNode>());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>> DefinitionsByNameAsync(IEnumerable<string> names, CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, IReadOnlyList<GraphNode>> result =
                names.Where(Definitions.ContainsKey).ToDictionary(n => n, n => Definitions[n], StringComparer.Ordinal);
            return Task.FromResult(result);
        }

        public Task<(IReadOnlyList<GraphEdge> Edges, IReadOnlyDictionary<string, GraphNode> Neighbours)> IncidentEdgesAsync(string nodeId, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<GraphEdge>> EdgesByRelationshipAsync(string relationship, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GraphEdge>>(new List<GraphEdge>());
    }

    private static GraphNode Clr(string full) =>
        new() { Id = $"clrtype:{full}", Type = "ClrType", Name = full.Split('.').Last(), Properties = new() { ["clrType"] = full } };

    [Fact]
    public async Task ClrTypeResolver_LinksUnambiguousDefinition()
    {
        var view = new FakeGraphView();
        view.ClrTypeNodes.Add(Clr("My.Pipelines.MyProcessor"));
        view.Definitions["MyProcessor"] = new List<GraphNode>
        {
            new() { Id = "C:/src/MyProcessor.cs::MyProcessor", Type = "Class", Name = "MyProcessor" }
        };

        var enrichment = await new ClrTypeResolverPostProcessor().ProcessAsync(view);

        Assert.Contains(enrichment.Edges, e => e.Relationship == "RESOLVES_TO"
            && e.SourceId == "clrtype:My.Pipelines.MyProcessor"
            && e.TargetId == "C:/src/MyProcessor.cs::MyProcessor");
        Assert.Empty(enrichment.Diagnostics);
    }

    [Fact]
    public async Task ClrTypeResolver_WarnsOnUnresolvedType()
    {
        var view = new FakeGraphView();
        view.ClrTypeNodes.Add(Clr("My.Missing.Handler"));

        var enrichment = await new ClrTypeResolverPostProcessor().ProcessAsync(view);

        Assert.Empty(enrichment.Edges);
        Assert.Contains(enrichment.Diagnostics, d => d.Code == "sitecore.clrtype-unresolved" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task ClrTypeResolver_FlagsAmbiguousMatches()
    {
        var view = new FakeGraphView();
        view.ClrTypeNodes.Add(Clr("A.Service"));
        view.Definitions["Service"] = new List<GraphNode>
        {
            new() { Id = "C:/a.cs::Service", Type = "Class", Name = "Service" },
            new() { Id = "C:/b.cs::Service", Type = "Class", Name = "Service" }
        };

        var enrichment = await new ClrTypeResolverPostProcessor().ProcessAsync(view);

        Assert.Equal(2, enrichment.Edges.Count(e => e.Relationship == "RESOLVES_TO" && e.Properties["confidence"] == "ambiguous"));
        Assert.Contains(enrichment.Diagnostics, d => d.Code == "sitecore.clrtype-ambiguous" && d.Severity == DiagnosticSeverity.Info);
    }

    [Fact]
    public async Task DiagnosticStore_ReplacesBySourceAndFilters()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();

        await storage.ReplaceDiagnosticsAsync("src.a", new[]
        {
            new GraphDiagnostic("c.warn", DiagnosticSeverity.Warning, "w", "n1"),
            new GraphDiagnostic("c.info", DiagnosticSeverity.Info, "i")
        });
        await storage.ReplaceDiagnosticsAsync("src.b", new[]
        {
            new GraphDiagnostic("c.err", DiagnosticSeverity.Error, "e")
        });

        Assert.Equal(3, (await storage.GetDiagnosticsAsync()).Count);

        // Re-running src.a replaces only its own rows (src.b's error survives).
        await storage.ReplaceDiagnosticsAsync("src.a", new[] { new GraphDiagnostic("c.warn2", DiagnosticSeverity.Warning, "w2") });
        var all = await storage.GetDiagnosticsAsync();
        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, d => d.Code == "c.info");

        // Severity filter: only Error-and-above.
        var errors = await storage.GetDiagnosticsAsync(DiagnosticSeverity.Error);
        Assert.Single(errors);
        Assert.Equal("c.err", errors[0].Code);
    }
}
