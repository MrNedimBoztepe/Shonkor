// Licensed to Shonkor under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

                var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(q, ct);
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

        // POST /api/ask - generate a natural-language answer grounded in the given node ids.
        // NOTE: intentionally NOT under /api/rag/* — that prefix is reserved for the SaaS, API-key-gated
        // endpoints (ApiKeyMiddleware never loopback-bypasses /api/rag). This is the local dashboard's
        // AI chat, so it lives under /api/* and follows the normal dashboard auth (dev loopback bypass).
        app.MapPost("/api/ask", async (AskRagRequest req, HttpContext context, ProjectManager pm, ISemanticAnalyzer semanticAnalyzer, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query) || req.NodeIds == null || req.NodeIds.Length == 0)
            {
                return Results.BadRequest("Query and NodeIds are required.");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);

                var contextNodes = new List<GraphNode>();
                foreach (var id in req.NodeIds)
                {
                    var node = await storage.GetNodeByIdAsync(id, ct);
                    if (node != null)
                    {
                        contextNodes.Add(node);
                    }
                }

                if (contextNodes.Count == 0)
                {
                    return Results.BadRequest("None of the provided NodeIds were found in the database.");
                }

                var responseText = await semanticAnalyzer.GenerateRAGResponseAsync(req.Query, contextNodes, ct);
                return Results.Ok(new { response = responseText });
            }
            catch (Exception ex)
            {
                return Fail("Failed to generate RAG response.", ex);
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

                var markdown = synthesizer.Synthesize(nodes, edges);
                return Results.Ok(new { Markdown = markdown, NodeCount = nodes.Count, EdgeCount = edges.Count });
            }
            catch (Exception ex)
            {
                return Fail("Capsule synthesis failed.", ex);
            }
        });
    }
}
