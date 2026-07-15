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

// Writable settings overlay: the dashboard's AI/tool settings (Ollama URL/model, embedding source,
// streaming, etc.) are written here by the loopback-only /api/settings endpoint, with reloadOnChange so
// a save takes effect on the next request/enrichment cycle without a restart. Machine-local, gitignored;
// secrets never go here (they stay in user-secrets / env). It is inserted so it overrides appsettings.json
// but stays BELOW environment variables / command-line args — deployment env (Docker/k8s) must still win
// over a machine-local dashboard edit (standard .NET precedence: env > JSON files).
{
    var localOverlay = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
    {
        Path = "appsettings.Local.json",
        Optional = true,
        ReloadOnChange = true
    };
    localOverlay.ResolveFileProvider();
    var envIndex = 0;
    for (var i = 0; i < builder.Configuration.Sources.Count; i++)
    {
        if (builder.Configuration.Sources[i] is Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource)
        {
            envIndex = i;
            break;
        }
        envIndex = i + 1; // default: after the last JSON/user-secrets source if no env source is present
    }
    builder.Configuration.Sources.Insert(envIndex, localOverlay);
}

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

// Shared per-directory Roslyn compilation cache: makes incremental semantic relinks (reconcile +
// semantic reindex_file) reuse the compilation (swap one tree) instead of rebuilding it per edit.
builder.Services.AddSingleton<SemanticCompilationCache>();

// Semantic enrichment backend (Ollama) + the background worker that drives it.
//
// DELIBERATELY NO RESILIENCE HANDLER HERE (#179). This registration is where a reader expects to find the
// retry policy — AddStandardResilienceHandler() is one line away in the same package — and adding one would
// be a BUG, not an improvement:
//
//   * The policy already exists, at the CALL SITE (OllamaResilience.Background / .Blocking), because it has to
//     cover the CLI and the bench too — they build their own HttpClient, so a DI-only policy would leave the
//     MCP stdio server (the thing agents actually use) with no retry at all. See OllamaResilience for why.
//   * A handler-level policy would sit INSIDE that pipeline, so every attempt the call-site pipeline makes
//     would itself be retried by the handler — retries nested in retries, multiplying the attempts and the
//     wait against a backend that is already struggling.
//   * The two operations need DIFFERENT policies on the same client (background retries transient failures;
//     the blocking RAG path must never retry a timeout), and one handler can only carry one.
//
// OllamaResiliencePolicyPlacementTests boots this host against a failing backend and counts the attempts that
// reach it, so this is enforced rather than merely asked for: add a handler policy and the count multiplies
// and the test fails.
builder.Services.AddHttpClient<ISemanticAnalyzer, OllamaSemanticAnalyzer>();
builder.Services.AddHttpClient<IEmbeddingService, OllamaEmbeddingService>();
builder.Services.AddHostedService<SemanticEnrichmentService>();

// Drift Layer 3: periodic reconciliation of each project's graph against its working tree (opt-in via
// Drift:ReconcileIntervalSeconds > 0). Catches out-of-band edits that bypassed reindex_file.
builder.Services.AddHostedService<DriftReconciliationService>();

var app = builder.Build();

// Security guardrail (H9): the loopback auth bypass is Development-only. If an operator sets
// Security:AllowLocalBypass=true in a non-Development environment (a common footgun behind a reverse
// proxy, where every request looks like loopback), the flag is IGNORED — warn loudly so the
// misconfiguration is visible rather than silently (in)effective.
if (!app.Environment.IsDevelopment()
    && app.Configuration.GetValue<bool?>("Security:AllowLocalBypass") == true)
{
    app.Logger.LogWarning(
        "Security:AllowLocalBypass=true is set in the {Environment} environment but is IGNORED — the loopback authentication bypass is enabled only in Development. Remove the flag to silence this warning. Every /api request outside Development requires a valid X-API-Key.",
        app.Environment.EnvironmentName);
}

// --- Middleware pipeline ---

// Security headers on every response (#260). The CSP is deliberately a SOURCE ALLOW-LIST, not a strict
// nonce/hash policy: the ATLAS shell still has inline event handlers, inline style attributes and an inline
// script, so keeping 'unsafe-inline' avoids a UI-breaking refactor for little gain — inline XSS is already
// handled by escapeHtml. What the allow-list DOES buy is real: an injected <script src> or stylesheet from an
// unlisted host is blocked, and the object/base/framing vectors are shut (frame-ancestors 'none' also gives
// clickjacking protection). Hosts are exactly what ATLAS loads: cdnjs (d3) and Google Fonts (googleapis for
// the CSS, gstatic for the font files). The legacy dashboard's unpkg/jsdelivr/Prism deps were dropped with it
// (#262).
const string contentSecurityPolicy =
    "default-src 'self'; " +
    "script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; " +
    "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
    "font-src 'self' https://fonts.gstatic.com; " +
    "img-src 'self' data:; " +
    "connect-src 'self'; " +
    "object-src 'none'; " +
    "base-uri 'self'; " +
    "frame-ancestors 'none'";
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["Content-Security-Policy"] = contentSecurityPolicy;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY"; // legacy fallback for browsers predating frame-ancestors
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

// ATLAS is the only UI: redirect the bare root to it. (The legacy /index.html dashboard was removed in #262.)
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path == "/")
    {
        ctx.Response.Redirect("/atlas/");
        return;
    }
    await next();
});

// Serve the dashboard's static assets (HTML/CSS/JS/images) FIRST, before the API-key check.
// The dashboard shell and its assets are public; gating them behind the API key would make the
// whole UI return 401 in production. The static-file middleware short-circuits the pipeline for
// matched files, so only unmatched requests (the /api/* endpoints) reach the ApiKeyMiddleware below.
app.UseDefaultFiles();

app.UseStaticFiles(new StaticFileOptions
{
    // Never cache HTML: the ATLAS shell references versioned assets, so the browser must always re-fetch the
    // shell to learn the current versions. Otherwise a stale cached shell keeps loading old JS/CSS after an
    // update.
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
app.MapSurprisingConnectionsEndpoints();
app.MapInsightsEndpoints();
app.MapIndexEndpoints();
app.MapProjectEndpoints();
app.MapBrowseEndpoints();
app.MapPluginEndpoints();
app.MapSettingsEndpoints();

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
