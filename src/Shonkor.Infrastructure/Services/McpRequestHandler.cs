// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;

using Shonkor.Infrastructure.Services;

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
    /// Resolves the project name to use for a call. When the session is tenant-locked the context
    /// project always wins (the explicit argument is ignored, preventing cross-tenant access).
    /// Otherwise the explicit argument wins, falling back to the directory-derived context.
    /// </summary>
    private string? ResolveProjectName(string? projectName) =>
        _lockToContextProject
            ? _contextProjectName
            : (!string.IsNullOrWhiteSpace(projectName) ? projectName : _contextProjectName);

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

    /// <summary>How many candidate hits the symbol-oriented tools pull before applying their selection heuristic.</summary>
    private const int SymbolSearchLimit = 8;

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
    /// Shared body for <c>impact_of</c> (incoming references) and <c>depends_on</c> (outgoing
    /// references): resolves the symbol, pulls its 1-hop neighbourhood, keeps the edges incident to it
    /// in the requested direction, and renders them grouped by relationship as
    /// <c>relation\tname\thandle  — summary</c>. <paramref name="emptyMessage"/> is a format string with
    /// <c>{0}</c>=name and <c>{1}</c>=type.
    /// </summary>
    private async Task<string> EdgeReportAsync(
        IGraphStorageProvider storage, string? projectName, string symbol, bool incoming, string verb, string emptyMessage)
    {
        var basePath = GetProjectBasePath(projectName);
        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
        if (def == null) return $"No definition found for '{symbol}'.";

        // Fetch only the symbol's own edges (+ their endpoint nodes), not its whole 1-hop neighbourhood.
        var (edges, neighbours) = await storage.GetIncidentEdgesAsync(def.Id).ConfigureAwait(false);
        // incoming: edges POINTING AT def (its dependents); outgoing: edges ORIGINATING at def (its dependencies).
        var incident = edges
            .Where(e => incoming ? e.TargetId == def.Id && e.SourceId != def.Id
                                 : e.SourceId == def.Id && e.TargetId != def.Id)
            .ToList();

        if (incident.Count == 0) return string.Format(emptyMessage, def.Name, def.Type);

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
        return sb.ToString().TrimEnd();
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
    public McpRequestHandler(ProjectManager projectManager, ContextCapsuleSynthesizer synthesizer, string? contextProjectName = null, bool lockToContextProject = false, IEmbeddingService? embeddingService = null, IEnumerable<IFileParser>? fileParsers = null)
    {
        _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _contextProjectName = contextProjectName;
        _lockToContextProject = lockToContextProject;
        _embeddingService = embeddingService;
        _fileParsers = fileParsers;
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
                    return SendResponse(id, new
                    {
                        protocolVersion = "2024-11-05",
                        capabilities = new
                        {
                        tools = new {}
                    },
                    serverInfo = new
                    {
                        name = "Shonkor MCP Server",
                        version = "1.0.0"
                    }
                });

            case "tools/list":
                return SendResponse(id, new
                {
                    tools = new object[]
                    {
                        new
                        {
                            name = "search_graph",
                            description = "Token-efficient FTS5 search for classes, methods, files, or markdown sections. Returns one compact line per hit (type, name, file:line, summary). Use 'type' to filter by node type (e.g. 'Class', 'Method'). Set verbose=true only when you also need each hit's graph connections.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    query = new { type = "string", description = "The search text or terms" },
                                    limit = new { type = "integer", description = "Max number of results to return (default 10)" },
                                    type = new { type = "string", description = "Filter results to a specific node type (e.g. 'Class', 'Method', 'Interface', 'File', 'Record', 'Property', 'MarkdownSection', 'Concept'). Omit for all types." },
                                    verbose = new { type = "boolean", description = "Include each hit's graph connections and full metadata as JSON (default false). Leave false for token-efficient lookups." },
                                    projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
                                },
                                required = new[] { "query" }
                            }
                        },
                        new
                        {
                            name = "locate",
                            description = "Minimal symbol locator: returns one 'name -> file:line | summary' line per hit. The most token-efficient way to find where a class, method, file, or section is defined and understand its purpose. Use this for pure 'where is X?' lookups.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    query = new { type = "string", description = "The symbol name or search text" },
                                    limit = new { type = "integer", description = "Max number of results to return (default 15)" },
                                    projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
                                },
                                required = new[] { "query" }
                            }
                        },
                        new
                        {
                            name = "get_subgraph",
                            description = "Retrieve nodes and edges connected to seed nodes within N hops. Token-efficient text by default: each node is 'handle  type  name  — summary' and edges are 'handle --REL--> handle'. The handle (e.g. '@/src/...File.cs::Class::Method') is a short, reusable id you can pass straight back as a seed. Set verbose=true for full JSON; maxChars to cap the size.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    seeds = new { type = "array", items = new { type = "string" }, description = "Node ids or short '@/…' handles to expand from." },
                                    hops = new { type = "integer", description = "Number of hops to traverse (default 2)" },
                                    verbose = new { type = "boolean", description = "Return full node/edge JSON instead of the compact text form (default false)." },
                                    maxChars = new { type = "integer", description = "Optional cap on the compact output size in characters (~4 chars/token). Truncates beyond it." },
                                    projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
                                },
                                required = new[] { "seeds" }
                            }
                        },
                        new
                        {
                            name = "impact_of",
                            description = "Impact analysis: list the nodes that reference a given symbol (incoming REFERENCES_TYPE/IMPLEMENTS/CALLS edges) — i.e. what would be affected if you change it. Returns 'relation  name  handle  — summary' per dependent, or states that nothing references it. The most token-efficient way to answer 'what breaks if I change X?'.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    symbol = new { type = "string", description = "The type/method/symbol name to analyze (e.g. 'GraphNode'). 'query' is accepted as an alias." },
                                    projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
                                },
                                required = new[] { "symbol" }
                            }
                        },
                        new
                        {
                            name = "depends_on",
                            description = "Inverse of impact_of: list what a symbol itself uses (outgoing REFERENCES_TYPE/IMPLEMENTS/CALLS edges) — its direct dependencies. Returns 'relation  name  handle  — summary' per dependency, or states it's self-contained. Use to understand a symbol's footprint before reading or changing it.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    symbol = new { type = "string", description = "The type/method/symbol name to analyze (e.g. 'GraphNode'). 'query' is accepted as an alias." },
                                    projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
                                },
                                required = new[] { "symbol" }
                            }
                        },
                        new
                        {
                            name = "get_source",
                            description = "Return the EXACT source code of a symbol (its stored body) plus its 'file:startLine-endLine' location — so you can read precisely what to edit without loading whole files. Resolves the symbol like impact_of. Supports maxChars to cap large bodies. The most token-efficient way to read a specific class/method before changing it.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    symbol = new { type = "string", description = "The type/method/symbol name to read (e.g. 'GraphNode'). 'query' is accepted as an alias." },
                                    maxChars = new { type = "integer", description = "Optional cap on the returned source length." },
                                    projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
                                },
                                required = new[] { "symbol" }
                            }
                        },
                        new
                        {
                            name = "find_usages",
                            description = "List the call/reference SITES of a symbol with a code snippet at each — a graph-aware grep. For every node that references the symbol, returns 'relation  name  file:line  ⟶ <the line that uses it>'. Use to see HOW something is used (and what would break) before changing its signature; richer than impact_of, which lists dependents without the usage line.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    symbol = new { type = "string", description = "The type/method/symbol name whose usages to find (e.g. 'GraphNode'). 'query' is accepted as an alias." },
                                    projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
                                },
                                required = new[] { "symbol" }
                            }
                        },
                        new
                        {
                            name = "reindex_file",
                            description = "Re-index a SINGLE file after you edit it, so the graph matches the working tree before you query again — closes the edit loop. Clears the file's old nodes/edges and re-parses it (a missing file is removed). Accepts an absolute path, a project-relative path, or an '@/' handle. Available only where the MCP server can see the files (local stdio/dev); degrades with a clear message otherwise. Cross-tech links refresh on a full scan, not here.",
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
                            name = "implementations_of",
                            description = "List the types that implement an interface or extend a base type (incoming IMPLEMENTS/EXTENDS edges), each as 'relation  name  file:line  — summary'. More precise than impact_of when you specifically need subtypes (e.g. all IFileParser implementations).",
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
                        },
                        new
                        {
                            name = "related_tests",
                            description = "List the tests that reference a symbol — i.e. what you should run after changing it. Returns 'testName  file:line' for each referencing node in a test file (xUnit/NUnit/Go/Python/JS conventions). States plainly when nothing covers it.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    symbol = new { type = "string", description = "The type/method/symbol name to find tests for. 'query' is accepted as an alias." },
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
                        },
                        new
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
                        },
                        new
                        {
                            name = "dependency_tree",
                            description = "Transitive dependency tree over reference edges (REFERENCES_TYPE/IMPLEMENTS/EXTENDS), rendered indented to a given depth. direction 'uses' = what the symbol depends on (outgoing); 'used_by' = what depends on it (incoming). NOTE: this is type/reference-level (the graph does not resolve method-level calls). Cycles are marked, and the tree is capped for safety.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    symbol = new { type = "string", description = "The root symbol (e.g. 'GraphNode'). 'query' is accepted as an alias." },
                                    direction = new { type = "string", description = "'uses' (outgoing, default) or 'used_by' (incoming)." },
                                    depth = new { type = "integer", description = "Tree depth (default 2, max 5)." },
                                    projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
                                },
                                required = new[] { "symbol" }
                            }
                        },
                        new
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
                        },
                        new
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
                        },
                        new
                        {
                            name = "search_semantic",
                            description = "Concept/meaning-based search via vector embeddings (e.g. 'where is authentication handled?'), complementing the keyword-based search_graph. Returns 'type  name  handle  — summary' per hit. Requires the embedding backend (web server + Ollama) and nodes that have been embedded by the enrichment worker; degrades to a clear message otherwise.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    query = new { type = "string", description = "Natural-language description of what you're looking for." },
                                    limit = new { type = "integer", description = "Max number of results (default 10)." },
                                    projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
                                },
                                required = new[] { "query" }
                            }
                        },
                        new
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
                                    maxHops = new { type = "integer", description = "Max path length to search (default 5)." },
                                    projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
                                },
                                required = new[] { "from", "to" }
                            }
                        },
                        new
                        {
                            name = "generate_capsule",
                            description = "Generate a token-optimized, prompt-friendly Markdown context capsule with inline Mermaid diagrams and source code snippets for a query.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    query = new { type = "string", description = "The seed query or topic to construct the capsule around" },
                                    hops = new { type = "integer", description = "Traversal hops for context expansion (default 2)" },
                                    maxChars = new { type = "integer", description = "Optional hard cap on the capsule size in characters (~4 chars per token). When exceeded, the capsule is truncated at a section boundary. Use to fit a token budget." }
                                },
                                required = new[] { "query" }
                            }
                        },
                        new
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
                        },
                        new
                        {
                            name = "record_decision",
                            description = "Record an architectural decision, rationale, or context in the graph connected to specific code nodes (classes, files, methods).",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    name = new { type = "string", description = "Title or core question of the decision" },
                                    content = new { type = "string", description = "Detailed explanation, alternatives considered, and rationale" },
                                    connectedNodeIds = new { type = "array", items = new { type = "string" }, description = "List of node IDs this decision influences" },
                                    projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
                                },
                                required = new[] { "name", "content" }
                            }
                        },
                        new
                        {
                            name = "record_milestone",
                            description = "Record a task milestone, progress update, or active focus/blocker status connected to specific code nodes.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    name = new { type = "string", description = "Title of the milestone or focus area (e.g. 'Configured SQLite User Schema')" },
                                    status = new { type = "string", description = "Status update (e.g. 'Completed', 'In Progress', 'Blocked')" },
                                    connectedNodeIds = new { type = "array", items = new { type = "string" }, description = "List of node IDs this milestone influences" },
                                    projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
                                },
                                required = new[] { "name", "status" }
                            }
                        },
                        new
                        {
                            name = "record_task",
                            description = "Record a concrete todo or action item identified by the AI during a session.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    name = new { type = "string", description = "Short title of the task" },
                                    content = new { type = "string", description = "Detailed description or steps" },
                                    status = new { type = "string", description = "e.g. 'Todo', 'In Progress', 'Done'" },
                                    connectedNodeIds = new { type = "array", items = new { type = "string" }, description = "Related nodes" },
                                    projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
                                },
                                required = new[] { "name", "status" }
                            }
                        },
                        new
                        {
                            name = "record_question",
                            description = "Record an open question, uncertainty, or needed clarification that blocks progress.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    name = new { type = "string", description = "The question" },
                                    content = new { type = "string", description = "Context around why this is a question" },
                                    connectedNodeIds = new { type = "array", items = new { type = "string" }, description = "Related nodes" },
                                    projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
                                },
                                required = new[] { "name" }
                            }
                        }
                    }
                });

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
            var projectName = args?["projectName"]?.ToString();
            var storage = await GetStorageAsync(projectName).ConfigureAwait(false);

            switch (toolName)
            {
                case "search_graph":
                    {
                        var query = args?["query"]?.ToString();
                        if (string.IsNullOrWhiteSpace(query))
                        {
                            return SendError(id, -32602, "Parameter 'query' is required");
                            
                        }
                        var limit = ReadInt(args?["limit"], 10);
                        var verbose = args?["verbose"]?.GetValue<bool>() ?? false;
                        var typeFilter = args?["type"]?.ToString();
                        var results = await storage.SearchAsync(query, limit, filterType: typeFilter).ConfigureAwait(false);
                        var basePath = GetProjectBasePath(projectName);

                        if (!verbose)
                        {
                            // Token-efficient default: one line per hit, paths relative to the project root,
                            // no connections, no indentation. Closer to ripgrep output than verbose JSON.
                            if (results.Count == 0)
                            {
                                return SendToolResponse(id, $"No matches for '{query}'{(typeFilter != null ? $" (type={typeFilter})" : "")}.");
                            }

                            var lines = results.Select(r =>
                            {
                                var rawLoc = string.IsNullOrEmpty(r.Node.FilePath) ? r.Node.Id : r.Node.FilePath;
                                var loc = Shorten(rawLoc, basePath);
                                if (r.Node.StartLine.HasValue) loc += $":{r.Node.StartLine}";
                                var summary = !string.IsNullOrEmpty(r.Node.Summary) ? $"\t{r.Node.Summary}" : "";
                                return $"{r.Node.Type}\t{r.Node.Name}\t{loc}{summary}";
                            });
                            return SendToolResponse(id, string.Join("\n", lines));
                        }

                        // verbose: full structured JSON incl. connections (compact, no indentation).
                        var formattedResults = results.Select(r => new
                        {
                            node = new
                            {
                                r.Node.Id,
                                r.Node.Type,
                                r.Node.Name,
                                r.Node.FilePath,
                                r.Node.StartLine,
                                r.Node.EndLine
                            },
                            score = Math.Round(r.Score, 3),
                            connections = r.RelatedEdges.Select(e => new
                            {
                                e.SourceId,
                                e.TargetId,
                                Relationship = e.Relationship
                            })
                        }).ToList();

                        return SendToolResponse(id, JsonSerializer.Serialize(formattedResults));
                    }

                case "locate":
                    {
                        var query = args?["query"]?.ToString();
                        if (string.IsNullOrWhiteSpace(query))
                        {
                            return SendError(id, -32602, "Parameter 'query' is required");
                            
                        }
                        var limit = ReadInt(args?["limit"], 15);
                        var results = await storage.SearchAsync(query, limit).ConfigureAwait(false);
                        if (results.Count == 0)
                        {
                            return SendToolResponse(id, $"No matches for '{query}'.");
                        }

                        var basePath = GetProjectBasePath(projectName);
                        var lines = results.Select(r =>
                        {
                            var rawLoc = string.IsNullOrEmpty(r.Node.FilePath) ? r.Node.Id : r.Node.FilePath;
                            var loc = Shorten(rawLoc, basePath);
                            if (r.Node.StartLine.HasValue) loc += $":{r.Node.StartLine}";
                            var summary = !string.IsNullOrEmpty(r.Node.Summary) ? $" | {r.Node.Summary}" : "";
                            return $"{r.Node.Name} -> {loc}{summary}";
                        });
                        return SendToolResponse(id, string.Join("\n", lines));
                    }

                case "get_subgraph":
                    {
                        var seedsNode = args?["seeds"] as JsonArray;
                        if (seedsNode == null || seedsNode.Count == 0)
                        {
                            return SendError(id, -32602, "Parameter 'seeds' is required and must be a non-empty array");
                            
                        }
                        var hops = ReadInt(args?["hops"], 2);
                        var verbose = args?["verbose"]?.GetValue<bool>() ?? false;
                        var maxChars = ReadInt(args?["maxChars"], 0); // 0 = no limit
                        var basePath = GetProjectBasePath(projectName);

                        // Accept either raw node ids or short "@/…" handles as seeds.
                        var seeds = seedsNode.Select(s => FromHandle(s?.ToString(), basePath))
                            .Where(s => !string.IsNullOrEmpty(s)).ToList();

                        var (nodes, edges) = await storage.GetSubgraphAsync(seeds, hops).ConfigureAwait(false);

                        if (!verbose)
                        {
                            // Token-efficient default: each node is 'handle<TAB>type<TAB>name[<TAB>— summary]'
                            // (the handle is a reusable seed); edges as 'handle --REL--> handle'.
                            var nodeLines = nodes.Select(n =>
                            {
                                var handle = ToHandle(n.Id, basePath);
                                var summary = !string.IsNullOrEmpty(n.Summary) ? $"\t— {n.Summary}" : "";
                                return $"{handle}\t{n.Type}\t{n.Name}{summary}";
                            });
                            var edgeLines = edges.Select(e =>
                                $"{ToHandle(e.SourceId, basePath)} --{e.Relationship}--> {ToHandle(e.TargetId, basePath)}");

                            var sb = new System.Text.StringBuilder();
                            sb.Append("NODES (").Append(nodes.Count).Append("):\n");
                            sb.Append(string.Join("\n", nodeLines));
                            sb.Append("\n\nEDGES (").Append(edges.Count).Append("):\n");
                            sb.Append(string.Join("\n", edgeLines));

                            var output = sb.ToString();
                            if (maxChars > 0 && output.Length > maxChars)
                            {
                                output = output[..maxChars].TrimEnd()
                                    + $"\n… (truncated to {maxChars} chars; raise maxChars or reduce hops)";
                            }
                            return SendToolResponse(id, output);
                        }

                        var formatted = new
                        {
                            nodes = nodes.Select(n => new
                            {
                                n.Id,
                                n.Type,
                                n.Name,
                                n.FilePath,
                                n.StartLine,
                                n.EndLine,
                                ContentLength = n.Content.Length
                            }),
                            edges = edges.Select(e => new
                            {
                                e.SourceId,
                                e.TargetId,
                                Relationship = e.Relationship
                            })
                        };

                        return SendToolResponse(id, JsonSerializer.Serialize(formatted));
                    }

                case "impact_of":
                    {
                        var symbol = ReadSymbol(args);
                        if (string.IsNullOrWhiteSpace(symbol))
                        {
                            return SendError(id, -32602, "Parameter 'symbol' is required");
                        }
                        // Incoming references: what would be affected if you change the symbol.
                        var report = await EdgeReportAsync(storage, projectName, symbol, incoming: true,
                            verb: "is referenced by",
                            emptyMessage: "Nothing references '{0}' ({1}) — safe to change in isolation, or it is an entry point.")
                            .ConfigureAwait(false);
                        return SendToolResponse(id, report);
                    }

                case "depends_on":
                    {
                        var symbol = ReadSymbol(args);
                        if (string.IsNullOrWhiteSpace(symbol))
                        {
                            return SendError(id, -32602, "Parameter 'symbol' is required");
                        }
                        // Outgoing references: the symbol's own direct dependencies.
                        var report = await EdgeReportAsync(storage, projectName, symbol, incoming: false,
                            verb: "depends on",
                            emptyMessage: "'{0}' ({1}) depends on nothing in the graph — it is self-contained or a leaf.")
                            .ConfigureAwait(false);
                        return SendToolResponse(id, report);
                    }

                case "verify_exists":
                    {
                        var symbol = ReadSymbol(args);
                        if (string.IsNullOrWhiteSpace(symbol))
                        {
                            return SendError(id, -32602, "Parameter 'symbol' is required");
                        }
                        var basePath = GetProjectBasePath(projectName);
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

                case "get_source":
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
                        // Prefer the stored body (e.g. method source); for nodes that store none (type
                        // declarations) fall back to reading the exact line range from the file locally.
                        var body = def.Content;
                        if (string.IsNullOrEmpty(body))
                        {
                            body = TryReadSourceSlice(def);
                            if (string.IsNullOrEmpty(body))
                            {
                                return SendToolResponse(id,
                                    $"'{def.Name}' ({def.Type}) at {ToHandle(def.Id, basePath)} stores no source, and no local file access is available to read it.");
                            }
                        }

                        var loc = Shorten(string.IsNullOrEmpty(def.FilePath) ? def.Id : def.FilePath, basePath);
                        var range = def.StartLine.HasValue
                            ? (def.EndLine.HasValue ? $":{def.StartLine}-{def.EndLine}" : $":{def.StartLine}")
                            : "";
                        var maxChars = ReadInt(args?["maxChars"], 0); // 0 = no limit
                        if (maxChars > 0 && body.Length > maxChars)
                        {
                            body = body[..maxChars].TrimEnd() + $"\n… (truncated to {maxChars} chars; raise maxChars)";
                        }
                        return SendToolResponse(id, $"{def.Name} ({def.Type}) — {loc}{range}\n\n{body}");
                    }

                case "find_usages":
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

                        var (edges, neighbours) = await storage.GetIncidentEdgesAsync(def.Id).ConfigureAwait(false);
                        var incoming = edges.Where(e => e.TargetId == def.Id && e.SourceId != def.Id).ToList();
                        if (incoming.Count == 0)
                        {
                            return SendToolResponse(id, $"No usages of '{def.Name}' ({def.Type}) found in the graph.");
                        }

                        var sb = new System.Text.StringBuilder();
                        sb.Append($"{incoming.Count} usage(s) of '{def.Name}':\n");
                        foreach (var e in incoming.OrderBy(e => e.Relationship))
                        {
                            var user = neighbours.GetValueOrDefault(e.SourceId);
                            var name = user?.Name ?? e.SourceId;
                            var loc = Shorten(user != null && !string.IsNullOrEmpty(user.FilePath) ? user.FilePath : e.SourceId, basePath);
                            if (user?.StartLine is int line) loc += $":{line}";
                            var snippet = FirstLineMentioning(user?.Content, def.Name);
                            var snippetText = snippet != null ? $"  ⟶ {snippet}" : "";
                            sb.Append($"{e.Relationship}\t{name}\t{loc}{snippetText}\n");
                        }
                        return SendToolResponse(id, sb.ToString().TrimEnd());
                    }

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

                        var scanner = new GraphIndexScanner(storage, _fileParsers);
                        var result = await scanner.ScanFileAsync(resolved).ConfigureAwait(false);

                        var handle = ToHandle(System.IO.Path.GetFullPath(resolved), basePath);
                        if (result.NodesCreated == 0)
                        {
                            return SendToolResponse(id, $"Cleared '{handle}' from the graph (file missing, unparsable, or empty).");
                        }
                        return SendToolResponse(id,
                            $"Reindexed {handle}: {result.NodesCreated} node(s), {result.EdgesCreated} edge(s) in {result.Duration.TotalMilliseconds:F0} ms. (Cross-tech links refresh on a full scan.)");
                    }

                case "implementations_of":
                    {
                        var symbol = ReadSymbol(args);
                        if (string.IsNullOrWhiteSpace(symbol))
                        {
                            return SendError(id, -32602, "Parameter 'symbol' is required");
                        }
                        var basePath = GetProjectBasePath(projectName);

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

                        var (edges, neighbours) = await storage.GetIncidentEdgesAsync(def.Id).ConfigureAwait(false);
                        var tests = edges
                            .Where(e => e.TargetId == def.Id && e.SourceId != def.Id)
                            .Select(e => neighbours.GetValueOrDefault(e.SourceId))
                            .Where(n => n != null && LooksLikeTest(n!.FilePath))
                            .DistinctBy(n => n!.Id)
                            .ToList();

                        if (tests.Count == 0)
                        {
                            return SendToolResponse(id,
                                $"No tests in the graph reference '{def.Name}' directly — the change may be untested, or covered only indirectly.");
                        }

                        var sb = new System.Text.StringBuilder();
                        sb.Append($"{tests.Count} test(s) likely covering '{def.Name}' (run these after changing it):\n");
                        foreach (var t in tests)
                        {
                            var loc = Shorten(string.IsNullOrEmpty(t!.FilePath) ? t.Id : t.FilePath, basePath);
                            if (t.StartLine is int line) loc += $":{line}";
                            sb.Append($"{t.Name}\t{loc}\n");
                        }
                        return SendToolResponse(id, sb.ToString().TrimEnd());
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

                case "signature":
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
                        var loc = Shorten(string.IsNullOrEmpty(def.FilePath) ? def.Id : def.FilePath, basePath);
                        if (def.StartLine is int sl) loc += $":{sl}";
                        return SendToolResponse(id, $"{BuildSignature(def)}\n@ {loc}");
                    }

                case "outline":
                    {
                        var rawPath = args?["path"]?.ToString() ?? args?["file"]?.ToString();
                        if (string.IsNullOrWhiteSpace(rawPath))
                        {
                            return SendError(id, -32602, "Parameter 'path' is required");
                        }
                        var basePath = GetProjectBasePath(projectName);
                        var resolved = FromHandle(rawPath, basePath);
                        if (!System.IO.Path.IsPathRooted(resolved))
                        {
                            resolved = System.IO.Path.Combine(basePath, resolved);
                        }
                        var fileId = System.IO.Path.GetFullPath(resolved);

                        var fileNode = await storage.GetNodeByIdAsync(fileId).ConfigureAwait(false);
                        if (fileNode == null)
                        {
                            return SendToolResponse(id, $"File '{ToHandle(fileId, basePath)}' is not indexed.");
                        }

                        var (nodes, edges) = await storage.GetSubgraphAsync(new[] { fileId }, 2).ConfigureAwait(false);
                        var byId = nodes.ToDictionary(n => n.Id);
                        var children = new Dictionary<string, List<string>>();
                        foreach (var e in edges.Where(e => e.Relationship == "CONTAINS"))
                        {
                            if (!children.TryGetValue(e.SourceId, out var list)) children[e.SourceId] = list = new();
                            list.Add(e.TargetId);
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

                case "dependency_tree":
                    {
                        var symbol = ReadSymbol(args);
                        if (string.IsNullOrWhiteSpace(symbol))
                        {
                            return SendError(id, -32602, "Parameter 'symbol' is required");
                        }
                        var direction = (args?["direction"]?.ToString() ?? "uses").ToLowerInvariant();
                        var usedBy = direction is "used_by" or "used-by" or "callers" or "incoming";
                        var depth = Math.Clamp(ReadInt(args?["depth"], 2), 1, 5);

                        var def = await ResolveDefinitionAsync(storage, symbol).ConfigureAwait(false);
                        if (def == null)
                        {
                            return SendToolResponse(id, $"No definition found for '{symbol}'.");
                        }

                        var depRelations = new HashSet<string> { "REFERENCES_TYPE", "IMPLEMENTS", "EXTENDS" };
                        var sb = new System.Text.StringBuilder();
                        sb.Append($"Dependency tree ({(usedBy ? "used-by" : "uses")}, depth {depth}) for '{def.Name}':\n");
                        sb.Append($"{def.Name} ({def.Type})\n");

                        var visited = new HashSet<string> { def.Id };
                        var emitted = 0;
                        const int maxNodes = 100;

                        async Task Walk(string nodeId, int level)
                        {
                            if (level > depth || emitted >= maxNodes) return;
                            var (edges, neighbours) = await storage.GetIncidentEdgesAsync(nodeId).ConfigureAwait(false);
                            var step = edges.Where(e => depRelations.Contains(e.Relationship)
                                && (usedBy ? e.TargetId == nodeId && e.SourceId != nodeId
                                           : e.SourceId == nodeId && e.TargetId != nodeId)).ToList();
                            foreach (var e in step.OrderBy(e => e.Relationship))
                            {
                                if (emitted >= maxNodes) { sb.Append(new string(' ', level * 2)).Append("… (truncated)\n"); break; }
                                var otherId = usedBy ? e.SourceId : e.TargetId;
                                var other = neighbours.GetValueOrDefault(otherId);
                                var arrow = usedBy ? $"<--{e.Relationship}--" : $"--{e.Relationship}-->";
                                sb.Append(new string(' ', level * 2)).Append($"{arrow} {other?.Name ?? otherId} ({other?.Type ?? "?"})");
                                emitted++;
                                if (!visited.Add(otherId)) { sb.Append("  ↺\n"); continue; }
                                sb.Append('\n');
                                await Walk(otherId, level + 1).ConfigureAwait(false);
                            }
                        }
                        await Walk(def.Id, 1).ConfigureAwait(false);

                        return SendToolResponse(id, sb.ToString().TrimEnd());
                    }

                case "get_open_threads":
                    {
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

                case "search_semantic":
                    {
                        var query = args?["query"]?.ToString();
                        if (string.IsNullOrWhiteSpace(query))
                        {
                            return SendError(id, -32602, "Parameter 'query' is required");
                        }
                        if (_embeddingService == null)
                        {
                            return SendToolResponse(id, "Semantic search is unavailable here (no embedding backend wired). Use search_graph (FTS) instead, or run via the web server with Ollama.");
                        }

                        var limit = ReadInt(args?["limit"], 10);
                        var basePath = GetProjectBasePath(projectName);

                        var embedding = await _embeddingService.GenerateEmbeddingAsync(query).ConfigureAwait(false);
                        if (embedding == null || embedding.Length == 0)
                        {
                            return SendToolResponse(id, "Could not embed the query (is the embedding backend / Ollama running?).");
                        }

                        var results = await storage.SearchSemanticAsync(embedding, limit).ConfigureAwait(false);
                        if (results.Count == 0)
                        {
                            return SendToolResponse(id, $"No semantically similar nodes for '{query}'. (Have nodes been embedded by the enrichment worker yet?)");
                        }

                        var lines = results.Select(r =>
                        {
                            var summary = !string.IsNullOrEmpty(r.Node.Summary) ? $"\t— {r.Node.Summary}" : "";
                            return $"{r.Node.Type}\t{r.Node.Name}\t{ToHandle(r.Node.Id, basePath)}{summary}";
                        });
                        return SendToolResponse(id, string.Join("\n", lines));
                    }

                case "find_path":
                    {
                        var fromArg = args?["from"]?.ToString();
                        var toArg = args?["to"]?.ToString();
                        if (string.IsNullOrWhiteSpace(fromArg) || string.IsNullOrWhiteSpace(toArg))
                        {
                            return SendError(id, -32602, "Parameters 'from' and 'to' are required");
                        }
                        var maxHops = ReadInt(args?["maxHops"], 5);
                        var basePath = GetProjectBasePath(projectName);

                        var fromDef = await ResolveDefinitionAsync(storage, fromArg).ConfigureAwait(false);
                        if (fromDef == null) return SendToolResponse(id, $"No definition found for 'from' = '{fromArg}'.");
                        var toDef = await ResolveDefinitionAsync(storage, toArg).ConfigureAwait(false);
                        if (toDef == null) return SendToolResponse(id, $"No definition found for 'to' = '{toArg}'.");
                        if (fromDef.Id == toDef.Id)
                        {
                            return SendToolResponse(id, $"'{fromArg}' and '{toArg}' resolve to the same node ({fromDef.Name}).");
                        }

                        var path = await GraphPathFinder.FindPathAsync(storage, fromDef.Id, toDef.Id, maxHops).ConfigureAwait(false);
                        if (path == null)
                        {
                            return SendToolResponse(id,
                                $"No path from '{fromDef.Name}' to '{toDef.Name}' within {maxHops} hops — they may be in different components. Try raising maxHops.");
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

                case "generate_capsule":
                    {
                        var query = args?["query"]?.ToString();
                        if (string.IsNullOrWhiteSpace(query))
                        {
                            return SendError(id, -32602, "Parameter 'query' is required");
                            
                        }
                        var hops = ReadInt(args?["hops"], 2);

                        var searchResults = await storage.SearchAsync(query, 5).ConfigureAwait(false);
                        var seeds = searchResults.Select(r => r.Node.Id).ToList();

                        if (seeds.Count == 0)
                        {
                            return SendToolResponse(id, $"No nodes found matching query: '{query}'");
                            
                        }

                        var (nodes, edges) = await storage.GetSubgraphAsync(seeds, hops).ConfigureAwait(false);
                        var markdown = _synthesizer.Synthesize(nodes, edges);

                        // Optional token budget: cap the capsule size, truncating at a section boundary.
                        var maxChars = ReadInt(args?["maxChars"], 0); // 0 = no limit
                        if (maxChars > 0 && markdown.Length > maxChars)
                        {
                            markdown = TruncateAtBoundary(markdown, maxChars);
                        }

                        return SendToolResponse(id, markdown);
                    }

                case "get_stats":
                    {
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

                case "record_decision":
                    {
                        var name = args?["name"]?.ToString();
                        var content = args?["content"]?.ToString();
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
                        {
                            return SendError(id, -32602, "Parameters 'name' and 'content' are required");
                        }
                        return await RecordNodeAsync(id, storage, "decision", "Decision", name, content,
                            new Dictionary<string, string> { ["created"] = UtcNow() },
                            args?["connectedNodeIds"] as JsonArray, "INFLUENCES",
                            (nodeId, count) => $"Successfully recorded decision '{name}' (ID: {nodeId}) and connected it to {count} nodes.")
                            .ConfigureAwait(false);
                    }

                case "record_milestone":
                    {
                        var name = args?["name"]?.ToString();
                        var status = args?["status"]?.ToString();
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(status))
                        {
                            return SendError(id, -32602, "Parameters 'name' and 'status' are required");
                        }
                        return await RecordNodeAsync(id, storage, "milestone", "Milestone", name, $"Status: {status}",
                            new Dictionary<string, string> { ["status"] = status, ["updated"] = UtcNow() },
                            args?["connectedNodeIds"] as JsonArray, "AFFECTS",
                            (nodeId, count) => $"Successfully recorded milestone '{name}' (ID: {nodeId}) with status '{status}' connected to {count} nodes.")
                            .ConfigureAwait(false);
                    }

                case "record_task":
                    {
                        var name = args?["name"]?.ToString();
                        var content = args?["content"]?.ToString();
                        var status = args?["status"]?.ToString() ?? "Todo";
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            return SendError(id, -32602, "Parameter 'name' is required");
                        }
                        return await RecordNodeAsync(id, storage, "task", "Task", name, content ?? $"Status: {status}",
                            new Dictionary<string, string> { ["status"] = status, ["created"] = UtcNow() },
                            args?["connectedNodeIds"] as JsonArray, "HasTask",
                            (nodeId, count) => $"Successfully recorded task '{name}' (ID: {nodeId}) connected to {count} nodes.")
                            .ConfigureAwait(false);
                    }

                case "record_question":
                    {
                        var name = args?["name"]?.ToString();
                        var content = args?["content"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            return SendError(id, -32602, "Parameter 'name' is required");
                        }
                        return await RecordNodeAsync(id, storage, "question", "Question", name, content,
                            new Dictionary<string, string> { ["status"] = "Open", ["created"] = UtcNow() },
                            args?["connectedNodeIds"] as JsonArray, "RELATES_TO",
                            (nodeId, count) => $"Successfully recorded question '{name}' (ID: {nodeId}) connected to {count} nodes.")
                            .ConfigureAwait(false);
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
