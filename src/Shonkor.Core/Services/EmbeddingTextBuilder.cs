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
    /// nomic-embed-text (the default backend) has a 2048-token window; the body is bounded so a large
    /// class/file isn't truncated arbitrarily by the backend and the header (type/name/signature/summary)
    /// always survives. ~1500 chars sits comfortably inside the window with the header.
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
        var body = HeadAndTail(node.Content ?? string.Empty, MaxBodyChars);

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

    private const string MiddleGap = "\n… [middle truncated] …\n";

    /// <summary>
    /// Bounds <paramref name="body"/> to <paramref name="max"/> chars while keeping BOTH ends of a large
    /// symbol (TICKET-105): the head carries the signature/opening, the tail carries return/cleanup logic —
    /// so a query targeting either end can still match, instead of only the head being embedded. Cuts at
    /// line boundaries where possible. Bodies within budget are returned unchanged.
    /// </summary>
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
