// Licensed to Shonkor under the MIT License.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Shonkor.Core;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;
using Shonkor.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Register ProjectManager
var workspacePath = FindWorkspacePath();
var projectManager = new ProjectManager(workspacePath);
builder.Services.AddSingleton(projectManager);

// Force load YamlDotNet into AppDomain so dynamic plugins can reference it
_ = typeof(YamlDotNet.Serialization.Deserializer);

// Register all core parsers for indexing endpoint
builder.Services.AddSingleton<IEnumerable<IFileParser>>(sp => new List<IFileParser>
{
    new RoslynAstParser(),
    new JavaScriptParser(),
    new PhpModuleParser(),

    new MarkdownHierarchyParser(),
    new GraphQLParser()
});

builder.Services.AddSingleton<ContextCapsuleSynthesizer>();

var app = builder.Build();

// Register the custom API Key Middleware for SaaS multi-tenancy
app.UseMiddleware<Shonkor.Web.Middleware.ApiKeyMiddleware>();

// Enable serving index.html and static assets
app.UseDefaultFiles();

var provider = new FileExtensionContentTypeProvider();
provider.Mappings[".po"] = "text/plain";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider
});

// Register SaaS Endpoints
app.MapGraphRagEndpoints();
app.MapWebhookEndpoints();

// === Endpoints ===

// 1. GET /api/stats - Get Graph Statistics for active project
app.MapGet("/api/stats", async (HttpContext context, ProjectManager pm, CancellationToken ct) =>
{
    try
    {
        var projectName = context.Request.Headers["X-Project-Name"].ToString();
        var storage = string.IsNullOrEmpty(projectName) ? pm.GetActiveStorageProvider() : pm.GetStorageProvider(projectName);
        var stats = await storage.GetStatisticsAsync();
        return Results.Ok(stats);
    }
    catch (Exception ex)
    {
        return Fail("Request failed.", ex);
    }
});

app.MapGet("/api/interactions", async (HttpContext context, ProjectManager pm, CancellationToken ct) =>
{
    try
    {
        var projectName = context.Request.Headers["X-Project-Name"].ToString();
        var storage = string.IsNullOrEmpty(projectName) ? pm.GetActiveStorageProvider() : pm.GetStorageProvider(projectName);
        var interactions = await storage.GetNodesByTypesAsync(new[] { "Task", "Decision", "Question", "Milestone" }, ct);

        return Results.Ok(interactions);
    }
    catch (Exception ex)
    {
        return Fail("Request failed.", ex);
    }
});

// Update the status of an interaction node (Task/Question/Decision/Milestone) from the dashboard.
app.MapPost("/api/interactions/status", async (UpdateStatusRequest req, HttpContext context, ProjectManager pm, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Status))
    {
        return Results.BadRequest("Both 'id' and 'status' are required.");
    }

    try
    {
        var projectName = context.Request.Headers["X-Project-Name"].ToString();
        var storage = string.IsNullOrEmpty(projectName) ? pm.GetActiveStorageProvider() : pm.GetStorageProvider(projectName);

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

// 2. GET /api/search - Semantic search over nodes in active project
app.MapGet("/api/search", async (string q, int? limit, string? type, HttpContext context, ProjectManager pm, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest("Query string 'q' cannot be empty.");
    }

    try
    {
        var projectName = context.Request.Headers["X-Project-Name"].ToString();
        var storage = pm.GetStorageProvider(projectName);
        var maxResults = limit ?? 15;
        var results = await storage.SearchAsync(q, maxResults, type, ct);
        return Results.Ok(results);
    }
    catch (Exception ex)
    {
        return Fail("Search failed.", ex);
    }
});

// 3. GET /api/subgraph - Traverse subgraph N hops out from seeds in active project
app.MapGet("/api/subgraph", async (string seeds, int? hops, HttpContext context, ProjectManager pm, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(seeds))
    {
        return Results.BadRequest("Query parameter 'seeds' is required (comma-separated list of Node IDs).");
    }

    try
    {
        var projectName = context.Request.Headers["X-Project-Name"].ToString();
        var storage = pm.GetStorageProvider(projectName);
        var seedList = seeds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var maxHops = hops ?? 2;
        var (nodes, edges) = await storage.GetSubgraphAsync(seedList, maxHops, ct);
        return Results.Ok(new { Nodes = nodes, Edges = edges });
    }
    catch (Exception ex)
    {
        return Fail("Subgraph traversal failed.", ex);
    }
});

