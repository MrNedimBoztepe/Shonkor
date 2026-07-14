// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Models;
using static Shonkor.Infrastructure.Services.Mcp.McpToolHelpers;

namespace Shonkor.Infrastructure.Services.Mcp.Tools;

/// <summary>A symbol's signature (modifiers/return/params) + location — no body.</summary>
public sealed class SignatureTool : IMcpTool
{
    public string Name => "signature";

    public object GetSchema() => new
    {
        name = "signature",
        description = "Return ONLY a symbol's signature (modifiers, return type, parameters) plus its location — no body. The cheapest way to learn how to call something.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                symbol = new { type = "string", description = "The method/type/property name (e.g. 'GenerateEmbeddingAsync'). 'query' is accepted as an alias." },
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
        if (def == null)
        {
            throw McpToolException.SymbolNotFound(symbol!);
        }
        var loc = Shorten(string.IsNullOrEmpty(def.FilePath) ? def.Id : def.FilePath, basePath);
        if (def.StartLine is int sl) loc += $":{sl}";
        return SendToolResponse(id, $"{BuildSignature(def)}\n@ {loc}");
    }
}

/// <summary>The exact stored source body of a symbol + its file:line range.</summary>
public sealed class GetSourceTool : IMcpTool
{
    public string Name => "get_source";

    public object GetSchema() => new
    {
        name = "get_source",
        description = "Return the EXACT source code of a symbol (its stored body) plus its 'file:startLine-endLine' location — so you can read precisely what to edit without loading whole files. Resolves the symbol by name. Supports maxChars to cap large bodies. The most token-efficient way to read a specific class/method before changing it.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                symbol = new { type = "string", description = "The type/method/symbol name to read (e.g. 'GraphNode'). 'query' is accepted as an alias." },
                maxChars = new { type = "integer", description = "Cap on the returned source length (default 32768). Raise it to read a larger body." },
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
        if (def == null)
        {
            throw McpToolException.SymbolNotFound(symbol!);
        }
        // Prefer the stored body; for nodes that store none (or, for a DB indexed before TICKET-204, a body
        // truncated with the legacy "..." marker) read the exact line range from the file instead.
        var body = def.Content;
        if (string.IsNullOrEmpty(body) || IsLegacyTruncated(body))
        {
            var slice = ctx.TryReadSourceSlice(def, basePath);
            if (!string.IsNullOrEmpty(slice))
            {
                body = slice;
            }
            else if (string.IsNullOrEmpty(body))
            {
                return SendToolResponse(id,
                    $"'{def.Name}' ({def.Type}) at {ToHandle(def.Id, basePath)} stores no source, and no local file access is available to read it.");
            }
            // else: keep the legacy-truncated stored body (no filesystem access to improve on it).
        }

        var loc = Shorten(string.IsNullOrEmpty(def.FilePath) ? def.Id : def.FilePath, basePath);
        var range = def.StartLine.HasValue
            ? (def.EndLine.HasValue ? $":{def.StartLine}-{def.EndLine}" : $":{def.StartLine}")
            : "";
        // A stored body is unbounded (TICKET-204 removed the 500-char cap), so cap by DEFAULT rather than
        // only when the caller thinks to ask — one huge type must not blow the agent's context window.
        body = CapOutput(body, ReadOutputCap(args?["maxChars"]), "raise maxChars for the full body");
        return SendToolResponse(id, $"{def.Name} ({def.Type}) — {loc}{range}\n\n{body}" + await ctx.StaleSuffixAsync(storage, def).ConfigureAwait(false));
    }

    /// <summary>
    /// True when a stored body looks like it was truncated by the pre-TICKET-204 parser, which capped member
    /// content at 500 chars and appended "...". Such a DB predates full-body storage; prefer the file slice.
    /// </summary>
    private static bool IsLegacyTruncated(string body) =>
        body.Length >= 500 && body.EndsWith("...", StringComparison.Ordinal);
}

/// <summary>A file's type/member structure as an indented CONTAINS tree.</summary>
public sealed class OutlineTool : IMcpTool
{
    public string Name => "outline";

