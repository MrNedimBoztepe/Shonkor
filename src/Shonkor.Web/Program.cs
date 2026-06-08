// Licensed to Shonkor under the MIT License.

using Microsoft.AspNetCore.StaticFiles;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Web.Endpoints;
using Shonkor.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Composition root ---

// Multi-project registry, rooted at the nearest ancestor workspace containing a shonkor.json.
var projectManager = new ProjectManager(FindWorkspacePath());
builder.Services.AddSingleton(projectManager);

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

var app = builder.Build();

// --- Middleware pipeline ---

// Serve the dashboard's static assets (HTML/CSS/JS/images) FIRST, before the API-key check.
// The dashboard shell and its assets are public; gating them behind the API key would make the
// whole UI return 401 in production. The static-file middleware short-circuits the pipeline for
// matched files, so only unmatched requests (the /api/* endpoints) reach the ApiKeyMiddleware below.
app.UseDefaultFiles();

var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".po"] = "text/plain";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypeProvider });

// API key middleware for SaaS multi-tenancy. Runs AFTER static files so it only authenticates
// dynamic API requests, not public UI assets.
app.UseMiddleware<Shonkor.Web.Middleware.ApiKeyMiddleware>();

// --- Endpoints ---

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
