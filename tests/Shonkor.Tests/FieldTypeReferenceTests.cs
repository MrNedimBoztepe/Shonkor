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

public class FieldTypeReferenceTests
{
    private const string TemplateFieldTemplateId = "455a3e98-a627-4b40-8035-e683a0331ac7";
    private const string GuidA = "11111111-1111-1111-1111-111111111111";
    private const string GuidB = "22222222-2222-2222-2222-222222222222";
    private const string GuidC = "33333333-3333-3333-3333-333333333333";
    private const string GuidD = "44444444-4444-4444-4444-444444444444";
    private const string Page1 = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

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

    private static GraphNode FieldDef(string id, string name, string type) => new()
    {
        Id = id, Type = "SitecoreItem", Name = name,
        Properties = new Dictionary<string, string> { ["Field_Type"] = type }
    };

    [Fact]
    public async Task ClassifiesReferenceAndTextFields_LeavesUnknownAlone()
    {
        var view = new FakeGraphView();
        view.Items.Add(FieldDef("f-related", "RelatedArticles", "Multilist"));   // reference type
        view.Items.Add(FieldDef("f-summary", "Summary", "Single-Line Text"));     // text type
        view.Items.Add(new GraphNode
        {
            Id = Page1, Type = "SitecoreItem", Name = "Home", FilePath = "Home.yml",
            Properties = new Dictionary<string, string>
            {
                ["Field_RelatedArticles"] = $"{{{GuidA}}}|{{{GuidB}}}",  // reference field → real links
                ["Field_Summary"] = $"see {GuidC} for details",          // text field → spurious GUID
                ["Field_Mystery"] = $"{GuidD}"                           // unknown field → untouched
            }
        });
        // The field-definition items declare themselves via BASED_ON_TEMPLATE -> Template field template.
        view.EdgesByRel["BASED_ON_TEMPLATE"] = new List<GraphEdge>
        {
            new() { SourceId = "f-related", TargetId = TemplateFieldTemplateId, Relationship = "BASED_ON_TEMPLATE" },
            new() { SourceId = "f-summary", TargetId = TemplateFieldTemplateId, Relationship = "BASED_ON_TEMPLATE" }
        };

        var e = await new FieldTypeReferencePostProcessor().ProcessAsync(view);

        // Reference field → high-confidence typed edges to both targets.
        Assert.Contains(e.Edges, x => x.Relationship == "REFERENCES_ITEM" && x.SourceId == Page1 && x.TargetId == GuidA);
        Assert.Contains(e.Edges, x => x.Relationship == "REFERENCES_ITEM" && x.SourceId == Page1 && x.TargetId == GuidB);

        // Text field → spurious-reference diagnostic, not an edge.
        var diag = Assert.Single(e.Diagnostics);
        Assert.Equal("sitecore.spurious-reference", diag.Code);
        Assert.Equal(DiagnosticSeverity.Info, diag.Severity);
        Assert.Contains(GuidC, diag.Message);
        Assert.Equal(Page1, diag.NodeId);

        // Unknown field → no edge, no diagnostic for its GUID.
        Assert.DoesNotContain(e.Edges, x => x.TargetId == GuidD);
        Assert.DoesNotContain(e.Diagnostics, d => d.Message.Contains(GuidD));
    }

    [Fact]
    public async Task DoesNothing_WhenNoFieldDefinitionsAreSerialized()
    {
        var view = new FakeGraphView();
        view.Items.Add(new GraphNode
        {
            Id = Page1, Type = "SitecoreItem", Name = "Home",
            Properties = new Dictionary<string, string> { ["Field_RelatedArticles"] = $"{{{GuidA}}}" }
        });

        var e = await new FieldTypeReferencePostProcessor().ProcessAsync(view);

        Assert.Empty(e.Edges);
        Assert.Empty(e.Diagnostics);
    }
}
