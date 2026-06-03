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
}
