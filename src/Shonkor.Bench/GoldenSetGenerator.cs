// Licensed to Shonkor under the MIT License.

using System.Text.RegularExpressions;
using Shonkor.Core.Models;

namespace Shonkor.Bench;

/// <summary>
/// Builds a natural-language golden set from the codebase's OWN developer-written XML doc comments
/// (<c>/// &lt;summary&gt;…</c>, captured in each symbol's <see cref="GraphNode.Content"/>): the query is the
/// prose description, the expected hit is the symbol it documents. This is more credible than a symbol-name
/// self-retrieval set — the queries are real natural language a human might type, not the identifier itself —
/// and the ground truth is unambiguous (a summary belongs to exactly one symbol). The symbol's own name is
/// stripped from the query so a keyword retriever cannot trivially match it, isolating true NL→code recall.
/// </summary>
internal static partial class GoldenSetGenerator
{
    private static readonly HashSet<string> SymbolTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Class", "Interface", "Record", "Struct", "Enum", "Method" };

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SummaryRx();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRx();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRx();

    public static List<GoldenCase> Generate(IReadOnlyList<GraphNode> nodes, int max = 150, bool stripName = true)
    {
        var cases = new List<GoldenCase>();

        foreach (var n in nodes)
        {
            if (!SymbolTypes.Contains(n.Type) || string.IsNullOrWhiteSpace(n.Content)) continue;

            var m = SummaryRx().Match(n.Content);
            if (!m.Success) continue;

            // Strip inner doc tags (<see cref>, <c>, <paramref>…), the /// line prefixes, and decode entities.
            var text = TagRx().Replace(m.Groups[1].Value, " ");
            text = text.Replace("///", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = WhitespaceRx().Replace(text, " ").Trim();

            if (text.Contains("inheritdoc", StringComparison.OrdinalIgnoreCase)) continue;

            // Remove the symbol's own name so a keyword retriever can't win for free — a real NL→code test.
            if (stripName && !string.IsNullOrEmpty(n.Name))
            {
                text = Regex.Replace(text, Regex.Escape(n.Name), " ", RegexOptions.IgnoreCase);
            }

            // Normalize: collapse whitespace, re-attach orphaned punctuation left by stripping, trim.
            text = WhitespaceRx().Replace(text, " ");
            text = Regex.Replace(text, @"\s+([.,;:)])", "$1");
            text = text.Trim(' ', '.', ',', ';', ':', '-');

            if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 5) continue; // too short to be NL

            cases.Add(new GoldenCase
            {
                Id = $"doc-{n.Name}",
                Query = text.Length > 240 ? text[..240] : text,
                Expected = new List<string> { n.Id }
            });
        }

        // Deterministic order + one case per id, capped.
        return cases
            .GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }
}
