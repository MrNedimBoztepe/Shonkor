using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Plugins;
using Xunit;

namespace Shonkor.Tests;

public class UnresolvedDatasourceTests
{
    private sealed class FakeGraphView : IGraphView
    {
        public Dictionary<string, List<GraphNode>> NodesByType { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (List<GraphEdge> Edges, Dictionary<string, GraphNode> Neighbours)> Incident { get; } = new(StringComparer.Ordinal);

        public Task<GraphNode?> GetNodeAsync(string id, CancellationToken ct = default) => Task.FromResult<GraphNode?>(null);

        public Task<IReadOnlyList<GraphNode>> NodesByTypeAsync(string type, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GraphNode>>(NodesByType.TryGetValue(type, out var l) ? l : new List<GraphNode>());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>> DefinitionsByNameAsync(IEnumerable<string> names, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>>(new Dictionary<string, IReadOnlyList<GraphNode>>());

        public Task<(IReadOnlyList<GraphEdge> Edges, IReadOnlyDictionary<string, GraphNode> Neighbours)> IncidentEdgesAsync(string nodeId, CancellationToken ct = default)
            => Incident.TryGetValue(nodeId, out var v)
                ? Task.FromResult<(IReadOnlyList<GraphEdge>, IReadOnlyDictionary<string, GraphNode>)>((v.Edges, v.Neighbours))
                : Task.FromResult<(IReadOnlyList<GraphEdge>, IReadOnlyDictionary<string, GraphNode>)>((new List<GraphEdge>(), new Dictionary<string, GraphNode>()));
    }

    [Fact]
    public async Task FlagsMissingDatasource_ButNotResolvedOnesOrOtherRelations()
    {
        var page = new GraphNode { Id = "page1", Type = "SitecoreItem", Name = "Home", FilePath = "Home.yml" };
        var view = new FakeGraphView();
        view.NodesByType["SitecoreItem"] = new List<GraphNode> { page };
        view.Incident["page1"] = (
            new List<GraphEdge>
            {
                new() { SourceId = "page1", TargetId = "ds-exists", Relationship = "USES_DATASOURCE" },
                new() { SourceId = "page1", TargetId = "ds-missing", Relationship = "USES_DATASOURCE" },
                new() { SourceId = "page1", TargetId = "tmpl-missing", Relationship = "BASED_ON_TEMPLATE" } // not checked → no noise
            },
            new Dictionary<string, GraphNode>
            {
                ["page1"] = page,
                ["ds-exists"] = new() { Id = "ds-exists", Type = "SitecoreItem", Name = "DS" }
            });

        var enrichment = await new UnresolvedDatasourcePostProcessor().ProcessAsync(view);

        var diag = Assert.Single(enrichment.Diagnostics);
        Assert.Equal("sitecore.unresolved-datasource", diag.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diag.Severity);
        Assert.Contains("ds-missing", diag.Message);
        Assert.Equal("page1", diag.NodeId);
        Assert.Empty(enrichment.Edges);
        Assert.Empty(enrichment.Nodes);
    }

    [Fact]
    public async Task NoDiagnostics_WhenAllDatasourcesResolve()
    {
        var page = new GraphNode { Id = "p", Type = "SitecoreItem", Name = "P" };
        var view = new FakeGraphView();
        view.NodesByType["SitecoreItem"] = new List<GraphNode> { page };
        view.Incident["p"] = (
            new List<GraphEdge> { new() { SourceId = "p", TargetId = "ds", Relationship = "USES_DATASOURCE" } },
            new Dictionary<string, GraphNode> { ["p"] = page, ["ds"] = new() { Id = "ds", Type = "SitecoreItem", Name = "D" } });

        var enrichment = await new UnresolvedDatasourcePostProcessor().ProcessAsync(view);

        Assert.Empty(enrichment.Diagnostics);
    }
}
