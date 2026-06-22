// Licensed to Shonkor under the MIT License.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;

using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Services.Mcp;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// A lightweight, robust implementation of a Model Context Protocol (MCP) server 
/// communicating over standard input/output (stdio) using JSON-RPC.
/// Exposes high-precision GraphRAG tools to AI assistants like Claude Code and Antigravity.
/// </summary>
public sealed class McpRequestHandler
{
    private readonly ProjectManager _projectManager;
    private readonly ContextCapsuleSynthesizer _synthesizer;

    /// <summary>
    /// Optional embedding backend for semantic (vector) search. Available in the web/HTTP relay (Ollama
    /// wired via DI); null in the stdio CLI, where <c>search_semantic</c> degrades gracefully.
    /// </summary>
    private readonly IEmbeddingService? _embeddingService;

    /// <summary>
    /// Optional file parsers for single-file re-indexing (<c>reindex_file</c>). Available where the MCP
    /// server has filesystem access to the project (local stdio CLI / dev relay); null otherwise, where
    /// the tool degrades gracefully.
    /// </summary>
    private readonly IEnumerable<IFileParser>? _fileParsers;

    /// <summary>
    /// The project this server is bound to, derived from the working directory the MCP was launched in
    /// (the AI chat's directory) — NOT from the shared, web-mutable ActiveProjectName. May be null if the
    /// launch directory doesn't match any registered project, in which case the global active is used.
    /// </summary>
    private readonly string? _contextProjectName;

    /// <summary>
    /// When true, the session is hard-bound to <see cref="_contextProjectName"/> and the per-tool
    /// <c>projectName</c> argument is ignored. Set for authenticated multi-tenant (SaaS) requests so a
    /// caller authorized for tenant A cannot reach tenant B's graph by passing a different projectName.
    /// Left false for the local/stdio CLI and the trusted-local dev bypass, where free project switching
    /// is intentional.
    /// </summary>
    private readonly bool _lockToContextProject;

    /// <summary>
    /// The shared tool context (services + session state + stateful helpers) handed to every migrated
    /// <see cref="IMcpTool"/>. The remaining switch-based tools delegate their project resolution here too,
    /// so the session's set_project override is single-sourced.
    /// </summary>
    private readonly McpToolContext _ctx;

    /// <summary>The registry of migrated tool classes; consulted before the legacy switch in tools/call.</summary>
    private readonly McpToolRegistry _registry;

    /// <summary>Resolves the project name to use for a call (delegates to the shared context).</summary>
    private string? ResolveProjectName(string? projectName) => _ctx.ResolveProjectName(projectName);

    private Task<IGraphStorageProvider> GetStorageAsync(string? projectName)
    {
        var effective = ResolveProjectName(projectName);
        // Async resolution so the HTTP relay path doesn't block a thread on first-time schema init.
        return string.IsNullOrWhiteSpace(effective)
            ? _projectManager.GetActiveStorageProviderAsync()
            : _projectManager.GetStorageProviderAsync(effective);
    }

    /// <summary>Resolves the filesystem root of the requested (or context) project, for path shortening.</summary>
    private string GetProjectBasePath(string? projectName)
    {
        try
        {
            var name = ResolveProjectName(projectName) ?? _projectManager.GetActiveProjectName();
            var project = _projectManager.GetProjects().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return project?.Path ?? _projectManager.WorkspacePath;
        }
        catch
        {
            return _projectManager.WorkspacePath;
        }
    }

    /// <summary>
    /// Whether the EFFECTIVE project for this call (resolved via <see cref="ResolveProjectName"/>, not the
    /// raw arg — which is null when the project comes from the relay header) opts into semantic indexing,
    /// and a compilation cache is wired. Drives the cached semantic relink / semantic compile in
    /// reindex_file, check_edit and review.
    /// </summary>
    private bool IsSemanticProject(string? projectName)
    {
        if (_compilationCache is null) return false;
        var name = ResolveProjectName(projectName) ?? _projectManager.GetActiveProjectName();
        return _projectManager.GetProjects()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.SemanticCSharp == true;
    }

    /// <summary>How many candidate hits the symbol-oriented tools pull before applying their selection heuristic.</summary>
    private const int SymbolSearchLimit = 8;

