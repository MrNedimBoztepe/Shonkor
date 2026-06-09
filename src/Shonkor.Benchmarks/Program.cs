// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Benchmarks;

/// <summary>
/// Token &amp; latency benchmark: compares sending whole file contents to an LLM ("classic RAG")
/// versus Shonkor's pre-computed AI summaries. Reads the graph through the storage abstraction
/// (<see cref="SqliteGraphStorageProvider"/>) rather than hand-rolled SQL.
/// </summary>
internal static class Program
{
    // Cost/latency model constants.
    private const double CharsPerToken = 4.0;          // ~4 chars/token for code+English
    private const double CostPer1MTokens = 2.50;       // USD, GPT-4o / Claude 3.5 Sonnet average
    private const double SecondsPerToken = 0.005;      // context reading speed
    private const double BaseLatencySeconds = 0.5;     // average TTFB
    private const long MissingFileFallbackChars = 2000;
    private const int SampleSize = 50;

    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== Shonkor Token & Latency Benchmark ===");
        Console.WriteLine("Comparing classic full-content RAG vs Shonkor pre-computed summaries...\n");

        // DB path: first CLI arg, else SHONKOR_BENCHMARK_DB env var, else ./shonkor.db.
        var dbPath = args.FirstOrDefault()
                     ?? Environment.GetEnvironmentVariable("SHONKOR_BENCHMARK_DB")
                     ?? "shonkor.db";

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"[Error] Database not found at '{dbPath}'.");
            Console.Error.WriteLine("Usage: Shonkor.Benchmarks <path-to-shonkor.db>   (or set SHONKOR_BENCHMARK_DB)");
            return 1;
        }

        var provider = new SqliteGraphStorageProvider(dbPath);
        await provider.InitializeAsync();
        var allNodes = await provider.GetAllNodesAsync();

        // Nodes carrying a real AI-generated summary (skip placeholders / error markers).
        var summarized = allNodes
            .Where(n => !string.IsNullOrEmpty(n.Summary)
                        && !n.Summary!.StartsWith("Dies ist", StringComparison.Ordinal)
                        && !n.Summary!.StartsWith("[Ollama Error]", StringComparison.Ordinal))
            .Take(SampleSize)
            .ToList();

        if (summarized.Count == 0)
        {
            Console.WriteLine("No AI-generated summaries found in the database. Run the enrichment worker first.");
            return 0;
        }

        long classicChars = 0;
        long shonkorChars = 0;
        var countedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in summarized)
        {
            // Classic RAG would send the whole source file (once per distinct file).
            var filePath = node.FilePath;
            if (!string.IsNullOrEmpty(filePath) && countedFiles.Add(filePath))
            {
                classicChars += File.Exists(filePath) ? new FileInfo(filePath).Length : MissingFileFallbackChars;
            }

            // Shonkor sends only the compact summary.
            shonkorChars += node.Summary!.Length;
        }

        PrintReport(summarized.Count, classicChars, shonkorChars);
        return 0;
    }

    private static void PrintReport(int nodeCount, long classicChars, long shonkorChars)
    {
        var classicTokens = (long)(classicChars / CharsPerToken);
        var shonkorTokens = (long)(shonkorChars / CharsPerToken);

        var classicCost = classicTokens / 1_000_000.0 * CostPer1MTokens;
        var shonkorCost = shonkorTokens / 1_000_000.0 * CostPer1MTokens;

        var classicLatency = classicTokens * SecondsPerToken + BaseLatencySeconds;
        var shonkorLatency = shonkorTokens * SecondsPerToken + BaseLatencySeconds;

        Console.WriteLine($"Analyzed {nodeCount} nodes for a hypothetical broad RAG query.");
        Console.WriteLine("\n[1] Classic RAG Search (sending full file contents):");
        Console.WriteLine($"    - Input Tokens Sent: {classicTokens:N0}");
        Console.WriteLine($"    - Estimated Cost:    ${classicCost:F5}");
        Console.WriteLine($"    - Est. Latency:      {classicLatency:F2} seconds");

        Console.WriteLine("\n[2] Shonkor Graph Search (sending only pre-computed summaries):");
        Console.WriteLine($"    - Input Tokens Sent: {shonkorTokens:N0}");
        Console.WriteLine($"    - Estimated Cost:    ${shonkorCost:F5}");
        Console.WriteLine($"    - Est. Latency:      {shonkorLatency:F2} seconds");

        Console.WriteLine("\n--- SAVINGS ---");
        if (classicTokens > 0)
        {
            var tokenSavings = (1.0 - (double)shonkorTokens / classicTokens) * 100;
            var speedup = shonkorLatency > 0 ? classicLatency / shonkorLatency : 0;
            Console.WriteLine($"Tokens Saved: {tokenSavings:F1} %");
            Console.WriteLine($"Speedup:      {speedup:F1}x faster context processing");
        }
    }
}
