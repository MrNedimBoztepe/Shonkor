// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Models;
using static Shonkor.Infrastructure.Services.Mcp.McpToolHelpers;

namespace Shonkor.Infrastructure.Services.Mcp.Tools;

/// <summary>Re-index a single file after an edit so the graph matches the working tree.</summary>
public sealed class ReindexFileTool : IMcpTool
{
    public string Name => "reindex_file";

    public object GetSchema() => new
    {
        name = "reindex_file",
        description = "Re-index a SINGLE file after you edit it, so the graph matches the working tree before you query again — closes the edit loop. Clears the file's old nodes/edges, re-parses it (a missing file is removed), and relinks the file's outgoing REFERENCES_TYPE edges so impact/dependency stay correct. Accepts an absolute path, a project-relative path, or an '@/' handle. Available only where the MCP server can see the files (local stdio/dev); degrades with a clear message otherwise. Other cross-tech links (BINDS_TO/CALLS) refresh on a full scan.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "File to re-index: absolute, project-relative, or an '@/<relative>' handle." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            },
            required = new[] { "path" }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var rawPath = args?["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return SendError(id, -32602, "Parameter 'path' is required");
        }
        if (ctx.FileParsers == null)
        {
            return SendToolResponse(id,
                "reindex_file is unavailable here (no parsers / filesystem access). Run via the local MCP server in the project directory.");
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);
        if (!TryResolveContainedPath(rawPath, basePath, out var resolved, out var pathError))
        {
            return SendError(id, -32602, pathError!);
        }

        // Semantic projects route through the cached reconcile so CALLS / exact REFERENCES_TYPE refresh
        // incrementally; otherwise the plain single-file path (fast name-mode structure).
        var semantic = ctx.IsSemanticProject(projectName);

        var scanner = new GraphIndexScanner(storage, ctx.FileParsers, semanticCsharp: semantic, compilationCache: ctx.CompilationCache);
        var result = semantic
            ? await scanner.ReconcilePathsAsync(basePath, new[] { resolved }).ConfigureAwait(false)
            : await scanner.ScanFileAsync(resolved).ConfigureAwait(false);

        var handle = ToHandle(System.IO.Path.GetFullPath(resolved), basePath);
        if (result.NodesCreated == 0)
        {
            return SendToolResponse(id, $"Cleared '{handle}' from the graph (file missing, unparsable, or empty).");
        }
        return SendToolResponse(id,
            $"Reindexed {handle}: {result.NodesCreated} node(s), {result.EdgesCreated} edge(s) in {result.Duration.TotalMilliseconds:F0} ms. (REFERENCES_TYPE relinked for this file; other cross-tech links refresh on a full scan.)");
    }
}

/// <summary>Compile-check a C# file after editing (Roslyn syntax + optional semantic).</summary>
public sealed class CheckEditTool : IMcpTool
{
    public string Name => "check_edit";

    public object GetSchema() => new
    {
        name = "check_edit",
        description = "Compile-check a C# file after you edit it: returns the Roslyn SYNTAX errors (always reliable — catches the most common edit breakage like a missing brace/semicolon) and, for a semantic project, SEMANTIC errors scoped to that file. Run it right after editing to confirm the change compiles, BEFORE moving on — your edit validator. Self-contained (no `dotnet build`). Semantic checks resolve in-codebase symbols only (no NuGet refs), so 'type/namespace not found' noise from external packages is suppressed. Accepts an absolute/project-relative path or an '@/' handle. Local only (needs filesystem access).",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "C# file to check: absolute, project-relative, or an '@/<relative>' handle." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            },
            required = new[] { "path" }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var rawPath = args?["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return SendError(id, -32602, "Parameter 'path' is required");
        }
        if (ctx.FileParsers == null)
        {
            return SendToolResponse(id,
                "check_edit is unavailable here (no filesystem access). Run via the local MCP server in the project directory.");
        }
        var projectName = args?["projectName"]?.ToString();
        var basePath = ctx.GetProjectBasePath(projectName);
        if (!TryResolveContainedPath(rawPath, basePath, out var resolved, out var pathError))
        {
            return SendError(id, -32602, pathError!);
        }

