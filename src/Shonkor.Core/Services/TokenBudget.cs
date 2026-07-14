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
///
/// <para>
/// <b>What "conservative" turned out to mean (#173).</b> That claim went unmeasured until it was checked
/// against the real <c>bert-base-uncased</c> tokenizer. It was <b>false</b> for whole input classes: hex,
/// base64 and GUIDs under-counted by 2–3× (base64 measured at 2.75×), because the 4-chars/token rule assumes
/// text compresses like English words, and out-of-vocabulary alphanumeric runs shred to ~1.5 chars/token
/// instead. A section of base64 passed a 1000-token budget check while really costing ~2750 tokens — over the
/// window, silently truncated. Precisely the bug this class was written to remove, still live.
/// </para>
/// <para>
/// The estimate is now genuinely conservative on those classes (see <see cref="Estimate"/>), and the residual,
/// unavoidable under-counting of short out-of-vocabulary identifiers is bounded and budgeted for — see
/// <see cref="UnderCountSafetyFactor"/>, which states the invariant the design actually relies on instead of
/// the stronger one it used to claim.
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
    /// The embedding backend's context window (<c>nomic-embed-text</c> ≈ 2048 tokens). Text longer than this
    /// is silently truncated by the backend — the failure #111 exists to prevent.
    /// </summary>
    public const int ModelWindowTokens = 2048;

    /// <summary>
    /// How far <see cref="Estimate"/> is allowed to under-count a real tokenizer before the design breaks.
    ///
    /// <para>
    /// <b>This is the invariant the whole splitting design actually rests on</b> — and until #173 it was
    /// accidental rather than stated. <see cref="Estimate"/> cannot be a strict upper bound on a real
    /// tokenizer's count without becoming useless: a sub-word tokenizer shreds any <i>out-of-vocabulary</i>
    /// alphanumeric run to ~1.5–2 characters per token (measured: <c>Rrf</c> → 2 tokens, <c>Ollama</c> → 3),
    /// while real words cost ~4 characters per token. Charging every run at the worst case would over-count
    /// English prose ~2.5× and split it into needlessly small sections, diluting retrieval — a worse bug than
    /// the one being fixed.
    /// </para>
    /// <para>
    /// So the estimate is allowed to under-count by up to this factor, and the budgets below are sized so that
    /// even a full-factor under-count still fits the window: <see cref="SectionBudgetTokens"/> × this ≤
    /// <see cref="ModelWindowTokens"/>. A section that passes the budget check therefore cannot be truncated,
    /// even in the worst measured case. <c>TokenBudgetRealTokenizerTests</c> pins both halves of that.
    /// </para>
    /// </summary>
    public const int UnderCountSafetyFactor = 2;

    /// <summary>
    /// A run of alphanumeric characters longer than this is charged one token per character, as a backstop for
    /// digit-free high-entropy blobs (base64 that happens to contain no digit, minified JS/CSS) that would
    /// otherwise be costed as if they were English words. It is set deliberately <b>high</b>: the threshold
    /// that matters for correctness is the digit rule in <see cref="Estimate"/>, and lowering this to catch
    /// long code identifiers was measured and rejected — at 15 it over-counted a real source file by 29%
    /// (<c>SqliteGraphStorageProvider.cs</c>: 30 424 vs 23 508 estimated tokens) while buying nothing on any
    /// real input, because such a file is already costed at 1.2–1.5× the true count. That over-counting would
    /// have shrunk the embedded body of every code node for no measured gain.
    /// </summary>
    private const int WordLikeMaxRun = 40;

    /// <summary>
    /// A conservative, script-aware estimate of how many tokens <paramref name="text"/> costs, measured
    /// against the real <c>bert-base-uncased</c> WordPiece tokenizer (the one <c>nomic-embed-text</c> uses) and
    /// cl100k (#173).
    ///
    /// <para>
    /// It is <b>not</b> a strict upper bound, and claiming so would be false — see
    /// <see cref="UnderCountSafetyFactor"/> for why that is impossible to combine with usable prose estimates.
    /// What it guarantees is that it never under-counts by more than that factor, which is what the budgets
    /// are sized against.
    /// </para>
    ///
    /// <para>The rules, and why each is what it is:</para>
    /// <list type="bullet">
    ///   <item><b>CJK / Kana / Hangul → 1 token per character.</b> An uncased WordPiece tokenizer emits at most
    ///   one token per such character (a known character maps to itself, an unknown one to a single
    ///   <c>[UNK]</c>), so this is an exact upper bound, never optimistic.</item>
    ///   <item><b>Word-like alphanumeric runs (letters only, ≤ <see cref="WordLikeMaxRun"/> chars) → 1 token
    ///   per 4 characters (rounded up).</b> The standard English approximation, applied per word so a document
    ///   of many short words is not under-counted the way a flat <c>length / 4</c> would do. It under-counts
    ///   out-of-vocabulary identifiers somewhat (<c>Ollama</c> → 2 estimated, 3 real), which is what
    ///   <see cref="UnderCountSafetyFactor"/> absorbs.</item>
    ///   <item><b>Non-word-like runs (containing a digit, or longer than <see cref="WordLikeMaxRun"/>) → 1
    ///   token per character.</b> Hex, base64, GUIDs and long compound identifiers do not compress like real
    ///   words — measured at ~1.5 chars/token — so the 4-chars/token rule under-counted them by <i>more</i>
    ///   than the safety factor absorbs (base64: 2.75×), letting a section pass the budget check and still
    ///   overflow the window. One-token-per-char is the WordPiece worst case (a token consumes at least one
    ///   character), so it is provably never optimistic, and the over-counting it causes is harmless: it only
    ///   applies to non-prose content, where splitting earlier costs nothing.</item>
    ///   <item><b>Every other non-space character → 1 token.</b> Punctuation, brackets, operators and pipes
    ///   usually tokenize individually. This is what makes fenced code and tables — the cases a character
    ///   count gets most wrong — come out heavy, which is the point.</item>
    /// </list>
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var tokens = 0;
        var runLen = 0;
        var runHasDigit = false;

        foreach (var ch in text)
        {
            if (IsWide(ch))
            {
                tokens += FlushRun(ref runLen, ref runHasDigit) + 1;
            }
            else if (char.IsLetterOrDigit(ch))
            {
                runLen++;
                runHasDigit |= char.IsDigit(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                tokens += FlushRun(ref runLen, ref runHasDigit);
            }
            else
            {
                tokens += FlushRun(ref runLen, ref runHasDigit) + 1; // punctuation / symbol
            }
        }
        return tokens + FlushRun(ref runLen, ref runHasDigit);

        // A word-like run of n chars costs ceil(n / 4) tokens; a non-word-like run (digit-bearing or longer
        // than WordLikeMaxRun) costs n tokens — the WordPiece worst case, so the estimate can never be optimistic.
        static int FlushRun(ref int run, ref bool hasDigit)
        {
            if (run == 0) { hasDigit = false; return 0; }
            var cost = (hasDigit || run > WordLikeMaxRun) ? run : (run + 3) / 4;
            run = 0;
            hasDigit = false;
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
