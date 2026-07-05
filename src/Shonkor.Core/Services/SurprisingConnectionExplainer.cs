// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// Produces a local-LLM (Ollama) plain-language HYPOTHESIS for why two semantically-similar-but-unlinked
/// nodes (a "surprising connection") might be related. The output is always framed and labelled as an
/// inference, never a proven relationship — upholding the provenance guardrail that any model-generated
/// statement is INFERRED, never EXTRACTED.
/// </summary>
public static class SurprisingConnectionExplainer
{
    /// <summary>The label prefixed to every generated explanation so it is never mistaken for a hard fact.</summary>
    public const string InferredLabel = "[INFERRED · AI-generated hypothesis, not a proven relationship]";

    /// <summary>Builds the grounding prompt that asks the model to hypothesise the link, and to be explicit that it is a guess.</summary>
    public static string BuildQuery(GraphNode a, GraphNode b) =>
        $"These two code elements look semantically related but have NO direct dependency edge between them: " +
        $"(1) {a.Type} '{a.Name}', and (2) {b.Type} '{b.Name}'. " +
        "Briefly hypothesise WHY they might be conceptually related — e.g. a shared responsibility, duplicated " +
        "logic, or a missing link worth adding. Be explicit that this is an inference from similarity, not a " +
        "proven relationship.";

    /// <summary>
    /// Generates the explanation via <paramref name="analyzer"/> (both nodes supplied as grounding context)
    /// and returns it prefixed with <see cref="InferredLabel"/>.
    /// </summary>
    public static async Task<string> ExplainAsync(
        ISemanticAnalyzer analyzer, GraphNode a, GraphNode b, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var query = BuildQuery(a, b);
        var response = await analyzer.GenerateRAGResponseAsync(query, new[] { a, b }, cancellationToken).ConfigureAwait(false);
        return $"{InferredLabel}\n{response}";
    }
}