        if (!resolved.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return SendToolResponse(id, "check_edit currently supports C# (.cs) files only.");
        }
        if (!System.IO.File.Exists(resolved))
        {
            return SendToolResponse(id, $"File not found on disk: {ToHandle(resolved, basePath)}.");
        }

        var content = await System.IO.File.ReadAllTextAsync(resolved).ConfigureAwait(false);

        // Semantic checks only for a semantic project (and when the cache is wired); otherwise syntax-only.
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation? compilation = null;
        if (ctx.IsSemanticProject(projectName))
        {
            compilation = await ctx.CompilationCache!.ApplyEditsAsync(basePath, new[] { resolved }).ConfigureAwait(false);
        }

        var report = CSharpDiagnostics.Report(resolved, content, compilation);
        return SendToolResponse(id, report);
    }
}

/// <summary>Anti-drift: one file's sync state with a path, or a project-wide drift report without.</summary>
public sealed class FreshnessTool : IMcpTool
{
    public string Name => "freshness";

    public object GetSchema() => new
    {
        name = "freshness",
        description = "Anti-drift check: does the graph still match the files on disk? With 'path' → checks ONE file: Fresh (in sync), Stale (edited since indexing — run reindex_file), Untracked (on disk but never indexed), or Deleted (indexed but gone). Without 'path' → a project-wide drift report listing Changed/New/Deleted files (empty = graph matches the working tree). Use before trusting analysis for files you may have just changed.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                path = new { type = "string", description = "Optional. A file to check (absolute, project-relative, or an '@/<relative>' handle). Omit for a whole-project drift report." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        if (ctx.FileParsers == null)
        {
            return SendToolResponse(id,
                "freshness is unavailable here (no filesystem access). Run via the local MCP server in the project directory.");
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var rawPath = args?["path"]?.ToString();

        // With a path → single-file freshness check.
        if (!string.IsNullOrWhiteSpace(rawPath))
        {
            var fileBase = ctx.GetProjectBasePath(projectName);
            if (!TryResolveContainedPath(rawPath, fileBase, out var resolved, out var pathError))
            {
                return SendError(id, -32602, pathError!);
            }

            var fileScanner = new GraphIndexScanner(storage, ctx.FileParsers);
            var state = await fileScanner.CheckFreshnessAsync(resolved).ConfigureAwait(false);
            var handle = ToHandle(System.IO.Path.GetFullPath(resolved), fileBase);
            var hint = state switch
            {
                GraphIndexScanner.FreshnessState.Fresh => "the graph matches the file on disk.",
                GraphIndexScanner.FreshnessState.Stale => "the file was edited since indexing — run reindex_file before trusting analysis.",
                GraphIndexScanner.FreshnessState.Untracked => "the file is on disk but not in the graph — run reindex_file (or a full index).",
                GraphIndexScanner.FreshnessState.Deleted => "the file is in the graph but gone from disk — run reindex_file to remove it.",
                _ => string.Empty
            };
            return SendToolResponse(id, $"{state}: {handle} — {hint}");
        }

        // Without a path → project-wide drift report.
        var basePath = ctx.GetProjectBasePath(projectName);
        var resolvedName = ctx.ResolveProjectName(projectName) ?? ctx.ProjectManager.GetActiveProjectName();
        var excludePatterns = ctx.ProjectManager.GetProjectConfig(resolvedName).ExcludePatterns;

        var scanner = new GraphIndexScanner(storage, ctx.FileParsers);
        var drift = await scanner.DetectDriftAsync(basePath, excludePatterns).ConfigureAwait(false);

        if (drift.IsClean)
        {
            return SendToolResponse(id, "Graph matches the working tree — no drift detected.");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"Drift detected: {drift.Changed.Count} changed, {drift.New.Count} new, {drift.Deleted.Count} deleted. Run a full index (or reindex_file each) to reconcile.\n");
        void AppendGroup(string label, IReadOnlyList<string> files)
        {
            if (files.Count == 0) return;
            sb.Append($"\n{label}:\n");
            foreach (var f in files) sb.Append($"  {ToHandle(f, basePath)}\n");
        }
        AppendGroup("CHANGED (edited since indexing)", drift.Changed);
        AppendGroup("NEW (on disk, not indexed)", drift.New);
        AppendGroup("DELETED (indexed, gone from disk)", drift.Deleted);
        return SendToolResponse(id, sb.ToString().TrimEnd());
    }
}

/// <summary>The tests that transitively exercise a symbol — exactly what to run after a change.</summary>
public sealed class RelatedTestsTool : IMcpTool
{
    public string Name => "related_tests";

