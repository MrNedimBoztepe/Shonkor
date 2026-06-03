using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Shonkor.Benchmarks
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== Shonkor Token & Latency Benchmark ===");
            Console.WriteLine("Simulating a standard Architectural RAG Search vs Shonkor Graph Search...\n");

            string dbPath = @"C:\Projects\sitecoreMuM\shonkor.db";
            
            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"[Error] Database not found at {dbPath}. Please run the scanner on the sitecoreMuM project first.");
                return;
            }

            // Metrics
            int totalFilesProcessed = 0;
            long classicTotalChars = 0;
            long shonkorTotalChars = 0;

            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();

            var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT FilePath, Summary 
                FROM Nodes 
                WHERE Summary IS NOT NULL 
                  AND Summary NOT LIKE 'Dies ist%' 
                  AND Summary NOT LIKE '[Ollama Error]%'
                LIMIT 50;
            ";

            using var reader = cmd.ExecuteReader();
            var distinctFiles = new HashSet<string>();

            while (reader.Read())
            {
                var filePath = reader.GetString(0);
                var summary = reader.GetString(1);

                if (distinctFiles.Add(filePath))
                {
                    if (File.Exists(filePath))
                    {
                        var fileContent = File.ReadAllText(filePath);
                        classicTotalChars += fileContent.Length;
                    }
                    else
                    {
                        // Fallback average class size if file is missing/mocked
                        classicTotalChars += 2000; 
                    }
                }

                shonkorTotalChars += summary.Length;
                totalFilesProcessed++;
            }

            if (totalFilesProcessed == 0)
            {
                Console.WriteLine("No AI-generated summaries found in the database. Please run the background worker first.");
                return;
            }

            // Estimate Tokens (1 token ~ 4 chars for code/english)
            long classicTokens = classicTotalChars / 4;
            long shonkorTokens = shonkorTotalChars / 4;
            
            // Assume $2.50 per 1M input tokens (GPT-4o / Claude 3.5 Sonnet average)
            double costPer1M = 2.50;
            double classicCost = (classicTokens / 1_000_000.0) * costPer1M;
            double shonkorCost = (shonkorTokens / 1_000_000.0) * costPer1M;

            // Speed estimation (Average TTFB + reading speed of LLM on input tokens)
            // Typically context reading speed is fast (~5ms per input token)
            double classicLatencySec = (classicTokens * 0.005) + 0.5; 
            double shonkorLatencySec = (shonkorTokens * 0.005) + 0.5;

            Console.WriteLine($"Analyzed {totalFilesProcessed} Nodes for a hypothetical broad RAG query.");
            Console.WriteLine("\n[1] Classic RAG Search (Sending full file contents):");
            Console.WriteLine($"    - Input Tokens Sent: {classicTokens:N0}");
            Console.WriteLine($"    - Estimated Cost:    ${classicCost:F5}");
            Console.WriteLine($"    - Est. Latency:      {classicLatencySec:F2} seconds");

            Console.WriteLine("\n[2] Shonkor Graph Search (Sending only Pre-computed Summaries):");
            Console.WriteLine($"    - Input Tokens Sent: {shonkorTokens:N0}");
            Console.WriteLine($"    - Estimated Cost:    ${shonkorCost:F5}");
            Console.WriteLine($"    - Est. Latency:      {shonkorLatencySec:F2} seconds");

            Console.WriteLine("\n--- SAVINGS ---");
            if (classicTokens > 0)
            {
                double tokenSavings = (1.0 - ((double)shonkorTokens / classicTokens)) * 100;
                double speedup = classicLatencySec / shonkorLatencySec;
                
                Console.WriteLine($"Tokens Saved: {tokenSavings:F1} %");
                Console.WriteLine($"Speedup:      {speedup:F1}x faster context processing");
            }
        }
    }
}
