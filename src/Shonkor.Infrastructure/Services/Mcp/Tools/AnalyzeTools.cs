// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using static Shonkor.Infrastructure.Services.Mcp.McpToolHelpers;

namespace Shonkor.Infrastructure.Services.Mcp.Tools;

/// <summary>Reference/impact analysis: direct (depth 1) or transitive blast radius / dependency tree.</summary>
public sealed class ReferencesTool : IMcpTool
{
    public string Name => "references";

    public object GetSchema() => new
    {
        name = "references",
        description = "Reference/impact analysis over the graph's REFERENCES_TYPE/IMPLEMENTS/EXTENDS edges. direction='used_by' (default) = what references the symbol (what breaks if you change it); direction='uses' = the symbol's own dependencies. depth=1 (default) returns a flat 'relation  name  handle  — summary' list; depth>1 returns a transitive view — ranked by distance with affected tests flagged for used_by (blast radius), or an indented tree for uses. The one tool for 'what breaks if I change X?' and 'what does X depend on?'.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                symbol = new { type = "string", description = "The type/method/symbol name to analyze (e.g. 'GraphNode'). 'query' is accepted as an alias." },
                direction = new { type = "string", description = "'used_by' (incoming — what references it, default) or 'uses' (outgoing — what it depends on)." },
                depth = new { type = "integer", description = "1 (default) = direct only (flat list); >1 = transitive (max 6). used_by+depth>1 = ranked blast radius with [test] flags; uses+depth>1 = dependency tree." },
                provenance = new { type = "string", description = "Optional trust filter over edges: 'extracted' = only compiler-proven relationships; 'inferred' = proven + heuristic (excludes ambiguous); 'all' (default) = every edge. Each edge is tagged with its tier ([extracted]/[inferred]/[ambiguous]) regardless." },
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
            throw new McpToolException(McpErrorCode.MissingParameter, "Parameter 'symbol' is required", isArgumentError: true);
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var direction = (args?["direction"]?.ToString() ?? "used_by").ToLowerInvariant();
        var usedBy = direction is "used_by" or "used-by" or "callers" or "incoming";
        var depth = Math.Clamp(ReadInt(args?["depth"], 1), 1, 6);
        var maxProv = ReadProvenanceFilter(args);

        // depth 1 → flat, grouped-by-relation report (direct dependents / dependencies).
        if (depth <= 1)
        {
            var report = usedBy
                ? await ctx.EdgeReportAsync(storage, projectName, symbol, incoming: true,
                    verb: "is referenced by",
                    emptyMessage: "Nothing references '{0}' ({1}) — safe to change in isolation, or it is an entry point.",
                    maxProvenance: maxProv).ConfigureAwait(false)
                : await ctx.EdgeReportAsync(storage, projectName, symbol, incoming: false,
                    verb: "depends on",
                    emptyMessage: "'{0}' ({1}) depends on nothing in the graph — it is self-contained or a leaf.",
                    maxProvenance: maxProv).ConfigureAwait(false);
            return SendToolResponse(id, report);
        }

        var refDef = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
        if (refDef == null)
        {
            throw McpToolException.SymbolNotFound(symbol!);
        }
        var refBasePath = ctx.GetProjectBasePath(projectName);

        // depth > 1, used_by → transitive blast radius (ranked by distance, tests flagged).
        if (usedBy)
        {
            const int maxNodes = 200;
            var affected = new Dictionary<string, (int Depth, string Rel, GraphNode? Node, Provenance Prov)>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal) { refDef.Id };
            var frontier = new List<string> { refDef.Id };

            for (var d = 1; d <= depth && frontier.Count > 0 && affected.Count < maxNodes; d++)
            {
                var next = new List<string>();
                foreach (var nodeId in frontier)
                {
                    var (edges, neighbours) = await storage.GetIncidentEdgesAsync(nodeId).ConfigureAwait(false);
                    foreach (var e in edges)
                    {
                        if (e.TargetId != nodeId || e.SourceId == nodeId) continue;          // incoming only
                        if (StructuralRelationships.Contains(e.Relationship)) continue;       // skip containment
                        if (!PassesProvenance(e.Provenance, maxProv)) continue;               // trust filter
                        if (!visited.Add(e.SourceId)) continue;
                        affected[e.SourceId] = (d, e.Relationship, neighbours.GetValueOrDefault(e.SourceId), e.Provenance);
                        next.Add(e.SourceId);
                        if (affected.Count >= maxNodes) break;
                    }
                    if (affected.Count >= maxNodes) break;
                }
                frontier = next;
            }

