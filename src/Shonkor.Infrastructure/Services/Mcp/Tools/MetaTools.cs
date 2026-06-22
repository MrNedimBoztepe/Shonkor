// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using static Shonkor.Infrastructure.Services.Mcp.McpToolHelpers;

namespace Shonkor.Infrastructure.Services.Mcp.Tools;

/// <summary>One-call session bootstrap: graph size, the tool palette by intent, the edit loop.</summary>
public sealed class OrientTool : IMcpTool
{
    public string Name => "orient";

    public object GetSchema() => new
    {
        name = "orient",
        description = "START HERE in a new session: a one-call orientation to this project's Shonkor graph. Returns the graph size, the tool palette grouped by intent (find/read/impact/verify/tests), the recommended edit loop, and the count of open threads. Use it to know what's available and how to work, instead of grepping/reading whole files. Costs one call and saves many.",
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
        var stats = await storage.GetStatisticsAsync().ConfigureAwait(false);

        // Count still-open recorded threads (same rule as get_open_threads).
        var threadTypes = new[] { "Question", "Task", "Decision", "Milestone" };
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "done", "resolved", "completed", "accepted", "superseded", "closed" };
        var threads = await storage.GetNodesByTypesAsync(threadTypes).ConfigureAwait(false);
        var openThreads = threads.Count(n => !closed.Contains(n.Properties.GetValueOrDefault("status", "").Trim()));

        var topTypes = string.Join(", ", stats.NodesByType
            .Where(kv => kv.Key is not "Concept" and not "MarkdownSection")
            .OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} {kv.Value}"));

        var callsNote = stats.EdgesByRelation.GetValueOrDefault("CALLS", 0) > 0
            ? "method-level CALLS are indexed (semantic mode)."
            : "no CALLS edges yet — enable semantic indexing (per-project SemanticCSharp) for method-level impact/call_hierarchy.";

        // search_semantic is only listed/usable when an embedding backend is wired (web server + Ollama).
        var semanticNote = ctx.HasEmbeddingService ? " · search_semantic" : string.Empty;

        var guide =
$@"Shonkor graph for project '{projectName ?? "(active)"}' — START HERE.

GRAPH: {stats.TotalNodes} nodes, {stats.TotalEdges} edges. Top code types: {topTypes}.
Edges: {callsNote}

USE THE GRAPH instead of grepping or reading whole files:
  Find:    locate · search_graph{semanticNote}
  Read:    signature · get_source · outline · get_subgraph
  Impact:  references (direction=used_by|uses, depth) · find_usages · call_hierarchy
  Verify:  verify_exists (before claiming something exists)
  Tests:   related_tests (exactly which tests to run, transitively)

EDIT LOOP after you change code:
  1. check_edit <file>      → does it compile? (Roslyn syntax + semantic, no build)
  2. reindex_file <file>    → refresh the graph for that file
  3. related_tests <symbol> → run exactly the covering tests
  4. freshness [path]       → confirm the graph matches the working tree (a file, or whole project)

OPEN THREADS: {openThreads} (call get_open_threads to resume prior work).";

        return SendToolResponse(id, guide);
    }
}

/// <summary>Switches the active project for THIS session only (in-memory; never persisted).</summary>
public sealed class SetProjectTool : IMcpTool
{
    public string Name => "set_project";

    public object GetSchema() => new
    {
        name = "set_project",
        description = "Switch the ACTIVE project this session works with — for clients (e.g. Claude Desktop) that have no per-chat working directory, so you can say 'work with FPM' and have every following tool call resolve to that project's graph. Lists the available projects when called with no name. (No effect when the server is locked to a single tenant.)",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string", description = "The project to make active (must exist in the registry). Omit to list the available projects." }
            }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        if (ctx.LockToContextProject)
        {
            return SendToolResponse(id, "This server is locked to a single tenant; project switching is disabled here.");
        }
        var projects = ctx.ProjectManager.GetProjects();
        // The effective project for THIS session: the session override if set, else the context/active.
        var active = ctx.SessionProjectOverride ?? ctx.ContextProjectName ?? ctx.ProjectManager.GetActiveProjectName();
        var name = args?["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(name))
        {
            var list = string.Join("\n", projects.Select(p => $"  {(p.Name.Equals(active, StringComparison.OrdinalIgnoreCase) ? "* " : "  ")}{p.Name}\t{p.Path}"));
            return SendToolResponse(id, $"Active project (this session): {active}\nProjects:\n{list}\n(call set_project with name=<project> to switch — session-local, does not affect other chats.)");
        }
        var match = projects.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            return SendToolResponse(id, $"No project named '{name}'. Available: {string.Join(", ", projects.Select(p => p.Name))}.");
        }
        // Session-local switch only — never writes the shared, persisted ActiveProjectName.
        ctx.SessionProjectOverride = match.Name;
        var newStorage = await ctx.ProjectManager.GetStorageProviderAsync(match.Name).ConfigureAwait(false);
        var newStats = await newStorage.GetStatisticsAsync().ConfigureAwait(false);
        return SendToolResponse(id, $"Active project for this session is now '{match.Name}' ({match.Path}) — {newStats.TotalNodes} nodes, {newStats.TotalEdges} edges. Call orient for the workflow.");
    }
}
