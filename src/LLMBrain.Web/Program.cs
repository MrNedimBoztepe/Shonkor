// Licensed to LLMBrain under the MIT License.

using System.Text.Json;
using LLMBrain.Core.Interfaces;
using LLMBrain.Core.Models;
using LLMBrain.Core.Services;
using LLMBrain.Infrastructure.Services;
using LLMBrain.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

// Load Configuration from llmbrain.json
var config = LoadConfig();
builder.Services.AddSingleton(config);

// Register Storage Provider
builder.Services.AddSingleton<IGraphStorageProvider>(sp =>
{
    var storage = new SqliteGraphStorageProvider(config.DatabasePath);
    // Initialize DB schema synchronously on startup
    storage.InitializeAsync().GetAwaiter().GetResult();
    return storage;
});

// Register all core parsers for indexing endpoint
builder.Services.AddSingleton<IEnumerable<IFileParser>>(sp => new List<IFileParser>
{
    new RoslynAstParser(),
    new JavaScriptParser(),
    new PhpModuleParser(),
    new CmsConfigParser(),
    new MarkdownHierarchyParser()
});

builder.Services.AddSingleton<GraphIndexScanner>();
builder.Services.AddSingleton<ContextCapsuleSynthesizer>();

var app = builder.Build();

// Enable serving index.html and static assets
app.UseDefaultFiles();
app.UseStaticFiles();

// === Endpoints ===

// 1. GET /api/stats - Get Graph Statistics
app.MapGet("/api/stats", async (IGraphStorageProvider storage, CancellationToken ct) =>
{
    try
    {
        var stats = await storage.GetStatisticsAsync(ct);
        return Results.Ok(stats);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to retrieve stats: {ex.Message}");
    }
});

// 2. GET /api/search - Semantic search over nodes
app.MapGet("/api/search", async (string q, int? limit, IGraphStorageProvider storage, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest("Query string 'q' cannot be empty.");
    }

    try
    {
        var maxResults = limit ?? 15;
        var results = await storage.SearchAsync(q, maxResults, ct);
        return Results.Ok(results);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Search failed: {ex.Message}");
    }
});

// 3. GET /api/subgraph - Traverse subgraph N hops out from seeds
app.MapGet("/api/subgraph", async (string seeds, int? hops, IGraphStorageProvider storage, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(seeds))
    {
        return Results.BadRequest("Query parameter 'seeds' is required (comma-separated list of Node IDs).");
    }

    try
    {
        var seedList = seeds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var maxHops = hops ?? 2;
        var (nodes, edges) = await storage.GetSubgraphAsync(seedList, maxHops, ct);
        return Results.Ok(new { Nodes = nodes, Edges = edges });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Subgraph traversal failed: {ex.Message}");
    }
});

// 4. POST /api/capsule - Synthesize capsule for matching seeds
app.MapPost("/api/capsule", async (CapsuleRequest request, IGraphStorageProvider storage, ContextCapsuleSynthesizer synthesizer, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        return Results.BadRequest("Request 'query' is required.");
    }

    try
    {
        var searchResults = await storage.SearchAsync(request.Query, 5, ct);
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
        return Results.Problem($"Capsule synthesis failed: {ex.Message}");
    }
});

// 5. POST /api/index - Trigger file indexing scanner
app.MapPost("/api/index", async (IndexRequest? request, WebConfig webConfig, GraphIndexScanner scanner, IGraphStorageProvider storage, CancellationToken ct) =>
{
    try
    {
        var targetDir = request?.Directory ?? Directory.GetCurrentDirectory();
        // If relative, make it absolute relative to current directory
        targetDir = Path.GetFullPath(targetDir);

        if (!Directory.Exists(targetDir))
        {
            return Results.BadRequest($"Target directory does not exist: {targetDir}");
        }

        var exclusions = request?.ExcludePatterns ?? webConfig.ExcludePatterns;

        var result = await scanner.ScanDirectoryAsync(targetDir, exclusions, ct);
        var stats = await storage.GetStatisticsAsync(ct);

        return Results.Ok(new
        {
            Message = "Indexing completed successfully.",
            Result = result,
            Stats = stats
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Indexing operation failed: {ex.Message}");
    }
});

app.Run();

// === Helper Config Methods ===

static WebConfig LoadConfig()
{
    // Try to find llmbrain.json in current dir, or walk up to parent dir
    var currentDir = Directory.GetCurrentDirectory();
    var pathsToCheck = new[]
    {
        Path.Combine(currentDir, "llmbrain.json"),
        Path.Combine(currentDir, "..", "llmbrain.json"),
        Path.Combine(currentDir, "..", "..", "llmbrain.json")
    };

    foreach (var path in pathsToCheck)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<WebConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config != null)
                {
                    // If DB path is relative, resolve it relative to the config file directory
                    if (!Path.IsPathRooted(config.DatabasePath))
                    {
                        var configDir = Path.GetDirectoryName(path) ?? currentDir;
                        config.DatabasePath = Path.GetFullPath(Path.Combine(configDir, config.DatabasePath));
                    }
                    return config;
                }
            }
            catch
            {
                // Fallback to default
            }
        }
    }

    // Default configuration if file is missing
    var defaultConfig = new WebConfig
    {
        DatabasePath = Path.GetFullPath(Path.Combine(currentDir, "llmbrain.db"))
    };
    return defaultConfig;
}

public class WebConfig
{
    public string DatabasePath { get; set; } = "llmbrain.db";
    public List<string> ExcludePatterns { get; set; } = new()
    {
        "**/bin/**",
        "**/obj/**",
        "**/.git/**",
        "**/.vs/**",
        "**/.idea/**",
        "**/node_modules/**",
        "**/*.db",
        "**/*.log"
    };
}

public record CapsuleRequest(string Query, int? Hops);
public record IndexRequest(string? Directory, List<string>? ExcludePatterns);