            if (affected.Count == 0)
            {
                return SendToolResponse(id, $"Blast radius of '{refDef.Name}' ({refDef.Type}): nothing depends on it — safe to change in isolation, or it is an entry point. (CALLS-level impact needs semantic indexing.)");
            }

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var testCount = 0;
            foreach (var (_, info) in affected)
            {
                var fp = info.Node?.FilePath;
                if (!string.IsNullOrEmpty(fp)) files.Add(fp);
                if (LooksLikeTest(fp)) testCount++;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append($"Blast radius of '{refDef.Name}' ({refDef.Type}), depth {depth}: ");
            sb.Append($"{affected.Count} node(s) across {files.Count} file(s)");
            if (testCount > 0) sb.Append($", {testCount} test(s)");
            if (affected.Count >= maxNodes) sb.Append(" (capped)");
            sb.Append(".\n");

            foreach (var group in affected.Values.GroupBy(a => a.Depth).OrderBy(g => g.Key))
            {
                sb.Append($"\ndepth {group.Key}{(group.Key == 1 ? " (direct)" : "")}:\n");
                foreach (var a in group.OrderBy(a => a.Rel, StringComparer.Ordinal))
                {
                    var name = a.Node?.Name ?? a.Node?.Id ?? "?";
                    var handle = ToHandle(a.Node?.Id ?? string.Empty, refBasePath);
                    var testTag = LooksLikeTest(a.Node?.FilePath) ? "  [test]" : "";
                    sb.Append($"  {a.Rel}\t{name}\t{handle} {ProvenanceTag(a.Prov)}{testTag}\n");
                }
            }
            return SendToolResponse(id, sb.ToString().TrimEnd() + await ctx.StaleSuffixAsync(storage, refDef).ConfigureAwait(false));
        }

        // depth > 1, uses → transitive dependency tree (outgoing reference edges).
        {
            var depRelations = new HashSet<string> { "REFERENCES_TYPE", "IMPLEMENTS", "EXTENDS" };
            var sb = new System.Text.StringBuilder();
            sb.Append($"Dependency tree (uses, depth {depth}) for '{refDef.Name}':\n");
            sb.Append($"{refDef.Name} ({refDef.Type})\n");

            var visited = new HashSet<string> { refDef.Id };
            var emitted = 0;
            const int maxNodes = 100;

            async Task Walk(string nodeId, int level)
            {
                if (level > depth || emitted >= maxNodes) return;
                var (edges, neighbours) = await storage.GetIncidentEdgesAsync(nodeId).ConfigureAwait(false);
                var step = edges.Where(e => depRelations.Contains(e.Relationship)
                    && PassesProvenance(e.Provenance, maxProv)
                    && e.SourceId == nodeId && e.TargetId != nodeId).ToList();
                foreach (var e in step.OrderBy(e => e.Relationship))
                {
                    if (emitted >= maxNodes) { sb.Append(new string(' ', level * 2)).Append("… (truncated)\n"); break; }
                    var otherId = e.TargetId;
                    var other = neighbours.GetValueOrDefault(otherId);
                    sb.Append(new string(' ', level * 2)).Append($"--{e.Relationship}--> {other?.Name ?? otherId} ({other?.Type ?? "?"}) {ProvenanceTag(e.Provenance)}");
                    emitted++;
                    if (!visited.Add(otherId)) { sb.Append("  ↺\n"); continue; }
                    sb.Append('\n');
                    await Walk(otherId, level + 1).ConfigureAwait(false);
                }
            }
            await Walk(refDef.Id, 1).ConfigureAwait(false);

            return SendToolResponse(id, sb.ToString().TrimEnd());
        }
    }
}

/// <summary>Call/reference sites of a symbol, each with a code snippet — a graph-aware grep.</summary>
public sealed class FindUsagesTool : IMcpTool
{
    public string Name => "find_usages";

    public object GetSchema() => new
    {
        name = "find_usages",
        description = "List the call/reference SITES of a symbol with a code snippet at each — a graph-aware grep. For every node that references the symbol, returns 'relation  name  file:line  ⟶ <the line that uses it>'. Use to see HOW something is used (and what would break) before changing its signature; richer than 'references' (depth 1), which lists dependents without the usage line.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                symbol = new { type = "string", description = "The type/method/symbol name whose usages to find (e.g. 'GraphNode'). 'query' is accepted as an alias." },
                provenance = new { type = "string", description = "Optional trust filter: 'extracted' = only compiler-proven usages; 'inferred' = proven + heuristic (excludes ambiguous); 'all' (default) = every usage. Each line is tagged with its tier regardless." },
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
            throw new McpToolException(McpErrorCode.MissingParameter, "Parameter 'symbol' is required", isArgumentError: true);
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);
        var maxProv = ReadProvenanceFilter(args);

        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
        if (def == null)
        {
            throw McpToolException.SymbolNotFound(symbol!);
        }

