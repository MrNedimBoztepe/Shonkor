using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugins;

/// <summary>
/// Phase-2 resolver: links the synthetic <c>clrtype:{FullName}</c> anchors created by
/// <see cref="SitecoreConfigPlugin"/> to the real C# definition nodes, so "where is this processor/service
/// implemented?" traverses from config to code. Emits:
///   clrtype:X --RESOLVES_TO--> {file}::Class      (one unambiguous match)
///   ...all matches with confidence=ambiguous + an Info diagnostic   (several same-named definitions)
///   a Warning diagnostic                                            (no matching definition indexed)
/// </summary>
public sealed class ClrTypeResolverPostProcessor : IGraphPostProcessor
{
    public string Name => "sitecore.clrtype-resolver";

    public async Task<GraphEnrichment> ProcessAsync(IGraphView graph)
    {
        var clrTypes = await graph.NodesByTypeAsync("ClrType").ConfigureAwait(false);
        if (clrTypes.Count == 0) return GraphEnrichment.Empty;

        var names = clrTypes.Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal);
        var definitionsByName = await graph.DefinitionsByNameAsync(names).ConfigureAwait(false);

        var edges = new List<GraphEdge>();
        var diagnostics = new List<GraphDiagnostic>();

        foreach (var clr in clrTypes)
        {
            var fullType = clr.Properties.TryGetValue("clrType", out var f) ? f : clr.Name;

            if (definitionsByName.TryGetValue(clr.Name, out var matches) && matches.Count > 0)
            {
                if (matches.Count == 1)
                {
                    edges.Add(new GraphEdge { SourceId = clr.Id, TargetId = matches[0].Id, Relationship = "RESOLVES_TO" });
                }
                else
                {
                    // Same simple name in several namespaces/files — link all, flag low confidence.
                    foreach (var match in matches)
                    {
                        edges.Add(new GraphEdge
                        {
                            SourceId = clr.Id,
                            TargetId = match.Id,
                            Relationship = "RESOLVES_TO",
                            Properties = new Dictionary<string, string> { ["confidence"] = "ambiguous" }
                        });
                    }
                    diagnostics.Add(new GraphDiagnostic(
                        "sitecore.clrtype-ambiguous", DiagnosticSeverity.Info,
                        $"Type '{fullType}' resolves to {matches.Count} indexed definitions; linked all (ambiguous).",
                        clr.Id));
                }
            }
            else
            {
                diagnostics.Add(new GraphDiagnostic(
                    "sitecore.clrtype-unresolved", DiagnosticSeverity.Warning,
                    $"Config references type '{fullType}' but no matching C# definition is indexed.",
                    clr.Id));
            }
        }

        return new GraphEnrichment(Array.Empty<GraphNode>(), edges, diagnostics);
    }
}