// 4. POST /api/capsule - Synthesize capsule for matching seeds in active project
app.MapPost("/api/capsule", async (CapsuleRequest request, HttpContext context, ProjectManager pm, ContextCapsuleSynthesizer synthesizer, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        return Results.BadRequest("Request 'query' is required.");
    }

    try
    {
        var projectName = context.Request.Headers["X-Project-Name"].ToString();
        var storage = pm.GetStorageProvider(projectName);
        var searchResults = await storage.SearchAsync(request.Query, 5, null, ct);
        if (searchResults.Count == 0)
        {
            return Results.NotFound("No seed nodes matched the capsule query.");
        }

        var seeds = searchResults.Select(r => r.Node.Id).ToList();
        var maxHops = request.Hops ?? 2;
        var (nodes, edges) = await storage.GetSubgraphAsync(seeds, maxHops, ct);

        var markdown = synthesizer.Synthesize(nodes, edges);
        return Results.Ok(new { Markdown = markdown, NodeCount = nodes.Count, EdgeCount = edges.Count });
    }
    catch (Exception ex)
    {
        return Fail("Capsule synthesis failed.", ex);
    }
});

// 5. POST /api/index - Trigger file indexing scanner for active project
app.MapPost("/api/index", async (IndexRequest? request, HttpContext context, IConfiguration config, ProjectManager pm, IEnumerable<IFileParser> parsers, CancellationToken ct) =>
{
    try
    {
        var projectName = context.Request.Headers["X-Project-Name"].ToString();
        var project = string.IsNullOrWhiteSpace(projectName)
            ? pm.GetActiveProject()
            : pm.GetProjects().FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

        if (project == null)
        {
            return Results.BadRequest("No project configured.");
        }

        var targetDir = request?.Directory ?? project.Path;
        // If relative, make it absolute
        targetDir = Path.GetFullPath(targetDir);

        if (!Directory.Exists(targetDir))
        {
            return Results.BadRequest($"Target directory does not exist: {targetDir}");
        }

        var projectConfig = pm.GetProjectConfig(project.Name);
        var exclusions = request?.ExcludePatterns ?? projectConfig.ExcludePatterns;

        // Prevent concurrent scans of the same project (duplicate work / racey deletes).
        if (!pm.TryBeginScan(project.Name))
        {
            return Results.Conflict($"An index scan is already running for project '{project.Name}'.");
        }

        try
        {
            // Dynamic plugins loading from central workspace.
            // SECURITY: compiling/executing arbitrary .cs is RCE; only do it when explicitly enabled.
            var activeParsers = new List<IFileParser>(parsers);
            using var pluginLoad = PluginsEnabled(config)
                ? LoadWorkspacePlugins(pm.WorkspacePath)
                : Shonkor.Infrastructure.Services.PluginLoadResult.Empty;
            activeParsers.AddRange(pluginLoad.Parsers);

            var storage = pm.GetStorageProvider(project.Name);
            var scanner = new GraphIndexScanner(storage, activeParsers);

            var result = await scanner.ScanDirectoryAsync(targetDir, exclusions, ct);
            var stats = await storage.GetStatisticsAsync(ct);

            return Results.Ok(new
            {
                Message = "Indexing completed successfully.",
                Result = result,
                Stats = stats
            });
        }
        finally
        {
            pm.EndScan(project.Name);
        }
    }
    catch (Exception ex)
    {
        return Fail("Indexing operation failed.", ex);
    }
});

// === Project Management Endpoints ===

// 6. GET /api/projects - List all registered projects
app.MapGet("/api/projects", (ProjectManager pm) =>
{
    var projects = pm.GetProjects();
    var active = pm.GetActiveProjectName();
    return Results.Ok(new { Projects = projects, ActiveProject = active });
});