        var (edges, neighbours) = await storage.GetIncidentEdgesAsync(def.Id).ConfigureAwait(false);
        // Real usages only — exclude structural containment (e.g. the enclosing type/file).
        var incoming = edges.Where(e => e.TargetId == def.Id && e.SourceId != def.Id
            && !StructuralRelationships.Contains(e.Relationship)
            && PassesProvenance(e.Provenance, maxProv)).ToList();
        if (incoming.Count == 0)
        {
            return SendToolResponse(id, $"No usages of '{def.Name}' ({def.Type}) found in the graph.");
        }

        var filterNote = maxProv is { } mp ? $" (provenance ≤ {mp.ToString().ToLowerInvariant()})" : "";
        var sb = new System.Text.StringBuilder();
        sb.Append($"{incoming.Count} usage(s) of '{def.Name}'{filterNote}:\n");
        foreach (var e in incoming.OrderBy(e => e.Relationship))
        {
            var user = neighbours.GetValueOrDefault(e.SourceId);
            var name = user?.Name ?? e.SourceId;
            var loc = Shorten(user != null && !string.IsNullOrEmpty(user.FilePath) ? user.FilePath : e.SourceId, basePath);
            if (user?.StartLine is int line) loc += $":{line}";
            var snippet = FirstLineMentioning(user?.Content, def.Name);
            var snippetText = snippet != null ? $"  ⟶ {snippet}" : "";
            sb.Append($"{e.Relationship}\t{name}\t{loc} {ProvenanceTag(e.Provenance)}{snippetText}\n");
        }
        return SendToolResponse(id, sb.ToString().TrimEnd() + await ctx.StaleSuffixAsync(storage, def).ConfigureAwait(false));
    }
}

/// <summary>Method-level callers/callees over CALLS edges (semantic mode).</summary>
public sealed class CallHierarchyTool : IMcpTool
{
    public string Name => "call_hierarchy";

    public object GetSchema() => new
    {
        name = "call_hierarchy",
        description = "Method-level call hierarchy over CALLS edges, rendered indented to a given depth. direction 'callers' = who calls the method, transitively (incoming, default); 'callees' = what the method calls (outgoing). Requires semantic indexing (Indexing:SemanticCSharp / SHONKOR_SEMANTIC_CSHARP=true) — that's what emits CALLS edges; without it the tree is empty. Cycles (recursion) are marked, and the tree is capped for safety.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                symbol = new { type = "string", description = "The method name (e.g. 'ScanFileAsync'). 'query' is accepted as an alias. Overloads resolve to the first match; use file:line addressing for a specific one." },
                direction = new { type = "string", description = "'callers' (incoming, who calls it — default) or 'callees' (outgoing, what it calls)." },
                depth = new { type = "integer", description = "Tree depth (default 3, max 6)." },
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
            throw new McpToolException(McpErrorCode.MissingParameter, "Parameter 'symbol' is required", isArgumentError: true);
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var direction = (args?["direction"]?.ToString() ?? "callers").ToLowerInvariant();
        var callees = direction is "callees" or "callee" or "outgoing" or "calls";
        var depth = Math.Clamp(ReadInt(args?["depth"], 3), 1, 6);

        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
        if (def == null)
        {
            throw McpToolException.SymbolNotFound(symbol!);
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"Call hierarchy ({(callees ? "callees" : "callers")}, depth {depth}) for '{def.Name}':\n");
        sb.Append($"{def.Name} ({def.Type})\n");

        var visited = new HashSet<string> { def.Id };
        var emitted = 0;
        const int maxNodes = 100;

