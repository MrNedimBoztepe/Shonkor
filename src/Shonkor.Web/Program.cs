// Licensed to Shonkor under the MIT License.

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.StaticFiles;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Web.Endpoints;
using Shonkor.Web.HealthChecks;
using Shonkor.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// In Production, emit structured (JSON) logs so a container/k8s log pipeline can parse them.
// Development keeps the readable default console.
if (builder.Environment.IsProduction())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole();
}

// Health probes (mapped + exempted from auth below). The "ready" tag separates the readiness
// check (workspace writable + graph store reachable) from plain liveness (process is up).
builder.Services.AddHealthChecks()
    .AddCheck<StorageHealthCheck>("storage", tags: new[] { "ready" });

// --- Composition root ---

// Multi-project registry, rooted at the nearest ancestor workspace containing a shonkor.json.
// Registered lazily (via factory) so tests can substitute a ProjectManager rooted at a temp workspace
// instead of the developer's real one.
builder.Services.AddSingleton(_ => new ProjectManager(FindWorkspacePath()));

// Force-load YamlDotNet into the AppDomain so dynamic plugins can reference it.
_ = typeof(YamlDotNet.Serialization.Deserializer);

// Core file parsers used by the indexing endpoint.
builder.Services.AddSingleton<IEnumerable<IFileParser>>(_ => new List<IFileParser>
{
    new RoslynAstParser(),
    new JavaScriptParser(),
    new PhpModuleParser(),
    new MarkdownHierarchyParser(),
    new GraphQLParser()
});

builder.Services.AddSingleton<ContextCapsuleSynthesizer>();

// Semantic enrichment backend (Ollama) + the background worker that drives it.
builder.Services.AddHttpClient<ISemanticAnalyzer, OllamaSemanticAnalyzer>();
builder.Services.AddHttpClient<IEmbeddingService, OllamaEmbeddingService>();
builder.Services.AddHostedService<SemanticEnrichmentService>();

// Drift Layer 3: periodic reconciliation of each project's graph against its working tree (opt-in via
// Drift:ReconcileIntervalSeconds > 0). Catches out-of-band edits that bypassed reindex_file.
builder.Services.AddHostedService<DriftReconciliationService>();

var app = builder.Build();

// --- Middleware pipeline ---

// Serve the dashboard's static assets (HTML/CSS/JS/images) FIRST, before the API-key check.
// The dashboard shell and its assets are public; gating them behind the API key would make the
// whole UI return 401 in production. The static-file middleware short-circuits the pipeline for
// matched files, so only unmatched requests (the /api/* endpoints) reach the ApiKeyMiddleware below.
app.UseDefaultFiles();

var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".po"] = "text/plain";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider,
    // Never cache HTML: the shell references versioned assets (app.js?v=, app.css?v=), so the browser
    // must always re-fetch index.html to learn the current versions. Otherwise a stale cached shell
    // keeps loading old JS/CSS (e.g. an old API path) even after an update.
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.Name;
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
    }
});

// API key middleware for SaaS multi-tenancy. Runs AFTER static files so it only authenticates
// dynamic API requests, not public UI assets.
app.UseMiddleware<Shonkor.Web.Middleware.ApiKeyMiddleware>();

// --- Endpoints ---

// Health probes (public — exempted from the API key in ApiKeyMiddleware).
// Liveness: the process is up and serving (no dependency checks run).
var livenessOptions = new HealthCheckOptions { Predicate = _ => false };
app.MapHealthChecks("/health", livenessOptions);
app.MapHealthChecks("/health/live", livenessOptions);
// Readiness: the workspace is writable and the active graph store answers — i.e. the app can
// actually do work. Orchestrators should gate traffic on this one.
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// SaaS / integration endpoints.
app.MapGraphRagEndpoints();
app.MapWebhookEndpoints();
app.MapMcpEndpoints();
app.MapAdminEndpoints();

// Dashboard / local API endpoints.
app.MapStatsEndpoints();
app.MapSearchEndpoints();
app.MapIndexEndpoints();
app.MapProjectEndpoints();
app.MapBrowseEndpoints();
app.MapPluginEndpoints();

app.Run();

// Walks up from the current directory to the nearest ancestor containing a shonkor.json,
// which defines the workspace root for the project registry.
static string FindWorkspacePath()
{
    var currentDir = Directory.GetCurrentDirectory();
    var dir = currentDir;
    while (!string.IsNullOrEmpty(dir))
    {
        if (File.Exists(Path.Combine(dir, "shonkor.json")))
        {
            return dir;
        }
        var parent = Directory.GetParent(dir);
        if (parent == null || parent.FullName == dir)
        {
            break;
        }
        dir = parent.FullName;
    }
    return currentDir;
}

// Exposed so integration tests can host the app via WebApplicationFactory<Program>.
public partial class Program { }