    public object GetSchema() => new
    {
        name = "related_tests",
        description = "Precise test impact: the tests that exercise a symbol TRANSITIVELY (a test that calls A which calls the changed method is found too), via the call/reference graph — i.e. exactly what to run after changing it. Returns 'testName  file:line  [direct|via N hops]', ranked closest-first (xUnit/NUnit/Go/Python/JS conventions). Method-level transitivity needs semantic indexing (CALLS). States plainly when nothing covers it.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                symbol = new { type = "string", description = "The type/method/symbol name to find tests for. 'query' is accepted as an alias." },
                depth = new { type = "integer", description = "How many hops of transitive coverage to follow (default 3, max 6)." },
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

        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
        if (def == null)
        {
            return SendToolResponse(id, $"No definition found for '{symbol}'.");
        }

        var depth = Math.Clamp(ReadInt(args?["depth"], 3), 1, 6);

        // Transitive test impact: BFS over INCOMING impact edges; traverse through every node but collect
        // only test-file nodes, recording the shortest hop distance to each.
        const int maxVisit = 600;
        var tests = new Dictionary<string, (int Depth, GraphNode Node)>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { def.Id };
        var frontier = new List<string> { def.Id };

        for (var d = 1; d <= depth && frontier.Count > 0 && visited.Count < maxVisit; d++)
        {
            var next = new List<string>();
            foreach (var nodeId in frontier)
            {
                var (edges, neighbours) = await storage.GetIncidentEdgesAsync(nodeId).ConfigureAwait(false);
                foreach (var e in edges)
                {
                    if (e.TargetId != nodeId || e.SourceId == nodeId) continue;       // incoming only
                    if (StructuralRelationships.Contains(e.Relationship)) continue;    // skip containment
                    if (!visited.Add(e.SourceId)) continue;
                    next.Add(e.SourceId);                                              // keep traversing transitively
                    var n = neighbours.GetValueOrDefault(e.SourceId);
                    if (n != null && LooksLikeTest(n.FilePath)) tests[e.SourceId] = (d, n);
                    if (visited.Count >= maxVisit) break;
                }
                if (visited.Count >= maxVisit) break;
            }
            frontier = next;
        }

        if (tests.Count == 0)
        {
            return SendToolResponse(id,
                $"No tests in the graph reach '{def.Name}' within {depth} hop(s) — the change may be untested (or covered only beyond depth {depth} / via edges not in the graph).");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append($"{tests.Count} test(s) covering '{def.Name}' transitively (run these after changing it):\n");
        foreach (var t in tests.Values.OrderBy(t => t.Depth).ThenBy(t => t.Node.Name, StringComparer.Ordinal))
        {
            var loc = Shorten(string.IsNullOrEmpty(t.Node.FilePath) ? t.Node.Id : t.Node.FilePath, basePath);
            if (t.Node.StartLine is int line) loc += $":{line}";
            var via = t.Depth == 1 ? "direct" : $"via {t.Depth} hops";
            sb.Append($"{t.Node.Name}\t{loc}\t[{via}]\n");
        }
        return SendToolResponse(id, sb.ToString().TrimEnd() + await ctx.StaleSuffixAsync(storage, def).ConfigureAwait(false));
    }
}

/// <summary>A concrete edit checklist: definition + every reference site.</summary>
public sealed class EditPlanTool : IMcpTool
{
    public string Name => "edit_plan";

