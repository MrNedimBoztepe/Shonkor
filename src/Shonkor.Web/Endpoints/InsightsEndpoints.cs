// Licensed to Shonkor under the MIT License.

using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;

using static Shonkor.Web.EndpointHelpers;

namespace Shonkor.Web.Endpoints;

/// <summary>
/// Whole-graph topological insights for the dashboard's Insights panel — the REST twins of the
/// <c>hotspots</c> and <c>clusters</c> MCP tools (surprising-connections has its own endpoint). Both are
/// deterministic and model-free; they run <see cref="GraphAnalytics"/> over the coupling subgraph
/// (structural containment excluded), so they surface change-risk nodes and isolated/dead-code clusters.
/// </summary>
public static class InsightsEndpoints
{
    public static void MapInsightsEndpoints(this WebApplication app)
    {
        // Resolved once here and captured by the lambdas below (#256) — see EndpointHelpers.ApiLogger.
        var log = app.ApiLogger();
        // GET /api/insights/hotspots — change-risk "god nodes" ranked by betweenness centrality.
        app.MapGet("/api/insights/hotspots", async (int? limit, HttpContext context, ProjectManager pm, CancellationToken ct) =>
        {
            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                var take = Math.Clamp(limit ?? 15, 1, 100);

                var nodes = await storage.GetAllNodesAsync(ct);
                var edges = await storage.GetAllEdgesAsync(ct);
                var byId = nodes.ToDictionary(n => n.Id);

                var ranked = GraphAnalytics.Centrality(nodes, edges)
                    .Where(s => s.Degree > 0)
                    .OrderByDescending(s => s.Betweenness)
                    .ThenByDescending(s => s.Degree)
                    .Take(take)
                    .Select(s =>
                    {
                        var node = byId.GetValueOrDefault(s.NodeId);
                        return new
                        {
                            id = s.NodeId,
                            name = node?.Name ?? s.NodeId,
                            type = node?.Type ?? "?",
                            filePath = node?.FilePath,
                            startLine = node?.StartLine,
                            betweenness = Math.Round(s.Betweenness, 2),
                            degree = s.Degree
                        };
                    })
                    .ToList();

                return Results.Ok(new { hotspots = ranked });
            }
            catch (Exception ex)
            {
                return Fail(log, "Hotspot analysis failed.", ex);
            }
        });

        // GET /api/insights/clusters — modularity communities (default) or connected components.
        app.MapGet("/api/insights/clusters", async (string? mode, int? maxSmall, HttpContext context, ProjectManager pm, CancellationToken ct) =>
        {
            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                var components = (mode ?? "modularity").ToLowerInvariant() is "components" or "component";
                var smallCount = Math.Clamp(maxSmall ?? 10, 1, 50);

                var nodes = await storage.GetAllNodesAsync(ct);
                var edges = await storage.GetAllEdgesAsync(ct);
                var byId = nodes.ToDictionary(n => n.Id);

                var communities = components
                    ? GraphAnalytics.DetectCommunities(nodes, edges)
                    : GraphAnalytics.DetectModularityCommunities(nodes, edges);

                if (communities.Count == 0)
                {
                    return Results.Ok(new { mode = components ? "components" : "modularity", count = 0, largest = 0, clusters = Array.Empty<object>() });
                }

                // Group node -> clusterId into clusters, smallest first (small = isolated / likely-dead code).
                var clusters = communities
                    .GroupBy(kv => kv.Value, kv => kv.Key)
                    .Select(g => g.ToList())
                    .OrderBy(members => members.Count)
                    .ToList();

                var smallest = clusters.Take(smallCount).Select(members => new
                {
                    size = members.Count,
                    members = members.Take(12).Select(mid =>
                    {
                        var n = byId.GetValueOrDefault(mid);
                        return new { id = mid, name = n?.Name ?? mid, type = n?.Type ?? "?", filePath = n?.FilePath };
                    }),
                    more = Math.Max(0, members.Count - 12)
                });

                return Results.Ok(new
                {
                    mode = components ? "components" : "modularity",
                    count = clusters.Count,
                    largest = clusters[^1].Count,
                    clusters = smallest
                });
            }
            catch (Exception ex)
            {
                return Fail(log, "Cluster analysis failed.", ex);
            }
        });
    }
}
