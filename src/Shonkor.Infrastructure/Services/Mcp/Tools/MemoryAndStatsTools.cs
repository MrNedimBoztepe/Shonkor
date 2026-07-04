// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Services;
using static Shonkor.Infrastructure.Services.Mcp.McpToolHelpers;

namespace Shonkor.Infrastructure.Services.Mcp.Tools;

/// <summary>Overall database statistics, grouped by node/edge type.</summary>
public sealed class GetStatsTool : IMcpTool
{
    public string Name => "get_stats";

    public object GetSchema() => new
    {
        name = "get_stats",
        description = "Get overall database statistics, including total node and edge counts grouped by type.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
            }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var stats = await storage.GetStatisticsAsync().ConfigureAwait(false);
        var formattedStats = new
        {
            stats.TotalNodes,
            stats.TotalEdges,
            stats.NodesByType,
            stats.EdgesByRelation,
            stats.SchemeVersion,
            stats.CurrentSchemeVersion,
            // Surfaced so the AI knows the graph's ids are in an outdated format and a full
            // re-index (shonkor index .) is recommended before trusting method-level results.
            ReindexRecommended = stats.ReindexRecommended
                ? $"Graph built under node-id scheme v{stats.SchemeVersion} < current v{stats.CurrentSchemeVersion}; run a full re-index (shonkor index .) to migrate method/constructor ids."
                : null
        };
        return SendToolResponse(id, JsonSerializer.Serialize(formattedStats));
    }
}

/// <summary>Change-risk hotspots: the graph's highest-betweenness nodes ("god nodes"), purely topological.</summary>
public sealed class HotspotsTool : IMcpTool
{
    public string Name => "hotspots";

    public object GetSchema() => new
    {
        name = "hotspots",
        description = "Rank the graph's change-risk hotspots ('god nodes') by betweenness centrality over the coupling subgraph (structural containment excluded): the nodes through which the most shortest dependency paths pass, so a change there has the widest blast radius. Returns 'name  type  handle  betweenness=… degree=…', highest first. Purely topological — no model, no embeddings.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                limit = new { type = "integer", description = "How many top hotspots to return (default 15, max 100)." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);
        var limit = Math.Clamp(ReadInt(args?["limit"], 15), 1, 100);

        var nodes = await storage.GetAllNodesAsync().ConfigureAwait(false);
        var edges = await storage.GetAllEdgesAsync().ConfigureAwait(false);
        var scores = GraphAnalytics.Centrality(nodes, edges);

        var byId = nodes.ToDictionary(n => n.Id);
        var ranked = scores
            .Where(s => s.Degree > 0)
            .OrderByDescending(s => s.Betweenness)
            .ThenByDescending(s => s.Degree)
            .Take(limit)
            .ToList();

        if (ranked.Count == 0)
        {
            return SendToolResponse(id, "No coupling edges in the graph — nothing to rank. (Hotspots need semantic edges like REFERENCES_TYPE/CALLS; run an index first.)");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"Top {ranked.Count} change-risk hotspot(s) by betweenness centrality:\n");
        foreach (var s in ranked)
        {
            var node = byId.GetValueOrDefault(s.NodeId);
            var name = node?.Name ?? s.NodeId;
            var type = node?.Type ?? "?";
            sb.Append($"{name}\t{type}\t{ToHandle(s.NodeId, basePath)}\tbetweenness={s.Betweenness:0.##} degree={s.Degree}\n");
        }
        return SendToolResponse(id, sb.ToString().TrimEnd());
    }
}

/// <summary>Connected-component clusters over the coupling graph — surfaces isolated modules / dead code.</summary>
public sealed class ClustersTool : IMcpTool
{
    public string Name => "clusters";

    public object GetSchema() => new
    {
        name = "clusters",
        description = "Group the graph into connected-component clusters over the coupling subgraph (structural containment excluded): one giant cluster is normal; SMALL clusters are isolated modules or likely-dead subsystems worth a look. Returns the cluster count, the largest cluster's size, and the members of the smallest clusters. Purely topological — no model, deterministic.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                maxSmallClusters = new { type = "integer", description = "How many of the smallest clusters to list with members (default 10, max 50)." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);
        var maxSmall = Math.Clamp(ReadInt(args?["maxSmallClusters"], 10), 1, 50);