    public object GetSchema() => new
    {
        name = "edit_plan",
        description = "Produce a concrete edit checklist for changing a symbol: its definition location plus every reference site as '[ ] file:line  name  (relation)', ready to work through. Combines impact analysis with precise locations; ends with the verify steps (reindex_file, find_usages/related_tests).",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                symbol = new { type = "string", description = "The type/method/symbol you intend to change (e.g. rename or signature change). 'query' is accepted as an alias." },
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

        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
        if (def == null)
        {
            return SendToolResponse(id, $"No definition found for '{symbol}'.");
        }

        var defLoc = Shorten(string.IsNullOrEmpty(def.FilePath) ? def.Id : def.FilePath, basePath);
        if (def.StartLine is int dl) defLoc += $":{dl}";

        var (edges, neighbours) = await storage.GetIncidentEdgesAsync(def.Id).ConfigureAwait(false);
        var incoming = edges.Where(e => e.TargetId == def.Id && e.SourceId != def.Id).ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append($"Edit plan — '{def.Name}' ({def.Type}) defined at {defLoc}\n");
        if (incoming.Count == 0)
        {
            sb.Append("No reference sites — safe to change in isolation.\n");
        }
        else
        {
            sb.Append($"Update {incoming.Count} reference site(s):\n");
            var i = 1;
            foreach (var e in incoming.OrderBy(e => e.Relationship))
            {
                var user = neighbours.GetValueOrDefault(e.SourceId);
                var userName = user?.Name ?? e.SourceId;
                var loc = Shorten(user != null && !string.IsNullOrEmpty(user.FilePath) ? user.FilePath : e.SourceId, basePath);
                if (user?.StartLine is int ul) loc += $":{ul}";
                sb.Append($"[ ] {i++}. {loc}\t{userName}\t({e.Relationship})\n");
            }
        }
        sb.Append("After editing: reindex_file each changed path, then find_usages / related_tests to confirm.");
        return SendToolResponse(id, sb.ToString());
    }
}

/// <summary>A safe, overload-precise rename checklist from the graph's exact edges.</summary>
public sealed class RenamePlanTool : IMcpTool
{
    public string Name => "rename_plan";

    public object GetSchema() => new
    {
        name = "rename_plan",
        description = "A SAFE rename checklist: the declaration site plus every reference site to update, resolved from the GRAPH's exact edges — so for an overloaded method only callers of THIS overload are listed (a text find/replace can't tell overloads apart). Warns when other symbols share the name (which a text replace would wrongly hit). Use before renaming a symbol across files.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                symbol = new { type = "string", description = "The current name of the symbol to rename. 'query' is accepted as an alias." },
                new_name = new { type = "string", description = "The intended new name (shown in the plan; no edits are made)." },
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
        var newName = args?["new_name"]?.ToString();
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);

        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
        if (def == null)
        {
            return SendToolResponse(id, $"No definition found for '{symbol}'.");
        }

        // How many OTHER symbols share this name? A text find/replace would wrongly hit them.
        var sameNamed = (await storage.SearchAsync(symbol, SymbolSearchLimit).ConfigureAwait(false))
            .Count(h => IsExactNameMatch(h.Node, symbol) && h.Node.Id != def.Id);

        var (edges, neighbours) = await storage.GetIncidentEdgesAsync(def.Id).ConfigureAwait(false);
        // Real reference sites only (exclude structural containment — the parent type isn't a rename site).
        var sites = edges
            .Where(e => e.TargetId == def.Id && e.SourceId != def.Id && !StructuralRelationships.Contains(e.Relationship))
            .ToList();

        var defLoc = Shorten(string.IsNullOrEmpty(def.FilePath) ? def.Id : def.FilePath, basePath);
        if (def.StartLine is int dl) defLoc += $":{dl}";

