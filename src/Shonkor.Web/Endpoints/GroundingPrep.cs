// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Configuration;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;

namespace Shonkor.Web.Endpoints;

/// <summary>
/// Server-side grounding preparation for the ask endpoints (TICKET-206): resolves the context nodes,
/// applies the relevance floor (abstaining deterministically WITHOUT an LLM call when nothing clears it),
/// annotates injection-flagged sources, and assembles the <see cref="RagPromptOptions"/> (fenced chat
/// transcript, per-node match strength, answer language). Kept separate from the endpoint wiring so the
/// grounding decisions are unit-testable in isolation.
/// </summary>
public sealed record GroundingPrep(
    bool NoEvidence,
    string AbstentionText,
    IReadOnlyList<GraphNode> ContextNodes,
    RagPromptOptions Options,
    ContextPlan Plan)
{
    // The injection detector's diagnostic code — flagged context sources are annotated in the prompt.
    private const string SuspiciousCode = "security.suspicious-instruction-in-content";
    // Hard cap on how many node ids one ask may carry (TICKET-205), before the token budget prunes further.
    private const int DefaultMaxContextNodes = 20;

    /// <summary>Answer metadata for the UI (TICKET-205): what context actually reached the model.</summary>
    public object ContextMetadata() => new
    {
        nodesUsed = Plan.Nodes.Count,
        truncated = Plan.TruncatedNodeIds.Count,
        dropped = Plan.DroppedNodeCount,
        nodes = Plan.Nodes.Select(n => new { id = n.Id, name = n.Name, truncated = Plan.TruncatedNodeIds.Contains(n.Id) })
    };

    private static GroundingPrep Abstain(string text) =>
        new(NoEvidence: true, text, Array.Empty<GraphNode>(), RagPromptOptions.Default,
            new ContextPlan(Array.Empty<GraphNode>(), new HashSet<string>(), 0, 0));

    public static async Task<GroundingPrep> BuildAsync(
        AskRagRequest req,
        IGraphStorageProvider storage,
        IConfiguration config,
        CancellationToken ct)
    {
        // The deterministic abstention is a FIXED marker — always English, regardless of req.Language.
        var abstention = RagPromptBuilder.AbstentionMarkerEn;

        // Absolute relevance floor (default 0 = off until calibrated). When >0 and the client passed
        // per-node scores, context nodes below the floor are dropped; if none survive, we abstain here
        // without spending an LLM call — the model can't ground an answer in sub-threshold noise.
        var floor = config.GetValue<double?>("Rag:MinRelevanceScore") ?? 0.0;

        var scoreById = new Dictionary<string, double>(StringComparer.Ordinal);
        if (req.Scores is { Length: > 0 })
        {
            for (var i = 0; i < req.NodeIds.Length && i < req.Scores.Length; i++)
            {
                scoreById[req.NodeIds[i]] = req.Scores[i];
            }
        }

        // Cap + dedupe the requested ids (TICKET-205): a caller can't force an unbounded prompt; the token
        // budget prunes further. Order is preserved (retrieval already ranked them), first occurrence wins.
        var maxNodes = config.GetValue<int?>("Rag:MaxContextNodes") ?? DefaultMaxContextNodes;
        var requestedIds = req.NodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Max(1, maxNodes))
            .ToList();

        var contextNodes = new List<GraphNode>();
        var matchStrength = new Dictionary<string, double>(StringComparer.Ordinal);
        var anyScored = scoreById.Count > 0;
        var anyAboveFloor = false;
        foreach (var id in requestedIds)
        {
            var node = await storage.GetNodeByIdAsync(id, ct).ConfigureAwait(false);
            if (node is null) continue;

            if (scoreById.TryGetValue(id, out var score))
            {
                matchStrength[node.Id] = score;
                if (floor > 0 && score < floor) continue; // drop sub-threshold context
                anyAboveFloor = true;
            }
            contextNodes.Add(node);
        }

        // Abstain deterministically only when a floor is set AND scores were provided AND nothing cleared it.
        if (floor > 0 && anyScored && !anyAboveFloor)
        {
            return Abstain(abstention);
        }

        // Which of the surviving context nodes did the injection detector flag?
        var flagged = new HashSet<string>(StringComparer.Ordinal);
        if (contextNodes.Count > 0)
        {
            var diagnostics = await storage.GetDiagnosticsAsync(code: SuspiciousCode, cancellationToken: ct).ConfigureAwait(false);
            var contextIds = contextNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var d in diagnostics)
            {
                if (d.NodeId is { Length: > 0 } nid && contextIds.Contains(nid)) flagged.Add(nid);
            }
        }

        var history = (req.History ?? Array.Empty<ChatTurnDto>())
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => new ChatTurn(t.Role ?? "user", t.Text))
            .ToList();

        var options = new RagPromptOptions
        {
            History = history,
            MatchStrength = matchStrength.Count > 0 ? matchStrength : null,
            FlaggedNodeIds = flagged,
            Language = req.Language,
            NumCtx = config.GetValue<int?>("SemanticAnalyzer:NumCtx") ?? 8192
        };

        // Budget the context to the window now, so the answer metadata ("N nodes, M truncated") reflects
        // exactly what the analyzer will render (PlanContext is deterministic — both compute the same plan).
        var plan = RagPromptBuilder.PlanContext(contextNodes, options);

        return new GroundingPrep(NoEvidence: false, abstention, contextNodes, options, plan);
    }
}
