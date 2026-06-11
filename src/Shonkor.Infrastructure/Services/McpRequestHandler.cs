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
    public McpRequestHandler(ProjectManager projectManager, ContextCapsuleSynthesizer synthesizer, string? contextProjectName = null, bool lockToContextProject = false)
    {
        _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _contextProjectName = contextProjectName;
        _lockToContextProject = lockToContextProject;
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
                            description = "Retrieve nodes and edges connected to a set of seed nodes within N traversal hops. Token-efficient text output by default (nodes as 'type name file', edges as 'A --REL--> B'); set verbose=true for full JSON.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    seeds = new { type = "array", items = new { type = "string" }, description = "List of node IDs to expand from" },
                                    hops = new { type = "integer", description = "Number of hops to traverse (default 1)" },
                                    verbose = new { type = "boolean", description = "Return full node/edge JSON instead of the compact text form (default false)." },
                                    projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
                                },
                                required = new[] { "seeds" }
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
                        var limit = args?["limit"]?.GetValue<int>() ?? 10;
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
                        var limit = args?["limit"]?.GetValue<int>() ?? 15;
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
                        var seeds = seedsNode.Select(s => s?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                        var hops = args?["hops"]?.GetValue<int>() ?? 2;
                        var verbose = args?["verbose"]?.GetValue<bool>() ?? false;

                        var (nodes, edges) = await storage.GetSubgraphAsync(seeds, hops).ConfigureAwait(false);

                        if (!verbose)
                        {
                            // Token-efficient default: compact text. Nodes as 'type<TAB>name<TAB>file',
                            // edges as 'source --REL--> target', all paths relative to the project root.
                            var basePath = GetProjectBasePath(projectName);

                            var nodeLines = nodes.Select(n =>
                            {
                                var rawLoc = string.IsNullOrEmpty(n.FilePath) ? n.Id : n.FilePath;
                                return $"{n.Type}\t{n.Name}\t{Shorten(rawLoc, basePath)}";
                            });
                            var edgeLines = edges.Select(e =>
                                $"{Shorten(e.SourceId, basePath)} --{e.Relationship}--> {Shorten(e.TargetId, basePath)}");

                            var sb = new System.Text.StringBuilder();
                            sb.Append("NODES (").Append(nodes.Count).Append("):\n");
                            sb.Append(string.Join("\n", nodeLines));
                            sb.Append("\n\nEDGES (").Append(edges.Count).Append("):\n");
                            sb.Append(string.Join("\n", edgeLines));
                            return SendToolResponse(id, sb.ToString());
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

                case "generate_capsule":
                    {
                        var query = args?["query"]?.ToString();
                        if (string.IsNullOrWhiteSpace(query))
                        {
                            return SendError(id, -32602, "Parameter 'query' is required");
                            
                        }
                        var hops = args?["hops"]?.GetValue<int>() ?? 2;

                        var searchResults = await storage.SearchAsync(query, 5).ConfigureAwait(false);
                        var seeds = searchResults.Select(r => r.Node.Id).ToList();

                        if (seeds.Count == 0)
                        {
                            return SendToolResponse(id, $"No nodes found matching query: '{query}'");
                            
                        }

                        var (nodes, edges) = await storage.GetSubgraphAsync(seeds, hops).ConfigureAwait(false);
                        var markdown = _synthesizer.Synthesize(nodes, edges);

                        // Optional token budget: cap the capsule size, truncating at a section boundary.
                        var maxChars = args?["maxChars"]?.GetValue<int>();
                        if (maxChars is > 0 && markdown.Length > maxChars.Value)
                        {
                            markdown = TruncateAtBoundary(markdown, maxChars.Value);
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
                            stats.EdgesByRelation
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