    /// <summary>MCP protocol revision used as a fallback when the client doesn't request one in <c>initialize</c>.</summary>
    private const string DefaultProtocolVersion = "2025-06-18";

    /// <summary>
    /// Server version reported in the <c>initialize</c> handshake, read from the running assembly's
    /// informational version (set repo-wide in Directory.Build.props) — the single source of truth.
    /// The <c>+commithash</c> suffix, if any, is trimmed.
    /// </summary>
    private static readonly string ServerVersion =
        (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(McpRequestHandler).Assembly)
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    /// <summary>Node types that count as a "declaration" when resolving a symbol to its definition.</summary>
    private static readonly string[] DeclarationTypes =
        { "Class", "Interface", "Record", "Struct", "Enum", "Method", "Property", "Constructor" };

    /// <summary>The single source of truth for "this node IS the symbol" (case-insensitive name equality).</summary>
    private static bool IsExactNameMatch(GraphNode node, string symbol) =>
        string.Equals(node.Name, symbol, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the target symbol for symbol-oriented tools, accepting <c>query</c> as a lenient alias of <c>symbol</c>.</summary>
    private static string? ReadSymbol(JsonNode? args) =>
        args?["symbol"]?.ToString() ?? args?["query"]?.ToString();

    /// <summary>
    /// Fallback for <c>get_source</c> on nodes that store no body (e.g. type declarations): reads the
    /// exact line range from the file when this server has filesystem access (local; proxied by
    /// <see cref="_fileParsers"/> being wired). Returns <c>null</c> if unavailable. Lines are 0-based, as
    /// stored by the parser; without an EndLine a bounded window is read.
    /// </summary>
    private string? TryReadSourceSlice(GraphNode node)
    {
        if (_fileParsers == null || string.IsNullOrEmpty(node.FilePath) || !node.StartLine.HasValue) return null;
        if (!System.IO.File.Exists(node.FilePath)) return null;
        try
        {
            var lines = System.IO.File.ReadAllLines(node.FilePath);
            if (lines.Length == 0) return null;
            var start = Math.Clamp(node.StartLine.Value, 0, lines.Length - 1);
            var end = node.EndLine.HasValue
                ? Math.Clamp(node.EndLine.Value, start, lines.Length - 1)
                : Math.Min(start + 40, lines.Length - 1);
            return string.Join("\n", lines[start..(end + 1)]);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a compact signature line for a node from its stored properties (no body): a method/
    /// constructor as <c>modifiers returnType Name(params)</c>, a property as <c>modifiers type Name</c>,
    /// and a type as <c>modifiers kind Name</c>.
    /// </summary>
    private static string BuildSignature(GraphNode node)
    {
        var p = node.Properties;
        var mods = p.GetValueOrDefault("modifiers", string.Empty).Trim();
        string Compose(params string?[] parts) =>
            string.Join(" ", parts.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));

        switch (node.Type)
        {
            case "Method":
            case "Constructor":
                return Compose(mods, p.GetValueOrDefault("returnType", ""), $"{node.Name}({p.GetValueOrDefault("parameters", "").Trim()})");
            case "Property":
                return Compose(mods, p.GetValueOrDefault("returnType", ""), node.Name);
            default:
                return Compose(mods, node.Type.ToLowerInvariant(), node.Name);
        }
    }

    /// <summary>
    /// Heuristic for <c>related_tests</c>: whether a file path looks like a test file across common
    /// ecosystems (xUnit/NUnit <c>*.Tests</c>, Go <c>_test</c>, Python <c>test_</c>, JS <c>.spec.</c>/
    /// <c>.test.</c>/<c>__tests__</c>).
    /// </summary>
    private static bool LooksLikeTest(string? filePath)
    {
        var p = (filePath ?? string.Empty).ToLowerInvariant();
        return p.Contains("test") || p.Contains(".spec.") || p.Contains("__tests__") || p.Contains("/spec/") || p.Contains("\\spec\\");
    }

    /// <summary>
    /// Returns a one-line "may be stale" warning to append to an analysis result when the resolved symbol's
    /// FILE changed on disk since indexing (so the result may be wrong), or "" when fresh / untracked / no
    /// filesystem access. One file hash per call (bounded), local only — so trust scales: the agent never
    /// silently builds on stale graph data.
    /// </summary>
    private async Task<string> StaleSuffixAsync(IGraphStore storage, GraphNode def, CancellationToken ct = default)
    {
        if (_fileParsers == null || string.IsNullOrEmpty(def.FilePath)) return string.Empty;
        var stored = await storage.GetContentHashesAsync(new[] { def.FilePath }, ct).ConfigureAwait(false);
        if (!stored.TryGetValue(def.FilePath, out var storedHash) || string.IsNullOrEmpty(storedHash)) return string.Empty;
        try
        {
            if (!System.IO.File.Exists(def.FilePath))
                return $"\n⚠ '{def.Name}' is in the graph but its file is GONE from disk — run reindex_file.";
            var content = await System.IO.File.ReadAllTextAsync(def.FilePath, ct).ConfigureAwait(false);
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            if (hash != storedHash)
                return $"\n⚠ '{def.Name}' file was EDITED since indexing — this result may be stale; run reindex_file first.";
        }
        catch { /* unreadable right now — don't annotate */ }
        return string.Empty;
    }

    /// <summary>
    /// Returns the first non-empty line of <paramref name="content"/> that mentions <paramref name="name"/>
    /// (trimmed, capped at 160 chars), or <c>null</c> — a grep-like usage snippet for find_usages.
    /// </summary>
    private static string? FirstLineMentioning(string? content, string name)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(name)) return null;
        foreach (var rawLine in content.Split('\n'))
        {
            if (rawLine.Contains(name, StringComparison.Ordinal))
            {
                var trimmed = rawLine.Trim();
                return trimmed.Length > 160 ? trimmed[..160] + "…" : trimmed;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves a free-text symbol to its best-matching definition node: prefers an exact-name declaration
    /// (Class/Method/…), then any exact-name node, then the first declaration, then the first hit. Returns
    /// <c>null</c> when nothing matches.
    /// </summary>
    private static async Task<GraphNode?> ResolveDefinitionAsync(IGraphStorageProvider storage, string symbol)
    {
        var hits = (await storage.SearchAsync(symbol, SymbolSearchLimit).ConfigureAwait(false)).Select(h => h.Node).ToList();
        return hits.FirstOrDefault(n => IsExactNameMatch(n, symbol) && DeclarationTypes.Contains(n.Type))
            ?? hits.FirstOrDefault(n => IsExactNameMatch(n, symbol))
            ?? hits.FirstOrDefault(n => DeclarationTypes.Contains(n.Type))
            ?? hits.FirstOrDefault();
    }

    /// <summary>
    /// Reads an integer tool argument tolerantly: accepts a JSON number (int or float) or a numeric
    /// string, returning <paramref name="fallback"/> for null/missing/non-numeric input instead of
    /// throwing. Keeps a mistyped client argument (e.g. <c>"5"</c> or <c>5.0</c>) from surfacing as a
    /// generic <c>-32603</c> internal error.
    /// </summary>
    private static int ReadInt(JsonNode? value, int fallback)
    {
        if (value is null) return fallback;
        try
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.Number => (int)value.GetValue<double>(),
                JsonValueKind.String => int.TryParse(value.GetValue<string>(), out var s) ? s : fallback,
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Shared body for <c>references</c> at depth 1 (incoming = used_by, outgoing = uses):
    /// resolves the symbol, pulls its 1-hop neighbourhood, keeps the edges incident to it
    /// in the requested direction, and renders them grouped by relationship as
    /// <c>relation\tname\thandle  — summary</c>. <paramref name="emptyMessage"/> is a format string with
    /// <c>{0}</c>=name and <c>{1}</c>=type.
    /// </summary>
    /// <summary>
    /// Containment/grouping edges that are structure, not semantic impact or dependency: a type's parent
    /// file, a method's parent type, or a node's Helix module. Excluded from references/find_usages so a
    /// method's real impact (its CALLS / REFERENCES_TYPE) isn't drowned by its enclosing type.
    /// </summary>
    private static readonly HashSet<string> StructuralRelationships = new(StringComparer.Ordinal)
    {
        "CONTAINS", "BELONGS_TO_MODULE"
    };

    private async Task<string> EdgeReportAsync(
        IGraphStorageProvider storage, string? projectName, string symbol, bool incoming, string verb, string emptyMessage)
    {
        var basePath = GetProjectBasePath(projectName);
        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
        if (def == null) return $"No definition found for '{symbol}'.";

        // Fetch only the symbol's own edges (+ their endpoint nodes), not its whole 1-hop neighbourhood.
        var (edges, neighbours) = await storage.GetIncidentEdgesAsync(def.Id).ConfigureAwait(false);
        // incoming: edges POINTING AT def (its dependents); outgoing: edges ORIGINATING at def (its dependencies).
        // Structural containment edges are filtered out — they're not impact/dependency.
        var incident = edges
            .Where(e => !StructuralRelationships.Contains(e.Relationship)
                        && (incoming ? e.TargetId == def.Id && e.SourceId != def.Id
                                     : e.SourceId == def.Id && e.TargetId != def.Id))
            .ToList();

        var stale = await StaleSuffixAsync(storage, def).ConfigureAwait(false);

        if (incident.Count == 0) return string.Format(emptyMessage, def.Name, def.Type) + stale;

        var sb = new System.Text.StringBuilder();
        sb.Append($"'{def.Name}' ({def.Type}) {verb} {incident.Count} node(s):\n");
        foreach (var g in incident.GroupBy(e => e.Relationship).OrderByDescending(g => g.Count()))
        {
            foreach (var e in g)
            {
                var otherId = incoming ? e.SourceId : e.TargetId;
                var other = neighbours.GetValueOrDefault(otherId);
                var name = other?.Name ?? otherId;
                var summary = other != null && !string.IsNullOrEmpty(other.Summary) ? $"  — {other.Summary}" : "";
                sb.Append($"{g.Key}\t{name}\t{ToHandle(otherId, basePath)}{summary}\n");
            }
        }
        return sb.ToString().TrimEnd() + stale;
    }

    /// <summary>Returns <paramref name="path"/> relative to <paramref name="basePath"/> when contained, else the original.</summary>
    private static string Shorten(string? path, string basePath)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        if (!string.IsNullOrEmpty(basePath) && path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            var rel = path[basePath.Length..].TrimStart('\\', '/');
            return rel.Length > 0 ? rel : path;
        }
        return path;
    }

    /// <summary>
    /// Shortens a node id to a reusable, token-cheap "handle". File-path ids under the project root are
    /// emitted as <c>@/&lt;relative&gt;</c>; other ids (<c>decision::</c>, <c>concept_</c>, …) are left
    /// unchanged. The <c>@/</c> marker makes the transform reversible via <see cref="FromHandle"/> and
    /// unambiguous — real ids never start with <c>@/</c> — so a handle can be passed straight back as a seed.
    /// </summary>
    private static string ToHandle(string? id, string basePath)
    {
        if (string.IsNullOrEmpty(id)) return string.Empty;
        if (!string.IsNullOrEmpty(basePath) && id.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            var rel = id[basePath.Length..].TrimStart('\\', '/');
            if (rel.Length > 0) return "@/" + rel;
        }
        return id;
    }

    /// <summary>Expands a <c>@/&lt;relative&gt;</c> handle back to a real node id; other values pass through unchanged.</summary>
    private static string FromHandle(string? handle, string basePath)
    {
        if (string.IsNullOrEmpty(handle)) return string.Empty;
        if (handle.StartsWith("@/", StringComparison.Ordinal) && !string.IsNullOrEmpty(basePath))
        {
            return basePath.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar + handle[2..];
        }
        return handle;
    }

    /// <summary>
    /// Truncates markdown to at most <paramref name="maxChars"/> characters, preferring to cut at the
    /// last Markdown heading (## ) boundary before the limit so sections stay intact. Appends a notice.
    /// </summary>
    private static string TruncateAtBoundary(string markdown, int maxChars)
    {
        if (markdown.Length <= maxChars) return markdown;

        var slice = markdown[..maxChars];
        var boundary = slice.LastIndexOf("\n## ", StringComparison.Ordinal);
        if (boundary <= 0)
        {
            boundary = slice.LastIndexOf('\n');
        }
        if (boundary > 0)
        {
            slice = slice[..boundary];
        }

        return slice.TrimEnd() + "\n\n> [!NOTE]\n> Capsule truncated to fit the requested character budget. Increase maxChars or narrow the query for more detail.";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="McpRequestHandler"/> class.
    /// </summary>
    /// <param name="projectManager">The global project manager.</param>
    /// <param name="synthesizer">The context capsule synthesizer.</param>
    /// <param name="contextProjectName">
    /// The project bound to this server's working directory (the AI chat's directory). When set, it is used
    /// as the default for all tool calls that don't pass an explicit projectName, decoupling the MCP from
    /// the web dashboard's mutable active-project flag.
    /// </param>
    /// <summary>Shared compilation cache so a semantic-project <c>reindex_file</c> refreshes CALLS incrementally (swap one tree) instead of rebuilding the compilation per edit. Null = no caching (reindex_file stays name-mode).</summary>
    private readonly SemanticCompilationCache? _compilationCache;

    public McpRequestHandler(ProjectManager projectManager, ContextCapsuleSynthesizer synthesizer, string? contextProjectName = null, bool lockToContextProject = false, IEmbeddingService? embeddingService = null, IEnumerable<IFileParser>? fileParsers = null, SemanticCompilationCache? compilationCache = null)
    {
        _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _contextProjectName = contextProjectName;
        _lockToContextProject = lockToContextProject;
        _embeddingService = embeddingService;
        _fileParsers = fileParsers;
        _compilationCache = compilationCache;

        _ctx = new McpToolContext(projectManager, synthesizer, contextProjectName, lockToContextProject,
            embeddingService, fileParsers, compilationCache);
        _registry = new McpToolRegistry(McpToolRegistryFactory.CreateTools());
    }

    /// <summary>
    /// Starts the MCP JSON-RPC standard input/output listening loop.
    /// Runs until the input stream is closed (EOF).
    /// </summary>
    public async Task StartAsync()
    {
        // Set console encoding to UTF-8 to correctly handle special characters and emojis
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        using var reader = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
        
        while (true)
        {
            try
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    break; // EOF
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var response = await ProcessJsonRpcMessageAsync(line).ConfigureAwait(false);
                if (response != null)
                {
                    Console.WriteLine(response);
                }
            }
            catch (Exception ex)
            {
                // We must log errors to stderr because standard out is reserved strictly for JSON-RPC messages!
                Console.Error.WriteLine($"[MCP Error] {ex.Message}");
            }
        }
    }

    public async Task<string?> ProcessJsonRpcMessageAsync(string json)
    {
        JsonNode? idNode = null;
        try
        {
            var document = JsonNode.Parse(json);
            if (document is not JsonObject obj)
            {
                return null;
            }

            var method = obj["method"]?.ToString();
            idNode = obj["id"];

            // JSON-RPC 2.0 notifications have NO "id" key at all (key absent from the object).
            // A request with "id": null is a valid (if unusual) request and expects a response.
            // JsonNode returns null both for a missing key AND for an explicit JSON null value —
            // we disambiguate by checking whether the key is actually present in the object.
            if (!obj.ContainsKey("id"))
            {
                // True notification — no response should be sent.
                return null;
            }

            // "id": null is an unusual but valid JSON-RPC id; idNode is null for an explicit JSON null,
            // so fall back to a JSON-null element rather than dereferencing it.
            var id = idNode?.GetValue<JsonElement>() ?? NullJsonElement;

            switch (method)
            {
                case "initialize":
                    // Echo the client's requested protocol revision (the spec's negotiation rule),
                    // falling back to our default when the client omits it.
                    var requestedProto = obj["params"]?["protocolVersion"]?.ToString();
                    return SendResponse(id, new
                    {
                        protocolVersion = string.IsNullOrWhiteSpace(requestedProto) ? DefaultProtocolVersion : requestedProto,
                        capabilities = new
                        {
                        tools = new {}
                    },
                    serverInfo = new
                    {
                        name = "Shonkor MCP Server",
                        version = ServerVersion
                    }
                });

            case "tools/list":
                {
                    var toolDefs = new List<object>
                    {
                        new
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
                        },
                        new
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
                        },
                        new
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
                        },
                        new
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
                        },
                        new
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
                        },
                        new
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
                        },
                        new
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
                        },
                        new
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
                        },
                        new
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
                        },
                    };

                    // Append schemas for tools already migrated into the registry (capability-filtered;
                    // e.g. search_semantic only appears when an embedding backend is wired).
                    toolDefs.AddRange(_registry.GetSchemas(_ctx));

                    return SendResponse(id, new { tools = toolDefs });
                }

            case "tools/call":
                var toolName = obj["params"]?["name"]?.ToString();
                var args = obj["params"]?["arguments"] as JsonObject;
                return await HandleToolCallAsync(id, toolName, args).ConfigureAwait(false);

            default:
                return SendError(id, -32601, $"Method not found: '{method}'");
        }
        }
        catch (Exception ex)
        {
            if (idNode != null)
            {
                try
                {
                    return SendError(idNode.GetValue<JsonElement>(), -32603, $"Internal Error: {ex.Message}");
                }
                catch
                {
                    // Ignore error serialization issues to prevent recursive crashes
                }
            }
            Console.Error.WriteLine($"[MCP Internal Error] {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    public async Task<string> HandleToolCallAsync(JsonElement id, string? toolName, JsonObject? args)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return SendError(id, -32602, "Missing parameter: 'name'");
        }

        try
        {
            // Migrated tools live in the registry; the switch below is the shrinking fallback.
            // IsAvailable gates only tools/list advertising — a tool that is called anyway still runs and
            // returns its own graceful "unavailable" message rather than a generic tool-not-found error.
            var migrated = _registry.Find(toolName);
            if (migrated != null)
            {
                return await migrated.ExecuteAsync(id, args, _ctx).ConfigureAwait(false);
            }

            var projectName = args?["projectName"]?.ToString();
            var storage = await GetStorageAsync(projectName).ConfigureAwait(false);

            switch (toolName)
            {

                case "reindex_file":
                    {
                        var rawPath = args?["path"]?.ToString();
                        if (string.IsNullOrWhiteSpace(rawPath))
                        {
                            return SendError(id, -32602, "Parameter 'path' is required");
                        }
                        if (_fileParsers == null)
                        {
                            return SendToolResponse(id,
                                "reindex_file is unavailable here (no parsers / filesystem access). Run via the local MCP server in the project directory.");
                        }

                        var basePath = GetProjectBasePath(projectName);
                        var resolved = FromHandle(rawPath, basePath);
                        if (!System.IO.Path.IsPathRooted(resolved))
                        {
                            resolved = System.IO.Path.Combine(basePath, resolved);
                        }

                        // When the project opts into semantic indexing, route through the cached reconcile so
                        // CALLS / exact REFERENCES_TYPE refresh incrementally (swap one tree) — not just the
                        // fast name-mode structure. Falls back to the plain single-file path otherwise.
                        var semantic = IsSemanticProject(projectName);

                        var scanner = new GraphIndexScanner(storage, _fileParsers, semanticCsharp: semantic, compilationCache: _compilationCache);
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

                case "check_edit":
                    {
                        var rawPath = args?["path"]?.ToString();
                        if (string.IsNullOrWhiteSpace(rawPath))
                        {
                            return SendError(id, -32602, "Parameter 'path' is required");
                        }
                        if (_fileParsers == null)
                        {
                            return SendToolResponse(id,
                                "check_edit is unavailable here (no filesystem access). Run via the local MCP server in the project directory.");
                        }

                        var basePath = GetProjectBasePath(projectName);
                        var resolved = FromHandle(rawPath, basePath);
                        if (!System.IO.Path.IsPathRooted(resolved))
                        {
                            resolved = System.IO.Path.Combine(basePath, resolved);
                        }
                        resolved = System.IO.Path.GetFullPath(resolved);

                        if (!resolved.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        {
                            return SendToolResponse(id, "check_edit currently supports C# (.cs) files only.");
                        }
                        if (!System.IO.File.Exists(resolved))
                        {
                            return SendToolResponse(id, $"File not found on disk: {ToHandle(resolved, basePath)}.");
                        }

                        var content = await System.IO.File.ReadAllTextAsync(resolved).ConfigureAwait(false);

                        // Semantic checks only for a semantic project (and when the cache is wired); otherwise
                        // syntax-only, which is always reliable and the most common edit breakage.
                        Microsoft.CodeAnalysis.CSharp.CSharpCompilation? compilation = null;
                        var semantic = IsSemanticProject(projectName);
                        if (semantic)
                        {
                            compilation = await _compilationCache!.ApplyEditsAsync(basePath, new[] { resolved }).ConfigureAwait(false);
                        }

                        var report = CSharpDiagnostics.Report(resolved, content, compilation);
                        return SendToolResponse(id, report);
                    }

                case "freshness":
                    {
                        if (_fileParsers == null)
                        {
                            return SendToolResponse(id,
                                "freshness is unavailable here (no filesystem access). Run via the local MCP server in the project directory.");
                        }

                        var rawPath = args?["path"]?.ToString();

                        // With a path → single-file freshness check.
                        if (!string.IsNullOrWhiteSpace(rawPath))
                        {
                            var fileBase = GetProjectBasePath(projectName);
                            var resolved = FromHandle(rawPath, fileBase);
                            if (!System.IO.Path.IsPathRooted(resolved))
                            {
                                resolved = System.IO.Path.Combine(fileBase, resolved);
                            }

                            var fileScanner = new GraphIndexScanner(storage, _fileParsers);
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
                        var basePath = GetProjectBasePath(projectName);
                        var resolvedName = ResolveProjectName(projectName) ?? _projectManager.GetActiveProjectName();
                        var excludePatterns = _projectManager.GetProjectConfig(resolvedName).ExcludePatterns;

                        var scanner = new GraphIndexScanner(storage, _fileParsers);
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

                case "related_tests":
                    {
                        var symbol = ReadSymbol(args);
                        if (string.IsNullOrWhiteSpace(symbol))
                        {
                            return SendError(id, -32602, "Parameter 'symbol' is required");
                        }
                        var basePath = GetProjectBasePath(projectName);

                        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
                        if (def == null)
                        {
                            return SendToolResponse(id, $"No definition found for '{symbol}'.");
                        }

                        var depth = Math.Clamp(ReadInt(args?["depth"], 3), 1, 6);

                        // Transitive test impact: BFS over INCOMING impact edges (a test that calls A which
                        // calls the changed method covers it indirectly). Traverse THROUGH every node, but
                        // collect only test-file nodes, recording the shortest hop distance to each.
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
                        return SendToolResponse(id, sb.ToString().TrimEnd() + await StaleSuffixAsync(storage, def).ConfigureAwait(false));
                    }

                case "edit_plan":
                    {
                        var symbol = ReadSymbol(args);
                        if (string.IsNullOrWhiteSpace(symbol))
                        {
                            return SendError(id, -32602, "Parameter 'symbol' is required");
                        }
                        var basePath = GetProjectBasePath(projectName);

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

                case "rename_plan":
                    {
                        var symbol = ReadSymbol(args);
                        if (string.IsNullOrWhiteSpace(symbol))
                        {
                            return SendError(id, -32602, "Parameter 'symbol' is required");
                        }
                        var newName = args?["new_name"]?.ToString();
                        var basePath = GetProjectBasePath(projectName);

                        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
                        if (def == null)
                        {
                            return SendToolResponse(id, $"No definition found for '{symbol}'.");
                        }

                        // How many OTHER symbols share this name? A text find/replace would wrongly hit them;
                        // this plan uses the graph's exact edges, which point only at this node.
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
                        return SendToolResponse(id, sb.ToString() + await StaleSuffixAsync(storage, def).ConfigureAwait(false));
                    }

                case "review":
                    {
                        var basePath = GetProjectBasePath(projectName);
                        var rawPaths = (args?["paths"] as JsonArray)?.Select(x => x?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                                       ?? new List<string?>();
                        var single = args?["path"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(single)) rawPaths.Add(single);
                        if (rawPaths.Count == 0)
                        {
                            return SendError(id, -32602, "Provide the changed files via 'paths' (array) or 'path'.");
                        }

                        var fullPaths = rawPaths
                            .Select(p => { var r = FromHandle(p!, basePath); return System.IO.Path.IsPathRooted(r) ? System.IO.Path.GetFullPath(r) : System.IO.Path.GetFullPath(System.IO.Path.Combine(basePath, r)); })
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var changedFiles = new HashSet<string>(fullPaths, StringComparer.OrdinalIgnoreCase);
                        var depth = Math.Clamp(ReadInt(args?["depth"], 3), 1, 6);

                        var impactTypes = new HashSet<string>(StringComparer.Ordinal)
                        { "Class", "Interface", "Record", "Struct", "Enum", "Method", "Constructor", "Property" };
                        var semanticImpact = new HashSet<string>(StringComparer.Ordinal) { "REFERENCES_TYPE", "IMPLEMENTS", "EXTENDS", "CALLS" };
                        var semanticProject = IsSemanticProject(projectName);

                        var compileLines = new List<string>();
                        var changedDefs = new List<GraphNode>();
                        var compileErrorFiles = 0;
                        foreach (var full in fullPaths)
                        {
                            foreach (var n in await storage.GetNodesByFilePathAsync(full).ConfigureAwait(false))
                                if (impactTypes.Contains(n.Type)) changedDefs.Add(n);

                            var rel = Shorten(full, basePath);
                            if (_fileParsers != null && full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(full))
                            {
                                var content = await System.IO.File.ReadAllTextAsync(full).ConfigureAwait(false);
                                Microsoft.CodeAnalysis.CSharp.CSharpCompilation? comp = semanticProject
                                    ? await _compilationCache!.ApplyEditsAsync(basePath, new[] { full }).ConfigureAwait(false)
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

                case "set_project":
                    {
                        if (_lockToContextProject)
                        {
                            return SendToolResponse(id, "This server is locked to a single tenant; project switching is disabled here.");
                        }
                        var projects = _projectManager.GetProjects();
                        // The effective project for THIS session: the session override if set, else the context/active.
                        var active = _ctx.SessionProjectOverride ?? _contextProjectName ?? _projectManager.GetActiveProjectName();
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
                        _ctx.SessionProjectOverride = match.Name;
                        var newStorage = await _projectManager.GetStorageProviderAsync(match.Name).ConfigureAwait(false);
                        var newStats = await newStorage.GetStatisticsAsync().ConfigureAwait(false);
                        return SendToolResponse(id, $"Active project for this session is now '{match.Name}' ({match.Path}) — {newStats.TotalNodes} nodes, {newStats.TotalEdges} edges. Call orient for the workflow.");
                    }

                case "orient":
                    {
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
                        var semanticNote = _embeddingService != null ? " · search_semantic" : string.Empty;

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

                default:
                    return SendError(id, -32601, $"Tool not found: '{toolName}'");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP Tool Error] {ex.Message}\n{ex.StackTrace}");
            return SendError(id, -32603, $"Internal tool execution error: {ex.Message}");
        }
    }

    /// <summary>A reusable JSON-null element, used to echo back an explicit <c>"id": null</c> JSON-RPC id.</summary>
    private static readonly JsonElement NullJsonElement = JsonSerializer.SerializeToElement<object?>(null);

    private static string UtcNow() => DateTime.UtcNow.ToString("o");

    /// <summary>
    /// Shared implementation for the <c>record_*</c> tools: creates a typed knowledge node, links it
    /// to the supplied <paramref name="connectedNodeIds"/> via <paramref name="edgeRelationship"/>,
    /// persists both, and returns the JSON-RPC tool response built by <paramref name="messageFactory"/>.
    /// </summary>
    /// <param name="messageFactory">Builds the success message from the new node id and the edge count.</param>
    private async Task<string> RecordNodeAsync(
        JsonElement id,
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

        var node = new GraphNode
        {
            Id = nodeId,
            Name = name,
            Type = nodeType,
            Content = content,
            Properties = properties
        };
        await storage.UpsertNodesAsync(new[] { node }).ConfigureAwait(false);

        var edges = new List<GraphEdge>();
        if (connectedNodeIds != null)
        {
            foreach (var connectedNode in connectedNodeIds)
            {
                var connId = connectedNode?.ToString();
                if (!string.IsNullOrEmpty(connId))
                {
                    edges.Add(new GraphEdge
                    {
                        SourceId = connId,
                        TargetId = nodeId,
                        Relationship = edgeRelationship
                    });
                }
            }
        }

        if (edges.Count > 0)
        {
            await storage.UpsertEdgesAsync(edges).ConfigureAwait(false);
        }

        return SendToolResponse(id, messageFactory(nodeId, edges.Count));
    }

    private string SendResponse(JsonElement id, object result)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id,
            result = result
        };

        return JsonSerializer.Serialize(response);
    }

    private string SendToolResponse(JsonElement id, string text)
    {
        return SendResponse(id, new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = text
                }
            }
        });
    }

    private string SendError(JsonElement id, int code, string message)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id,
            error = new
            {
                code = code,
                message = message
            }
        };

        return JsonSerializer.Serialize(response);
    }
}
