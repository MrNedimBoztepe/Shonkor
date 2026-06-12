// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.CLI;

public static class Program
{
    private const string DefaultConfigFileName = "shonkor.json";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0].Equals("--help", StringComparison.OrdinalIgnoreCase) || args[0].Equals("-h", StringComparison.OrdinalIgnoreCase) || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "init":
                HandleInit();
                return 0;

            case "index":
                return await ParseAndRunIndexAsync(args);

            case "search":
                return await ParseAndRunSearchAsync(args);

            case "capsule":
                return await ParseAndRunCapsuleAsync(args);

            case "mcp":
                return await RunMcpServerAsync(args);

            case "mcp-proxy":
                return await McpProxyClient.RunAsync(args);

            default:
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Unknown command: '{args[0]}'");
                Console.ResetColor();
                Console.WriteLine("Run 'shonkor help' or 'shonkor --help' for usage instructions.");
                return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"==========================================================================");
        Console.WriteLine(@" Shonkor CLI - Precision GraphRAG Source Code & Documentation Indexer");
        Console.WriteLine(@"==========================================================================");
        Console.ResetColor();
        Console.WriteLine("Usage:");
        Console.WriteLine("  shonkor <command> [arguments] [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  init    ");
        Console.ResetColor();
        Console.WriteLine("Initialize default 'shonkor.json' configuration in the current directory.");
        
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  index   ");
        Console.ResetColor();
        Console.WriteLine("Traverse, parse, and index target source files into the knowledge graph.");
        
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  search  ");
        Console.ResetColor();
        Console.WriteLine("Perform high-precision FTS5 semantic search on the graph nodes.");
        
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  capsule ");
        Console.ResetColor();
        Console.WriteLine("Generate a token-optimized Markdown context capsule with Mermaid diagrams.");
        
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  mcp     ");
        Console.ResetColor();
        Console.WriteLine("Start standard Model Context Protocol (MCP) JSON-RPC stdio server.");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  mcp-proxy ");
        Console.ResetColor();
        Console.WriteLine("Proxy MCP JSON-RPC traffic over HTTP to a Shonkor SaaS remote backend.");
        Console.WriteLine();

        Console.WriteLine("Command Details & Options:");
        
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  index [directory]");
        Console.ResetColor();
        Console.WriteLine("    [directory]              The directory to index (defaults to current directory '.')");
        Console.WriteLine("    -c, --config <file>      Path to config json file (defaults to 'shonkor.json')");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  search <query>");
        Console.ResetColor();
        Console.WriteLine("    <query>                  The search query terms (supports FTS5 matching)");
        Console.WriteLine("    -l, --limit <number>     Max search results to retrieve (defaults to 10)");
        Console.WriteLine("    -c, --config <file>      Path to config json file (defaults to 'shonkor.json')");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  capsule <query>");
        Console.ResetColor();
        Console.WriteLine("    <query>                  Search term(s) to match the primary seed nodes");
        Console.WriteLine("    -h, --hops <number>      Graph traversal expansion depth limit (defaults to 2)");
        Console.WriteLine("    -o, --out <path>         Output Markdown capsule file path (defaults to 'shonkor-capsule.md')");
        Console.WriteLine("    -c, --config <file>      Path to config json file (defaults to 'shonkor.json')");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  mcp");
        Console.ResetColor();
        Console.WriteLine("    -c, --config <file>      Path to config json file (defaults to 'shonkor.json')");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  mcp-proxy");
        Console.ResetColor();
        Console.WriteLine("    -u, --url <url>          The remote Shonkor Web API endpoint for MCP relay (default: http://localhost:5000/api/mcp/relay)");
        Console.WriteLine("    -p, --project <name>     Target project on the remote server (optional, usually provided by env var SHONKOR_PROJECT)");
        Console.WriteLine(@"==========================================================================");
    }

    private static void HandleInit()
    {
        try
        {
            var configPath = Path.Combine(Directory.GetCurrentDirectory(), DefaultConfigFileName);
            if (File.Exists(configPath))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Configuration file already exists at: {configPath}");
                Console.ResetColor();
                return;
            }


            var defaultConfig = new CliConfig();
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(defaultConfig, options);
            File.WriteAllText(configPath, json);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Successfully initialized default configuration file: {DefaultConfigFileName}");
            Console.WriteLine("You can edit this file to adjust the database path and exclusion patterns.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error initializing configuration: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static async Task<int> ParseAndRunIndexAsync(string[] args)
    {
        var directory = ".";
        var configPath = DefaultConfigFileName;

        // Skip command arg index [0]
        var i = 1;
        if (i < args.Length && !args[i].StartsWith("-"))
        {
            directory = args[i];
            i++;
        }

        for (; i < args.Length; i++)
        {
            if ((args[i] == "-c" || args[i] == "--config") && i + 1 < args.Length)
            {
                configPath = args[i + 1];
                i++;
            }
        }

        try
        {
            var config = LoadConfig(configPath);
            var absoluteDir = Path.GetFullPath(directory);

            Console.WriteLine($"Starting indexer on directory: {absoluteDir}");
            Console.WriteLine($"Using database file: {config.DatabasePath}");

            using var storage = new SqliteGraphStorageProvider(config.DatabasePath);
            await storage.InitializeAsync();

            var parsers = new List<IFileParser>
            {
                new RoslynAstParser(),
                new JavaScriptParser(),
                new PhpModuleParser(),
                new MarkdownHierarchyParser(),
                new GraphQLParser()
            };

            var scanner = new GraphIndexScanner(storage, parsers);

            Console.WriteLine("Scanning and indexing files... (this may take a few moments)");
            var result = await scanner.ScanDirectoryAsync(absoluteDir, config.ExcludePatterns);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n=== Indexing Completed Successfully ===");
            Console.WriteLine($"- Files Scanned: {result.FilesScanned}");
            Console.WriteLine($"- Nodes Created: {result.NodesCreated}");
            Console.WriteLine($"- Edges Created: {result.EdgesCreated}");
            Console.WriteLine($"- Elapsed Time:  {result.Duration.TotalSeconds:F2} seconds");
            Console.ResetColor();

            var stats = await storage.GetStatisticsAsync();
            Console.WriteLine("\n=== Database Statistics ===");
            Console.WriteLine($"- Total Nodes: {stats.TotalNodes}");
            Console.WriteLine($"- Total Edges: {stats.TotalEdges}");
            Console.WriteLine("- Composition by Type:");
            foreach (var typeStat in stats.NodesByType)
            {
                Console.WriteLine($"  * {typeStat.Key}: {typeStat.Value}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Indexing failed: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static async Task<int> ParseAndRunSearchAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Search query is required.");
            Console.ResetColor();
            Console.WriteLine("Usage: shonkor search <query> [-l/--limit <limit>] [-c/--config <configPath>]");
            return 1;
        }

        var query = args[1];
        var limit = 10;
        var configPath = DefaultConfigFileName;

        for (var i = 2; i < args.Length; i++)
        {
            if ((args[i] == "-l" || args[i] == "--limit") && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedLimit))
            {
                limit = parsedLimit;
                i++;
            }
            else if ((args[i] == "-c" || args[i] == "--config") && i + 1 < args.Length)
            {
                configPath = args[i + 1];
                i++;
            }
        }

        try
        {
            var config = LoadConfig(configPath);
            if (!File.Exists(config.DatabasePath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Database file not found: {config.DatabasePath}. Please run the 'index' command first.");
                Console.ResetColor();
                return 1;
            }

            using var storage = new SqliteGraphStorageProvider(config.DatabasePath);
            await storage.InitializeAsync();

            Console.WriteLine($"Searching for: '{query}' (Max Results: {limit})");
            var results = await storage.SearchAsync(query, limit);

            if (results.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("No matches found.");
                Console.ResetColor();
                return 0;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nFound {results.Count} matching node(s):");
            Console.ResetColor();

            foreach (var result in results)
            {
                var n = result.Node;
                var lineInfo = n.StartLine.HasValue && n.EndLine.HasValue
                    ? $":L{n.StartLine}-{n.EndLine}"
                    : string.Empty;

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[{n.Type}] {n.Name} (Score: {result.Score:F4})");
                Console.ResetColor();
                Console.WriteLine($"  * File Path: {n.FilePath}{lineInfo}");
                
                if (result.RelatedEdges.Count > 0)
                {
                    Console.WriteLine($"  * Connections ({result.RelatedEdges.Count}):");
                    foreach (var edge in result.RelatedEdges.Take(5))
                    {
                        var isSource = edge.SourceId == n.Id;
                        var partnerId = isSource ? edge.TargetId : edge.SourceId;
                        var direction = isSource ? "-->" : "<--";
                        Console.WriteLine($"    - {direction} ({edge.Relationship}) {partnerId}");
                    }
                    if (result.RelatedEdges.Count > 5)
                    {
                        Console.WriteLine($"    - ... and {result.RelatedEdges.Count - 5} more connections");
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Search failed: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static async Task<int> ParseAndRunCapsuleAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: Capsule query is required.");
            Console.ResetColor();
            Console.WriteLine("Usage: shonkor capsule <query> [-h/--hops <hops>] [-o/--out <outPath>] [-c/--config <configPath>]");
            return 1;
        }

        var query = args[1];
        var hops = 2;
        var outPath = "shonkor-capsule.md";
        var configPath = DefaultConfigFileName;

        for (var i = 2; i < args.Length; i++)
        {
            if ((args[i] == "-h" || args[i] == "--hops") && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedHops))
            {
                hops = parsedHops;
                i++;
            }
            else if ((args[i] == "-o" || args[i] == "--out") && i + 1 < args.Length)
            {
                outPath = args[i + 1];
                i++;
            }
            else if ((args[i] == "-c" || args[i] == "--config") && i + 1 < args.Length)
            {
                configPath = args[i + 1];
                i++;
            }
        }

        try
        {
            var config = LoadConfig(configPath);
            if (!File.Exists(config.DatabasePath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Database file not found: {config.DatabasePath}. Please run the 'index' command first.");
                Console.ResetColor();
                return 1;
            }

            using var storage = new SqliteGraphStorageProvider(config.DatabasePath);
            await storage.InitializeAsync();

            Console.WriteLine($"Searching for seed nodes matching query: '{query}'");
            var searchResults = await storage.SearchAsync(query, 5); // Match up to 5 seeds

            if (searchResults.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("No seed nodes matched the query. Capsule synthesis aborted.");
                Console.ResetColor();
                return 1;
            }

            var seeds = searchResults.Select(r => r.Node.Id).ToList();
            Console.WriteLine($"Found {seeds.Count} seed node(s). Extracting {hops}-hop subgraph...");

            var (nodes, edges) = await storage.GetSubgraphAsync(seeds, hops);

            Console.WriteLine($"Retrieved a subgraph with {nodes.Count} nodes and {edges.Count} edges.");
            Console.WriteLine("Synthesizing context capsule...");

            var synthesizer = new ContextCapsuleSynthesizer();
            var capsule = synthesizer.Synthesize(nodes, edges);

            var absoluteOut = Path.GetFullPath(outPath);
            await File.WriteAllTextAsync(absoluteOut, capsule);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n=== Context Capsule Created Successfully ===");
            Console.WriteLine($"- Saved to: {absoluteOut}");
            Console.WriteLine($"- Size:     {capsule.Length} characters (~{capsule.Length / 4} tokens)");
            Console.ResetColor();

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Capsule synthesis failed: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static async Task<int> RunMcpServerAsync(string[] args)
    {
        if (args.Length > 1 && args[1].Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            return await McpInstaller.InstallAsync();
        }

        var configPath = DefaultConfigFileName;

        for (var i = 1; i < args.Length; i++)
        {
            if ((args[i] == "-c" || args[i] == "--config") && i + 1 < args.Length)
            {
                configPath = args[i + 1];
                i++;
            }
        }

        try
        {
            // Resolve the workspace without hardcoded machine-specific paths:
            // 1) explicit SHONKOR_WORKSPACE env var, else
            // 2) walk up from the current directory looking for a projects.json / shonkor.json marker, else
            // 3) fall back to the current working directory.
            var envWorkspace = Environment.GetEnvironmentVariable("SHONKOR_WORKSPACE");
            var workspacePath = !string.IsNullOrWhiteSpace(envWorkspace)
                ? envWorkspace
                : ResolveWorkspacePath();

            var pm = new Shonkor.Infrastructure.Services.ProjectManager(workspacePath);

            // The active project is derived from THIS process's working directory (the AI chat's directory),
            // not from the web-mutable ActiveProjectName. An explicit SHONKOR_PROJECT env var overrides.
            var envProject = Environment.GetEnvironmentVariable("SHONKOR_PROJECT");
            var contextProjectName = !string.IsNullOrWhiteSpace(envProject)
                ? envProject
                : pm.FindProjectByPath(Directory.GetCurrentDirectory())?.Name;

            Console.Error.WriteLine(contextProjectName is not null
                ? $"[MCP] Bound to project '{contextProjectName}' (from {(string.IsNullOrWhiteSpace(envProject) ? "working directory " + Directory.GetCurrentDirectory() : "SHONKOR_PROJECT")})."
                : $"[MCP] No project matched the working directory ({Directory.GetCurrentDirectory()}); falling back to the registry's active project.");

            var synthesizer = new ContextCapsuleSynthesizer();

            // Parsers enable reindex_file: the stdio CLI runs in the project directory, so it can re-index
            // a file the AI just edited and refresh the graph before the next query.
            var mcpParsers = new List<IFileParser>
            {
                new RoslynAstParser(),
                new JavaScriptParser(),
                new PhpModuleParser(),
                new MarkdownHierarchyParser(),
                new GraphQLParser()
            };
            var server = new McpRequestHandler(pm, synthesizer, contextProjectName, fileParsers: mcpParsers);

            await server.StartAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start MCP server: {ex.Message}");
            return 1;
        }
    }

    private static string ResolveWorkspacePath()
    {
        var dir = Directory.GetCurrentDirectory();
        var current = dir;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "projects.json")) ||
                File.Exists(Path.Combine(current, "shonkor.json")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null || parent.FullName == current)
            {
                break;
            }
            current = parent.FullName;
        }

        return dir;
    }

    private static CliConfig LoadConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return new CliConfig();
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<CliConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return config ?? new CliConfig();
        }
        catch
        {
            return new CliConfig();
        }
    }
}

public class CliConfig
{
    public string DatabasePath { get; set; } = "shonkor.db";
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
