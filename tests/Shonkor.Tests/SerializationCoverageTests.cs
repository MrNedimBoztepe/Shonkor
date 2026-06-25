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

public class SerializationCoverageTests
{
    private const string StandardTemplate = "1930bbeb-7805-471a-a3be-4858ac7cf696"; // denylisted system item
    private const string Page1 = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string TemplateX = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";        // serialized
    private const string MissingTemplate = "cccccccc-cccc-cccc-cccc-cccccccccccc"; // not serialized
    private const string MissingRendering = "dddddddd-dddd-dddd-dddd-dddddddddddd"; // not serialized

    private sealed class FakeGraphView : IGraphView
    {
        public List<GraphNode> Items { get; } = new();
        public Dictionary<string, List<GraphEdge>> EdgesByRel { get; } = new(StringComparer.Ordinal);

        public Task<GraphNode?> GetNodeAsync(string id, CancellationToken ct = default) => Task.FromResult<GraphNode?>(null);
        public Task<IReadOnlyList<GraphNode>> NodesByTypeAsync(string type, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GraphNode>>(type == "SitecoreItem" ? Items : new List<GraphNode>());
        public Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>> DefinitionsByNameAsync(IEnumerable<string> names, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>>(new Dictionary<string, IReadOnlyList<GraphNode>>());
        public Task<(IReadOnlyList<GraphEdge> Edges, IReadOnlyDictionary<string, GraphNode> Neighbours)> IncidentEdgesAsync(string nodeId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<GraphEdge>> EdgesByRelationshipAsync(string relationship, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GraphEdge>>(EdgesByRel.TryGetValue(relationship, out var l) ? l : new List<GraphEdge>());
    }

    [Fact]
    public async Task FlagsMissingTemplateAndRendering_NotCoveredOrDenylisted()
    {
        var view = new FakeGraphView();
        view.Items.Add(new GraphNode { Id = Page1, Type = "SitecoreItem", Name = "Home", FilePath = "Home.yml" });
        view.Items.Add(new GraphNode { Id = TemplateX, Type = "SitecoreItem", Name = "PageTemplate" });

        view.EdgesByRel["BASED_ON_TEMPLATE"] = new List<GraphEdge>
        {
            new() { SourceId = Page1, TargetId = TemplateX, Relationship = "BASED_ON_TEMPLATE" },         // covered
            new() { SourceId = Page1, TargetId = MissingTemplate, Relationship = "BASED_ON_TEMPLATE" },   // gap
            new() { SourceId = Page1, TargetId = StandardTemplate, Relationship = "BASED_ON_TEMPLATE" }   // denylisted
        };
        view.EdgesByRel["HAS_RENDERING"] = new List<GraphEdge>
        {
            new() { SourceId = Page1, TargetId = MissingRendering, Relationship = "HAS_RENDERING" }        // gap
        };

        var e = await new SerializationCoveragePostProcessor().ProcessAsync(view);

        Assert.Empty(e.Nodes);
        Assert.Empty(e.Edges);
        Assert.Equal(2, e.Diagnostics.Count);
        Assert.All(e.Diagnostics, d =>
        {
            Assert.Equal("sitecore.serialization-coverage", d.Code);
            Assert.Equal(DiagnosticSeverity.Info, d.Severity);
        });
        Assert.Contains(e.Diagnostics, d => d.Message.Contains(MissingTemplate) && d.Message.Contains("template"));
        Assert.Contains(e.Diagnostics, d => d.Message.Contains(MissingRendering) && d.Message.Contains("rendering"));
        Assert.DoesNotContain(e.Diagnostics, d => d.Message.Contains(StandardTemplate));
        Assert.DoesNotContain(e.Diagnostics, d => d.Message.Contains(TemplateX));
    }
}
