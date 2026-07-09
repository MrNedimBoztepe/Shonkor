// Licensed to Shonkor under the MIT License.

using System.Text;
using System.Text.RegularExpressions;

namespace Shonkor.Core.Services;

/// <summary>Result of validating an answer's citations against the allowed label set.</summary>
public sealed record CitationReport(
    IReadOnlyList<string> ValidCitations,
    IReadOnlyList<string> InvalidCitations,
    int TotalCitations,
    int UncitedParagraphs)
{
    public bool HasInvalidCitations => InvalidCitations.Count > 0;
}

/// <summary>
/// Validates the citations a RAG answer emits (TICKET-206): every <c>[Name @ file:lines]</c> reference must
/// name a node that was actually in the provided context. Invalid citations (the model inventing a source)
/// are surfaced, not silently trusted. Pure and deterministic — the grounding safety net behind the prompt.
/// </summary>
public static partial class CitationValidator
{
    // The RAG prompt's citation form: [Name @ anything]. Captures the name before '@'.
    [GeneratedRegex(@"\[([^@\[\]]+?)\s*@\s*[^\]]+\]", RegexOptions.Compiled)]
    private static partial Regex CitationPattern();

    /// <summary>The distinct names cited by <paramref name="answer"/>, in first-seen order.</summary>
    public static IReadOnlyList<string> ExtractCitedNames(string answer)
    {
        if (string.IsNullOrEmpty(answer)) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (Match m in CitationPattern().Matches(answer))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length > 0 && seen.Add(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>Validates <paramref name="answer"/>'s citations against <paramref name="validNames"/>.</summary>
    public static CitationReport Validate(string answer, IReadOnlySet<string> validNames)
    {
        ArgumentNullException.ThrowIfNull(validNames);
        if (string.IsNullOrEmpty(answer))
        {
            return new CitationReport(Array.Empty<string>(), Array.Empty<string>(), 0, 0);
        }

        var matches = CitationPattern().Matches(answer);
        var valid = new List<string>();
        var invalid = new List<string>();
        var seenInvalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches)
        {
            var name = m.Groups[1].Value.Trim();
            if (validNames.Contains(name)) valid.Add(name);
            else if (seenInvalid.Add(name)) invalid.Add(name);
        }

        var paragraphs = answer
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToList();
        var uncited = paragraphs.Count(p => !CitationPattern().IsMatch(p));

        return new CitationReport(valid, invalid, matches.Count, uncited);
    }

    /// <summary>
    /// Appends a visible warning footer when the answer cites sources that were not in the context, so an
    /// invented citation is flagged to the reader instead of passing as grounded. Returns the answer
    /// unchanged when every citation is valid. The model's own text is never rewritten (only annotated).
    /// </summary>
    public static string AnnotateInvalid(string answer, IReadOnlySet<string> validNames, string? language = null)
    {
        var report = Validate(answer, validNames);
        if (!report.HasInvalidCitations) return answer;

        var german = !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder(answer.TrimEnd());
        sb.AppendLine().AppendLine();
        sb.AppendLine(german
            ? "> ⚠ **Unbelegte Quellen:** Die folgenden zitierten Quellen sind NICHT im bereitgestellten Kontext enthalten und daher nicht belegt:"
            : "> ⚠ **Unsupported sources:** the following cited sources are NOT in the provided context and are therefore unverified:");
        foreach (var name in report.InvalidCitations)
        {
            sb.AppendLine($"> - {name}");
        }
        return sb.ToString();
    }
}
