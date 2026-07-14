// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Models;
using static Shonkor.Infrastructure.Services.Mcp.McpToolHelpers;

namespace Shonkor.Infrastructure.Services.Mcp.Tools;

/// <summary>
/// Provenance-weighted reverse blast radius: which nodes transitively DEPEND on a target (what breaks if
/// you change it), each annotated with the WEAKEST trust tier along the best path to it. Edges are directed
/// Source=dependent → Target=dependency, so dependents are the incoming edges (evidence: SemanticCsharpLinker
/// CALLS/REFERENCES_TYPE/EXTENDS all emit caller/referrer/derived as Source). Structural containment
/// (CONTAINS/BELONGS_TO_MODULE) is not a dependency and is skipped. Returns raw graph facts — no scores/grades.
/// </summary>
public sealed class BlastRadiusTool : IMcpTool
{
    public string Name => "blast_radius";

    public object GetSchema() => new
    {
        name = "blast_radius",
        description = "Reverse impact analysis weighted by trust tier: the nodes that transitively depend on a "
            + "target symbol/file (what breaks if you change it), each with its shortest depth AND the minimum "
            + "provenance tier along the best path (a path is only as trustworthy as its weakest edge — "
            + "Extracted > Inferred > Ambiguous). Unlike a naive blast radius, a dependent reached only through "
            + "a heuristic (Ambiguous) edge is reported as such, not as a certain fact. Returns raw facts "
            + "(affected nodes + traversed edges + tiers), no derived scores/grades.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                nodeOrFile = new { type = "string", description = "The target: a symbol name (e.g. 'GraphNode'), a node id / '@/' handle, or a file path. A file expands to the symbols it defines." },
                maxDepth = new { type = "integer", description = "Max reverse-traversal depth (default 5). No hard cap — a persistent index can afford deeper traversal; when the limit truncates results, 'truncatedAtDepth' is true." },
                minTier = new { type = "string", description = "Minimum trust tier to traverse: 'extracted' = only compiler-proven edges (conservative); 'inferred' = proven + heuristic (excludes ambiguous); 'ambiguous'/'all' (default) = every edge. Edges below the tier are pruned DURING traversal, so the result is a true subset." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            },
            required = new[] { "nodeOrFile" }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var target = args?["nodeOrFile"]?.ToString() ?? args?["symbol"]?.ToString() ?? args?["query"]?.ToString();
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new McpToolException(McpErrorCode.MissingParameter, "Parameter 'nodeOrFile' is required", isArgumentError: true);
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);
        var maxDepth = Math.Max(1, ReadInt(args?["maxDepth"], 5));
        var minTier = ReadMinTier(args);

        // Resolve the target to one or more SEED nodes (a file expands to its defined symbols).
        var seeds = await ResolveSeedsAsync(storage, target, basePath).ConfigureAwait(false);
        if (seeds.Count == 0)
        {
            return SendToolResponse(id, JsonSerializer.Serialize(new
            {
                target,
                found = false,
                message = $"No node, symbol or indexed file matched '{target}'.",
                affected = Array.Empty<object>(),
                edgesTraversed = Array.Empty<object>(),
                truncatedAtDepth = false
            }));
        }

