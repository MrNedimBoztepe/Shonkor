using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// A mock semantic analyzer that fakes AI-generated summaries and concepts.
/// Useful for testing the Background Worker pipeline when Ollama or OpenAI are not available.
/// </summary>
public class MockSemanticAnalyzer : ISemanticAnalyzer
{
    public async Task<SemanticAnalysisResult> AnalyzeNodeAsync(GraphNode node, CancellationToken cancellationToken = default)
    {
        // Simulate a network/AI delay (e.g. 200ms)
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);

        // Generate a fake summary based on the node name and type
        string fakeSummary = $"This is an automatically generated (mock) summary for {node.Type} '{node.Name}'. The purpose of this module is presumably the processing of business logic or data.";

        var fakeConcepts = new List<string>
        {
            "Data Processing",
            "Business Logic"
        };

        if (node.Name.Contains("Controller", StringComparison.OrdinalIgnoreCase))
        {
            fakeConcepts.Add("Web API");
            fakeSummary = $"This {node.Type} receives HTTP requests and orchestrates the corresponding backend services.";
        }
        else if (node.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase) || node.Name.Contains("Storage", StringComparison.OrdinalIgnoreCase))
        {
            fakeConcepts.Add("Data Access");
            fakeSummary = $"This class is responsible for reading and writing data on the storage medium.";
        }

        return new SemanticAnalysisResult
        {
            Summary = fakeSummary,
            ExtractedConcepts = fakeConcepts
        };
    }

    public Task<string> GenerateRAGResponseAsync(string query, IReadOnlyList<GraphNode> contextNodes, CancellationToken cancellationToken = default)
    {
        return Task.FromResult($"This is a mock RAG response to your query: '{query}'. It is based on {contextNodes.Count} nodes.");
    }
}
