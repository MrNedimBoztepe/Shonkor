using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using static Shonkor.Web.EndpointHelpers;

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
            // Resolve the embedding backend optionally: a missing registration must NOT 500 the whole
            // relay (every MCP tool routes through here). search_semantic degrades gracefully when null.
            var embeddingService = context.RequestServices.GetService<IEmbeddingService>();
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

            // File parsers enable reindex_file — but ONLY for trusted-local (non-tenant-locked) sessions.
            // In SaaS the tenant's files aren't on this server; running it there would wrongly clear the
            // file's graph. So a tenant-locked session gets null parsers and reindex_file stays disabled.
            //
            // The ACTIVE plugins MUST be merged into the parser list, exactly as IndexEndpoints does: the DI
            // singleton no longer carries a JS/TS parser (that moved to the `shonkor-typescript` plugin, #292),
            // so without merging, a reindex_file of a .ts/.tsx/.js/.jsx file over this HTTP relay would parse
            // it with NO parser, produce zero nodes, and reindex_file would then CLEAR the file's existing
            // JSComponent/IMPORTS nodes from the graph (EditLoopTools: NodesCreated == 0 => cleared) — silent
            // data loss. The per-request plugin load is disposed after the message is processed (finally
            // block) so the sidecar process is torn down and does not leak per request; a PluginHost supplies
            // the logger so plugin diagnostics (timeouts/degradation/parse errors) stay visible.
            var config = context.RequestServices.GetService<IConfiguration>();
            var pluginLoad = AssemblyPluginLoadResult.Empty;
            if (!isTenantLocked && config != null && PluginsEnabled(config))
            {
                var pluginHost = new PluginHost(
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Shonkor.Plugin"));
                pluginLoad = AssemblyPluginLoader.LoadActive(projectManager.WorkspacePath, pluginHost);
            }
            var fileParsers = BuildRelayFileParsers(
                isTenantLocked,
                context.RequestServices.GetService<IEnumerable<IFileParser>>(),
                pluginLoad.Parsers);
            // Shared compilation cache (singleton) so a semantic-project reindex_file refreshes CALLS
            // incrementally. Only for trusted-local sessions, alongside the file parsers it depends on.
            var compilationCache = isTenantLocked ? null : context.RequestServices.GetService<SemanticCompilationCache>();

            // lockToContextProject prevents the per-tool projectName argument from escaping the
            // authenticated tenant. Only enabled when the request was key-authenticated.
            // The embedding backend (Ollama) is wired here so search_semantic works over the HTTP relay.
            // persistentSession: false — this handler lives for exactly one POST, so session-scoped
            // state (set_project's override) cannot be carried; the tool refuses instead of pretending.
            // logger: over the HTTP relay there is no stdio protocol to protect, so tool failures go through
            // the host's logging like any other API error (#256) rather than straight to the process's stderr.
            try
            {
                // Carry the active plugins' graph post-processors too (#319), so a reindex_file over this
                // relay constructs its GraphIndexScanner exactly like the Web index (IndexEndpoints). They are
                // a whole-graph phase run on a full scan only, never on the single-file reindex — so on this
                // path they never execute; the wiring keeps the construction consistent across entry points.
                // Tenant-locked relays get none (no local files → reindex_file is already disabled above).
                var handler = new McpRequestHandler(projectManager, synthesizer, projectName,
                    lockToContextProject: isTenantLocked, embeddingService: embeddingService, fileParsers: fileParsers,
                    compilationCache: compilationCache, persistentSession: false,
                    logger: context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Shonkor.Mcp"),
                    postProcessors: pluginLoad.PostProcessors);

                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(body))
                {
                    return Results.BadRequest(new { error = "Empty request body" });
                }

                var responseJson = await handler.ProcessJsonRpcMessageAsync(body);

                if (string.IsNullOrWhiteSpace(responseJson))
                {
                    // The handler returns null ONLY for a true JSON-RPC notification (no "id" key). A
                    // notification is a valid, complete request that expects no response body — the spec's
                    // answer is 202 Accepted, not 400. (Malformed JSON now yields a -32700 response instead
                    // of null, so it no longer reaches this branch.)
                    return Results.Accepted();
                }

                return Results.Text(responseJson, "application/json");
            }
            finally
            {
                // Per-request teardown: dispose the plugin load so any sidecar process (e.g. #292 TypeScript)
                // is killed instead of leaking one process per relay request. Empty when no plugins loaded.
                // Async teardown (#308): this handler is already async, so await the sidecar's graceful kill
                // rather than blocking a threadpool thread on it via sync-over-async.
                await pluginLoad.DisposeAsync();
            }
        })
        .WithName("RelayMcpMessage")
        .WithSummary("Relays an MCP JSON-RPC message to the backend GraphRAG server.");
    }
}
