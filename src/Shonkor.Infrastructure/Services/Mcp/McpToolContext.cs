// Licensed to Shonkor under the MIT License.

using System.Text.Json.Nodes;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using static Shonkor.Infrastructure.Services.Mcp.McpToolHelpers;

namespace Shonkor.Infrastructure.Services.Mcp;

/// <summary>
/// Per-server shared state and stateful helpers handed to every <see cref="IMcpTool"/>. Holds the
/// services a tool may need (project registry, synthesizer, optional embedding backend, optional file
/// parsers for filesystem-aware tools, optional semantic compilation cache) and the session's project
/// resolution. Lives for the lifetime of one MCP server process (single stdio loop).
/// </summary>
public sealed class McpToolContext
{
    public ProjectManager ProjectManager { get; }
    public ContextCapsuleSynthesizer Synthesizer { get; }

    /// <summary>Optional embedding backend for semantic search; null in the stdio CLI.</summary>
    public IEmbeddingService? EmbeddingService { get; }

    /// <summary>Optional file parsers for filesystem-aware tools (reindex/check_edit/freshness); null when remote.</summary>
    public IEnumerable<IFileParser>? FileParsers { get; }

    /// <summary>Optional shared Roslyn compilation cache for incremental semantic relinking; null = name mode.</summary>
    public SemanticCompilationCache? CompilationCache { get; }

    /// <summary>The project bound to this server's working directory (the AI chat's directory), or null.</summary>
    public string? ContextProjectName { get; }

    /// <summary>When true, the session is hard-bound to <see cref="ContextProjectName"/> (multi-tenant SaaS safety).</summary>
    public bool LockToContextProject { get; }

    /// <summary>
    /// A session-local active-project override set by <c>set_project</c>. Lives only in this process — it
    /// deliberately does NOT touch the shared, persisted ActiveProjectName, so switching the project in one
    /// chat never affects another session or client. Null until set.
    /// </summary>
    public string? SessionProjectOverride { get; set; }

    /// <summary>
    /// Whether this context outlives the current message. True for the stdio server (one context per
    /// process); false for the per-request HTTP relay, where session state like
    /// <see cref="SessionProjectOverride"/> would be discarded with the request.
    /// </summary>
    public bool PersistentSession { get; }

    public bool HasEmbeddingService => EmbeddingService != null;
    public bool HasFileParsers => FileParsers != null;

    public McpToolContext(
        ProjectManager projectManager,
        ContextCapsuleSynthesizer synthesizer,
        string? contextProjectName,
        bool lockToContextProject,
        IEmbeddingService? embeddingService,
        IEnumerable<IFileParser>? fileParsers,
        SemanticCompilationCache? compilationCache,
        bool persistentSession = true)
    {
        ProjectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
        Synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        ContextProjectName = contextProjectName;
        LockToContextProject = lockToContextProject;
        EmbeddingService = embeddingService;
        FileParsers = fileParsers;
        CompilationCache = compilationCache;
        PersistentSession = persistentSession;
    }

    /// <summary>
    /// Resolves the project name to use for a call. When tenant-locked the context project always wins
    /// (the explicit argument is ignored, preventing cross-tenant access). Otherwise: an explicit per-call
    /// argument wins, then this session's set_project override, then the directory-derived context.
    /// </summary>
    public string? ResolveProjectName(string? projectName) =>
        LockToContextProject
            ? ContextProjectName
            : (!string.IsNullOrWhiteSpace(projectName) ? projectName : (SessionProjectOverride ?? ContextProjectName));

    public Task<IGraphStorageProvider> GetStorageAsync(string? projectName)
    {
        var effective = ResolveProjectName(projectName);
        return string.IsNullOrWhiteSpace(effective)
            ? ProjectManager.GetActiveStorageProviderAsync()
            : ProjectManager.GetStorageProviderAsync(effective);
    }

    /// <summary>
    /// Retrieves the best <paramref name="count"/> matches for <paramref name="query"/>, fusing keyword (FTS)
    /// and — when an embedding backend is wired — vector results via reciprocal-rank fusion. Falls back to
    /// FTS-only when no backend is available or the embedding call fails, so a hiccup degrades rather than
    /// fails. The single hybrid-retrieval entry point shared by <c>search_hybrid</c> and capsule seeding, so
    /// they can never drift apart.
    /// </summary>
    public Task<IReadOnlyList<SearchResult>> HybridSearchAsync(
        IGraphStorageProvider storage, string query, int count, CancellationToken cancellationToken = default)
        => HybridRetrieval.SearchAsync(storage, EmbeddingService, query, count, cancellationToken);

    /// <summary>Resolves the filesystem root of the requested (or context) project, for path shortening.</summary>
    public string GetProjectBasePath(string? projectName)
    {
        try
        {
            var name = ResolveProjectName(projectName) ?? ProjectManager.GetActiveProjectName();
            var project = ProjectManager.GetProjects().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return project?.Path ?? ProjectManager.WorkspacePath;
        }
        catch
        {
            return ProjectManager.WorkspacePath;
        }
    }

