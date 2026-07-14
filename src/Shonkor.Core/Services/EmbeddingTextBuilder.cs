// Licensed to Shonkor under the MIT License.

using System.Text;
using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// Builds the text fed to the embedding model for a graph node. Shared by the web enrichment worker and
/// the CLI embed pass so both index-time paths embed identical documents.
/// <para>
/// <c>code</c> (default) yields a structured code document — identity + signature + summary + a bounded
/// body slice — which retrieves markedly better on natural-language ("intent") queries than embedding the
/// one-sentence AI summary alone. <c>summary</c> keeps the legacy summary-only behaviour.
/// </para>
/// </summary>
public static class EmbeddingTextBuilder
{
    /// <summary>
    /// The body's char cap for English prose. Retained for callers and tests that reason in characters, but
    /// it is <b>no longer the budget</b> — see <see cref="TokenBudget.EmbeddingBodyTokens"/>. A char cap is a
    /// proxy for the backend's token window that silently fails on CJK and code-dense text (#111).
    /// </summary>
    public const int MaxBodyChars = 1500;

    /// <summary>Builds the embedding document for <paramref name="node"/> under the given source mode.</summary>
    /// <param name="node">The node to embed.</param>
    /// <param name="summary">The node's AI summary, if any (folded into the code document; the whole text for <c>summary</c> mode).</param>
    /// <param name="source"><c>code</c> (default) or <c>summary</c>; unrecognized values fall back to <c>code</c>.</param>
    public static string Build(GraphNode node, string? summary, string source)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (string.Equals(source, "summary", StringComparison.OrdinalIgnoreCase))
        {
            return summary ?? string.Empty;
        }

        var signature = node.Properties.TryGetValue("signature", out var sig) ? sig : string.Empty;
        // Budget in TOKENS, not characters (#111) — and in the same unit the markdown parser uses to decide
        // when to split, so parser and embedder can no longer disagree about what fits the backend window.
        var body = HeadAndTailWithinTokens(node.Content ?? string.Empty, TokenBudget.EmbeddingBodyTokens);

        var sb = new StringBuilder();
        sb.Append(node.Type).Append(' ').Append(node.Name).Append('\n');
        if (!string.IsNullOrWhiteSpace(signature))
        {
            sb.Append(signature).Append('\n');
        }
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.Append(summary).Append('\n');
        }
        sb.Append(body);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the embedding document for a <c>Concept</c> node. A concept carries no source body, so
    /// embedding its bare name yields an almost meaningless vector. What gives it retrievable meaning is the
    /// company it keeps: the names of the nodes linked to it (plus its summary, when one exists).
    /// </summary>
    public static string BuildConcept(string name, string? summary, IReadOnlyList<string>? connectedNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var sb = new StringBuilder();
        sb.Append("Concept ").Append(name).Append('\n');
        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.Append(summary).Append('\n');
        }
        var related = connectedNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray() ?? [];
        if (related.Length > 0)
        {
            sb.Append("Related: ").Append(string.Join(", ", related));
        }
        return sb.ToString().TrimEnd();
    }

    private const string MiddleGap = "\n… [middle truncated] …\n";

    /// <summary>
    /// Bounds <paramref name="body"/> to <paramref name="max"/> chars while keeping BOTH ends of a large
    /// symbol (TICKET-105): the head carries the signature/opening, the tail carries return/cleanup logic —
    /// so a query targeting either end can still match, instead of only the head being embedded. Cuts at
    /// line boundaries where possible. Bodies within budget are returned unchanged.
    /// </summary>
    /// <summary>
    /// Head-and-tail slice of <paramref name="body"/> that fits <paramref name="budgetTokens"/> (#111).
    /// <para>
    /// <see cref="HeadAndTail"/> measures in characters, which is the proxy that silently truncated CJK and
    /// code-dense bodies at the backend. This binary-searches the character cap that lands inside the token
    /// budget — so a Chinese body is cut to roughly a quarter of the characters an English one keeps, which
    /// is exactly right, because it costs about four times as many tokens per character.
    /// </para>
    /// </summary>
    public static string HeadAndTailWithinTokens(string body, int budgetTokens)
    {
        if (TokenBudget.Fits(body, budgetTokens)) return body;

        int lo = 0, hi = body.Length, best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (TokenBudget.Fits(HeadAndTail(body, mid), budgetTokens)) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return HeadAndTail(body, best);
    }

    public static string HeadAndTail(string body, int max)
    {
        if (body.Length <= max)
        {
            return body;
        }
        if (max <= MiddleGap.Length + 2)
        {
            return body[..max];
        }

        var budget = max - MiddleGap.Length;
        var headLen = (int)(budget * 0.6);
        var tailLen = budget - headLen;

        var head = body[..headLen];
        var lastNl = head.LastIndexOf('\n');
        if (lastNl > headLen / 2)
        {
            head = head[..lastNl];
        }

        var tail = body[^tailLen..];
        var firstNl = tail.IndexOf('\n');
        if (firstNl >= 0 && firstNl < tailLen / 2)
        {
            tail = tail[(firstNl + 1)..];
        }

        return head + MiddleGap + tail;
    }
}
