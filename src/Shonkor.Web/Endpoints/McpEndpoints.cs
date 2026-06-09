using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web.Endpoints;

public static class McpEndpoints
{
    public static void MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mcp").WithTags("MCP Proxy");

        group.MapPost("/relay", async (
            HttpContext context,
            [FromServices] ProjectManager projectManager,
            [FromServices] ContextCapsuleSynthesizer synthesizer) =>
        {
            // Authentication is enforced centrally by ApiKeyMiddleware (single source of truth).
            // When a request is authenticated via API key, the middleware records the authoritative
            // tenant in HttpContext.Items["AuthenticatedProjectName"] — server-side and unforgeable,
            // unlike a request header. If present, we LOCK the MCP session to that tenant so the
            // per-tool `projectName` argument cannot reach another tenant's graph.
            var authenticatedTenant = context.Items["AuthenticatedProjectName"] as string;
            var isTenantLocked = !string.IsNullOrWhiteSpace(authenticatedTenant);

            string? projectName;
            if (isTenantLocked)
            {
                // SaaS path: the tenant is fixed by the validated API key. Ignore any client-supplied
                // project hints — they must not be able to widen access beyond their key's tenant.
                projectName = authenticatedTenant;
            }
            else
            {
                // Trusted-local path (loopback bypass in Development): derive the project from the
                // request for convenience. Free project selection is intentional here.
                projectName = context.Request.Headers["X-Project-Name"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(projectName))
                {
                    var repoUrl = context.Request.Headers["X-Repo-Url"].FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(repoUrl))
                    {
                        projectName = projectManager.FindProjectByRepoUrl(repoUrl)?.Name;
                    }
                }
            }

            // lockToContextProject prevents the per-tool projectName argument from escaping the
            // authenticated tenant. Only enabled when the request was key-authenticated.
            var handler = new McpRequestHandler(projectManager, synthesizer, projectName, lockToContextProject: isTenantLocked);

            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(body))
            {
                return Results.BadRequest(new { error = "Empty request body" });
            }

            var responseJson = await handler.ProcessJsonRpcMessageAsync(body);

            if (string.IsNullOrWhiteSpace(responseJson))
            {
                // This means the handler returned null (e.g. invalid JSON RPC)
                return Results.BadRequest(new { error = "Invalid JSON-RPC request or no response generated" });
            }

            return Results.Text(responseJson, "application/json");
        })
        .WithName("RelayMcpMessage")
        .WithSummary("Relays an MCP JSON-RPC message to the backend GraphRAG server.");
    }
}
