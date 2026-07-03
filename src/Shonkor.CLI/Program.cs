// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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

            case "agents":
                HandleAgents();
                return 0;

            case "plugin":
            case "plugins":
                return HandlePlugin(args);

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
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  agents  ");
        Console.ResetColor();
        Console.WriteLine("Print an AGENTS.md/CLAUDE.md snippet teaching AI assistants to use the graph (e.g. shonkor agents >> AGENTS.md).");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  plugin  ");
        Console.ResetColor();
        Console.WriteLine("Manage parser plugins: install <zip> | activate <id> | deactivate <id> | list | uninstall <id>.");
        Console.WriteLine();

        Console.WriteLine("Command Details & Options:");
        
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  index [directory]");
        Console.ResetColor();
        Console.WriteLine("    [directory]              The directory to index (defaults to current directory '.')");
        Console.WriteLine("    -c, --config <file>      Path to config json file (defaults to 'shonkor.json')");
        Console.WriteLine("    --embed                  Generate code embeddings (needs a reachable Ollama) so semantic/hybrid search works");
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

    /// <summary>
    /// Prints a ready-to-paste AGENTS.md / CLAUDE.md section so an AI assistant reflexively uses the
    /// Shonkor graph (and its edit-validation loop) instead of grepping. Written to stdout — the user
    /// decides where to put it (e.g. `shonkor agents >> AGENTS.md`).
    /// </summary>
    private static void HandleAgents()
    {
        Console.WriteLine(
@"## Shonkor code graph (MCP)

This project is indexed by **Shonkor** — a precise, self-contained code graph exposed via the `shonkor` MCP server. Prefer its tools over grepping or reading whole files: they are deterministic, token-cheap, and stay in sync with the working tree.

- **Start each session** with `orient` — it shows the graph, the tool palette, and the workflow.
- **Find:** `locate`, `search_graph`. **Read precisely:** `signature`, `get_source`, `outline` (no need to read whole files).
- **Before changing code:** `references` (direction `used_by` = what breaks if you change it, `uses` = footprint; `depth>1` for transitive/blast radius), `call_hierarchy` (callers/callees), `find_usages` (call sites).
- **After editing a C# file:** `check_edit` (does it compile? — Roslyn, no build) → `reindex_file` (refresh the graph) → run exactly the tests `related_tests` names.
- **Never claim a symbol exists** without `verify_exists`. Check `freshness` (a file, or the whole project) when in doubt.
");
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
        var embed = false;

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
            else if (args[i] == "--embed")
            {
                embed = true;
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

            // Merge in the workspace's ACTIVE plugins (pre-built assemblies; installation is inert).
            var pluginWorkspace = Environment.GetEnvironmentVariable("SHONKOR_WORKSPACE");
            if (string.IsNullOrWhiteSpace(pluginWorkspace)) pluginWorkspace = ResolveWorkspacePath();
            using var pluginLoad = AssemblyPluginLoader.LoadActive(pluginWorkspace);
            if (pluginLoad.Parsers.Count > 0)
            {
                parsers.AddRange(pluginLoad.Parsers);
                Console.WriteLine($"Loaded {pluginLoad.Parsers.Count} active plugin parser(s).");
            }

            // Semantic C# linking (exact REFERENCES_TYPE/IMPLEMENTS/EXTENDS/CALLS via Roslyn) is now the
            // DEFAULT: it is non-lossy (unresolved refs fall back to name matching) so it is never worse
            // than the old syntactic resolver, only more precise. Set SHONKOR_SEMANTIC_CSHARP=false to force
            // the faster name-based resolver (trades precision for lower indexing latency on large repos).
            var semanticCsharp = !string.Equals(Environment.GetEnvironmentVariable("SHONKOR_SEMANTIC_CSHARP"), "false", StringComparison.OrdinalIgnoreCase);
            var scanner = new GraphIndexScanner(storage, parsers, semanticCsharp: semanticCsharp, postProcessors: pluginLoad.PostProcessors.Concat(Shonkor.Infrastructure.Services.FirstPartyPostProcessors.Create()));

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

            // Opt-in code embeddings (--embed): populate vectors so semantic/hybrid search works on this
            // CLI-built graph (which otherwise has none — enrichment normally runs only in the web worker).
            // Requires a reachable embedding backend; if it isn't, the index still succeeds.
            if (embed)
            {
                await RunEmbedPassAsync(storage);
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

    /// <summary>Node types worth embedding for code/doc search — the ones a query is meant to land on.</summary>
    private static readonly string[] EmbeddableTypes =
    {
        "Class", "Interface", "Record", "Struct", "Enum", "Method", "Constructor", "Property", "File", "MarkdownSection"
    };

    /// <summary>
    /// Populates code embeddings for the graph so semantic/hybrid search works on a CLI-built database.
    /// Embedding-only (no LLM summarization): each node's structured code document is embedded and written
    /// back via <see cref="ISemanticGraphStore.UpdateNodeEmbeddingAsync"/>. Bounded parallelism; a dead
    /// backend is reported and skipped without failing the (already successful) index.
    /// </summary>
    private static async Task RunEmbedPassAsync(IGraphStorageProvider storage)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var source = (config["Embedding:Source"] ?? "code").Trim().ToLowerInvariant();
        var model = config["EmbeddingService:OllamaModel"] ?? "nomic-embed-text";
        var maxParallelism = Math.Max(1, int.TryParse(config["SemanticEnrichment:MaxParallelism"], out var mp) ? mp : 4);

        using var httpClient = new HttpClient();
        var embeddingService = new OllamaEmbeddingService(httpClient, config, NullLogger<OllamaEmbeddingService>.Instance);

        // Probe once so an unreachable OR stalled backend fails fast (bounded), instead of per-node or a
        // ~minutes-long hang on a backend that accepts the connection but never responds.
        try
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var probe = await embeddingService.GenerateEmbeddingAsync("probe", probeCts.Token);
            if (probe.Length == 0)
            {
                Console.WriteLine("\n[embed] Backend returned an empty embedding — skipping. Semantic search will fall back to FTS.");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[embed] Embedding backend unreachable/slow ({ex.Message}). Skipping; semantic search will fall back to FTS.");
            return;
        }

        var nodes = await storage.GetNodesByTypesAsync(EmbeddableTypes);
        var toEmbed = nodes.Where(n => !string.IsNullOrWhiteSpace(n.Content) || !string.IsNullOrWhiteSpace(n.Name)).ToList();
        if (toEmbed.Count == 0)
        {
            Console.WriteLine("\n[embed] No embeddable nodes found.");
            return;
        }

        Console.WriteLine($"\n[embed] Generating code embeddings for {toEmbed.Count} nodes (source={source})...");
        var done = 0;
        await Parallel.ForEachAsync(
            toEmbed,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
            async (node, ct) =>
            {
                try
                {
                    var text = EmbeddingTextBuilder.Build(node, node.Summary, source);
                    if (string.IsNullOrWhiteSpace(text)) return;
                    var vector = await embeddingService.GenerateEmbeddingAsync(text, EmbeddingKind.Document, ct);
                    if (vector.Length > 0)
                    {
                        await storage.UpdateNodeEmbeddingAsync(node.Id, vector, model, ct);
                    }
                    var n = Interlocked.Increment(ref done);
                    if (n % 100 == 0) Console.WriteLine($"[embed]   {n}/{toEmbed.Count}...");
                }
                catch (Exception ex)
                {
                    // Skip a single node's failure; keep embedding the rest.
                    Console.Error.WriteLine($"[embed] Node '{node.Id}' failed: {ex.Message}");
                }
            }).ConfigureAwait(false);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[embed] Done — {done} embeddings written. Semantic and hybrid search are now available.");
        Console.ResetColor();
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

    /// <summary>
    /// Returns an <see cref="OllamaEmbeddingService"/> when an Ollama backend is reachable, else <c>null</c>.
    /// Uses a short-timeout GET on <c>/api/tags</c> so an absent backend (connection refused) returns
    /// immediately and MCP startup is not delayed. The returned service reuses <paramref name="sharedClient"/>,
    /// whose lifetime the caller owns for the server's duration.
    /// </summary>
    private static async Task<IEmbeddingService?> TryCreateMcpEmbeddingServiceAsync(IConfiguration config, HttpClient sharedClient)
    {
        var url = (config["EmbeddingService:OllamaUrl"] ?? "http://localhost:11434").TrimEnd('/');
        // Probe over IPv4 to avoid the "localhost" -> ::1 first-attempt detour, which on a cold process
        // can exceed a short timeout before falling back to 127.0.0.1 (a present backend would then be
        // wrongly seen as absent). An absent backend still fails instantly (connection refused), so a
        // slightly generous timeout stays fast when Ollama isn't running.
        var probeUrl = url.Replace("localhost", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
        try
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var probe = new HttpClient();
            var response = await probe.GetAsync($"{probeUrl}/api/tags", probeCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        return new OllamaEmbeddingService(sharedClient, config, NullLogger<OllamaEmbeddingService>.Instance);
    }

    private static async Task<int> RunMcpServerAsync(string[] args)
    {
        if (args.Length > 1 && args[1].Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            return await McpInstaller.InstallAsync();
        }
        if (args.Length > 1 && (args[1].Equals("uninstall", StringComparison.OrdinalIgnoreCase) || args[1].Equals("status", StringComparison.OrdinalIgnoreCase)))
        {
            return args[1].Equals("status", StringComparison.OrdinalIgnoreCase)
                ? await McpInstaller.StatusAsync()
                : await McpInstaller.UninstallAsync();
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
            // Wire an embedding service when a backend is reachable, so search_semantic works over a graph
            // that has embeddings (built with `shonkor index --embed`). Absent backend → null → FTS-only
            // (unchanged behaviour, no startup delay: connection-refused returns immediately).
            var mcpConfig = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            using var embedHttpClient = new HttpClient();
            var embeddingService = await TryCreateMcpEmbeddingServiceAsync(mcpConfig, embedHttpClient).ConfigureAwait(false);
            Console.Error.WriteLine(embeddingService is not null
                ? "[MCP] Embedding backend detected — semantic search enabled (requires embeddings in the graph)."
                : "[MCP] No embedding backend — keyword (FTS) + graph search only.");

            var server = new McpRequestHandler(pm, synthesizer, contextProjectName, fileParsers: mcpParsers,
                compilationCache: new SemanticCompilationCache(), embeddingService: embeddingService);

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

    /// <summary>
    /// Plugin lifecycle CLI: install a ZIP (inert), then explicitly activate it. Resolves the workspace
    /// like the MCP server (SHONKOR_WORKSPACE, else walk up to the registry marker).
    /// </summary>
    private static int HandlePlugin(string[] args)
    {
        var workspace = Environment.GetEnvironmentVariable("SHONKOR_WORKSPACE");
        if (string.IsNullOrWhiteSpace(workspace)) workspace = ResolveWorkspacePath();
        var registry = new PluginRegistry(workspace);

        static int Report(PluginOperationResult r)
        {
            if (r.Success)
            {
                Console.WriteLine(r.Message);
                return 0;
            }
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(r.Message);
            Console.ResetColor();
            return 1;
        }

        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
            {
                var plugins = registry.List();
                if (plugins.Count == 0)
                {
                    Console.WriteLine($"No plugins installed in {Path.Combine(workspace, "plugins")}.");
                    return 0;
                }
                Console.WriteLine($"Plugins in {Path.Combine(workspace, "plugins")}:");
                foreach (var p in plugins)
                {
                    var mark = p.State switch
                    {
                        PluginState.Active => "●",
                        PluginState.Failed => "✗",
                        _ => "○"
                    };
                    Console.WriteLine($"  {mark} {p.Manifest.Id}\tv{p.Manifest.Version}\t[{p.State}]\t{p.Manifest.Name}");
                    if (p.State == PluginState.Failed && !string.IsNullOrEmpty(p.Error))
                    {
                        Console.WriteLine($"      ! {p.Error}");
                    }
                }
                return 0;
            }
            case "install":
                if (args.Length < 3) { Console.Error.WriteLine("Usage: shonkor plugin install <path-to.zip>"); return 1; }
                return Report(registry.InstallFromZip(args[2]));
            case "activate":
                if (args.Length < 3) { Console.Error.WriteLine("Usage: shonkor plugin activate <id>"); return 1; }
                return Report(registry.Activate(args[2]));
            case "deactivate":
                if (args.Length < 3) { Console.Error.WriteLine("Usage: shonkor plugin deactivate <id>"); return 1; }
                return Report(registry.Deactivate(args[2]));
            case "uninstall":
            case "remove":
                if (args.Length < 3) { Console.Error.WriteLine("Usage: shonkor plugin uninstall <id>"); return 1; }
                return Report(registry.Uninstall(args[2]));
            default:
                Console.Error.WriteLine($"Unknown plugin subcommand: '{sub}'. Use: list | install <zip> | activate <id> | deactivate <id> | uninstall <id>.");
                return 1;
        }
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