    /// <summary>
    /// #105. Note <c>file</c>: this tool accepts it as an alias of <c>path</c>, so it must be declared too —
    /// an undeclared alias is exactly the hole the central guard exists to close.
    /// </summary>
    public IReadOnlyList<string> PathArguments => ["path", "file"];

    public object GetSchema() => new
    {
        name = "outline",
        description = "List a file's structure cheaply: its types and members as an indented '<type>  <name>:<line>' tree (via CONTAINS), so you can see what's in a file without reading it. Accepts an absolute/project-relative path or an '@/' handle.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "File to outline: absolute, project-relative, or an '@/<relative>' handle." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            },
            required = new[] { "path" }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var rawPath = args?["path"]?.ToString() ?? args?["file"]?.ToString();
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            throw new McpToolException(McpErrorCode.MissingParameter, "Parameter 'path' is required", isArgumentError: true);
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);
        // #105: `path`/`file` arrived resolved and contained — the dispatcher aborted the call otherwise.
        var fileId = rawPath!;

        var fileNode = await storage.GetNodeByIdAsync(fileId).ConfigureAwait(false);
        if (fileNode == null)
        {
            // #118: the file is not in the graph — the caller's premise is wrong, so this fails rather than
            // returning a bland answer an agent could read as "the file exists and is empty".
            throw McpToolException.FileNotIndexed(ToHandle(fileId, basePath));
        }

        // Walk CONTAINS specifically, breadth-first from the file, rather than pulling a generic n-hop
        // subgraph. Two reasons, both introduced by the markdown nesting (#112):
        //   - Depth: a `###` is now file → h1 → h2 → h3, so a fixed 2-hop fetch simply MISSED it. The tree can
        //     be six levels deep, and raising the hop count on a generic subgraph would drag in the file's
        //     whole reference neighbourhood at every level — expensive, and none of it is outline material.
        //   - Precision: outline only ever renders CONTAINS. Fetching other edge types was always waste.
        // The walk is bounded by the containment tree itself, which is exactly what we are rendering.
        var byId = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { fileId };
        var frontier = new Queue<string>();
        frontier.Enqueue(fileId);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            var (incident, neighbours) = await storage.GetIncidentEdgesAsync(current).ConfigureAwait(false);
            foreach (var e in incident.Where(e => e.Relationship == "CONTAINS" && e.SourceId == current))
            {
                if (neighbours.TryGetValue(e.TargetId, out var child)) byId[e.TargetId] = child;
                if (!children.TryGetValue(current, out var list)) children[current] = list = new();
                list.Add(e.TargetId);
                if (seen.Add(e.TargetId)) frontier.Enqueue(e.TargetId);
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(ToHandle(fileId, basePath)).Append('\n');
        void Render(string nid, int depth)
        {
            if (!children.TryGetValue(nid, out var kids)) return;
            foreach (var cid in kids.OrderBy(c => byId.GetValueOrDefault(c)?.StartLine ?? 0))
            {
                var c = byId.GetValueOrDefault(cid);
                if (c == null) continue;
                var line = c.StartLine is int csl ? $":{csl}" : "";
                sb.Append(new string(' ', depth * 2)).Append($"{c.Type}\t{c.Name}{line}\n");
                Render(cid, depth + 1);
            }
        }
        Render(fileId, 1);
        var outline = sb.ToString().TrimEnd();
        return SendToolResponse(id, outline.Contains('\n') ? outline : outline + "\n  (no members)");
    }
}

/// <summary>N-hop subgraph around seed nodes; compact text by default, full JSON when verbose.</summary>
public sealed class GetSubgraphTool : IMcpTool
{
    public string Name => "get_subgraph";

