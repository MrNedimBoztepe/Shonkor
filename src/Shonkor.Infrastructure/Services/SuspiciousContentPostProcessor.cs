// Licensed to Shonkor under the MIT License.

using System.Text.RegularExpressions;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// First-party phase-2 post-processor for TICKET-107 / finding M3 (prompt-injection surface). Indexed
/// content (code comments, docs, strings) is later handed verbatim to an LLM by <c>get_source</c>,
/// <c>generate_capsule</c> and the RAG answer path. This processor flags nodes whose content contains
/// prompt-injection-style instructions ("ignore previous instructions", "you are now …", a fake
/// "system:"/"assistant:" turn) so an operator/agent can see which retrieved material is untrustworthy.
/// It does not alter the graph — the RAG prompt already frames context as data; this just makes the risk
/// visible via <c>get_diagnostics</c>. Additive and failure-isolated per the post-processor contract.
/// </summary>
public sealed class SuspiciousContentPostProcessor : IGraphPostProcessor
{
    // Content-bearing node types worth scanning; short/virtual nodes (Concept, HelixModule, …) are skipped.
    private static readonly string[] ScannedTypes =
        { "File", "MarkdownSection", "Class", "Interface", "Record", "Struct", "Method", "Property" };

    // Conservative, case-insensitive patterns — chosen to catch classic injection phrasings while keeping
    // false positives low (each requires an imperative directed at the model, not incidental prose).
    private static readonly Regex[] Patterns =
    {
        new(@"ignore\s+(all\s+)?(previous|prior|above)\s+(instructions|prompts|context)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"disregard\s+(the\s+)?(above|previous|earlier)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"you\s+are\s+now\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*(system|assistant|developer)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline),
        new(@"\bnew\s+instructions\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };

    public string Name => "security.suspicious-content";

    public async Task<GraphEnrichment> ProcessAsync(IGraphView graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var diagnostics = new List<GraphDiagnostic>();
        foreach (var type in ScannedTypes)
        {
            var nodes = await graph.NodesByTypeAsync(type).ConfigureAwait(false);
            foreach (var node in nodes)
            {
                if (string.IsNullOrEmpty(node.Content))
                {
                    continue;
                }

                var match = Patterns.FirstOrDefault(p => p.IsMatch(node.Content));
                if (match is null)
                {
                    continue;
                }

                diagnostics.Add(new GraphDiagnostic(
                    Code: "security.suspicious-instruction-in-content",
                    Severity: DiagnosticSeverity.Warning,
                    Message: $"Node '{node.Name}' contains text resembling an LLM instruction (possible prompt injection). " +
                             "Retrieved content is treated as untrusted data by the RAG prompt; review before trusting.",
                    NodeId: node.Id,
                    FilePath: node.FilePath));
            }
        }

        return new GraphEnrichment(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), diagnostics);
    }
}

/// <summary>
/// The always-on, first-party phase-2 post-processors, wired into every index path (CLI, index endpoint,
/// webhook, drift). Centralized so the set is defined once rather than re-listed at each call site.
/// </summary>
public static class FirstPartyPostProcessors
{
    public static IEnumerable<IGraphPostProcessor> Create() => new IGraphPostProcessor[]
    {
        new AmbiguousCSharpTypePostProcessor(),
        new SuspiciousContentPostProcessor()
    };
}
