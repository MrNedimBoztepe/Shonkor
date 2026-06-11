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
        string fakeSummary = $"Dies ist eine automatisch generierte (Mock) Zusammenfassung für {node.Type} '{node.Name}'. Der Zweck dieses Moduls ist vermutlich die Verarbeitung von Geschäftslogik oder Daten.";
        
        var fakeConcepts = new List<string>
        {
            "Data Processing",
            "Business Logic"
        };

        if (node.Name.Contains("Controller", StringComparison.OrdinalIgnoreCase))
        {
            fakeConcepts.Add("Web API");
            fakeSummary = $"Dieser {node.Type} nimmt HTTP-Anfragen entgegen und orchestriert die entsprechenden Backend-Services.";
        }
        else if (node.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase) || node.Name.Contains("Storage", StringComparison.OrdinalIgnoreCase))
        {
            fakeConcepts.Add("Data Access");
            fakeSummary = $"Diese Klasse ist für das Lesen und Schreiben von Daten auf dem Speichermedium zuständig.";
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