        var arrow = string.IsNullOrWhiteSpace(newName) ? "" : $" → '{newName}'";
        var sb = new System.Text.StringBuilder();
        sb.Append($"Rename plan — '{def.Name}' ({def.Type}){arrow}\n");
        sb.Append($"[ ] 0. {defLoc}\t(declaration — rename here)\n");

        if (sites.Count == 0)
        {
            sb.Append("No reference sites in the graph — only the declaration needs renaming.\n");
        }
        else
        {
            var precise = sites.Any(e => e.Relationship == "CALLS")
                ? " (overload-precise: only callers of THIS overload)"
                : "";
            sb.Append($"Update {sites.Count} reference site(s){precise}:\n");
            var i = 1;
            foreach (var e in sites.OrderBy(e => e.Relationship))
            {
                var user = neighbours.GetValueOrDefault(e.SourceId);
                var userName = user?.Name ?? e.SourceId;
                var loc = Shorten(user != null && !string.IsNullOrEmpty(user.FilePath) ? user.FilePath : e.SourceId, basePath);
                if (user?.StartLine is int ul) loc += $":{ul}";
                sb.Append($"[ ] {i++}. {loc}\t{userName}\t({e.Relationship})\n");
            }
        }

        if (sameNamed > 0)
        {
            sb.Append($"⚠ {sameNamed} other symbol(s) are also named '{def.Name}' — a text find/replace would wrongly rename them; this plan targets only the exact node's edges.\n");
        }
        sb.Append("After editing: check_edit + reindex_file each changed file, then related_tests to confirm.");
        return SendToolResponse(id, sb.ToString() + await ctx.StaleSuffixAsync(storage, def).ConfigureAwait(false));
    }
}

/// <summary>A code-review briefing for a set of changed files: compile + impact + tests + risk.</summary>
public sealed class ReviewTool : IMcpTool
{
    public string Name => "review";

