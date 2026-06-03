// Licensed to Shonkor under the MIT License.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shonkor.Core.Models;

namespace Shonkor.Core.Interfaces;

public record SemanticAnalysisResult
{
    public string Summary { get; init; } = string.Empty;
    public List<string> ExtractedConcepts { get; init; } = new();
    
    // Benchmark & Performance Metrics
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public long LatencyMs { get; init; }
}

/// <summary>
/// Defines a contract for enriching code nodes with AI-generated semantic metadata.
/// </summary>
public interface ISemanticAnalyzer
{
    /// <summary>
    /// Analyzes the content of a node and returns a semantic summary and extracted concepts.
    /// </summary>
    Task<SemanticAnalysisResult> AnalyzeNodeAsync(GraphNode node, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synthesizes a natural language answer to the user's query based on the provided graph context.
    /// </summary>
    Task<string> GenerateRAGResponseAsync(string query, IReadOnlyList<GraphNode> contextNodes, CancellationToken cancellationToken = default);
}