        var nodes = await storage.GetAllNodesAsync().ConfigureAwait(false);
        var edges = await storage.GetAllEdgesAsync().ConfigureAwait(false);
        var communities = GraphAnalytics.DetectCommunities(nodes, edges);
        if (communities.Count == 0)
        {
            return SendToolResponse(id, "The graph is empty — nothing to cluster.");
        }

        var byId = nodes.ToDictionary(n => n.Id);
        var clusters = communities
            .GroupBy(kv => kv.Value, kv => kv.Key)
            .Select(g => g.ToList())
            .OrderBy(members => members.Count)
            .ToList();

        var largest = clusters[^1].Count;
        var sb = new System.Text.StringBuilder();
        sb.Append($"{clusters.Count} connected cluster(s); largest = {largest} node(s). Smallest {Math.Min(maxSmall, clusters.Count)} (candidates for isolated/dead code):\n");
        foreach (var members in clusters.Take(maxSmall))
        {
            var names = members
                .Take(8)
                .Select(mid => byId.TryGetValue(mid, out var n) ? n.Name : mid);
            var more = members.Count > 8 ? $" … (+{members.Count - 8})" : "";
            var head = ToHandle(members[0], basePath);
            sb.Append($"  [{members.Count}]\t{head}\t{string.Join(", ", names)}{more}\n");
        }
        return SendToolResponse(id, sb.ToString().TrimEnd());
    }
}

/// <summary>Anti-hallucination fact-check: does a symbol with this exact name exist in the graph?</summary>
public sealed class VerifyExistsTool : IMcpTool
{
    public string Name => "verify_exists";

    public object GetSchema() => new
    {
        name = "verify_exists",
        description = "Fact-check that a symbol actually exists in the graph BEFORE asserting it. Returns 'YES — name (type) at handle' on an exact name match, otherwise 'NO' plus the nearest matching names. Use this to avoid claiming a class/method exists when it doesn't.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                symbol = new { type = "string", description = "The exact symbol name to verify (e.g. 'GraphNode'). 'query' is accepted as an alias." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            },
            required = new[] { "symbol" }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var symbol = ReadSymbol(args);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return SendError(id, -32602, "Parameter 'symbol' is required");
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);
        var hits = (await storage.SearchAsync(symbol, SymbolSearchLimit).ConfigureAwait(false)).Select(h => h.Node).ToList();

        var exact = hits.FirstOrDefault(n => IsExactNameMatch(n, symbol));
        if (exact != null)
        {
            return SendToolResponse(id, $"YES — '{exact.Name}' ({exact.Type}) exists at {ToHandle(exact.Id, basePath)}.");
        }

        // Honest negative: don't let the caller assume — offer the nearest names instead.
        if (hits.Count == 0)
        {
            return SendToolResponse(id, $"NO — nothing named '{symbol}' is in the graph.");
        }
        // Distinct BEFORE Take so duplicate names don't shrink the suggestion list below 5.
        var nearest = string.Join(", ", hits.Select(n => $"{n.Name} ({n.Type})").Distinct().Take(5));
        return SendToolResponse(id, $"NO exact match for '{symbol}'. Nearest: {nearest}.");
    }
}

/// <summary>Lists the still-open recorded threads (Question/Task/Decision/Milestone) to resume work.</summary>
public sealed class GetOpenThreadsTool : IMcpTool
{
    public string Name => "get_open_threads";

    public object GetSchema() => new
    {
        name = "get_open_threads",
        description = "Resume context cheaply: lists the still-open recorded interactions (Question/Task/Decision/Milestone, i.e. anything not done/resolved/accepted/superseded) as 'type [status] name id'. Call at the start of a session to recover what was in progress without re-deriving it.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);

        var interactionTypes = new[] { "Question", "Task", "Decision", "Milestone" };
        var closedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "done", "resolved", "completed", "accepted", "superseded", "closed"
        };

        var items = await storage.GetNodesByTypesAsync(interactionTypes).ConfigureAwait(false);
        var open = items
            .Where(n => !closedStatuses.Contains(n.Properties.GetValueOrDefault("status", "").Trim()))
            .ToList();

