// Licensed to Shonkor under the MIT License.

using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web.Endpoints;

/// <summary>Request to list surprising connections (semantically similar but structurally unlinked node pairs).</summary>
public record SurprisingListRequest(double? MinSimilarity, int? Limit);

/// <summary>Request to explain one surprising connection via the local LLM.</summary>
public record SurprisingExplainRequest(string SourceId, string TargetId);

public static class SurprisingConnectionsEndpoints
{
    // Bound the O(N²) similarity comparison; embeddings beyond this cap are not compared.
    private const int MaxEmbeddedNodes = 1000;

    public static void MapSurprisingConnectionsEndpoints(this WebApplication app)
    {
        // POST /api/surprising-connections — embedding-derived pairs that look related but have no direct edge.
        app.MapPost("/api/surprising-connections", async (SurprisingListRequest? req, HttpContext context, ProjectManager pm, CancellationToken ct) =>
        {
            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                var nodes = await storage.GetNodesWithEmbeddingsAsync(MaxEmbeddedNodes, ct);
                if (nodes.Count < 2)
                {
                    return Results.Ok(new { pairs = Array.Empty<object>(), note = "Fewer than two embedded nodes — run an enrichment/embedding pass first." });
                }

                var edges = await storage.GetAllEdgesAsync(ct);
                var minSim = Math.Clamp(req?.MinSimilarity ?? 0.85, 0.0, 1.0);
                var limit = Math.Clamp(req?.Limit ?? 20, 1, 100);
                var pairs = GraphAnalytics.SurprisingConnections(nodes, edges, minSim, limit);

                var byId = nodes.ToDictionary(n => n.Id);
                var result = pairs.Select(p => new
                {
                    sourceId = p.SourceId,
                    targetId = p.TargetId,
                    sourceName = byId.TryGetValue(p.SourceId, out var s) ? s.Name : p.SourceId,
                    targetName = byId.TryGetValue(p.TargetId, out var t) ? t.Name : p.TargetId,
                    similarity = p.Similarity,
                    // Embedding-derived, so surfaced strictly as a hint — never a proven edge.
                    provenance = "INFERRED"
                });
                return Results.Ok(new { pairs = result });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] surprising-connections failed. :: {ex}");
                return Results.Problem("Surprising-connection detection failed.");
            }
        });

        // POST /api/surprising-connections/explain — a local-LLM hypothesis for one pair, labelled INFERRED.
        app.MapPost("/api/surprising-connections/explain", async (SurprisingExplainRequest req, HttpContext context, ProjectManager pm, ISemanticAnalyzer analyzer, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req?.SourceId) || string.IsNullOrWhiteSpace(req?.TargetId))
            {
                return Results.BadRequest("SourceId and TargetId are required.");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                var a = await storage.GetNodeByIdAsync(req.SourceId, ct);
                var b = await storage.GetNodeByIdAsync(req.TargetId, ct);
                if (a is null || b is null)
                {
                    return Results.NotFound("One or both nodes were not found in the graph.");
                }

                var explanation = await SurprisingConnectionExplainer.ExplainAsync(analyzer, a, b, ct);
                return Results.Ok(new { explanation, inferred = true });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] surprising-connections explain failed. :: {ex}");
                return Results.Problem("Failed to generate the surprising-connection explanation.");
            }
        });
    }
}
