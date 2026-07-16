// Licensed to Shonkor under the MIT License.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shonkor.Core.Models;
using Shonkor.Core.Services;

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

    /// <summary>
    /// Grounding-aware overload (TICKET-206): shapes the prompt via <paramref name="options"/> (chat
    /// transcript fenced as data, per-node match strength, flagged sources, answer language) and validates
    /// the answer's citations against the context. The default implementation ignores the options and
    /// forwards to <see cref="GenerateRAGResponseAsync(string, IReadOnlyList{GraphNode}, CancellationToken)"/>,
    /// so implementations that don't ground still satisfy callers.
    /// </summary>
    Task<string> GenerateRAGResponseAsync(string query, IReadOnlyList<GraphNode> contextNodes, RagPromptOptions options, CancellationToken cancellationToken = default)
        => GenerateRAGResponseAsync(query, contextNodes, cancellationToken);

    /// <summary>
    /// Streams the answer incrementally as typed <see cref="RagStreamEvent"/>s (#231).
    ///
    /// <para>
    /// This used to yield <c>string</c>, and everything that was not a token — "answer incomplete",
    /// "unsupported sources" — was appended to that same stream as English prose. Signal and content shared a
    /// channel, so a consumer could only string-match, and <b>the model could forge the markers by emitting
    /// the text</b>. Now a <see cref="RagStreamEventKind.Token"/> is the only kind carrying model-authored
    /// text; everything else is an event the model has no way to produce.
    /// </para>
    /// <para>
    /// The default implementation yields the full <see cref="GenerateRAGResponseAsync"/> result as one token
    /// followed by a completed <see cref="RagStreamEventKind.Done"/>, so implementations that cannot stream
    /// still satisfy callers; backends that support streaming override this.
    /// </para>
    /// </summary>
    async IAsyncEnumerable<RagStreamEvent> StreamRAGResponseAsync(
        string query,
        IReadOnlyList<GraphNode> contextNodes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return RagStreamEvent.OfToken(
            await GenerateRAGResponseAsync(query, contextNodes, cancellationToken).ConfigureAwait(false));
        yield return RagStreamEvent.OfDone(complete: true);
    }

    /// <summary>Grounding-aware streaming overload (TICKET-206); defaults to the non-options stream.</summary>
    IAsyncEnumerable<RagStreamEvent> StreamRAGResponseAsync(
        string query,
        IReadOnlyList<GraphNode> contextNodes,
        RagPromptOptions options,
        CancellationToken cancellationToken = default)
        => StreamRAGResponseAsync(query, contextNodes, cancellationToken);
}