    public object GetSchema() => new
    {
        name = "get_subgraph",
        description = "Retrieve nodes and edges connected to seed nodes within N hops. Token-efficient text by default: each node is 'handle  type  name  — summary' and edges are 'handle --REL--> handle'. The handle (e.g. '@/src/...File.cs::Class::Method') is a short, reusable id you can pass straight back as a seed. Set verbose=true for full JSON; maxChars to cap the size.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                seeds = new { type = "array", items = new { type = "string" }, description = "Node ids or short '@/…' handles to expand from." },
                hops = new { type = "integer", description = "Number of hops to traverse (default 2, max 5)" },
                verbose = new { type = "boolean", description = "Return full node/edge JSON instead of the compact text form (default false)." },
                maxChars = new { type = "integer", description = "Cap on the output size in characters (~4 chars/token, default 32768). Applies to the compact AND the verbose form." },
                projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
            },
            required = new[] { "seeds" }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var seedsNode = args?["seeds"] as JsonArray;
        if (seedsNode == null || seedsNode.Count == 0)
        {
            throw new McpToolException(McpErrorCode.MissingParameter, "Parameter 'seeds' is required and must be a non-empty array", isArgumentError: true);
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var clamps = new ClampReport();
        var hops = clamps.Clamp(args?["hops"], "hops", 2, 1, MaxHops);
        var verbose = args?["verbose"]?.GetValue<bool>() ?? false;
        var maxChars = ReadOutputCap(args?["maxChars"]);
        var basePath = ctx.GetProjectBasePath(projectName);

        // Accept either raw node ids or short "@/…" handles as seeds.
        var seeds = seedsNode.Select(s => FromHandle(s?.ToString(), basePath))
            .Where(s => !string.IsNullOrEmpty(s)).ToList();

        var (nodes, edges) = await storage.GetSubgraphAsync(seeds, hops).ConfigureAwait(false);

        if (!verbose)
        {
            var nodeLines = nodes.Select(n =>
            {
                var handle = ToHandle(n.Id, basePath);
                var summary = !string.IsNullOrEmpty(n.Summary) ? $"\t— {n.Summary}" : "";
                return $"{handle}\t{n.Type}\t{n.Name}{summary}";
            });
            var edgeLines = edges.Select(e =>
                $"{ToHandle(e.SourceId, basePath)} --{e.Relationship}--> {ToHandle(e.TargetId, basePath)} {ProvenanceTag(e.Provenance)}");

            var sb = new System.Text.StringBuilder();
            sb.Append("NODES (").Append(nodes.Count).Append("):\n");
            sb.Append(string.Join("\n", nodeLines));
            sb.Append("\n\nEDGES (").Append(edges.Count).Append("):\n");
            sb.Append(string.Join("\n", edgeLines));

            return SendToolResponse(id, CapOutput(sb.ToString(), maxChars, "raise maxChars or reduce hops"));
        }

        // The verbose branch emits JSON, so the cap must be STRUCTURAL (#117). TICKET-210 capped it by
        // characters, which is honest about truncating but hands back a document the caller cannot
        // JSON.parse — a dangling brace is not a usable answer. Dropping whole nodes/edges keeps the result
        // valid and states the omission outright, instead of implying it with a severed string.
        var json = BuildCappedSubgraphJson(nodes, edges, maxChars);
        return SendToolJsonResponse(id, json, clamps.Suffix);
    }

    /// <summary>
    /// Serializes the subgraph to fit <paramref name="maxChars"/> by <b>dropping whole nodes and edges</b>,
    /// never by cutting the string (#117). The result is always parseable and always reports what it left out.
    /// <para>
    /// Nodes are dropped from the tail: <c>GetSubgraphAsync</c> returns them breadth-first from the seeds, so
    /// the tail is the material furthest from what the caller asked about — the right thing to lose first.
    /// Edges whose endpoints no longer survive are dropped with them, so the graph stays referentially intact
    /// rather than carrying edges into nodes that are not there.
    /// </para>
    /// </summary>
    private static string BuildCappedSubgraphJson(
        IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, int maxChars)
    {
        string Render(int nodeCount, int edgeCount)
        {
            var keptNodes = nodes.Take(nodeCount).ToList();
            var keptIds = keptNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            var keptEdges = edges
                .Where(e => keptIds.Contains(e.SourceId) && keptIds.Contains(e.TargetId))
                .Take(edgeCount)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                nodes = keptNodes.Select(n => new
                {
                    n.Id, n.Type, n.Name, n.FilePath, n.StartLine, n.EndLine, ContentLength = n.Content.Length
                }),
                edges = keptEdges.Select(e => new
                {
                    e.SourceId, e.TargetId, Relationship = e.Relationship, Provenance = e.Provenance.ToString()
                }),
                truncated = keptNodes.Count < nodes.Count || keptEdges.Count < edges.Count,
                omitted = new
                {
                    nodes = nodes.Count - keptNodes.Count,
                    edges = edges.Count - keptEdges.Count
                },
                reason = keptNodes.Count < nodes.Count || keptEdges.Count < edges.Count ? "maxChars" : null
            });
        }

        var full = Render(nodes.Count, edges.Count);
        if (full.Length <= maxChars) return full;

        // Largest prefix of nodes that still fits. Binary search — the payload grows monotonically with the
        // node count, so ~log2(n) serializations settle it rather than a linear walk.
        int lo = 0, hi = nodes.Count, best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Render(mid, edges.Count).Length <= maxChars) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return Render(best, edges.Count);
    }
}

