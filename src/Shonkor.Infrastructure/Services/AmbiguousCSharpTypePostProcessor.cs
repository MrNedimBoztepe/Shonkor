// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// First-party phase-2 post-processor for TICKET-004 / finding H1. In the default (non-semantic) C#
/// linking mode, <c>REFERENCES_TYPE</c> edges are resolved by NAME — so a type name that exists in more
/// than one namespace links to EVERY same-named definition, producing false edges that inflate
/// <c>blast_radius</c>/<c>impact_of</c>/<c>rename_plan</c>. This post-processor does not change any edges
/// (that would need exact semantic resolution + edge-property persistence); it makes the imprecision
/// VISIBLE by emitting a Warning diagnostic for each ambiguous type name that is actually referenced, so
/// agents/UI know which impact results are name-based and can enable exact resolution.
/// Additive and failure-isolated per the <see cref="IGraphPostProcessor"/> contract.
/// </summary>
public sealed class AmbiguousCSharpTypePostProcessor : IGraphPostProcessor
{
    private static readonly string[] CSharpDefinitionTypes = { "Class", "Interface", "Record", "Struct", "Enum" };

    public string Name => "csharp.ambiguous-type-references";

    public async Task<GraphEnrichment> ProcessAsync(IGraphView graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // Collect C# type definitions and group them by simple name.
        var definitions = new List<GraphNode>();
        foreach (var type in CSharpDefinitionTypes)
        {
            definitions.AddRange(await graph.NodesByTypeAsync(type).ConfigureAwait(false));
        }

        var ambiguous = definitions
            .Where(d => !string.IsNullOrEmpty(d.Name))
            .GroupBy(d => d.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();

        if (ambiguous.Count == 0)
        {
            return GraphEnrichment.Empty;
        }

        // Only warn about ambiguity that actually matters — i.e. names that something references, since an
        // unreferenced same-named type pair produces no (over-)edges.
        var refEdges = await graph.EdgesByRelationshipAsync("REFERENCES_TYPE").ConfigureAwait(false);
        var referencedTargetIds = new HashSet<string>(refEdges.Select(e => e.TargetId), StringComparer.Ordinal);

        var diagnostics = new List<GraphDiagnostic>();
        foreach (var group in ambiguous)
        {
            var defs = group.ToList();
            if (!defs.Any(d => referencedTargetIds.Contains(d.Id)))
            {
                continue;
            }

            var locations = string.Join("; ", defs.Select(d => d.FilePath ?? d.Id));
            diagnostics.Add(new GraphDiagnostic(
                Code: "csharp.ambiguous-type-reference",
                Severity: DiagnosticSeverity.Warning,
                Message: $"Type name '{group.Key}' has {defs.Count} definitions ({locations}). " +
                         $"References to '{group.Key}' that name-based resolution can't disambiguate link to ALL of them, so " +
                         "impact/rename results for this type may over-connect. Semantic C# resolution (on by default) resolves " +
                         "compilable references exactly; residual over-connection here comes from references it could not resolve " +
                         "(e.g. a partial or non-compiling checkout).",
                NodeId: defs[0].Id,
                FilePath: defs[0].FilePath));
        }

        return new GraphEnrichment(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), diagnostics);
    }
}
