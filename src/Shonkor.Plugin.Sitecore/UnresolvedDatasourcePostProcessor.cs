using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugin.Sitecore;

/// <summary>
/// Phase-2 diagnostic (F8): flags Sitecore renderings/routes whose datasource points to an item that is
/// NOT in the graph — a deleted item or one that was never serialized. Scoped to <c>USES_DATASOURCE</c>
/// (the high-signal case): template/base-template references commonly target out-of-repo system items, so
/// flagging those would be noise. Emits no nodes/edges — only diagnostics, surfaced via <c>get_diagnostics</c>.
/// </summary>
public sealed class UnresolvedDatasourcePostProcessor : IGraphPostProcessor
{
    public string Name => "sitecore.unresolved-datasource";

    // The content node types that carry datasource references (classic Unicorn items + headless routes).
    private static readonly string[] SourceTypes = { "SitecoreItem", "XmCloudRouteData" };

    public async Task<GraphEnrichment> ProcessAsync(IGraphView graph)
    {
        var diagnostics = new List<GraphDiagnostic>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in SourceTypes)
        {
            var nodes = await graph.NodesByTypeAsync(type).ConfigureAwait(false);
            foreach (var node in nodes)
            {
                // IncidentEdges returns the node's edges straight from the edge table (dangling edges
                // included); a target that exists shows up in Neighbours, a missing one does not.
                var (edges, neighbours) = await graph.IncidentEdgesAsync(node.Id).ConfigureAwait(false);
                foreach (var edge in edges)
                {
                    if (edge.SourceId != node.Id) continue;                  // outgoing only
                    if (edge.Relationship != "USES_DATASOURCE") continue;
                    if (neighbours.ContainsKey(edge.TargetId)) continue;     // datasource resolves — fine

                    if (!reported.Add($"{node.Id}|{edge.TargetId}")) continue;

                    diagnostics.Add(new GraphDiagnostic(
                        "sitecore.unresolved-datasource", DiagnosticSeverity.Warning,
                        $"'{node.Name}' uses a datasource '{edge.TargetId}' that is not in the graph (deleted item or not serialized).",
                        node.Id, node.FilePath));
                }
            }
        }

        return new GraphEnrichment(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), diagnostics);
    }
}
