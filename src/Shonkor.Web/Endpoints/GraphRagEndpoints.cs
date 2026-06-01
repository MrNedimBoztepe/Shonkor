using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web.Endpoints;

public record RagQueryRequest(string Query, int? Hops);

public static class GraphRagEndpoints
{
    public static void MapGraphRagEndpoints(this WebApplication app)
    {
        // POST /api/rag/query
        // Exposes Shonkor's structural graph directly to external AI Agents (e.g. ChatGPT, Antigravity)
        // using the X-API-Key for multi-tenant security.
        app.MapPost("/api/rag/query", async (RagQueryRequest request, HttpContext context, ProjectManager pm, ContextCapsuleSynthesizer synthesizer) =>
        {
            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return Results.BadRequest("Query is required.");
            }

            try
            {
                var projectName = context.Request.Headers["X-Project-Name"].ToString();
                var storage = string.IsNullOrWhiteSpace(projectName) ? pm.GetActiveStorageProvider() : pm.GetStorageProvider(projectName);

                var searchResults = await storage.SearchAsync(request.Query, 5);
                if (searchResults.Count == 0)
                {
                    return Results.NotFound("No nodes matched the query in the structural graph.");
                }

                var seeds = searchResults.Select(r => r.Node.Id).ToList();
                var maxHops = request.Hops ?? 2;
                var (nodes, edges) = await storage.GetSubgraphAsync(seeds, maxHops);

                // Generate the token-optimized architecture capsule (Markdown)
                var markdown = synthesizer.Synthesize(nodes, edges);

                // Return a clean JSON response tailored for LLMs
                return Results.Ok(new 
                { 
                    Context = markdown,
                    NodesAnalyzed = nodes.Count,
                    EdgesTraversed = edges.Count,
                    ProjectContext = projectName
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] GraphRAG query failed. :: {ex}");
                return Results.Problem("GraphRAG query failed.");
            }
        });
    }
}
