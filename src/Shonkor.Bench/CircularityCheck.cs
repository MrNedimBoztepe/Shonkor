// Licensed to Shonkor under the MIT License.

using System.Text.RegularExpressions;

namespace Shonkor.Bench;

/// <summary>
/// Detects CIRCULARITY in a retrieval golden set (TICKET-202): a natural-language query is "circular" for
/// the vector retriever when it shares too many content words with the target's own embedding document —
/// then a hit is trivial (the query text IS in the embedded text) and the reported precision is an upper
/// bound, not a real NL→code measurement. Pure and deterministic; used both to gate a curated/paraphrased
/// set and to report how circular an existing set (e.g. doc-intent) is.
/// </summary>
public static partial class CircularityCheck
{
    // Default: a query sharing MORE than this many distinct content words with the target document is circular.
    public const int DefaultThreshold = 4;

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]+")]
    private static partial Regex WordRx();

    // Common English words + code/doc filler that carry no discriminative signal for circularity.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "over", "each", "when", "then", "than",
        "its", "are", "was", "has", "have", "not", "but", "all", "any", "one", "via", "per", "use", "used",
        "uses", "using", "returns", "return", "given", "your", "you", "our", "their", "them", "they", "which",
        "who", "whose", "what", "how", "why", "where", "does", "done", "can", "cannot", "may", "must", "will",
        "would", "should", "could", "a", "an", "of", "to", "in", "on", "is", "it", "as", "by", "or", "if",
        "so", "no", "we", "be", "at", "class", "method", "type", "value", "values", "node", "nodes"
    };

    /// <summary>The set of significant (non-stopword, length≥3) lowercase content words in <paramref name="text"/>.</summary>
    public static HashSet<string> ContentWords(string? text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return words;
        foreach (Match m in WordRx().Matches(text))
        {
            var w = m.Value;
            if (w.Length >= 3 && !StopWords.Contains(w)) words.Add(w.ToLowerInvariant());
        }
        return words;
    }

    /// <summary>Count of distinct content words shared between <paramref name="query"/> and <paramref name="document"/>.</summary>
    public static int SharedContentWordCount(string? query, string? document)
    {
        var q = ContentWords(query);
        if (q.Count == 0) return 0;
        q.IntersectWith(ContentWords(document));
        return q.Count;
    }

    /// <summary>True when the query shares MORE than <paramref name="threshold"/> content words with the document.</summary>
    public static bool IsCircular(string? query, string? document, int threshold = DefaultThreshold) =>
        SharedContentWordCount(query, document) > threshold;
}