/// <summary>A token-optimized Markdown context capsule (Mermaid + code) for a query.</summary>
public sealed class GenerateCapsuleTool : IMcpTool
{
    public string Name => "generate_capsule";

    public object GetSchema() => new
    {
        name = "generate_capsule",
        description = "Generate a token-optimized, prompt-friendly Markdown context capsule with inline Mermaid diagrams and source code snippets for a query.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "The seed query or topic to construct the capsule around" },
                hops = new { type = "integer", description = "Traversal hops for context expansion (default 2, max 5)" },
                maxChars = new { type = "integer", description = "Code-body budget in characters (~4 chars/token, default 12000 ≈ 3k tokens). The seed nodes that matched your query are ALWAYS rendered in full; lower-relevance neighbours have their bodies omitted (with a notice) once this budget is spent — never silent tail truncation. Raise it for more full-body context." },
                projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
            },
            required = new[] { "query" }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var query = args?["query"]?.ToString();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpToolException(McpErrorCode.MissingParameter, "Parameter 'query' is required", isArgumentError: true);
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var clamps = new ClampReport();
        var hops = clamps.Clamp(args?["hops"], "hops", 2, 1, MaxHops);

        // Seed with the same hybrid retrieval agents get from search_hybrid (vector + FTS, FTS-only
        // fallback) — the old FTS-only seeding missed intent-phrased queries the tool is built for.
        var seedResults = await ctx.HybridSearchAsync(storage, query, 5).ConfigureAwait(false);
        var seeds = seedResults.Select(r => r.Node.Id).ToList();

        if (seeds.Count == 0)
        {
            return SendToolResponse(id, $"No nodes found matching query: '{query}'");
        }

        var (nodes, edges) = await storage.GetSubgraphAsync(seeds, hops).ConfigureAwait(false);

        // Budget-aware synthesis (TICKET-003), matching the web /api/capsule path: the seed nodes that
        // matched the query are rendered FIRST and IN FULL, a hub 2-hop expansion is capped at 40 nodes,
        // and lower-relevance bodies are omitted with a notice once the code budget is spent. This replaces
        // the old blind "truncate at the last ## before maxChars", which could drop the very seeds that
        // matched — the exact bug this tool had.
        var maxChars = ReadInt(args?["maxChars"], 0);
        var markdown = ctx.Synthesizer.Synthesize(nodes, edges, new Shonkor.Core.Services.CapsuleOptions
        {
            SeedIds = seeds,
            MaxContentChars = maxChars > 0 ? maxChars : 12000,
            MaxNodes = 40
        });

        return SendToolResponse(id, clamps.Annotate(markdown));
    }
}

/// <summary>arc42 building-block view: modules, key types, and a Mermaid module-dependency diagram.</summary>
public sealed class ArchitectureTool : IMcpTool
{
    public string Name => "architecture";