    /// <summary>Whether the effective project opts into semantic indexing and a compilation cache is wired.</summary>
    public bool IsSemanticProject(string? projectName)
    {
        if (CompilationCache is null) return false;
        var name = ResolveProjectName(projectName) ?? ProjectManager.GetActiveProjectName();
        return ProjectManager.GetProjects()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.SemanticCSharp == true;
    }

    /// <summary>
    /// Fallback for <c>get_source</c> on nodes that store no body: reads the exact line range from the file
    /// when this server has filesystem access. Returns <c>null</c> if unavailable. The node's stored
    /// <see cref="GraphNode.FilePath"/> is re-validated against <paramref name="basePath"/> before any read,
    /// so a poisoned/legacy graph entry pointing outside the project can never turn get_source into an
    /// arbitrary-file reader. <see cref="GraphNode.StartLine"/>/<see cref="GraphNode.EndLine"/> are 1-based.
    /// </summary>
    public string? TryReadSourceSlice(GraphNode node, string basePath)
    {
        if (FileParsers == null || string.IsNullOrEmpty(node.FilePath) || !node.StartLine.HasValue) return null;
        if (!TryResolveContainedPath(node.FilePath, basePath, out _, out _)) return null; // never read outside the root
        if (!System.IO.File.Exists(node.FilePath)) return null;
        try
        {
            var lines = System.IO.File.ReadAllLines(node.FilePath);
            if (lines.Length == 0) return null;
            var start = Math.Clamp(node.StartLine.Value - 1, 0, lines.Length - 1);
            var end = node.EndLine.HasValue
                ? Math.Clamp(node.EndLine.Value - 1, start, lines.Length - 1)
                : Math.Min(start + 40, lines.Length - 1);
            return string.Join("\n", lines[start..(end + 1)]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a one-line "may be stale" warning to append to an analysis result when the resolved symbol's
    /// FILE changed on disk since indexing, or "" when fresh / untracked / no filesystem access.
    /// </summary>
    /// <summary>
    /// Names the project an answer came from, and how big it is — e.g. <c> in project 'shonkor' (2.726 nodes)</c>.
    ///
    /// <para>
    /// Meant above all for the EMPTY answer (#286). "No matches for 'X'." reads as <i>this symbol does not
    /// exist</i>. The true statement is <i>this symbol does not exist in project X</i>, and a reader supplies
    /// the missing words themselves — wrongly, when the index is pointed somewhere else. That is the #157
    /// class in our own tool surface: a plausible answer to a question nobody asked. An agent hitting this
    /// had to run <c>get_stats</c> AND <c>orient</c> and reason from node types to work out it was querying a
    /// different repository; the node count is what finally gave it away, so it belongs where it is free.
    /// </para>
    /// <para>
    /// Never throws: a scope note that can fail would turn a working search into an error, which is a worse
    /// trade than an unlabelled result.
    /// </para>
    /// </summary>
    public async Task<string> ScopeSuffixAsync(IGraphStore storage, string? projectName, CancellationToken ct = default)
    {
        try
        {
            var name = ResolveProjectName(projectName);
            if (string.IsNullOrWhiteSpace(name)) name = ProjectManager.GetActiveProjectName();
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;

            var stats = await storage.GetStatisticsAsync(ct).ConfigureAwait(false);
            return $" in project '{name}' ({stats.TotalNodes:N0} nodes)";
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<string> StaleSuffixAsync(IGraphStore storage, GraphNode def, CancellationToken ct = default)
    {
        if (FileParsers == null || string.IsNullOrEmpty(def.FilePath)) return string.Empty;
        var stored = await storage.GetContentHashesAsync(new[] { def.FilePath }, ct).ConfigureAwait(false);
        if (!stored.TryGetValue(def.FilePath, out var storedHash) || string.IsNullOrEmpty(storedHash)) return string.Empty;
        try
        {
            if (!System.IO.File.Exists(def.FilePath))
                return $"\n⚠ '{def.Name}' is in the graph but its file is GONE from disk — run reindex_file.";
            var content = await Shonkor.Core.Services.SourceText.ReadAsync(def.FilePath, ct).ConfigureAwait(false);
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            if (hash != storedHash)
                return $"\n⚠ '{def.Name}' file was EDITED since indexing — this result may be stale; run reindex_file first.";
        }
        catch { /* unreadable right now — don't annotate */ }
        return string.Empty;
    }

    /// <summary>
    /// Shared body for <c>references</c> at depth 1 (incoming = used_by, outgoing = uses): resolves the
    /// symbol, keeps the edges incident to it in the requested direction, and renders them grouped by
    /// relationship. <paramref name="emptyMessage"/> is a format string with {0}=name and {1}=type.
    /// </summary>
    public async Task<string> EdgeReportAsync(
        IGraphStorageProvider storage, string? projectName, string symbol, bool incoming, string verb, string emptyMessage,
        Provenance? maxProvenance = null)
    {
        var basePath = GetProjectBasePath(projectName);
        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
        if (def == null) return $"No definition found for '{symbol}'.";

        var (edges, neighbours) = await storage.GetIncidentEdgesAsync(def.Id).ConfigureAwait(false);
        var incident = edges
            .Where(e => !StructuralRelationships.Contains(e.Relationship)
                        && PassesProvenance(e.Provenance, maxProvenance)
                        && (incoming ? e.TargetId == def.Id && e.SourceId != def.Id
                                     : e.SourceId == def.Id && e.TargetId != def.Id))
            .ToList();

        var stale = await StaleSuffixAsync(storage, def).ConfigureAwait(false);

        if (incident.Count == 0)
        {
            // #288 (Option 3): never emit an all-clear for a node type that structurally cannot carry the
            // queried edge — point the caller at the node that can, instead of "safe to change in isolation".
            var hint = EdgeCarrierRedirectHint(def);
            if (!string.IsNullOrEmpty(hint))
                return $"No {(incoming ? "inbound references to" : "outbound dependencies of")} '{def.Name}' ({def.Type})." + hint + stale;
            return string.Format(emptyMessage, def.Name, def.Type) + stale;
        }

        var filterNote = maxProvenance is { } mp ? $" (provenance ≤ {mp.ToString().ToLowerInvariant()})" : "";
        var sb = new System.Text.StringBuilder();
        sb.Append($"'{def.Name}' ({def.Type}) {verb} {incident.Count} node(s){filterNote}:\n");
        foreach (var g in incident.GroupBy(e => e.Relationship).OrderByDescending(g => g.Count()))
        {
            foreach (var e in g)
            {
                var otherId = incoming ? e.SourceId : e.TargetId;
                var other = neighbours.GetValueOrDefault(otherId);
                var name = other?.Name ?? otherId;
                var summary = other != null && !string.IsNullOrEmpty(other.Summary) ? $"  — {other.Summary}" : "";
                sb.Append($"{g.Key}\t{name}\t{ToHandle(otherId, basePath)} {ProvenanceTag(e.Provenance)}{summary}\n");
            }
        }
        return sb.ToString().TrimEnd() + stale;
    }

    /// <summary>
    /// Shared implementation for the <c>record</c> tool: creates a typed knowledge node, links it to the
    /// supplied <paramref name="connectedNodeIds"/> via <paramref name="edgeRelationship"/>, persists both,
    /// and returns the JSON-RPC tool response built by <paramref name="messageFactory"/>.
    /// </summary>
    /// <summary>Max stored length for a recorded node's free-text content — bounds the persistent write.</summary>
    public const int MaxRecordContentChars = 8 * 1024;

    /// <summary>Max stored length for a recorded node's name (rendered in listings; kept short).</summary>
    public const int MaxRecordNameChars = 512;

    public async Task<string> RecordNodeAsync(
        System.Text.Json.JsonElement id,
        IGraphStorageProvider storage,
        string idPrefix,
        string nodeType,
        string name,
        string content,
        Dictionary<string, string> properties,
        JsonArray? connectedNodeIds,
        string edgeRelationship,
        Func<string, int, string> messageFactory)
    {
        var nodeId = $"{idPrefix}::{Guid.NewGuid().ToString("N")[..8]}";

        // record is a cross-session, persistent write channel: bound the stored content and name so a
        // single call can't balloon the graph (unbounded write) and can't smuggle an oversized payload.
        var boundedContent = Truncate(content, MaxRecordContentChars);
        var boundedName = Truncate(name, MaxRecordNameChars);

        var node = new GraphNode
        {
            Id = nodeId,
            Name = boundedName,
            Type = nodeType,
            Content = boundedContent,
            Properties = properties
        };
        await storage.UpsertNodesAsync(new[] { node }).ConfigureAwait(false);

        // Only link to nodes that actually EXIST: an unverified connectedNodeId would create a dangling
        // edge (an injected relationship to a node id the caller never proved is real). Non-existent ids
        // are dropped and reported rather than silently persisted.
        var edges = new List<GraphEdge>();
        var skipped = 0;
        if (connectedNodeIds != null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var connectedNode in connectedNodeIds)
            {
                var connId = connectedNode?.ToString();
                if (string.IsNullOrEmpty(connId) || !seen.Add(connId)) continue;
                var exists = await storage.GetNodeByIdAsync(connId).ConfigureAwait(false);
                if (exists is null) { skipped++; continue; }
                edges.Add(new GraphEdge
                {
                    SourceId = connId,
                    TargetId = nodeId,
                    Relationship = edgeRelationship
                });
            }
        }

        if (edges.Count > 0)
        {
            await storage.UpsertEdgesAsync(edges).ConfigureAwait(false);
        }

        var message = messageFactory(nodeId, edges.Count);
        if (skipped > 0)
        {
            message += $" ({skipped} connected id(s) skipped — no such node in the graph; no dangling edge created.)";
        }
        return SendToolResponse(id, message);
    }

    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars) return value ?? string.Empty;
        return value[..maxChars] + "… [truncated]";
    }
}
