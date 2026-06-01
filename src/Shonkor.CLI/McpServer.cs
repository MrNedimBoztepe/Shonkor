// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;

using Shonkor.Infrastructure.Services;

namespace Shonkor.CLI;

/// <summary>
/// A lightweight, robust implementation of a Model Context Protocol (MCP) server 
/// communicating over standard input/output (stdio) using JSON-RPC.
/// Exposes high-precision GraphRAG tools to AI assistants like Claude Code and Antigravity.
/// </summary>
public sealed class McpServer
{
    private readonly ProjectManager _projectManager;
    private readonly ContextCapsuleSynthesizer _synthesizer;

    /// <summary>
    /// The project this server is bound to, derived from the working directory the MCP was launched in
    /// (the AI chat's directory) — NOT from the shared, web-mutable ActiveProjectName. May be null if the
    /// launch directory doesn't match any registered project, in which case the global active is used.
    /// </summary>
    private readonly string? _contextProjectName;

    /// <summary>Resolves the project name to use for a call: explicit argument wins, else the directory-derived context.</summary>
    private string? ResolveProjectName(string? projectName) =>
        !string.IsNullOrWhiteSpace(projectName) ? projectName : _contextProjectName;

    private IGraphStorageProvider GetStorage(string? projectName)
    {
        var effective = ResolveProjectName(projectName);
        return string.IsNullOrWhiteSpace(effective)
            ? _projectManager.GetActiveStorageProvider()
            : _projectManager.GetStorageProvider(effective);
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
    /// Initializes a new instance of the <see cref="McpServer"/> class.
    /// </summary>
    /// <param name="projectManager">The global project manager.</param>
    /// <param name="synthesizer">The context capsule synthesizer.</param>
    /// <param name="contextProjectName">
    /// The project bound to this server's working directory (the AI chat's directory). When set, it is used
    /// as the default for all tool calls that don't pass an explicit projectName, decoupling the MCP from
    /// the web dashboard's mutable active-project flag.
    /// </param>
    public McpServer(ProjectManager projectManager, ContextCapsuleSynthesizer synthesizer, string? contextProjectName = null)
    {
        _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
        _synthesizer = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _contextProjectName = contextProjectName;
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

                await HandleJsonRpcMessageAsync(line).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // We must log errors to stderr because standard out is reserved strictly for JSON-RPC messages!
                Console.Error.WriteLine($"[MCP Error] {ex.Message}");
            }
        }
    }

    private async Task HandleJsonRpcMessageAsync(string json)
    {
        JsonNode? idNode = null;
        try
        {
            var document = JsonNode.Parse(json);
            if (document is not JsonObject obj)
            {
                return;
            }

            var method = obj["method"]?.ToString();
            idNode = obj["id"];

            // If it's a notification (no ID), just ignore or handle it (like initialized notification)
            if (idNode == null)
            {
                return;
            }

            var id = idNode.GetValue<JsonElement>();

            switch (method)
            {
                case "initialize":
                    SendResponse(id, new
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
                break;

            case "tools/list":
                SendResponse(id, new
                {
                    tools = new object[]
                    {
                        new
                        {
                            name = "search_graph",
                            description = "Token-efficient FTS5 search for classes, methods, files, or markdown sections. Returns one compact line per hit (type, name, file:line). Set verbose=true only when you also need each hit's graph connections.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new
                                {
                                    query = new { type = "string", description = "The search text or terms" },
                                    limit = new { type = "integer", description = "Max number of results to return (default 10)" },
                                    verbose = new { type = "boolean", description = "Include each hit's graph connections and full metadata as JSON (default false). Leave false for token-efficient lookups." },
                                    projectName = new { type = "string", description = "Optional project context name (e.g. 'MuM' or 'Shonkor'). If omitted, uses the active project." }
                                },
                                required = new[] { "query" }
                            }
                        },
                        new
                        {
                            name = "locate",
                            description = "Minimal symbol locator: returns one 'name -> file:line' line per hit. The most token-efficient way to find where a class, method, file, or section is defined. Use this for pure 'where is X?' lookups.",
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
                break;

            case "tools/call":
                var toolName = obj["params"]?["name"]?.ToString();
                var args = obj["params"]?["arguments"] as JsonObject;
                await HandleToolCallAsync(id, toolName, args).ConfigureAwait(false);
                break;

            default:
                SendError(id, -32601, $"Method not found: '{method}'");
                break;
        }
        }
        catch (Exception ex)
        {
            if (idNode != null)
            {
                try
                {
                    SendError(idNode.GetValue<JsonElement>(), -32603, $"Internal Error: {ex.Message}");
                }
                catch
                {
                    // Ignore error serialization issues to prevent recursive crashes
                }
            }
            Console.Error.WriteLine($"[MCP Internal Error] {ex.Message}\n{ex.StackTrace}");
        }
    }

    private async Task HandleToolCallAsync(JsonElement id, string? toolName, JsonObject? args)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            SendError(id, -32602, "Missing parameter: 'name'");
            return;
        }

        try
        {
            var projectName = args?["projectName"]?.ToString();
            var storage = GetStorage(projectName);

            switch (toolName)
            {
                case "search_graph":
                    {
                        var query = args?["query"]?.ToString();
                        if (string.IsNullOrWhiteSpace(query))
                        {
                            SendError(id, -32602, "Parameter 'query' is required");
                            return;
                        }
                        var limit = args?["limit"]?.GetValue<int>() ?? 10;
                        var verbose = args?["verbose"]?.GetValue<bool>() ?? false;
                        var results = await storage.SearchAsync(query, limit).ConfigureAwait(false);
                        var basePath = GetProjectBasePath(projectName);

                        if (!verbose)
                        {
                            // Token-efficient default: one line per hit, paths relative to the project root,
                            // no connections, no indentation. Closer to ripgrep output than verbose JSON.
                            if (results.Count == 0)
                            {
                                SendToolResponse(id, $"No matches for '{query}'.");
                                break;
                            }

                            var lines = results.Select(r =>
                            {
                                var rawLoc = string.IsNullOrEmpty(r.Node.FilePath) ? r.Node.Id : r.Node.FilePath;
                                var loc = Shorten(rawLoc, basePath);
                                if (r.Node.StartLine.HasValue) loc += $":{r.Node.StartLine}";
                                return $"{r.Node.Type}\t{r.Node.Name}\t{loc}";
                            });
                            SendToolResponse(id, string.Join("\n", lines));
                            break;
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

                        SendToolResponse(id, JsonSerializer.Serialize(formattedResults));
                    }
                    break;

                case "locate":
                    {
                        var query = args?["query"]?.ToString();
                        if (string.IsNullOrWhiteSpace(query))
                        {
                            SendError(id, -32602, "Parameter 'query' is required");
                            return;
                        }
                        var limit = args?["limit"]?.GetValue<int>() ?? 15;
                        var results = await storage.SearchAsync(query, limit).ConfigureAwait(false);
                        if (results.Count == 0)
                        {
                            SendToolResponse(id, $"No matches for '{query}'.");
                            break;
                        }

                        var basePath = GetProjectBasePath(projectName);
                        var lines = results.Select(r =>
                        {
                            var rawLoc = string.IsNullOrEmpty(r.Node.FilePath) ? r.Node.Id : r.Node.FilePath;
                            var loc = Shorten(rawLoc, basePath);
                            if (r.Node.StartLine.HasValue) loc += $":{r.Node.StartLine}";
                            return $"{r.Node.Name} -> {loc}";
                        });
                        SendToolResponse(id, string.Join("\n", lines));
                    }
                    break;

                case "get_subgraph":
                    {
                        var seedsNode = args?["seeds"] as JsonArray;
                        if (seedsNode == null || seedsNode.Count == 0)
                        {
                            SendError(id, -32602, "Parameter 'seeds' is required and must be a non-empty array");
                            return;
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
                            SendToolResponse(id, sb.ToString());
                            break;
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

                        SendToolResponse(id, JsonSerializer.Serialize(formatted));
                    }
                    break;

                case "generate_capsule":
                    {
                        var query = args?["query"]?.ToString();
                        if (string.IsNullOrWhiteSpace(query))
                        {
                            SendError(id, -32602, "Parameter 'query' is required");
                            return;
                        }
                        var hops = args?["hops"]?.GetValue<int>() ?? 2;

                        var searchResults = await storage.SearchAsync(query, 5).ConfigureAwait(false);
                        var seeds = searchResults.Select(r => r.Node.Id).ToList();

                        if (seeds.Count == 0)
                        {
                            SendToolResponse(id, $"No nodes found matching query: '{query}'");
                            return;
                        }

                        var (nodes, edges) = await storage.GetSubgraphAsync(seeds, hops).ConfigureAwait(false);
                        var markdown = _synthesizer.Synthesize(nodes, edges);

                        // Optional token budget: cap the capsule size, truncating at a section boundary.
                        var maxChars = args?["maxChars"]?.GetValue<int>();
                        if (maxChars is > 0 && markdown.Length > maxChars.Value)
                        {
                            markdown = TruncateAtBoundary(markdown, maxChars.Value);
                        }

                        SendToolResponse(id, markdown);
                    }
                    break;

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
                        SendToolResponse(id, JsonSerializer.Serialize(formattedStats));
                    }
                    break;

                case "record_decision":
                    {
                        var name = args?["name"]?.ToString();
                        var content = args?["content"]?.ToString();
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
                        {
                            SendError(id, -32602, "Parameters 'name' and 'content' are required");
                            return;
                        }
                        var connectedNodeIds = args?["connectedNodeIds"] as JsonArray;
                        var decisionId = $"decision::{Guid.NewGuid().ToString("N").Substring(0, 8)}";

                        var decisionNode = new GraphNode
                        {
                            Id = decisionId,
                            Name = name,
                            Type = "Decision",
                            Content = content,
                            Properties = new Dictionary<string, string>
                            {
                                ["created"] = DateTime.UtcNow.ToString("o")
                            }
                        };

                        await storage.UpsertNodesAsync(new[] { decisionNode }).ConfigureAwait(false);

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
                                        TargetId = decisionId,
                                        Relationship = "INFLUENCES"
                                    });
                                }
                            }
                        }

                        if (edges.Count > 0)
                        {
                            await storage.UpsertEdgesAsync(edges).ConfigureAwait(false);
                        }

                        SendToolResponse(id, $"Successfully recorded decision '{name}' (ID: {decisionId}) and connected it to {edges.Count} nodes.");
                    }
                    break;

                case "record_milestone":
                    {
                        var name = args?["name"]?.ToString();
                        var status = args?["status"]?.ToString();
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(status))
                        {
                            SendError(id, -32602, "Parameters 'name' and 'status' are required");
                            return;
                        }
                        var connectedNodeIds = args?["connectedNodeIds"] as JsonArray;
                        var milestoneId = $"milestone::{Guid.NewGuid().ToString("N").Substring(0, 8)}";

                        var milestoneNode = new GraphNode
                        {
                            Id = milestoneId,
                            Name = name,
                            Type = "Milestone",
                            Content = $"Status: {status}",
                            Properties = new Dictionary<string, string>
                            {
                                ["status"] = status,
                                ["updated"] = DateTime.UtcNow.ToString("o")
                            }
                        };

                        await storage.UpsertNodesAsync(new[] { milestoneNode }).ConfigureAwait(false);

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
                                        TargetId = milestoneId,
                                        Relationship = "AFFECTS"
                                    });
                                }
                            }
                        }

                        if (edges.Count > 0)
                        {
                            await storage.UpsertEdgesAsync(edges).ConfigureAwait(false);
                        }

                        SendToolResponse(id, $"Successfully recorded milestone '{name}' (ID: {milestoneId}) with status '{status}' connected to {edges.Count} nodes.");
                    }
                    break;

                case "record_task":
                    {
                        var name = args?["name"]?.ToString();
                        var content = args?["content"]?.ToString();
                        var status = args?["status"]?.ToString() ?? "Todo";
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            SendError(id, -32602, "Parameter 'name' is required");
                            return;
                        }
                        var connectedNodeIds = args?["connectedNodeIds"] as JsonArray;
                        var taskId = $"task::{Guid.NewGuid().ToString("N").Substring(0, 8)}";

                        var taskNode = new GraphNode
                        {
                            Id = taskId,
                            Name = name,
                            Type = "Task",
                            Content = content ?? $"Status: {status}",
                            Properties = new Dictionary<string, string>
                            {
                                ["status"] = status,
                                ["created"] = DateTime.UtcNow.ToString("o")
                            }
                        };

                        await storage.UpsertNodesAsync(new[] { taskNode }).ConfigureAwait(false);

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
                                        TargetId = taskId,
                                        RelationType = "HasTask"
                                    });
                                }
                            }
                        }

                        if (edges.Count > 0)
                        {
                            await storage.UpsertEdgesAsync(edges).ConfigureAwait(false);
                        }

                        SendToolResponse(id, $"Successfully recorded task '{name}' (ID: {taskId}) connected to {edges.Count} nodes.");
                    }
                    break;

                case "record_question":
                    {
                        var name = args?["name"]?.ToString();
                        var content = args?["content"]?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            SendError(id, -32602, "Parameter 'name' is required");
                            return;
                        }
                        var connectedNodeIds = args?["connectedNodeIds"] as JsonArray;
                        var questionId = $"question::{Guid.NewGuid().ToString("N").Substring(0, 8)}";

                        var questionNode = new GraphNode
                        {
                            Id = questionId,
                            Name = name,
                            Type = "Question",
                            Content = content,
                            Properties = new Dictionary<string, string>
                            {
                                ["status"] = "Open",
                                ["created"] = DateTime.UtcNow.ToString("o")
                            }
                        };

                        await storage.UpsertNodesAsync(new[] { questionNode }).ConfigureAwait(false);

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
                                        TargetId = questionId,
                                        Relationship = "RELATES_TO"
                                    });
                                }
                            }
                        }

                        if (edges.Count > 0)
                        {
                            await storage.UpsertEdgesAsync(edges).ConfigureAwait(false);
                        }

                        SendToolResponse(id, $"Successfully recorded question '{name}' (ID: {questionId}) connected to {edges.Count} nodes.");
                    }
                    break;

                default:
                    SendError(id, -32601, $"Tool not found: '{toolName}'");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendError(id, -32603, $"Internal tool execution error: {ex.Message}");
            Console.Error.WriteLine($"[MCP Tool Error] {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void SendResponse(JsonElement id, object result)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id,
            result = result
        };

        var json = JsonSerializer.Serialize(response);
        Console.WriteLine(json);
    }

    private void SendToolResponse(JsonElement id, string text)
    {
        SendResponse(id, new
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

    private void SendError(JsonElement id, int code, string message)
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

        var json = JsonSerializer.Serialize(response);
        Console.WriteLine(json);
    }
}