    public object GetSchema() => new
    {
        name = "review",
        description = "Code-review briefing for a set of changed C# files (a diff): per file it COMPILE-checks the current content (syntax + semantic), then aggregates the transitive IMPACT of the changed types/methods (who outside the change references/calls them), the TESTS to run (transitively), and the top RISKS. One call → a deterministic 'what does this change affect and break?' review. Local filesystem only for the compile step.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                paths = new { type = "array", items = new { type = "string" }, description = "The changed files (absolute, project-relative, or '@/' handles). 'path' is accepted for a single file." },
                path = new { type = "string", description = "A single changed file (alternative to 'paths')." },
                depth = new { type = "integer", description = "Transitive impact depth (default 3, max 6)." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);
        var rawPaths = (args?["paths"] as JsonArray)?.Select(x => x?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                       ?? new List<string?>();
        var single = args?["path"]?.ToString();
        if (!string.IsNullOrWhiteSpace(single)) rawPaths.Add(single);
        if (rawPaths.Count == 0)
        {
            return SendError(id, -32602, "Provide the changed files via 'paths' (array) or 'path'.");
        }

        // Reject the whole review if ANY supplied path escapes the project root — a review that silently
        // dropped an out-of-root path would under-report impact, and honoring it would read arbitrary files.
        var fullPaths = new List<string>();
        foreach (var p in rawPaths)
        {
            if (!TryResolveContainedPath(p, basePath, out var full, out var pathError))
            {
                return SendError(id, -32602, pathError!);
            }
            if (!fullPaths.Contains(full, StringComparer.OrdinalIgnoreCase)) fullPaths.Add(full);
        }
        var changedFiles = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
        var depth = Math.Clamp(ReadInt(args?["depth"], 3), 1, 6);

        var impactTypes = new HashSet<string>(StringComparer.Ordinal)
        { "Class", "Interface", "Record", "Struct", "Enum", "Method", "Constructor", "Property" };
        var semanticImpact = new HashSet<string>(StringComparer.Ordinal) { "REFERENCES_TYPE", "IMPLEMENTS", "EXTENDS", "CALLS" };
        var semanticProject = ctx.IsSemanticProject(projectName);

        var compileLines = new List<string>();
        var changedDefs = new List<GraphNode>();
        var compileErrorFiles = 0;
        foreach (var full in fullPaths)
        {
            foreach (var n in await storage.GetNodesByFilePathAsync(full).ConfigureAwait(false))
                if (impactTypes.Contains(n.Type)) changedDefs.Add(n);

            var rel = Shorten(full, basePath);
            if (ctx.FileParsers != null && full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(full))
            {
                var content = await System.IO.File.ReadAllTextAsync(full).ConfigureAwait(false);
                Microsoft.CodeAnalysis.CSharp.CSharpCompilation? comp = semanticProject
                    ? await ctx.CompilationCache!.ApplyEditsAsync(basePath, new[] { full }).ConfigureAwait(false)
                    : null;
                var firstLine = CSharpDiagnostics.Report(full, content, comp).Split('\n')[0];
                if (!firstLine.StartsWith("OK", StringComparison.Ordinal)) compileErrorFiles++;
                compileLines.Add($"  {rel}: {firstLine}");
            }
            else
            {
                compileLines.Add($"  {rel}: (not compile-checked)");
            }
        }

        // Transitive impact seeded from ALL changed definitions; skip nodes inside the change itself.
        const int maxVisit = 800;
        var affected = new Dictionary<string, GraphNode?>(StringComparer.Ordinal);
        var tests = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var visited = new HashSet<string>(changedDefs.Select(d => d.Id), StringComparer.Ordinal);
        var frontier = changedDefs.Select(d => d.Id).ToList();
        for (var d = 1; d <= depth && frontier.Count > 0 && visited.Count < maxVisit; d++)
        {
            var next = new List<string>();
            foreach (var nodeId in frontier)
            {
                var (edges, neighbours) = await storage.GetIncidentEdgesAsync(nodeId).ConfigureAwait(false);
                foreach (var e in edges)
                {
                    if (e.TargetId != nodeId || e.SourceId == nodeId) continue;
                    if (!semanticImpact.Contains(e.Relationship)) continue;
                    if (!visited.Add(e.SourceId)) continue;
                    var n = neighbours.GetValueOrDefault(e.SourceId);
                    if (n != null && !string.IsNullOrEmpty(n.FilePath) && changedFiles.Contains(n.FilePath)) continue; // part of the change
                    next.Add(e.SourceId);
                    affected[e.SourceId] = n;
                    if (n != null && LooksLikeTest(n.FilePath)) tests[e.SourceId] = n;
                }
            }
            frontier = next;
        }

        var affectedFiles = affected.Values.Where(n => !string.IsNullOrEmpty(n?.FilePath)).Select(n => n!.FilePath!).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var sb = new System.Text.StringBuilder();
        sb.Append($"Review of {fullPaths.Count} changed file(s) — {changedDefs.Count} changed symbol(s).\n");
        sb.Append("\nCOMPILE:\n");
        foreach (var l in compileLines) sb.Append(l).Append('\n');
        sb.Append($"\nIMPACT: {affected.Count} node(s) across {affectedFiles} file(s) outside the change depend on it.\n");
        if (tests.Count > 0)
        {
            sb.Append($"TESTS TO RUN ({tests.Count}):\n");
            foreach (var t in tests.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                var loc = Shorten(string.IsNullOrEmpty(t.FilePath) ? t.Id : t.FilePath, basePath);
                sb.Append($"  {t.Name}\t{loc}\n");
            }
        }
        else
        {
            sb.Append("TESTS TO RUN: none found in the graph — the change may be untested.\n");
        }
        sb.Append("\nRISK: ");
        sb.Append(compileErrorFiles > 0 ? $"{compileErrorFiles} file(s) do NOT compile. " : "all checked files compile. ");
        sb.Append(affected.Count >= maxVisit ? "Impact is large (capped) — high blast radius." : $"{affected.Count} impacted node(s).");
        return SendToolResponse(id, sb.ToString().TrimEnd());
    }
}
