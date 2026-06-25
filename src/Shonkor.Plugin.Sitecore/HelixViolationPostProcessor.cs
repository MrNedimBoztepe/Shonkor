using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugin.Sitecore;

/// <summary>
/// Phase-2 diagnostic: enforces the Sitecore Helix layer-dependency rule over the C# coupling graph.
/// Helix stability flows downward — Project → Feature → Foundation — so a module may depend only on
/// modules in a LOWER (more stable) layer. This flags two violations: an <b>upward</b> dependency (e.g. a
/// Foundation module referencing a Feature module) and a <b>same-layer</b> cross-module dependency (e.g.
/// one Feature module referencing another). It reasons over the C# coupling relationships, whose node ids
/// encode the declaring file path (<c>{filePath}::{Type}</c>); endpoints outside a Helix layer are ignored.
/// One diagnostic per offending (source module → target module) pair, anchored to an exemplar source node.
/// Emits no nodes/edges — diagnostics only, surfaced via <c>get_diagnostics</c>.
/// </summary>
public sealed class HelixViolationPostProcessor : IGraphPostProcessor
{
    public string Name => "sitecore.helix-violation";

    // The C# coupling relationships whose endpoints are type definitions (ids encode the declaring file).
    private static readonly string[] CouplingRelationships = { "REFERENCES_TYPE", "IMPLEMENTS", "EXTENDS" };

    // Layer stability rank: lower = more stable. A dependency may only point to a STRICTLY lower rank.
    private static readonly Dictionary<string, int> LayerRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Foundation"] = 0,
        ["Feature"] = 1,
        ["Project"] = 2
    };

    // Matches Helix paths like  src/Feature/Checkout/code/...  or  src\Foundation\Serialization\... —
    // the same shape HelixSemanticPlugin uses to mint Concept nodes, so the two agree on module identity.
    private static readonly Regex HelixPattern = new Regex(
        @"[/\\](?<layer>Feature|Foundation|Project)[/\\](?<module>[^/\\]+)[/\\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<GraphEnrichment> ProcessAsync(IGraphView graph)
    {
        var diagnostics = new List<GraphDiagnostic>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var relationship in CouplingRelationships)
        {
            var edges = await graph.EdgesByRelationshipAsync(relationship).ConfigureAwait(false);
            foreach (var edge in edges)
            {
                var source = ModuleOf(edge.SourceId);
                var target = ModuleOf(edge.TargetId);
                if (source is null || target is null) continue;             // at least one end isn't a Helix module
                if (source.Value.Concept == target.Value.Concept) continue; // intra-module dependency — fine

                // Allowed only when the target is strictly more stable (a lower rank) than the source.
                if (target.Value.Rank < source.Value.Rank) continue;

                // One diagnostic per (source module → target module) fact, not per offending reference.
                if (!reported.Add($"{source.Value.Concept}->{target.Value.Concept}")) continue;

                var kind = target.Value.Rank > source.Value.Rank ? "upward" : "same-layer";
                diagnostics.Add(new GraphDiagnostic(
                    "sitecore.helix-violation", DiagnosticSeverity.Warning,
                    $"Helix {kind} dependency: {source.Value.Layer} module '{source.Value.Module}' depends on " +
                    $"{target.Value.Layer} module '{target.Value.Module}' — a module may only depend on more stable (lower) layers.",
                    edge.SourceId, FilePathOf(edge.SourceId)));
            }
        }

        return new GraphEnrichment(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), diagnostics);
    }

    private readonly record struct ModuleInfo(string Layer, string Module, int Rank, string Concept);

    /// <summary>Resolves the Helix (layer, module) a node belongs to from its file path, or null if outside any layer.</summary>
    private static ModuleInfo? ModuleOf(string nodeId)
    {
        var path = FilePathOf(nodeId);
        if (path is null) return null;

        var m = HelixPattern.Match(path);
        if (!m.Success) return null;

        // Canonicalise layer casing so "feature" and "Feature" map to the same concept (module keeps its casing).
        var rawLayer = m.Groups["layer"].Value;
        var layer = char.ToUpperInvariant(rawLayer[0]) + rawLayer.Substring(1).ToLowerInvariant();
        var module = m.Groups["module"].Value;
        return new ModuleInfo(layer, module, LayerRank[layer], $"Concept:{layer}:{module}");
    }

    /// <summary>The declaring file path encoded in a C# node id (everything before the first "::"), or null.</summary>
    private static string? FilePathOf(string nodeId)
    {
        var idx = nodeId.IndexOf("::", StringComparison.Ordinal);
        return idx > 0 ? nodeId.Substring(0, idx) : null;
    }
}
