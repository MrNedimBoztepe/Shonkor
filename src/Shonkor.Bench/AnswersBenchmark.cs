// Licensed to Shonkor under the MIT License.
//
// Answer-groundedness evaluation (TICKET-201, restoring what the Shonkor.Eval merge dropped):
// measures the RAG ANSWER path — is the generated answer faithful to the provided context?
//  - Citation validity: does every [Name @ file:lines] reference a node actually in the context?
//  - Must-cite recall: does an answerable question cite the expected symbol(s)?
//  - Abstention recall/precision: does the model say "not supported by the current graph data"
//    exactly when the context doesn't cover the question — and only then?
//  - Uncited-paragraph rate: how much prose carries no citation at all?
// The context is FIXED per case (node ids/names from the golden set), so this isolates answer
// faithfulness from retrieval quality; answers run through the production BuildRagPrompt pipeline
// (same prompt as /api/ask) with temperature=0 and a fixed seed.

using System.Text.RegularExpressions;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Bench;

internal static class AnswersBenchmark
{
    // Matches the RAG prompt's citation form: [Name @ file:lines]. Captures the name (before '@').
    private static readonly Regex CitationPattern = new(@"\[([^@\[\]]+?)\s*@\s*[^\]]+\]", RegexOptions.Compiled);

    // The abstention phrase the RAG prompt instructs the model to use when the context doesn't cover it.
    private const string AbstentionMarker = "not supported by the current graph data";

    public static async Task<AnswersResult> RunAsync(
        IGraphStorageProvider storage,
        ISemanticAnalyzer analyzer,
        IReadOnlyList<AnswerCase> cases,
        TextWriter log,
        CancellationToken cancellationToken = default)
    {
        // Resolve every context reference up front: exact node id first, then definition-by-name
        // (names keep the golden set portable across machines — node ids embed absolute paths).
        var allRefs = cases.SelectMany(c => c.ContextNodeIds).Distinct(StringComparer.Ordinal).ToList();
        var byName = await storage.GetDefinitionsByNamesAsync(allRefs, cancellationToken).ConfigureAwait(false);

        var failures = new List<string>();
        int answerable = 0, abstainCases = 0, skipped = 0;
        int totalCitations = 0, validCitations = 0;
        int mustCiteTotal = 0, mustCiteHits = 0;
        int abstainCorrect = 0, falseAbstentions = 0;
        int paragraphsTotal = 0, uncitedParagraphs = 0;
        int contentChecksTotal = 0, contentChecksPassed = 0;

        foreach (var c in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var contextNodes = new List<GraphNode>();
            foreach (var reference in c.ContextNodeIds)
            {
                var node = await storage.GetNodeByIdAsync(reference, cancellationToken).ConfigureAwait(false);
                if (node is not null)
                {
                    contextNodes.Add(node);
                }
                else if (byName.TryGetValue(reference, out var defs) && defs.Count > 0)
                {
                    contextNodes.Add(defs[0]);
                }
            }

            if (contextNodes.Count == 0)
            {
                skipped++;
                failures.Add($"{c.Id}: SKIPPED — none of the context references resolved ({string.Join(", ", c.ContextNodeIds)})");
                continue;
            }

            var answer = await analyzer.GenerateRAGResponseAsync(c.Question, contextNodes, cancellationToken).ConfigureAwait(false);
            var abstained = answer.Contains(AbstentionMarker, StringComparison.OrdinalIgnoreCase);

            if (c.IsAbstain)
            {
                abstainCases++;
                if (abstained) abstainCorrect++;
                else failures.Add($"{c.Id}: expected abstention, got an answer ({Snippet(answer)})");
            }
            else
            {
                answerable++;
                if (abstained)
                {
                    falseAbstentions++;
                    failures.Add($"{c.Id}: abstained although the context covers the question");
                }

                var contextNames = new HashSet<string>(contextNodes.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
                var citations = CitationPattern.Matches(answer).Select(m => m.Groups[1].Value.Trim()).ToList();
                totalCitations += citations.Count;
                validCitations += citations.Count(name => contextNames.Contains(name));
                if (citations.Any(name => !contextNames.Contains(name)))
                {
                    failures.Add($"{c.Id}: cites source(s) not in the context: {string.Join(", ", citations.Where(n => !contextNames.Contains(n)).Distinct())}");
                }

                if (c.MustCite.Count > 0)
                {
                    mustCiteTotal++;
                    var hit = c.MustCite.Any(expected =>
                        citations.Any(name => name.Contains(expected, StringComparison.OrdinalIgnoreCase)));
                    if (hit) mustCiteHits++;
                    else failures.Add($"{c.Id}: none of the expected symbol(s) cited ({string.Join(", ", c.MustCite)})");
                }

                var paragraphs = answer
                    .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(p => p.Length > 0)
                    .ToList();
                paragraphsTotal += paragraphs.Count;
                uncitedParagraphs += paragraphs.Count(p => !CitationPattern.IsMatch(p));
            }

            foreach (var required in c.MustContain)
            {
                contentChecksTotal++;
                if (answer.Contains(required, StringComparison.OrdinalIgnoreCase)) contentChecksPassed++;
                else failures.Add($"{c.Id}: answer misses required content '{required}'");
            }
            foreach (var forbidden in c.MustNotContain)
            {
                contentChecksTotal++;
                if (!answer.Contains(forbidden, StringComparison.OrdinalIgnoreCase)) contentChecksPassed++;
                else failures.Add($"{c.Id}: answer contains forbidden content '{forbidden}'");
            }

            log.Write('.');
        }
        log.WriteLine();

        return new AnswersResult(
            Cases: cases.Count,
            Answerable: answerable,
            Abstain: abstainCases,
            Skipped: skipped,
            CitationValidity: Ratio(validCitations, totalCitations),
            MustCiteRecall: Ratio(mustCiteHits, mustCiteTotal),
            AbstentionRecall: Ratio(abstainCorrect, abstainCases),
            AbstentionPrecision: Ratio(abstainCorrect, abstainCorrect + falseAbstentions),
            UncitedParagraphRate: Ratio(uncitedParagraphs, paragraphsTotal),
            ContentCheckPassRate: Ratio(contentChecksPassed, contentChecksTotal),
            Failures: failures);
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator > 0 ? (double)numerator / denominator : 0;

    private static string Snippet(string answer)
    {
        var flat = answer.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }
}