    public object GetSchema() => new
    {
        name = "architecture",
        description = "Architecture overview for DOCUMENTATION (arc42 building-block view) and onboarding: derives modules from the source layout, lists each module's types (key ones first, by how many things reference them), and renders a Mermaid diagram of the cross-module dependencies (REFERENCES_TYPE/IMPLEMENTS/EXTENDS/CALLS aggregated to module level). One call → the structural skeleton to write up; the prose/rationale is yours to add.",
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
        var basePath = ctx.GetProjectBasePath(projectName);
        var defTypes = new[] { "Class", "Interface", "Record", "Struct", "Enum" };
        var defs = await storage.GetNodesByTypesAsync(defTypes).ConfigureAwait(false);
        if (defs.Count == 0)
        {
            return SendToolResponse(id, "No types in the graph to describe (index the project first).");
        }

        // Derive a module name from each type's path: the segment after 'src/', else the first directory.
        string? ModuleOf(GraphNode n)
        {
            if (string.IsNullOrEmpty(n.FilePath)) return null;
            var rel = Shorten(n.FilePath, basePath);
            var segs = rel.Split('/', '\\').Where(s => s.Length > 0).ToArray();
            if (segs.Length == 0) return null;
            if (segs.Length > 1 && segs[0].Equals("src", StringComparison.OrdinalIgnoreCase)) return segs[1];
            return segs.Length > 1 ? segs[0] : "(root)";
        }

        var moduleOfId = new Dictionary<string, string>(StringComparer.Ordinal);
        var byModule = new Dictionary<string, List<GraphNode>>(StringComparer.Ordinal);
        foreach (var n in defs)
        {
            var m = ModuleOf(n);
            if (m is null) continue;
            moduleOfId[n.Id] = m;
            (byModule.TryGetValue(m, out var list) ? list : byModule[m] = new List<GraphNode>()).Add(n);
        }

        const int maxTypes = 3000;
        var semantic = new HashSet<string>(StringComparer.Ordinal) { "REFERENCES_TYPE", "IMPLEMENTS", "EXTENDS", "CALLS" };
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var moduleDeps = new Dictionary<(string From, string To), int>();
        var processed = 0;
        foreach (var n in defs)
        {
            if (!moduleOfId.TryGetValue(n.Id, out var mFrom)) continue;
            if (processed++ >= maxTypes) break;
            var (edges, _) = await storage.GetIncidentEdgesAsync(n.Id).ConfigureAwait(false);
            foreach (var e in edges)
            {
                if (!semantic.Contains(e.Relationship)) continue;
                if (e.TargetId == n.Id && e.SourceId != n.Id)
                    inDegree[n.Id] = inDegree.GetValueOrDefault(n.Id) + 1;
                if (e.SourceId == n.Id && moduleOfId.TryGetValue(e.TargetId, out var mTo) && mTo != mFrom)
                    moduleDeps[(mFrom, mTo)] = moduleDeps.GetValueOrDefault((mFrom, mTo)) + 1;
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"Architecture of '{projectName ?? "(active)"}' — {byModule.Count} module(s), {defs.Count} type(s).\n");
        sb.Append("\nBUILDING BLOCKS (module: types, most-referenced first):\n");
        foreach (var kv in byModule.OrderByDescending(kv => kv.Value.Count))
        {
            var key = kv.Value.OrderByDescending(t => inDegree.GetValueOrDefault(t.Id)).ThenBy(t => t.Name, StringComparer.Ordinal);
            var names = string.Join(", ", key.Take(6).Select(t => $"{t.Name}{(inDegree.GetValueOrDefault(t.Id) is int r and > 0 ? $"({r})" : "")}"));
            sb.Append($"  {kv.Key} ({kv.Value.Count}): {names}\n");
        }

        sb.Append("\nMODULE DEPENDENCIES (Mermaid):\n```mermaid\ngraph LR\n");
        static string Mid(string m) => "m_" + new string(m.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        foreach (var m in byModule.Keys.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append($"  {Mid(m)}[\"{m}\"]\n");
        foreach (var d in moduleDeps.OrderByDescending(d => d.Value))
            sb.Append($"  {Mid(d.Key.From)} -->|{d.Value}| {Mid(d.Key.To)}\n");
        sb.Append("```\n(Edge labels = number of cross-module type/call references. Prose/rationale is yours to add — this is the structural skeleton for arc42 ch. 5.)");

        return SendToolResponse(id, sb.ToString());
    }
}
