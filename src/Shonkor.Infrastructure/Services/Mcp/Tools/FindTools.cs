// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Shonkor.Core.Services;
using static Shonkor.Infrastructure.Services.Mcp.McpToolHelpers;

namespace Shonkor.Infrastructure.Services.Mcp.Tools;

/// <summary>FTS5 keyword search; compact one-line-per-hit by default, full JSON when verbose.</summary>
public sealed class SearchGraphTool : IMcpTool
{
    public string Name => "search_graph";

    public object GetSchema() => new
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
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var query = args?["query"]?.ToString();
        if (string.IsNullOrWhiteSpace(query))
        {
            return SendError(id, -32602, "Parameter 'query' is required");
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var limit = ReadInt(args?["limit"], 10);
        var verbose = args?["verbose"]?.GetValue<bool>() ?? false;
        var typeFilter = args?["type"]?.ToString();
        var results = await storage.SearchAsync(query, limit, filterType: typeFilter).ConfigureAwait(false);
        var basePath = ctx.GetProjectBasePath(projectName);

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
}

/// <summary>Minimal "where is X?" locator: one 'name -> file:line | summary' line per hit.</summary>
public sealed class LocateTool : IMcpTool
{
    public string Name => "locate";

    public object GetSchema() => new
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
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var query = args?["query"]?.ToString();
        if (string.IsNullOrWhiteSpace(query))
        {
            return SendError(id, -32602, "Parameter 'query' is required");
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var limit = ReadInt(args?["limit"], 15);
        var results = await storage.SearchAsync(query, limit).ConfigureAwait(false);
        if (results.Count == 0)
        {
            return SendToolResponse(id, $"No matches for '{query}'.");
        }

        var basePath = ctx.GetProjectBasePath(projectName);
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
}

/// <summary>Vector/meaning search. Capability-gated: only available when an embedding backend is wired.</summary>
public sealed class SearchSemanticTool : IMcpTool
{
    public string Name => "search_semantic";

    public bool IsAvailable(McpToolContext ctx) => ctx.HasEmbeddingService;

    public object GetSchema() => new
    {
        name = "search_semantic",
        description = "Concept/meaning-based search via vector embeddings (e.g. 'where is authentication handled?'), complementing the keyword-based search_graph. Returns 'type  name  handle  — summary' per hit. Requires nodes that have been embedded by the enrichment worker.",
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
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var query = args?["query"]?.ToString();
        if (string.IsNullOrWhiteSpace(query))
        {
            return SendError(id, -32602, "Parameter 'query' is required");
        }
        if (ctx.EmbeddingService == null)
        {
            return SendToolResponse(id, "Semantic search is unavailable here (no embedding backend wired). Use search_graph (FTS) instead, or run via the web server with Ollama.");
        }
        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var limit = ReadInt(args?["limit"], 10);
        var basePath = ctx.GetProjectBasePath(projectName);

        var embedding = await ctx.EmbeddingService.GenerateEmbeddingAsync(query, Shonkor.Core.Interfaces.EmbeddingKind.Query).ConfigureAwait(false);
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
}

/// <summary>
/// Hybrid search: Reciprocal Rank Fusion of FTS (keyword/BM25) and vector similarity — combines the
/// exact-token strength of <c>search_graph</c> with the meaning strength of <c>search_semantic</c>.
/// Capability-gated on an embedding backend; without embedded nodes it effectively returns the FTS ranking.
/// </summary>
public sealed class SearchHybridTool : IMcpTool
{
    public string Name => "search_hybrid";

    public bool IsAvailable(McpToolContext ctx) => ctx.HasEmbeddingService;

    public object GetSchema() => new
    {
        name = "search_hybrid",
        description = "Best-of-both search: fuses keyword (FTS) and vector (meaning) results via Reciprocal Rank Fusion. Use when a query has both concrete identifiers and a conceptual intent. Returns 'type  name  handle  — summary' per hit. Requires embedded nodes for the vector half; otherwise ranks like search_graph.",
        inputSchema = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "The search text — identifiers and/or a natural-language intent." },
                limit = new { type = "integer", description = "Max number of results (default 10)." },
                projectName = new { type = "string", description = "Optional project context name. If omitted, uses the active project." }
            },
            required = new[] { "query" }
        }
    };

    public async Task<string> ExecuteAsync(JsonElement id, JsonObject? args, McpToolContext ctx)
    {
        var query = args?["query"]?.ToString();
        if (string.IsNullOrWhiteSpace(query))
        {
            return SendError(id, -32602, "Parameter 'query' is required");
        }

        var projectName = args?["projectName"]?.ToString();
        var storage = await ctx.GetStorageAsync(projectName).ConfigureAwait(false);
        var limit = ReadInt(args?["limit"], 10);
        var basePath = ctx.GetProjectBasePath(projectName);

        // Fetch a wider candidate set from each retriever (limit*2), then fuse and take the top `limit`.
        var ftsResults = await storage.SearchAsync(query, limit * 2).ConfigureAwait(false);

        IReadOnlyList<Shonkor.Core.Models.SearchResult> semResults = [];
        if (ctx.EmbeddingService != null)
        {
            try
            {
                var embedding = await ctx.EmbeddingService.GenerateEmbeddingAsync(query, Shonkor.Core.Interfaces.EmbeddingKind.Query).ConfigureAwait(false);
                if (embedding is { Length: > 0 })
                {
                    semResults = await storage.SearchSemanticAsync(embedding, limit * 2).ConfigureAwait(false);
                }
            }
            catch
            {
                // Embedding backend hiccup — fall back to FTS-only fusion rather than failing the query.
            }
        }

        var fused = HybridFusion.ReciprocalRankFusion(ftsResults, semResults, limit);
        if (fused.Count == 0)
        {
            return SendToolResponse(id, $"No hybrid matches for '{query}'.");
        }

        var lines = fused.Select(r =>
        {
            var summary = !string.IsNullOrEmpty(r.Node.Summary) ? $"\t— {r.Node.Summary}" : "";
            return $"{r.Node.Type}\t{r.Node.Name}\t{ToHandle(r.Node.Id, basePath)}{summary}";
        });
        return SendToolResponse(id, string.Join("\n", lines));
    }
}