// 7. POST /api/projects - Register a new project
app.MapPost("/api/projects", (Project newProject, ProjectManager pm) =>
{
    if (string.IsNullOrWhiteSpace(newProject.Name) || string.IsNullOrWhiteSpace(newProject.Path))
    {
        return Results.BadRequest("Project Name and Path are required.");
    }

    try
    {
        pm.AddProject(newProject.Name, newProject.Path, newProject.DatabasePath);
        return Results.Ok(new { Message = $"Project '{newProject.Name}' registered successfully." });
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (Exception ex)
    {
        return Fail("Failed to add project.", ex);
    }
});

// 8. DELETE /api/projects/{name} - Deregister a project
app.MapDelete("/api/projects/{name}", (string name, ProjectManager pm) =>
{
    try
    {
        pm.DeleteProject(name);
        return Results.Ok(new { Message = $"Project '{name}' removed successfully." });
    }
    catch (Exception ex)
    {
        return Fail("Failed to delete project.", ex);
    }
});

// 9. POST /api/projects/active - Switch the active project
app.MapPost("/api/projects/active", (ActiveProjectRequest req, ProjectManager pm) =>
{
    if (string.IsNullOrWhiteSpace(req.Name))
    {
        return Results.BadRequest("Project name is required.");
    }

    try
    {
        pm.SetActiveProject(req.Name);
        return Results.Ok(new { Message = $"Active project set to '{req.Name}'." });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (Exception ex)
    {
        return Fail("Failed to switch active project.", ex);
    }
});

// 10. GET /api/projects/{name}/config - Get project configuration (shonkor.json)
app.MapGet("/api/projects/{name}/config", (string name, ProjectManager pm) =>
{
    try
    {
        var config = pm.GetProjectConfig(name);
        return Results.Ok(config);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (Exception ex)
    {
        return Fail("Failed to retrieve config.", ex);
    }
});

// 11. POST /api/projects/{name}/config - Save project configuration (shonkor.json)
app.MapPost("/api/projects/{name}/config", (string name, WebConfig newConfig, ProjectManager pm) =>
{
    try
    {
        pm.SaveProjectConfig(name, newConfig);
        return Results.Ok(new { Message = $"Configuration for '{name}' saved successfully." });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (Exception ex)
    {
        return Fail("Failed to save config.", ex);
    }
});

// 12. GET /api/browse - Local filesystem folder browser
app.MapGet("/api/browse", (string? path, HttpContext context, IHostEnvironment env, IConfiguration config) =>
{
    // This endpoint exposes the host filesystem and is only meant for the local dashboard.
    // It is disabled outside Development unless explicitly opted in, and never for non-loopback callers.
    var allowBrowse = config.GetValue<bool?>("Security:AllowFilesystemBrowse") ?? env.IsDevelopment();
    var isLocal = context.Connection.RemoteIpAddress != null &&
                  System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
    if (!allowBrowse || !isLocal)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    try
    {
        var targetPath = path;
        
        // If path is empty, return drives list
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            var drives = Directory.GetLogicalDrives().ToList();
            return Results.Ok(new
            {
                CurrentPath = string.Empty,
                ParentPath = string.Empty,
                Folders = new List<string>(),
                Drives = drives
            });
        }

        targetPath = Path.GetFullPath(targetPath);

        if (!Directory.Exists(targetPath))
        {
            return Results.BadRequest($"Path does not exist: {targetPath}");
        }

        var folders = Directory.GetDirectories(targetPath)
                               .Select(Path.GetFileName)
                               .Where(name => !string.IsNullOrEmpty(name) && !name.StartsWith(".")) // Exclude hidden folders
                               .OrderBy(name => name)
                               .ToList();

        var parent = Directory.GetParent(targetPath)?.FullName ?? string.Empty;

        return Results.Ok(new
        {
            CurrentPath = targetPath,
            ParentPath = parent,
            Folders = folders,
            Drives = new List<string>()
        });
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Problem("Access denied to the specified directory.", statusCode: 403);
    }
    catch (Exception ex)
    {
        return Fail("Failed to browse directory.", ex);
    }
});

// === Plugin Management Endpoints ===

// 13. GET /api/plugins - List dynamic plugins for the active project
app.MapGet("/api/plugins", (IConfiguration config, ProjectManager pm) =>
{
    try
    {
        var pluginsDir = Path.Combine(pm.WorkspacePath, "plugins");
        if (!Directory.Exists(pluginsDir))
        {
            return Results.Ok(new { Plugins = Array.Empty<object>(), PluginsDirectory = pluginsDir });
        }

        var pluginFiles = Directory.GetFiles(pluginsDir, "*.cs");

        // Compile the whole directory ONCE (was previously recompiled per file -> O(n^2)),
        // and only when plugins are enabled. The collectible context is unloaded right after.
        var loadedCount = 0;
        var pluginsEnabled = PluginsEnabled(config);
        if (pluginsEnabled)
        {
            using var trialLoad = LoadWorkspacePlugins(pm.WorkspacePath);
            loadedCount = trialLoad.Parsers.Count;
        }

        var anyLoaded = loadedCount > 0;
        var plugins = pluginFiles.Select(file => new
        {
            FileName = Path.GetFileName(file),
            Name = Path.GetFileNameWithoutExtension(file),
            FullPath = file,
            Status = !pluginsEnabled ? "disabled" : (anyLoaded ? "loaded" : "no-parser"),
            Error = "",
            LastModified = File.GetLastWriteTimeUtc(file).ToString("o"),
            SizeBytes = new FileInfo(file).Length
        }).ToList();

        return Results.Ok(new { Plugins = plugins, PluginsDirectory = pluginsDir, PluginsEnabled = pluginsEnabled });
    }
    catch (Exception ex)
    {
        return Fail("Failed to list plugins.", ex);
    }
});

// 14. POST /api/plugins/create - Generate a boilerplate C# plugin template
app.MapPost("/api/plugins/create", (PluginCreateRequest req, HttpContext context, IConfiguration config, IHostEnvironment env, ProjectManager pm) =>
{
    try
    {
        // Writing a plugin only makes sense if plugins can run, and authoring is a local-trust action.
        var allowAuthoring = config.GetValue<bool?>("Security:AllowFilesystemBrowse") ?? env.IsDevelopment();
        var isLocal = context.Connection.RemoteIpAddress != null &&
                      System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
        if (!PluginsEnabled(config) || !allowAuthoring || !isLocal)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Extension))
        {
            return Results.BadRequest("Plugin Name and file Extension are required.");
        }

        var pluginsDir = Path.Combine(pm.WorkspacePath, "plugins");
        Directory.CreateDirectory(pluginsDir);

        // Sanitize name for C# class: PascalCase, no spaces
        var className = string.Concat(req.Name.Split(' ', '-', '_', '.')
            .Where(s => s.Length > 0)
            .Select(s => char.ToUpperInvariant(s[0]) + s[1..])) + "Parser";

        var ext = req.Extension.StartsWith(".") ? req.Extension : "." + req.Extension;
        var filePath = Path.Combine(pluginsDir, $"{className}.cs");

        if (File.Exists(filePath))
        {
            return Results.BadRequest($"Plugin file '{className}.cs' already exists.");
        }

        // Generate fully-functional boilerplate C# parser
        var boilerplate = GeneratePluginBoilerplate(className, ext, req.Name);
        File.WriteAllText(filePath, boilerplate);

        return Results.Ok(new
        {
            Message = $"Plugin '{className}' created successfully at {filePath}.",
            FileName = $"{className}.cs",
            FullPath = filePath,
            Extension = ext
        });
    }
    catch (Exception ex)
    {
        return Fail("Failed to create plugin.", ex);
    }
});

// 15. DELETE /api/plugins/{fileName} - Delete a plugin file
app.MapDelete("/api/plugins/{fileName}", (string fileName, ProjectManager pm) =>
{
    try
    {
        var pluginsDir = Path.Combine(pm.WorkspacePath, "plugins");
        var filePath = Path.Combine(pluginsDir, fileName);

        if (!File.Exists(filePath))
        {
            return Results.NotFound($"Plugin file '{fileName}' not found.");
        }

        File.Delete(filePath);
        return Results.Ok(new { Message = $"Plugin '{fileName}' deleted successfully." });
    }
    catch (Exception ex)
    {
        return Fail("Failed to delete plugin.", ex);
    }
});

// 16. GET /api/node-types - Get all available node types across core parsers and dynamic plugins
app.MapGet("/api/node-types", (IConfiguration config, ProjectManager pm, IEnumerable<IFileParser> coreParsers) =>
{
    try
    {
        var allParsers = new List<IFileParser>(coreParsers);

        using var pluginLoad = PluginsEnabled(config)
            ? LoadWorkspacePlugins(pm.WorkspacePath)
            : Shonkor.Infrastructure.Services.PluginLoadResult.Empty;
        allParsers.AddRange(pluginLoad.Parsers);

        var types = allParsers
            .SelectMany(p => p.NodeTypeDescriptors)
            .GroupBy(t => t.TypeName)
            .Select(g => g.First())
            .ToList();

        // Add System Types
        types.Add(new NodeTypeDescriptor("File", "Documentation", true));
        types.Add(new NodeTypeDescriptor("Task", "Interaction", true));
        types.Add(new NodeTypeDescriptor("Decision", "Interaction", true));
        types.Add(new NodeTypeDescriptor("Milestone", "Interaction", true));
        types.Add(new NodeTypeDescriptor("Question", "Interaction", true));
        types.Add(new NodeTypeDescriptor("HelixModule", "Code", true)); // From CrossTechLinker

        return Results.Ok(new { Types = types });
    }
    catch (Exception ex)
    {
        return Fail("Failed to get node types.", ex);
    }
});

app.Run();

// === Plugin Boilerplate Generator ===

/// <summary>
/// Generates a fully-functional C# source file implementing IFileParser
/// for runtime Roslyn compilation via PluginLoader.
/// </summary>
static string GeneratePluginBoilerplate(string className, string extension, string languageName)
{
    return $@"using System.Collections.Generic;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugins;

/// <summary>
/// Dynamic {languageName} parser plugin for Shonkor.
/// This file is compiled at runtime via the Roslyn Plugin Engine.
/// Extend the ParseAsync method with your custom extraction logic.
/// </summary>
public class {className} : IFileParser
{{
    public IReadOnlySet<string> SupportedExtensions {{ get; }} = new HashSet<string> {{ ""{extension}"" }};

    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(
        string filePath, string content)
    {{
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        // --- File-level node ---
        var fileId = $""file::{{filePath}}"";
        nodes.Add(new GraphNode
        {{
            Id = fileId,
            Type = ""File"",
            Name = System.IO.Path.GetFileName(filePath),
            FilePath = filePath,
            Content = content.Length > 10000 ? content[..10000] : content
        }});

        // --- Custom extraction logic ---
        // TODO: Add your own parsing rules here.
        // Example: Split content by lines, detect function definitions, classes, etc.
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {{
            var line = lines[i].TrimStart();
            // Placeholder: detect lines that look like function/method definitions
            // Customize this regex or logic for {languageName} syntax
            if (line.StartsWith(""def "") || line.StartsWith(""function "") || line.StartsWith(""fn ""))
            {{
                var funcName = line.Split(new[] {{ ' ', '(' }}, System.StringSplitOptions.RemoveEmptyEntries);
                if (funcName.Length >= 2)
                {{
                    var name = funcName[1].TrimEnd(':', '(', ')');
                    var funcId = $""func::{{filePath}}::{{name}}"";
                    nodes.Add(new GraphNode
                    {{
                        Id = funcId,
                        Type = ""Method"",
                        Name = name,
                        FilePath = filePath,
                        StartLine = i + 1
                    }});
                    edges.Add(new GraphEdge {{ SourceId = fileId, TargetId = funcId, Relationship = ""CONTAINS"" }});
                }}
            }}
        }}

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, edges));
    }}
}}";
}

