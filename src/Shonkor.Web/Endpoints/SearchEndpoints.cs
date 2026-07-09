// Licensed to Shonkor under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;

using static Shonkor.Web.EndpointHelpers;

namespace Shonkor.Web.Endpoints;

/// <summary>Search, semantic search, RAG answer, subgraph traversal and capsule synthesis endpoints.</summary>
public static class SearchEndpoints
{
    public static void MapSearchEndpoints(this WebApplication app)
    {
        // GET /api/search - full-text (FTS5) search over nodes.
        app.MapGet("/api/search", async (string q, int? limit, int? offset, string? type, HttpContext context, ProjectManager pm, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.BadRequest("Query string 'q' cannot be empty.");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                var results = await storage.SearchAsync(q, limit ?? 15, offset ?? 0, type, ct);
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Fail("Search failed.", ex);
            }
        });

        // GET /api/search/semantic - vector-embedding similarity search.
        app.MapGet("/api/search/semantic", async (string q, int? limit, HttpContext context, ProjectManager pm, IEmbeddingService embeddingService, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.BadRequest("Query string 'q' cannot be empty.");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);

                var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(q, EmbeddingKind.Query, ct);
                if (queryEmbedding == null || queryEmbedding.Length == 0)
                {
                    return Fail("Failed to generate embedding for the search query.", new Exception("Embedding generation returned empty."));
                }

                var results = await storage.SearchSemanticAsync(queryEmbedding, limit ?? 15, ct);
                return Results.Ok(results);
            }
            catch (Exception ex)
            {
                return Fail("Semantic search failed.", ex);
            }
        });

        // GET /api/search/hybrid - Reciprocal Rank Fusion of FTS (BM25) + vector similarity (TICKET-008).
        // Additive endpoint: existing /api/search and /api/search/semantic are unchanged. Falls back to
        // FTS-only when no embedding backend is reachable, so it never hard-fails the dashboard.
        app.MapGet("/api/search/hybrid", async (string q, int? limit, HttpContext context, ProjectManager pm, IEmbeddingService embeddingService, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.BadRequest("Query string 'q' cannot be empty.");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                var k = limit ?? 15;

                var ftsResults = await storage.SearchAsync(q, k * 2, 0, null, ct);

                IReadOnlyList<SearchResult> semResults = Array.Empty<SearchResult>();
                try
                {
                    var qv = await embeddingService.GenerateEmbeddingAsync(q, EmbeddingKind.Query, ct);
                    if (qv is { Length: > 0 })
                    {
                        semResults = await storage.SearchSemanticAsync(qv, k * 2, ct);
                    }
                }
                catch
                {
                    // Embedding backend down — degrade gracefully to FTS-only fusion (no throw).
                }

                var fused = HybridFusion.ReciprocalRankFusion(ftsResults, semResults, k);
                return Results.Ok(fused);
            }
            catch (Exception ex)
            {
                return Fail("Hybrid search failed.", ex);
            }
        });

        // POST /api/ask - generate a natural-language answer grounded in the given node ids.
        // NOTE: intentionally NOT under /api/rag/* — that prefix is reserved for the SaaS, API-key-gated
        // endpoints (ApiKeyMiddleware never loopback-bypasses /api/rag). This is the local dashboard's
        // AI chat, so it lives under /api/* and follows the normal dashboard auth (dev loopback bypass).
        app.MapPost("/api/ask", async (AskRagRequest req, HttpContext context, ProjectManager pm, ISemanticAnalyzer semanticAnalyzer, IConfiguration config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query) || req.NodeIds == null || req.NodeIds.Length == 0)
            {
                return Results.BadRequest("Query and NodeIds are required.");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                var prep = await GroundingPrep.BuildAsync(req, storage, config, ct);

                if (prep.NoEvidence)
                {
                    // Deterministic abstention WITHOUT an LLM call: nothing cleared the relevance floor.
                    return Results.Ok(new { response = prep.AbstentionText, grounded = false });
                }
                if (prep.ContextNodes.Count == 0)
                {
                    return Results.BadRequest("None of the provided NodeIds were found in the database.");
                }

                var responseText = await semanticAnalyzer.GenerateRAGResponseAsync(req.Query, prep.ContextNodes, prep.Options, ct);
                return Results.Ok(new { response = responseText, context = prep.ContextMetadata() });
            }
            catch (Exception ex)
            {
                return Fail("Failed to generate RAG response.", ex);
            }
        });

        // POST /api/ask/stream - same as /api/ask but streams the answer token-by-token (TICKET-104), so
        // the dashboard shows first tokens immediately. Writes text/plain chunks; disable streaming with
        // Features:StreamingAnswers=false (then the full answer is written in one chunk).
        app.MapPost("/api/ask/stream", async (AskRagRequest req, HttpContext context, ProjectManager pm, ISemanticAnalyzer semanticAnalyzer, IConfiguration config, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query) || req.NodeIds == null || req.NodeIds.Length == 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Query and NodeIds are required.", ct);
                return;
            }

            var storage = await pm.GetStorageForRequestAsync(context, ct);
            var prep = await GroundingPrep.BuildAsync(req, storage, config, ct);

            if (prep.NoEvidence)
            {
                // Deterministic abstention WITHOUT an LLM call — write it as the (single-chunk) answer.
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(prep.AbstentionText, ct);
                return;
            }
            if (prep.ContextNodes.Count == 0)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("None of the provided NodeIds were found in the database.", ct);
                return;
            }

            context.Response.ContentType = "text/plain; charset=utf-8";
            // Context metadata as response headers (TICKET-205) — available before the body streams, so the
            // UI can show "Context: N nodes, M truncated" without polluting the text/plain answer stream.
            context.Response.Headers["X-Context-Nodes-Used"] = prep.Plan.Nodes.Count.ToString();
            context.Response.Headers["X-Context-Truncated"] = prep.Plan.TruncatedNodeIds.Count.ToString();
            context.Response.Headers["X-Context-Dropped"] = prep.Plan.DroppedNodeCount.ToString();
            var streamingEnabled = config.GetValue<bool?>("Features:StreamingAnswers") ?? true;

            try
            {
                if (streamingEnabled)
                {
                    await foreach (var chunk in semanticAnalyzer.StreamRAGResponseAsync(req.Query, prep.ContextNodes, prep.Options, ct).WithCancellation(ct))
                    {
                        await context.Response.WriteAsync(chunk, ct);
                        await context.Response.Body.FlushAsync(ct);
                    }
                }
                else
                {
                    var full = await semanticAnalyzer.GenerateRAGResponseAsync(req.Query, prep.ContextNodes, prep.Options, ct);
                    await context.Response.WriteAsync(full, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected or request aborted — nothing to do; the response is already partial.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Streaming RAG response failed. :: {ex.Message}");
                // Headers/body may already be sent; append a marker rather than trying to set a status code.
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
                await context.Response.WriteAsync("\n[Error streaming the answer]", CancellationToken.None);
            }
        });

        // GET /api/subgraph - traverse N hops out from comma-separated seed node ids.
        app.MapGet("/api/subgraph", async (string seeds, int? hops, HttpContext context, ProjectManager pm, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(seeds))
            {
                return Results.BadRequest("Query parameter 'seeds' is required (comma-separated list of Node IDs).");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                var seedList = seeds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var (nodes, edges) = await storage.GetSubgraphAsync(seedList, hops ?? 2, ct);
                return Results.Ok(new { Nodes = nodes, Edges = edges });
            }
            catch (Exception ex)
            {
                return Fail("Subgraph traversal failed.", ex);
            }
        });

        // GET /api/node/references - impact analysis for ONE node: its direct dependents (incoming edges)
        // and its direct dependencies (outgoing edges), grouped per side with relation + AI summary.
        // Both directions come from a single incident-edge fetch, so the panel needs only one round-trip.
        app.MapGet("/api/node/references", async (string id, HttpContext context, ProjectManager pm, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.BadRequest("Query parameter 'id' is required.");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);

                var node = await storage.GetNodeByIdAsync(id, ct);
                if (node == null)
                {
                    return Results.NotFound(new { error = $"Node '{id}' not found." });
                }

                var (edges, neighbours) = await storage.GetIncidentEdgesAsync(id, ct);

                object Reference(string otherId, string relation)
                {
                    var n = neighbours.GetValueOrDefault(otherId);
                    return new
                    {
                        id = otherId,
                        name = n?.Name ?? otherId,
                        type = n?.Type ?? "Unknown",
                        relation,
                        summary = n?.Summary,
                        filePath = n?.FilePath,
                        startLine = n?.StartLine
                    };
                }

                var incoming = edges
                    .Where(e => e.TargetId == id && e.SourceId != id)
                    .Select(e => Reference(e.SourceId, e.Relationship))
                    .ToList();
                var outgoing = edges
                    .Where(e => e.SourceId == id && e.TargetId != id)
                    .Select(e => Reference(e.TargetId, e.Relationship))
                    .ToList();

                return Results.Ok(new
                {
                    node = new { node.Id, node.Name, node.Type, node.Summary, node.FilePath, node.StartLine },
                    incoming,
                    outgoing
                });
            }
            catch (Exception ex)
            {
                return Fail("Failed to load node references.", ex);
            }
        });

        // GET /api/path - shortest connection between two nodes (by id), as an ordered chain with each
        // hop's real relation + direction. Backs the dashboard's "Find Path" tool.
        app.MapGet("/api/path", async (string from, string to, int? maxHops, HttpContext context, ProjectManager pm, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                return Results.BadRequest("Query parameters 'from' and 'to' (node ids) are required.");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);

                var fromNode = await storage.GetNodeByIdAsync(from, ct);
                if (fromNode == null) return Results.NotFound(new { error = $"Start node '{from}' not found." });
                var toNode = await storage.GetNodeByIdAsync(to, ct);
                if (toNode == null) return Results.NotFound(new { error = $"Target node '{to}' not found." });

                var hops = Math.Clamp(maxHops ?? 6, 1, 20);
                var path = await GraphPathFinder.FindPathAsync(storage, from, to, hops, ct);
                if (path == null)
                {
                    return Results.Ok(new { found = false, message = $"No path between these nodes within {hops} hops." });
                }

                var steps = path.Select((s, i) => new
                {
                    id = s.Node.Id,
                    name = s.Node.Name,
                    type = s.Node.Type,
                    summary = s.Node.Summary,
                    relation = s.Relation,
                    // null for the source; "forward" = prev --rel--> this, "backward" = prev <--rel-- this.
                    direction = i == 0 ? null : (s.Forward ? "forward" : "backward")
                });

                return Results.Ok(new { found = true, hops = path.Count - 1, steps });
            }
            catch (Exception ex)
            {
                return Fail("Path finding failed.", ex);
            }
        });

        // POST /api/capsule - synthesize a token-optimized capsule for nodes matching the query.
        app.MapPost("/api/capsule", async (CapsuleRequest request, HttpContext context, ProjectManager pm, ContextCapsuleSynthesizer synthesizer, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return Results.BadRequest("Request 'query' is required.");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                var searchResults = await storage.SearchAsync(request.Query, 5, 0, null, ct);
                if (searchResults.Count == 0)
                {
                    return Results.NotFound("No seed nodes matched the capsule query.");
                }

                var seeds = searchResults.Select(r => r.Node.Id).ToList();
                var (nodes, edges) = await storage.GetSubgraphAsync(seeds, request.Hops ?? 2, ct);

                // Budget-aware capsule (TICKET-003): seeds in full, remainder up to a ~3k-token code budget.
                var markdown = synthesizer.Synthesize(nodes, edges,
                    new CapsuleOptions { SeedIds = seeds, MaxContentChars = 12000, MaxNodes = 40 });
                return Results.Ok(new { Markdown = markdown, NodeCount = nodes.Count, EdgeCount = edges.Count });
            }
            catch (Exception ex)
            {
                return Fail("Capsule synthesis failed.", ex);
            }
        });
    }
}
