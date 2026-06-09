// Licensed to Shonkor under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shonkor.Infrastructure.Services;

using static Shonkor.Web.EndpointHelpers;

namespace Shonkor.Web.Endpoints;

/// <summary>Dashboard read/stats endpoints and interaction-node status updates.</summary>
public static class StatsEndpoints
{
    public static void MapStatsEndpoints(this WebApplication app)
    {
        // GET /api/stats - graph statistics for the active/selected project.
        app.MapGet("/api/stats", async (HttpContext context, ProjectManager pm, CancellationToken ct) =>
        {
            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                return Results.Ok(await storage.GetStatisticsAsync(ct));
            }
            catch (Exception ex)
            {
                return Fail("Request failed.", ex);
            }
        });

        // GET /api/interactions - all interaction nodes (Task/Decision/Question/Milestone).
        app.MapGet("/api/interactions", async (HttpContext context, ProjectManager pm, CancellationToken ct) =>
        {
            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);
                var interactions = await storage.GetNodesByTypesAsync(new[] { "Task", "Decision", "Question", "Milestone" }, ct);
                return Results.Ok(interactions);
            }
            catch (Exception ex)
            {
                return Fail("Request failed.", ex);
            }
        });

        // POST /api/interactions/status - update an interaction node's status from the dashboard.
        app.MapPost("/api/interactions/status", async (UpdateStatusRequest req, HttpContext context, ProjectManager pm, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Status))
            {
                return Results.BadRequest("Both 'id' and 'status' are required.");
            }

            try
            {
                var storage = await pm.GetStorageForRequestAsync(context, ct);

                var node = await storage.GetNodeByIdAsync(req.Id, ct);
                if (node == null)
                {
                    return Results.NotFound($"Node '{req.Id}' not found.");
                }

                var properties = new Dictionary<string, string>(node.Properties)
                {
                    ["status"] = req.Status,
                    ["updated"] = DateTime.UtcNow.ToString("o")
                };

                var updated = node with { Properties = properties };
                await storage.UpsertNodesAsync(new[] { updated }, ct);

                return Results.Ok(new { Message = "Status updated.", node.Id, req.Status });
            }
            catch (Exception ex)
            {
                return Fail("Failed to update status.", ex);
            }
        });
    }
}