        async Task Walk(string nodeId, int level)
        {
            if (level > depth || emitted >= maxNodes) return;
            var (edges, neighbours) = await storage.GetIncidentEdgesAsync(nodeId).ConfigureAwait(false);
            // callers: edges INTO this node (TargetId == node); callees: edges OUT (SourceId == node).
            var step = edges.Where(e => e.Relationship == "CALLS"
                && (callees ? e.SourceId == nodeId && e.TargetId != nodeId
                            : e.TargetId == nodeId && e.SourceId != nodeId)).ToList();
            foreach (var e in step.OrderBy(e => (callees ? e.TargetId : e.SourceId)))
            {
                if (emitted >= maxNodes) { sb.Append(new string(' ', level * 2)).Append("… (truncated)\n"); break; }
                var otherId = callees ? e.TargetId : e.SourceId;
                var other = neighbours.GetValueOrDefault(otherId);
                var arrow = callees ? "--calls-->" : "<--calls--";
                sb.Append(new string(' ', level * 2)).Append($"{arrow} {other?.Name ?? otherId} ({other?.Type ?? "?"})");
                emitted++;
                if (!visited.Add(otherId)) { sb.Append("  ↺\n"); continue; }
                sb.Append('\n');
                await Walk(otherId, level + 1).ConfigureAwait(false);
            }
        }
        await Walk(def.Id, 1).ConfigureAwait(false);

        if (emitted == 0)
        {
            sb.Append(callees ? "  (no outgoing calls)" : "  (no callers)");
            sb.Append(" — note: CALLS edges require semantic indexing (Indexing:SemanticCSharp).");
        }

        return SendToolResponse(id, sb.ToString().TrimEnd() + await ctx.StaleSuffixAsync(storage, def).ConfigureAwait(false));
    }
}

/// <summary>Types implementing an interface / extending a base type (IMPLEMENTS/EXTENDS).</summary>
public sealed class ImplementationsOfTool : IMcpTool
{
    public string Name => "implementations_of";

    public object GetSchema() => new
    {
        name = "implementations_of",
        description = "List the types that implement an interface or extend a base type (incoming IMPLEMENTS/EXTENDS edges), each as 'relation  name  file:line  — summary'. More precise than 'references' when you specifically need subtypes (e.g. all IFileParser implementations).",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                symbol = new { type = "string", description = "Interface or base type name (e.g. 'IFileParser'). 'query' is accepted as an alias." },
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
            throw new McpToolException(McpErrorCode.MissingParameter, "Parameter 'symbol' is required", isArgumentError: true);
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);

        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
        var name = def?.Name ?? symbol;

        // IMPLEMENTS/EXTENDS edges target the base type by NAME, so query by name.
        var (edges, neighbours) = await storage.GetIncidentEdgesAsync(name).ConfigureAwait(false);
        var impls = edges
            .Where(e => (e.Relationship == "IMPLEMENTS" || e.Relationship == "EXTENDS") && e.TargetId == name && e.SourceId != name)
            .ToList();

        if (impls.Count == 0)
        {
            return SendToolResponse(id, $"No implementations or subclasses of '{name}' found in the graph.");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"{impls.Count} type(s) implement/extend '{name}':\n");
        foreach (var e in impls.OrderBy(e => e.Relationship))
        {
            var impl = neighbours.GetValueOrDefault(e.SourceId);
            var implName = impl?.Name ?? e.SourceId;
            var loc = Shorten(impl != null && !string.IsNullOrEmpty(impl.FilePath) ? impl.FilePath : e.SourceId, basePath);
            if (impl?.StartLine is int line) loc += $":{line}";
            var summary = impl != null && !string.IsNullOrEmpty(impl.Summary) ? $"  — {impl.Summary}" : "";
            sb.Append($"{e.Relationship}\t{implName}\t{loc}{summary}\n");
        }
        return SendToolResponse(id, sb.ToString().TrimEnd());
    }
}

/// <summary>Embedding-derived "surprising connections": semantically similar but structurally unlinked pairs.</summary>
public sealed class SurprisingConnectionsTool : IMcpTool
{
    public string Name => "surprising_connections";

    public object GetSchema() => new
    {
        name = "surprising_connections",
        description = "Find 'surprising connections': node pairs whose embeddings are highly similar yet have NO direct edge between them — code that looks semantically related but carries no structural dependency (candidate missing links or duplication). These are INFERRED, embedding-derived hints — never proven relationships. Requires an embedding pass to have run; returns 'A ~ B  similarity=…', strongest first.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                limit = new { type = "integer", description = "Max pairs to return (default 20, max 100)." },
                minSimilarity = new { type = "number", description = "Cosine-similarity threshold in 0..1 (default 0.85). Higher = fewer, more confident pairs." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);
        var limit = Math.Clamp(ReadInt(args?["limit"], 20), 1, 100);
        var minSim = Math.Clamp(ReadDouble(args?["minSimilarity"], 0.85), 0.0, 1.0);