        if (open.Count == 0)
        {
            return SendToolResponse(id, "No open threads (tasks/questions/decisions/milestones).");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("Open threads (").Append(open.Count).Append("):\n");
        foreach (var g in open.GroupBy(n => n.Type))
        {
            foreach (var n in g)
            {
                var status = n.Properties.GetValueOrDefault("status", "Open");
                sb.Append($"{n.Type}\t[{status}]\t{n.Name}\t{n.Id}\n");
            }
        }
        return SendToolResponse(id, sb.ToString().TrimEnd());
    }
}

/// <summary>Persists a typed memory node (decision/milestone/task/question), linked to code nodes.</summary>
public sealed class RecordTool : IMcpTool
{
    public string Name => "record";

    public object GetSchema() => new
    {
        name = "record",
        description = "Record a memory node in the graph, connected to specific code nodes. 'type' selects what: 'decision' (architectural choice + rationale; requires content), 'milestone' (progress/focus/blocker; requires status), 'task' (a concrete todo; status defaults to Todo), or 'question' (an open uncertainty). Use to persist context an agent should be able to resume later (see get_open_threads).",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                type = new { type = "string", description = "One of: 'decision', 'milestone', 'task', 'question'." },
                name = new { type = "string", description = "Title / the decision question / the task title / the open question." },
                content = new { type = "string", description = "Detail: rationale & alternatives (decision), description/steps (task), or context (question). Required for 'decision'." },
                status = new { type = "string", description = "For 'milestone' (required, e.g. 'Completed'/'In Progress'/'Blocked') or 'task' (e.g. 'Todo'/'In Progress'/'Done')." },
                connectedNodeIds = new { type = "array", items = new { type = "string" }, description = "List of node IDs this memory connects to." },
                projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
            },
            required = new[] { "type", "name" }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var type = (args?["type"]?.ToString() ?? string.Empty).Trim().ToLowerInvariant();
        var name = args?["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return SendError(id, -32602, "Parameter 'name' is required");
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var content = args?["content"]?.ToString();
        var status = args?["status"]?.ToString();
        var connected = args?["connectedNodeIds"] as JsonArray;

        switch (type)
        {
            case "decision":
                if (string.IsNullOrWhiteSpace(content))
                {
                    return SendError(id, -32602, "A 'decision' requires 'content' (the rationale/alternatives).");
                }
                return await ctx.RecordNodeAsync(id, storage, "decision", "Decision", name, content,
                    new Dictionary<string, string> { ["created"] = UtcNow() },
                    connected, "INFLUENCES",
                    (nodeId, count) => $"Successfully recorded decision '{name}' (ID: {nodeId}) and connected it to {count} nodes.")
                    .ConfigureAwait(false);

            case "milestone":
                if (string.IsNullOrWhiteSpace(status))
                {
                    return SendError(id, -32602, "A 'milestone' requires 'status'.");
                }
                return await ctx.RecordNodeAsync(id, storage, "milestone", "Milestone", name, $"Status: {status}",
                    new Dictionary<string, string> { ["status"] = status, ["updated"] = UtcNow() },
                    connected, "AFFECTS",
                    (nodeId, count) => $"Successfully recorded milestone '{name}' (ID: {nodeId}) with status '{status}' connected to {count} nodes.")
                    .ConfigureAwait(false);

            case "task":
                {
                    var taskStatus = string.IsNullOrWhiteSpace(status) ? "Todo" : status;
                    return await ctx.RecordNodeAsync(id, storage, "task", "Task", name, content ?? $"Status: {taskStatus}",
                        new Dictionary<string, string> { ["status"] = taskStatus, ["created"] = UtcNow() },
                        connected, "HasTask",
                        (nodeId, count) => $"Successfully recorded task '{name}' (ID: {nodeId}) connected to {count} nodes.")
                        .ConfigureAwait(false);
                }

            case "question":
                return await ctx.RecordNodeAsync(id, storage, "question", "Question", name, content ?? string.Empty,
                    new Dictionary<string, string> { ["status"] = "Open", ["created"] = UtcNow() },
                    connected, "RELATES_TO",
                    (nodeId, count) => $"Successfully recorded question '{name}' (ID: {nodeId}) connected to {count} nodes.")
                    .ConfigureAwait(false);

            default:
                return SendError(id, -32602, "Parameter 'type' must be one of: decision, milestone, task, question.");
        }
    }
}
