using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugins;

/// <summary>
/// Phase-2 diagnostic (F7): surfaces serialization-coverage gaps — items the serialized set <i>references</i>
/// (their template, base template, or a rendering definition) but does <i>not</i> <i>contain</i>. Unlike F8
/// (datasources, a high-signal Warning), these references often point at standard Sitecore items that live in
/// the core/master DB and are intentionally out of repo, so this is <b>advisory (Info)</b> and filters a small
/// denylist of well-known system items. The honest framing: "verify these are intentionally out of scope."
/// Reasons purely over persisted topology (edge target absent from the serialized node set). No nodes/edges.
/// </summary>
public sealed class SerializationCoveragePostProcessor : IGraphPostProcessor
{
    public string Name => "sitecore.serialization-coverage";

    // Structural references whose dangling target indicates a serialization gap, with a human-readable label.
    private static readonly (string Relationship, string Label)[] CoverageRelationships =
    {
        ("BASED_ON_TEMPLATE", "template"),
        ("INHERITS_FROM", "base template"),
        ("HAS_RENDERING", "rendering definition")
    };

    public async Task<GraphEnrichment> ProcessAsync(IGraphView graph)
    {
        // The serialized set: every item id present in the graph. A reference target outside this set is a gap.
        var items = await graph.NodesByTypeAsync("SitecoreItem").ConfigureAwait(false);
        var present = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items) present[item.Id] = item;

        var diagnostics = new List<GraphDiagnostic>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (relationship, label) in CoverageRelationships)
        {
            var edges = await graph.EdgesByRelationshipAsync(relationship).ConfigureAwait(false);
            foreach (var edge in edges)
            {
                var target = edge.TargetId;
                if (present.ContainsKey(target)) continue;                               // serialized — covered
                if (SitecoreCmsConstants.SystemItemDenylist.Contains(target)) continue;   // known out-of-repo system item

                if (!reported.Add($"{label}|{target}")) continue;                         // one per missing target+kind

                var sourceName = present.TryGetValue(edge.SourceId, out var src) ? src.Name : edge.SourceId;
                diagnostics.Add(new GraphDiagnostic(
                    "sitecore.serialization-coverage", DiagnosticSeverity.Info,
                    $"'{sourceName}' references a {label} '{target}' that is not in the serialized set — verify it is intentionally out of scope.",
                    edge.SourceId, src?.FilePath));
            }
        }

        return new GraphEnrichment(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), diagnostics);
    }
}