        // Bound the O(N²) comparison; embeddings past this cap are not compared.
        const int maxNodes = 1000;
        var nodes = await storage.GetNodesWithEmbeddingsAsync(maxNodes).ConfigureAwait(false);
        if (nodes.Count < 2)
        {
            return SendToolResponse(id, "Fewer than two embedded nodes — run an embedding/enrichment pass first (surprising-connection detection needs vectors).");
        }

        var edges = await storage.GetAllEdgesAsync().ConfigureAwait(false);
        var pairs = GraphAnalytics.SurprisingConnections(nodes, edges, minSim, limit);
        if (pairs.Count == 0)
        {
            return SendToolResponse(id, $"No surprising connections at similarity ≥ {minSim:0.##} — no semantically-similar-but-unlinked pairs found among {nodes.Count} embedded node(s).");
        }

        var byId = nodes.ToDictionary(n => n.Id);
        var sb = new System.Text.StringBuilder();
        sb.Append($"{pairs.Count} surprising connection(s) — semantically similar but NOT structurally linked (INFERRED, embedding-derived):\n");
        foreach (var p in pairs)
        {
            var a = byId.GetValueOrDefault(p.SourceId);
            var b = byId.GetValueOrDefault(p.TargetId);
            sb.Append($"{a?.Name ?? p.SourceId} ~ {b?.Name ?? p.TargetId}\tsimilarity={p.Similarity:0.###}\t{ToHandle(p.SourceId, basePath)} ~ {ToHandle(p.TargetId, basePath)}\n");
        }
        return SendToolResponse(id, sb.ToString().TrimEnd());
    }
}

/// <summary>The shortest connection chain between two symbols, with real edge directions.</summary>
public sealed class FindPathTool : IMcpTool
{
    public string Name => "find_path";

    public object GetSchema() => new
    {
        name = "find_path",
        description = "Explain HOW two symbols are connected: returns the shortest chain between them as 'A --REL--> B <--REL-- C …', with each arrow showing the real edge direction. Far cheaper than dumping a subgraph when you only need the connection. Returns a clear 'no path' message if they're unconnected within maxHops.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                from = new { type = "string", description = "Start symbol (name of a class/method/etc.)." },
                to = new { type = "string", description = "Target symbol to reach." },
                maxHops = new { type = "integer", description = "Max path length to search (default 5, max 10)." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            },
            required = new[] { "from", "to" }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var fromArg = args?["from"]?.ToString();
        var toArg = args?["to"]?.ToString();
        if (string.IsNullOrWhiteSpace(fromArg) || string.IsNullOrWhiteSpace(toArg))
        {
            throw new McpToolException(McpErrorCode.MissingParameter, "Parameters 'from' and 'to' are required", isArgumentError: true);
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var clamps = new ClampReport();
        var maxHops = clamps.Clamp(args?["maxHops"], "maxHops", 5, 1, MaxPathHops);

        // #118: the SUBJECT not existing is the caller's mistake — a typo, or a symbol it invented — so it
        // fails (isError). It must not look like an ordinary answer.
        var fromDef = await ResolveDefinitionAsync(storage, fromArg).ConfigureAwait(false)
                      ?? throw McpToolException.SymbolNotFound(fromArg!);
        var toDef = await ResolveDefinitionAsync(storage, toArg).ConfigureAwait(false)
                    ?? throw McpToolException.SymbolNotFound(toArg!);

        if (fromDef.Id == toDef.Id)
        {
            return SendToolResponse(id, $"'{fromArg}' and '{toArg}' resolve to the same node ({fromDef.Name}).");
        }

        var path = await GraphPathFinder.FindPathAsync(storage, fromDef.Id, toDef.Id, maxHops).ConfigureAwait(false);
        if (path == null)
        {
            // ...whereas THIS is a real finding, not a failure: both symbols exist and are genuinely
            // unconnected within the bound. Flagging it isError would teach the model that a correct negative
            // is a malfunction and invite pointless retries (#118).
            return SendToolResponse(id, clamps.Annotate(
                $"No path from '{fromDef.Name}' to '{toDef.Name}' within {maxHops} hops — they may be in different components. Try raising maxHops."));
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"Path ({path.Count - 1} hop(s)) from '{fromDef.Name}' to '{toDef.Name}':\n");
        sb.Append(path[0].Node.Name);
        for (var i = 1; i < path.Count; i++)
        {
            var step = path[i];
            sb.Append(step.Forward ? $" --{step.Relation}--> " : $" <--{step.Relation}-- ").Append(step.Node.Name);
        }
        return SendToolResponse(id, sb.ToString());
    }
}
