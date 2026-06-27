using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugin.Sitecore;

/// <summary>
/// Phase-2 resolver: links the synthetic <c>clrtype:{FullName}</c> anchors created by
/// <see cref="SitecoreConfigPlugin"/> to the real C# definition nodes, so "where is this processor/service
/// implemented?" traverses from config to code. Emits:
///   clrtype:X --RESOLVES_TO--> {file}::Class      (one unambiguous match)
///   ...all matches with confidence=ambiguous + an Info diagnostic   (several same-named definitions)
///   a Warning diagnostic                                            (no matching definition indexed)
///   an Info diagnostic                                              (unresolved, but a framework/platform type)
/// Types from platform/third-party assemblies (Sitecore.*, System.*, etc.) live in compiled DLLs, never in
/// indexed source, so their non-resolution is expected — they are downgraded to Info instead of Warning so the
/// warning stream stays a real signal of *your own* missing code.
/// </summary>
public sealed class ClrTypeResolverPostProcessor : IGraphPostProcessor
{
    public string Name => "sitecore.clrtype-resolver";

    /// <summary>
    /// Namespace prefixes whose types are defined in platform or third-party assemblies, not in the
    /// project's own indexed source. An unresolved reference to one of these is expected, not a problem.
    /// </summary>
    private static readonly string[] FrameworkPrefixes =
    {
        // Platform + core framework assemblies.
        "Sitecore.", "System.", "Microsoft.", "Newtonsoft.", "Castle.", "log4net.", "Owin.",
        "Lucene.", "Mvc.", "Mongo", "Solr.",
        // Common third-party Sitecore modules (ship as compiled DLLs, never in indexed source).
        "Glass.", "Unicorn.", "Rainbow.", "Dianoga.", "Synthesis.", "Fortis.", "Constellation.",
        "Cognifide.", "Spe.", "Feydra.", "Leprechaun.",
        // DI containers / utility libraries common in Sitecore Helix solutions.
        "SimpleInjector.", "Autofac.", "Ninject.", "StructureMap.", "DryIoc.",
        "MediatR.", "AutoMapper.", "FluentValidation."
    };

    private static bool IsExternalType(string? fullType, IReadOnlyList<string> prefixes) =>
        !string.IsNullOrEmpty(fullType) &&
        prefixes.Any(p => fullType!.StartsWith(p, StringComparison.Ordinal));

    /// <summary>Older single-argument entrypoint — runs with no user-configured external prefixes.</summary>
    public Task<GraphEnrichment> ProcessAsync(IGraphView graph) => ProcessAsync(graph, GraphPostProcessorContext.Empty);

    public async Task<GraphEnrichment> ProcessAsync(IGraphView graph, GraphPostProcessorContext context)
    {
        // Built-in framework prefixes plus any namespaces the user declared external for this project.
        var externalPrefixes = context.ExternalTypePrefixes is { Count: > 0 }
            ? FrameworkPrefixes.Concat(context.ExternalTypePrefixes).ToArray()
            : FrameworkPrefixes;

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
            else if (IsExternalType(fullType, externalPrefixes))
            {
                // Defined in a platform/third-party assembly — not expected to be in indexed source.
                diagnostics.Add(new GraphDiagnostic(
                    "sitecore.clrtype-external", DiagnosticSeverity.Info,
                    $"Config references framework type '{fullType}' (defined in a platform/third-party assembly, not in indexed source).",
                    clr.Id));
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