        var seedIds = seeds.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);

        // Reverse BFS with bottleneck relaxation. Per affected node we track the SHORTEST depth and the BEST
        // (highest-trust) minimum-tier-along-path, relaxing until neither improves (bounded by maxDepth and
        // the 3-level tier lattice, so it terminates even on cycles — a node re-enqueues only on improvement).
        var shortestDepth = new Dictionary<string, int>(StringComparer.Ordinal);
        var bestTier = new Dictionary<string, Provenance>(StringComparer.Ordinal);
        var traversed = new Dictionary<(string Source, string Target, string Rel), Provenance>();
        var nodeCache = new Dictionary<string, GraphNode?>(StringComparer.Ordinal);
        var truncated = false;

        // Queue item: the reached node, the depth it was reached at, and the min-tier along that path.
        var queue = new Queue<(string Id, int Depth, Provenance Tier)>();
        foreach (var s in seeds) queue.Enqueue((s.Id, 0, Provenance.Extracted));

        while (queue.Count > 0)
        {
            var (cur, depth, tierSoFar) = queue.Dequeue();
            var (edges, neighbours) = await storage.GetIncidentEdgesAsync(cur).ConfigureAwait(false);
            foreach (var kv in neighbours) nodeCache[kv.Key] = kv.Value;

            foreach (var e in edges)
            {
                // Dependents point AT cur (incoming). Skip self-loops and structural containment.
                if (e.TargetId != cur || e.SourceId == cur) continue;
                if (StructuralRelationships.Contains(e.Relationship)) continue;
                if (!PassesProvenance(e.Provenance, minTier)) continue; // prune below the tier during traversal

                var newDepth = depth + 1;
                if (newDepth > maxDepth) { truncated = true; continue; }

                var src = e.SourceId;
                var newTier = WeakerOf(tierSoFar, e.Provenance); // weakest link along the path
                traversed[(src, cur, e.Relationship)] = e.Provenance;

                var improved = false;
                if (!shortestDepth.TryGetValue(src, out var sd) || newDepth < sd) { shortestDepth[src] = newDepth; improved = true; }
                if (!bestTier.TryGetValue(src, out var bt) || (int)newTier < (int)bt) { bestTier[src] = newTier; improved = true; }
                if (improved) queue.Enqueue((src, newDepth, newTier));
            }
        }

        // Assemble affected nodes (exclude the seeds themselves), best tier + shortest depth per node.
        async Task<GraphNode?> NodeOf(string nid)
        {
            if (nodeCache.TryGetValue(nid, out var n)) return n;
            var fetched = await storage.GetNodeByIdAsync(nid).ConfigureAwait(false);
            nodeCache[nid] = fetched;
            return fetched;
        }

        var affectedRows = new List<(int Depth, string Name, object Row)>();
        foreach (var nid in bestTier.Keys)
        {
            if (seedIds.Contains(nid)) continue;
            var n = await NodeOf(nid).ConfigureAwait(false);
            var name = n?.Name ?? nid;
            var depth = shortestDepth.GetValueOrDefault(nid);
            affectedRows.Add((depth, name, new
            {
                node = ToHandle(nid, basePath),
                name,
                type = n?.Type ?? "Unknown",
                depth,
                minTierAlongPath = TierName(bestTier[nid])
            }));
        }

        var result = new
        {
            target = seeds.Select(s => ToHandle(s.Id, basePath)).ToArray(),
            targetNames = seeds.Select(s => s.Name).ToArray(),
            maxDepth,
            minTier = minTier is { } mt ? TierName(mt) : "all",
            affected = affectedRows
                .OrderBy(a => a.Depth)
                .ThenBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => a.Row)
                .ToArray(),
            edgesTraversed = traversed
                .Select(t => new { source = ToHandle(t.Key.Source, basePath), target = ToHandle(t.Key.Target, basePath), relationship = t.Key.Rel, tier = TierName(t.Value) })
                .OrderBy(x => x.source, StringComparer.Ordinal)
                .ToArray(),
            truncatedAtDepth = truncated
        };

        return SendToolResponse(id, JsonSerializer.Serialize(result));
    }

    /// <summary>The weaker (lower-trust, higher-int) of two tiers — a path is only as trustworthy as its worst edge.</summary>
    private static Provenance WeakerOf(Provenance a, Provenance b) => (int)a >= (int)b ? a : b;

    private static string TierName(Provenance p) => p switch
    {
        Provenance.Extracted => "extracted",
        Provenance.Inferred => "inferred",
        Provenance.Ambiguous => "ambiguous",
        _ => "unknown"
    };

    /// <summary>
    /// Parses <c>minTier</c> into the max-uncertainty admitted during traversal (the same math as the
    /// <c>provenance</c> filter, framed as a minimum trust): 'extracted' → only proven; 'inferred' → proven
    /// + heuristic; 'ambiguous'/'all'/missing → no pruning (null).
    /// </summary>
    private static Provenance? ReadMinTier(JsonObject? args) =>
        (args?["minTier"] ?? args?["provenance"])?.ToString()?.Trim().ToLowerInvariant() switch
        {
            "extracted" => Provenance.Extracted,
            "inferred" => Provenance.Inferred,
            _ => null
        };

    /// <summary>
    /// Resolves the target to seed nodes: an exact node id / '@/' handle, else a File node (→ the symbols it
    /// defines), else a symbol name, else an indexed file path (→ its defined symbols).
    /// </summary>
    private static async Task<List<GraphNode>> ResolveSeedsAsync(
        Shonkor.Core.Interfaces.IGraphStorageProvider storage, string target, string basePath)
    {
        static bool IsDefinition(GraphNode n) =>
            n.Type is "Class" or "Interface" or "Record" or "Struct" or "Enum" or "Method" or "Constructor" or "Property";

        var resolvedId = FromHandle(target, basePath);
        var node = await storage.GetNodeByIdAsync(resolvedId).ConfigureAwait(false)
                   ?? await storage.GetNodeByIdAsync(target).ConfigureAwait(false);

        if (node is { Type: "File" })
        {
            var defs = (await storage.GetNodesByFilePathAsync(node.Id).ConfigureAwait(false)).Where(IsDefinition).ToList();
            return defs.Count > 0 ? defs : new List<GraphNode> { node };
        }
        if (node is not null)
        {
            return new List<GraphNode> { node };
        }

        var def = await ResolveDefinitionAsync(storage, target).ConfigureAwait(false);
        if (def is not null) return new List<GraphNode> { def };

        // A file path that isn't itself a node id (no File node) — seed from its defined symbols.
        var full = System.IO.Path.IsPathRooted(resolvedId) ? resolvedId : System.IO.Path.Combine(basePath, resolvedId);
        var byFile = (await storage.GetNodesByFilePathAsync(System.IO.Path.GetFullPath(full)).ConfigureAwait(false)).Where(IsDefinition).ToList();
        return byFile;
    }
}
