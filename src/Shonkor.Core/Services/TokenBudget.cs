// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Services;

/// <summary>
/// The one place that decides "does this text fit the embedding model's window?" (#111).
///
/// <para>
/// <b>The bug this replaces.</b> The parser split sections at <c>4000 characters</c> and the embedder
/// truncated bodies at <c>1500 characters</c> — two different constants, both standing in for the same thing:
/// the backend's token window (<c>nomic-embed-text</c> ≈ 2048 tokens). The proxy holds for English prose at
/// roughly 4 characters per token. It <b>fails silently</b> everywhere else:
/// </para>
/// <list type="bullet">
///   <item><b>CJK</b> — about <i>one token per character</i>, so a 4000-char section is ~4000 tokens. The
///   backend truncates it and the tail of the section is embedded into nothing. Nothing errors; the section
///   is simply half-searchable, and no one finds out.</item>
///   <item><b>Fenced code and tables</b> — punctuation-dense, so far more tokens per visible character than
///   prose.</item>
/// </list>
///
/// <para>
/// <b>Why this is an estimate and not a tokenizer.</b> The obvious move is a real tokenizer
/// (<c>Microsoft.ML.Tokenizers</c>). It was rejected deliberately: an exact tokenizer needs the <i>vocabulary
/// of the actual embedding model</i>, and the model is <b>user-configurable</b>
/// (<c>EmbeddingService:OllamaModel</c>). Shipping one model's vocab would produce a tokenizer that is exact
/// for the wrong model the moment a user swaps it — and an <i>exact-looking</i> wrong answer is worse than an
/// honest approximation, because nothing signals the mismatch.
/// </para>
///
/// <para>
/// So the estimate is deliberately <b>conservative</b>: it over-counts rather than under-counts. Over-counting
/// splits a section earlier than strictly necessary (cheap, harmless). Under-counting hands the backend a
/// document it silently truncates (invisible, and exactly the failure we are removing).
/// </para>
/// </summary>
public static class TokenBudget
{
    /// <summary>
    /// Token budget for one Markdown section before it splits into <c>::part::N</c> nodes. Calibrated so
    /// English prose behaves exactly as it did under the old 4000-character rule (≈ 4 chars/token), which is
    /// what keeps the <c>doc-sections</c> golden set from regressing. CJK and code-dense text now split where
    /// they previously overflowed the backend.
    /// </summary>
    public const int SectionBudgetTokens = 1000;

    /// <summary>
    /// Token budget for the body slice inside an embedding document. Calibrated to the old 1500-character
    /// rule for prose. Parser and embedder now measure in the <b>same unit</b> — previously they used two
    /// different constants for the same underlying limit and could disagree about what fits.
    /// </summary>
    public const int EmbeddingBodyTokens = 375;

    /// <summary>
    /// A conservative, script-aware estimate of how many tokens <paramref name="text"/> costs.
    ///
    /// <para>The rules, and why each is what it is:</para>
    /// <list type="bullet">
    ///   <item><b>CJK / Kana / Hangul → 1 token per character.</b> Sub-word tokenizers rarely merge these, so
    ///   this is close to exact and never optimistic.</item>
    ///   <item><b>Alphanumeric runs → 1 token per 4 characters (rounded up).</b> The standard English
    ///   approximation, applied per word rather than to the whole string, so a document of many short words
    ///   is not under-counted the way a flat <c>length / 4</c> would do.</item>
    ///   <item><b>Every other non-space character → 1 token.</b> Punctuation, brackets, operators and pipes
    ///   usually tokenize individually. This is what makes fenced code and tables — the cases a character
    ///   count gets most wrong — come out heavy, which is the point.</item>
    /// </list>
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var tokens = 0;
        var alnumRun = 0;

        foreach (var ch in text)
        {
            if (IsWide(ch))
            {
                tokens += FlushRun(ref alnumRun) + 1;
            }
            else if (char.IsLetterOrDigit(ch))
            {
                alnumRun++;
            }
            else if (char.IsWhiteSpace(ch))
            {
                tokens += FlushRun(ref alnumRun);
            }
            else
            {
                tokens += FlushRun(ref alnumRun) + 1; // punctuation / symbol
            }
        }
        return tokens + FlushRun(ref alnumRun);

        // A word of n alphanumeric characters costs ceil(n / 4) tokens, minimum 1.
        static int FlushRun(ref int run)
        {
            if (run == 0) return 0;
            var cost = (run + 3) / 4;
            run = 0;
            return cost;
        }
    }

    /// <summary>True when <paramref name="text"/> is estimated to fit within <paramref name="budgetTokens"/>.</summary>
    public static bool Fits(string? text, int budgetTokens) => Estimate(text) <= budgetTokens;

    /// <summary>
    /// Characters that a sub-word tokenizer generally does not merge: CJK ideographs, Japanese kana, Hangul,
    /// and the CJK punctuation/fullwidth blocks. These are the scripts where a character count under-estimates
    /// the token count by roughly 4×, which is what silently truncated them.
    /// </summary>
    private static bool IsWide(char ch) =>
        (ch >= '　' && ch <= '〿') ||   // CJK symbols & punctuation
        (ch >= '぀' && ch <= 'ヿ') ||   // Hiragana + Katakana
        (ch >= '㐀' && ch <= '䶿') ||   // CJK Extension A
        (ch >= '一' && ch <= '鿿') ||   // CJK Unified Ideographs
        (ch >= '가' && ch <= '힯') ||   // Hangul syllables
        (ch >= '豈' && ch <= '﫿') ||   // CJK compatibility ideographs
        (ch >= '＀' && ch <= '￯');     // Halfwidth & fullwidth forms
}