// === Helper Config Methods ===

// Logs the full exception server-side but returns only a generic message to the client,
// so internal paths/stack details are never leaked over the API.
static IResult Fail(string clientMessage, Exception ex)
{
    Console.Error.WriteLine($"[API] {clientMessage} :: {ex}");
    return Results.Problem(clientMessage);
}

// Dynamic plugin compilation is effectively RCE and is therefore opt-in only.
static bool PluginsEnabled(IConfiguration config) => config.GetValue<bool>("Security:EnablePlugins");

// Compiles workspace plugins into a collectible context; returns Empty on any failure.
static Shonkor.Infrastructure.Services.PluginLoadResult LoadWorkspacePlugins(string workspacePath)
{
    try
    {
        var pluginsDir = Path.Combine(workspacePath, "plugins");
        return Shonkor.Infrastructure.Services.PluginLoader.LoadPlugins(pluginsDir);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[Plugins] Failed to compile dynamic plugins. :: {ex}");
        return Shonkor.Infrastructure.Services.PluginLoadResult.Empty;
    }
}

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



public record CapsuleRequest(string Query, int? Hops);
public record IndexRequest(string? Directory, List<string>? ExcludePatterns);
public record PluginCreateRequest(string Name, string Extension);
public record UpdateStatusRequest(string Id, string Status);
