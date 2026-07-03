// Licensed to Shonkor under the MIT License.
//
// Grounding evaluation (TICKET-102): measures the RAG ANSWER path, not just retrieval.
//  - Citation validity: does every [Name @ file:lines] reference a node actually in the provided context?
//  - Must-cite: does an answerable question cite the expected symbol(s)?
//  - Abstention recall: does an UN-answerable question correctly say "nicht belegt" instead of inventing?
// Context is assembled exactly like the dashboard: top-K FTS hits for the query become the answer's nodes.

using System.Text.RegularExpressions;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

internal static class AnswerEval
{
    // Matches the RAG prompt's citation form: [Name @ file:lines]. Captures the name (before '@').
    private static readonly Regex CitationPattern = new(@"\[([^@\]]+?)\s*@\s*[^\]]+\]", RegexOptions.Compiled);

    // The abstention marker the RAG prompt instructs the model to use when the context doesn't cover it.
    private const string AbstentionMarker = "nicht belegt";

    public static async Task RunAsync(
        SqliteGraphStorageProvider storage,
        ISemanticAnalyzer analyzer,
        List<GoldenCase> cases,
        int contextK)
    {
        var answerable = cases.Where(c => c.IsAnswerable).ToList();
        var unanswerable = cases.Where(c => !c.IsAnswerable).ToList();

        double citationValiditySum = 0, mustCiteSum = 0;
        int answeredWithCitations = 0;
        int abstained = 0;

        Console.WriteLine($"[answer-eval] {cases.Count} cases ({answerable.Count} answerable, {unanswerable.Count} abstention), contextK={contextK}");

        foreach (var c in cases)
        {
            var hits = await storage.SearchAsync(c.Query, contextK);
            var contextNodes = hits.Select(h => h.Node).ToList();
            var contextNames = new HashSet<string>(contextNodes.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);

            var answer = await analyzer.GenerateRAGResponseAsync(c.Query, contextNodes);

            if (c.IsAnswerable)
            {
                var citations = CitationPattern.Matches(answer)
                    .Select(m => m.Groups[1].Value.Trim())
                    .ToList();
                if (citations.Count > 0)
                {
                    answeredWithCitations++;
                    var valid = citations.Count(name => contextNames.Contains(name));
                    citationValiditySum += (double)valid / citations.Count;
                }
                var citedExpected = c.Expected.Count == 0
                    || c.Expected.Any(e => citations.Any(name => name.Contains(e, StringComparison.OrdinalIgnoreCase)));
                mustCiteSum += citedExpected ? 1 : 0;
            }
            else
            {
                if (answer.Contains(AbstentionMarker, StringComparison.OrdinalIgnoreCase))
                {
                    abstained++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("| Metric | Value |");
        Console.WriteLine("|--------|------:|");
        if (answerable.Count > 0)
        {
            Console.WriteLine($"| Citation validity (valid refs / cited refs) | {(answeredWithCitations > 0 ? citationValiditySum / answeredWithCitations : 0):F3} |");
            Console.WriteLine($"| Answers carrying ≥1 citation | {(double)answeredWithCitations / answerable.Count:F3} |");
            Console.WriteLine($"| Must-cite hit rate | {mustCiteSum / answerable.Count:F3} |");
        }
        if (unanswerable.Count > 0)
        {
            Console.WriteLine($"| Abstention recall | {(double)abstained / unanswerable.Count:F3} |");
        }
        Console.WriteLine();
    }
}
