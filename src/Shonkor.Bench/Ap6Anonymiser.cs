// Licensed to Shonkor under the MIT License.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shonkor.Core.Services;

namespace Shonkor.Bench;

/// <summary>
/// One entry of the out-of-repo class-C mapping (<c>&lt;corpus&gt;/bench/ap6-mapping.json</c>, written by
/// <c>keys-c.sh</c>): what an anonymised token stands for. Only <see cref="Kind"/>, <see cref="Path"/> and
/// <see cref="Name"/> are read by the harness; the rest (ids, full names) stays in the file.
/// </summary>
internal sealed record Ap6MappingEntry(string? Kind, string? Path, string? Name, string? FullName);

/// <summary>The class-C mapping: tokens → real names, plus the corpus revision the tokens are a function of.</summary>
internal sealed record Ap6Mapping(
    int? SchemaVersion,
    string? CorpusRoot,
    string? CorpusRevision,
    List<string>? DenyWords,
    Dictionary<string, Ap6MappingEntry>? Entries)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static Ap6Mapping Load(string path) => Parse(File.ReadAllText(path));

    public static Ap6Mapping Parse(string jsonText) =>
        JsonSerializer.Deserialize<Ap6Mapping>(jsonText, JsonOptions) ?? new Ap6Mapping(null, null, null, null, null);

    /// <summary>SHA-256 digests of the lower-cased deny words — the form <see cref="Ap6Corpus.FindLeaks"/> takes.</summary>
    public HashSet<string> DenyWordHashes() =>
        (DenyWords ?? []).Select(w => Ap6Corpus.Sha256Hex(w.ToLowerInvariant())).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Both directions of the class-C anonymisation (#473): tokens in a query become real names for the arm's
/// prompt (only ever written into the run directory, outside both repositories), and real paths / type
/// names in an arm's answer become tokens again for scoring. Anything in an answer that has no mapping
/// entry is <b>counted</b>, never carried — the counters are what reaches the report.
/// </summary>
internal static class Ap6Anonymiser
{
    /// <summary>
    /// The mapping kinds an answer <b>symbol</b> can name: a controller or a model class. A rendering, view or
    /// template is a file or an item, never a type name in an answer — so its <c>name</c> must not compete with
    /// a class name when a symbol is read back. <see cref="Ap6Preconditions.CheckTokens"/> checks key-symbol
    /// uniqueness over exactly this set, so the precondition and the scorer agree on what a name can mean.
    /// </summary>
    public static readonly string[] SymbolKinds = ["controller", "model"];

    public static bool IsSymbolKind(string? kind) => kind is not null && SymbolKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>A class-C answer translated back into tokens, with the parts that could not be.</summary>
    /// <param name="Files">Tokens of the answer's files that map to an entry path.</param>
    /// <param name="Symbols">Tokens of the answer's type names that map to exactly one entry (after path disambiguation).</param>
    /// <param name="UnmappedFiles">Answer files with no entry — a wrong or vendor file.</param>
    /// <param name="UnmappedSymbols">Answer symbols whose type name matches no entry.</param>
    /// <param name="AmbiguousSymbols">Answer symbols whose type name matches several entries and no answer file picks one — a miss, flagged.</param>
    public sealed record TokenisedAnswer(
        List<string> Files, List<string> Symbols, int UnmappedFiles, int UnmappedSymbols, int AmbiguousSymbols);

    /// <summary>The token kinds, as one regex alternation — the single spelling <see cref="TokenInText"/> and <see cref="AllowedWord"/> both build on.</summary>
    private const string TokenKinds = "Rendering|Controller|View|Template|Model|Page";

    /// <summary>Tokens in prose, word-bounded the way <see cref="Ap6Corpus.ContainsWord"/> defines a word (hyphen included).</summary>
    private static readonly Regex TokenInText =
        new($@"(?<![A-Za-z0-9_-])({TokenKinds})-\d{{2,4}}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant);

    /// <summary>The stand-ins the redaction writes. Nothing else may look like one, so they are listed once and read from here.</summary>
    public static readonly string[] Placeholders = ["<corpus>", "<guid>", "<item-path>", "<abs-path>", "<redacted>"];

    /// <summary>What <see cref="RedactToolInput"/> writes for a string it cannot vouch for.</summary>
    public const string Redacted = "<redacted>";

    /// <summary>See the remarks on <see cref="RedactToolInput"/>: the placeholders must survive the one decode that reads them back.</summary>
    private static readonly JsonWriterOptions InputWriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions InputSerializerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The query an arm receives: every token replaced by the entry's <c>name</c>. Throws when a token has
    /// no entry — that is a precondition failure (<see cref="Ap6Preconditions"/>), not something to paper over
    /// with the token left in place (the arm would then search for "Rendering-017" and fail for the wrong reason).
    /// </summary>
    public static string ResolveQuery(string query, Ap6Mapping mapping) =>
        TokenInText.Replace(query, m =>
        {
            if (mapping.Entries is not null && mapping.Entries.TryGetValue(m.Value, out var e) && !string.IsNullOrEmpty(e.Name))
                return e.Name;
            throw new InvalidOperationException($"token '{m.Value}' has no mapping entry with a name");
        });

    /// <summary>All tokens occurring in <paramref name="text"/>, in order of appearance, distinct.</summary>
    public static IReadOnlyList<string> TokensIn(string text) =>
        TokenInText.Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Answer → tokens. <paramref name="files"/> are already normalised repository-relative paths
    /// (<see cref="Ap6Scorer.NormalizePath"/>); <paramref name="symbols"/> already <c>Type</c> /
    /// <c>Type.Member</c> (<see cref="Ap6Scorer.NormalizeSymbol"/>). Paths compare with
    /// <see cref="FilePaths.Comparer"/>, names ordinally: a type name is an identifier, a path is a file.
    /// </summary>
    public static TokenisedAnswer ToTokens(IReadOnlyList<string> files, IReadOnlyList<string> symbols, Ap6Mapping mapping)
    {
        var entries = mapping.Entries ?? [];
        var byPath = new Dictionary<string, string>(FilePaths.Comparer);
        foreach (var (token, e) in entries)
            if (!string.IsNullOrEmpty(e.Path)) byPath.TryAdd(e.Path.Replace('\\', '/'), token);

        var fileTokens = new List<string>();
        var unmappedFiles = 0;
        foreach (var f in files)
        {
            if (byPath.TryGetValue(f, out var token)) { if (!fileTokens.Contains(token, StringComparer.Ordinal)) fileTokens.Add(token); }
            else unmappedFiles++;
        }

        var symbolEntries = entries.Where(kv => IsSymbolKind(kv.Value.Kind)).ToList();
        var symbolTokens = new List<string>();
        int unmappedSymbols = 0, ambiguous = 0;
        foreach (var s in symbols)
        {
            // `Ns.Type` names the type in its last segment, `Type.Member` in its second-last: try the last first
            // (a bare type is the common answer), then the one before it.
            var segments = s.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var hits = new List<string>();
            foreach (var candidate in segments.Reverse().Take(2))
            {
                hits = symbolEntries.Where(kv => string.Equals(kv.Value.Name, candidate, StringComparison.Ordinal)).Select(kv => kv.Key).ToList();
                if (hits.Count > 0) break;
            }
            if (hits.Count == 0) { unmappedSymbols++; continue; }
            if (hits.Count > 1)
            {
                // Several classes share the simple name (#488): the answer's own files decide, if exactly one of them does.
                var viaFiles = hits.Where(fileTokens.Contains).ToList();
                if (viaFiles.Count == 1) hits = viaFiles;
                else { ambiguous++; continue; }
            }
            if (!symbolTokens.Contains(hits[0], StringComparer.Ordinal)) symbolTokens.Add(hits[0]);
        }
        return new TokenisedAnswer(fileTokens, symbolTokens, unmappedFiles, unmappedSymbols, ambiguous);
    }

    /// <summary>
    /// Redacts a class-C tool-call argument for the results file: every entry's path, full name and name
    /// becomes its token (longest first, so a path wins over the file stem inside it), the corpus root
    /// becomes <c>&lt;corpus&gt;</c>, Sitecore item paths and GUIDs become placeholders, and deny words are
    /// blanked. The output is still checked by <see cref="Ap6Corpus.FindLeaks"/> before anything is written.
    /// </summary>
    public static string RedactArgument(string text, Ap6Mapping mapping)
    {
        var result = string.IsNullOrEmpty(mapping.CorpusRoot) ? CollapseSlashes(text) : ReplaceRoot(text, mapping.CorpusRoot, "<corpus>");

        var literals = new List<(string Literal, string Token)>();
        foreach (var (token, e) in mapping.Entries ?? [])
        {
            if (!string.IsNullOrEmpty(e.Path)) literals.Add((e.Path.Replace('\\', '/'), token));
            if (!string.IsNullOrEmpty(e.FullName)) literals.Add((e.FullName, token));
            if (!string.IsNullOrEmpty(e.Name)) literals.Add((e.Name, token));
        }
        foreach (var (literal, token) in literals.OrderByDescending(l => l.Literal.Length))
        {
            var pattern = literal.Contains('/')
                ? Regex.Escape(literal)
                : $@"(?<![A-Za-z0-9_]){Regex.Escape(literal)}(?![A-Za-z0-9_])";
            result = Regex.Replace(result, pattern, token, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        result = RedactPaths(result);
        foreach (var w in (mapping.DenyWords ?? []).Where(w => !string.IsNullOrEmpty(w)).OrderByDescending(w => w.Length))
            result = Regex.Replace(result, $@"(?<![A-Za-z0-9_]){Regex.Escape(w)}(?![A-Za-z0-9_])", "<redacted>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return result;
    }

    /// <summary>
    /// Redacts a class-A/B tool-call argument for the results file: every path under <paramref name="root"/>
    /// (the arm's cwd — the Brain checkout) becomes repository-relative. The rg arm reads files by absolute
    /// path, and an absolute path under the projects root is a fixed leak pattern of
    /// <see cref="Ap6Corpus.FindLeaks"/> — without this no <c>results-A/B.json</c> could ever be written.
    /// Anything absolute that is not under the root goes through <see cref="RedactPaths"/>.
    /// </summary>
    public static string RelativiseArgument(string text, string root) =>
        RedactPaths(string.IsNullOrEmpty(root) ? text : ReplaceRoot(text, root, string.Empty));

    /// <summary>
    /// Tool-call inputs are raw JSON: a Windows path arrives as <c>C:\\Corpora\\X</c> and would become
    /// <c>C://Corpora//X</c>; runs of '/' collapse so a root and the entry paths match. A <c>://</c> in a URL
    /// collapses too — harmless here.
    /// </summary>
    private static string CollapseSlashes(string text) => Regex.Replace(text.Replace('\\', '/'), "/{2,}", "/");

    /// <summary>
    /// Every occurrence of <paramref name="root"/> becomes <paramref name="replacement"/>, case-insensitively (a
    /// drive letter is written both ways). With an empty replacement the separator after the root goes too, so
    /// what remains is the relative path.
    /// </summary>
    private static string ReplaceRoot(string text, string root, string replacement)
    {
        var pattern = Regex.Escape(CollapseSlashes(root).TrimEnd('/')) + (replacement.Length == 0 ? "/" : string.Empty);
        return Regex.Replace(CollapseSlashes(text), pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // ---------- redact by default (#511) ----------

    /// <summary>
    /// The only words a class-C string may still contain in clear.
    ///
    /// <para><b>The invariant this list is kept under.</b> Over-redaction costs readability; under-redaction
    /// is a customer-data leak into a repository whose history cannot be un-published. So the list may be
    /// incomplete — that is the safe direction — and it must <b>never</b> be extended in reaction to a
    /// concrete string that came out <c>&lt;redacted&gt;</c> and looked harmless. Every entry here is a word
    /// this repository writes itself: Shonkor's own vocabulary, the answer schema's keys, and the words the
    /// harness's own <c>armViolation</c> / <c>notRunReason</c> texts are built from. A word first seen in a
    /// corpus does not belong here no matter how generic it looks.</para>
    ///
    /// <para>Most of the surface is derived rather than listed: tokens and placeholders are recognised
    /// structurally (<see cref="TokenKinds"/>, <see cref="Placeholders"/>) and tool <i>names</i> are judged
    /// by <see cref="IsOwnToolName"/> rather than word by word. What is left is this handful.</para>
    ///
    /// <para><b>Not</b> derived from the call: #514 removed a per-call extension by which every word of the
    /// tool's own name was allowed inside that call's string values. It let a model-supplied name widen the
    /// allow-list, and — worse — it was an extension layer 1 had and layer 2 did not, so a value containing
    /// e.g. "locate" passed the transform and was then reported as a leak by the check, after the runs were
    /// paid for. One predicate, no per-call parameters; a value that echoes a tool name is over-redacted,
    /// which is the direction this list is allowed to fail in.</para>
    ///
    /// <para><b>Known limit:</b> a string with no ASCII letters (a bare number, an IP address) carries no
    /// word for the classifier to judge and is written as it stands. It names nobody in a code-search
    /// corpus, and making digits a word would blank the harness's own counts; see the follow-up issue
    /// linked from #511 rather than widening this list to compensate.</para>
    /// </summary>
    public static readonly string[] AllowedWords =
    [
        // Shonkor / graph vocabulary and the harness's own nouns.
        "arm", "bench", "call", "calls", "depth", "edge", "edges", "file", "files", "graph", "hops", "limit",
        "mcp", "node", "nodes", "path", "query", "rg", "shonkor", "src", "symbol", "symbols", "tool", "tools",
        // File kinds of the corpus and of Brain — a suffix, never a name.
        "cs", "cshtml", "csproj", "json", "md", "sln", "yml",
        // The answer schema's keys and JSON's own literals (a value may legitimately echo one).
        "additionalProperties", "array", "const", "integer", "object", "properties", "required", "schemaVersion",
        "string", "type", "items", "true", "false", "null",
        // The words the harness's own generated texts use — armViolation, notRunReason, the not-run prefixes.
        "API", "Bash", "HTTP", "Read", "connected", "error", "event", "executed", "has", "in", "init", "lacks",
        "loop", "no", "non", "outside", "rate", "server", "servers", "status", "stream", "subtype", "the",
        "unknown", "usage", "without",
        // The "(s)" of our own "tool(s)" / "command(s)" messages: one letter on its own names nobody.
        "s",
    ];

    private static readonly HashSet<string> AllowedWordSet = new(AllowedWords, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One pass over a string: a placeholder or a mapping token is kept whole (so <c>&lt;item-path&gt;</c> is
    /// not read as the words "item" and "path"), anything else identifier-shaped is a word to be judged.
    /// Punctuation, digits and separators fall between the matches and are left alone — they carry no name.
    /// </summary>
    private static readonly Regex AllowedWord = new(
        $@"(?<keep>{string.Join("|", Placeholders.Select(Regex.Escape))}|(?:{TokenKinds})-\d{{2,4}})|(?<word>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant);

    /// <summary>Every word of <paramref name="text"/> that is neither a token, nor a placeholder, nor allow-listed — empty means the string may be written as it stands.</summary>
    public static IReadOnlyList<string> DisallowedWords(string text) =>
        AllowedWord.Matches(text)
            .Where(m => m.Groups["word"].Success && !AllowedWordSet.Contains(m.Groups["word"].Value))
            .Select(m => m.Groups["word"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The predicate both layers share, with no per-call variation of any kind: layer 1
    /// (<see cref="RedactToolInput"/>, <see cref="RedactText"/>) makes it true, layer 2
    /// (<see cref="Ap6Corpus.FindResultsLeaks"/>) checks that it is. Anything one layer allows and the other
    /// does not is a run-set thrown away after it was paid for, which is what #514 found here.
    /// </summary>
    public static bool IsAllowedString(string text) => DisallowedWords(text).Count == 0;

    /// <summary>
    /// A class-C free-text string, safe to write: first <see cref="RedactArgument"/> (mapping entries become
    /// tokens, the corpus root, item paths and GUIDs become placeholders, deny words are blanked), then every
    /// word that survived and is not allow-listed becomes <see cref="Redacted"/>. <paramref name="changed"/>
    /// says whether anything was blanked in the second step, which is what the row's <c>redactedStrings</c> counts.
    /// </summary>
    public static string RedactText(string text, Ap6Mapping mapping, out bool changed)
    {
        var mapped = RedactArgument(text, mapping);
        var blanked = false;
        var result = AllowedWord.Replace(mapped, m =>
        {
            if (m.Groups["keep"].Success) return m.Value;
            var word = m.Groups["word"].Value;
            if (AllowedWordSet.Contains(word)) return word;
            blanked = true;
            return Redacted;
        });
        changed = blanked;
        // Consecutive stand-ins say nothing more than one does and make a path unreadable.
        return CollapseRedactions(result);
    }

    private static readonly Regex RepeatedRedaction = new(@"(?:<redacted>)(?:\s*<redacted>)+", RegexOptions.CultureInvariant);

    private static string CollapseRedactions(string text) => RepeatedRedaction.Replace(text, Redacted);

    /// <summary>
    /// A class-C tool-call input, rebuilt rather than patched (#511). The old redaction was a set of
    /// subtractions — corpus root, mapping entries, GUIDs, deny words — and therefore only ever as good as
    /// its list of things to remove; the first smoke run wrote three customer identifiers into
    /// <c>results-C.json</c> because none of them was on any of those lists. This walks the JSON and emits a
    /// new document: numbers, booleans and nulls pass (they name nobody), and every string — value
    /// <b>and object key</b> — must earn its way out through <see cref="RedactText"/>. A JSON kind it does
    /// not know becomes <see cref="Redacted"/>, and input that does not parse becomes <see cref="Redacted"/>
    /// whole — an unparseable input is exactly the case where a subtractive redaction would have guessed.
    ///
    /// <para>Keys used to pass untouched as "the tool's schema, written by us". They are not: the model
    /// writes the input object, so it can put a name in a key as easily as in a value, and a key was
    /// visited by neither layer (#514). Judging them costs readability where a schema key is not
    /// allow-listed vocabulary — the row still shows the shape and the values — and it keeps one predicate
    /// for every string in the file.</para>
    /// </summary>
    /// <remarks>
    /// The result is a JSON string <i>inside</i> the results document, so the outer serialiser escapes it a
    /// second time anyway. This writer therefore does not escape <c>&lt;</c> and <c>&gt;</c> itself: with the
    /// default encoder one decode of the results file hands back the literal text <c>&lt;redacted&gt;</c>
    /// — an escape sequence that outlived its decode, and that a reader, a grep and the leak check would all
    /// have to know about. Relaxed here means "not escaped twice", not "not escaped".
    /// </remarks>
    /// <param name="inputJson">The raw <c>tool_use.input</c> JSON as the stream carried it.</param>
    /// <returns>The rebuilt JSON text and how many strings were blanked.</returns>
    public static (string Json, int Redacted) RedactToolInput(string inputJson, Ap6Mapping mapping)
    {
        var count = 0;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(inputJson); }
        catch (JsonException) { return (JsonSerializer.Serialize(Redacted, InputSerializerOptions), 1); }
        using (doc)
        {
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, InputWriterOptions)) Write(doc.RootElement, writer);
            return (Encoding.UTF8.GetString(buffer.ToArray()), count);
        }

        void Write(JsonElement e, Utf8JsonWriter w)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach (var p in e.EnumerateObject())
                    {
                        var key = RedactText(p.Name, mapping, out var keyChanged);
                        if (keyChanged) count++;
                        w.WritePropertyName(key);
                        Write(p.Value, w);
                    }
                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in e.EnumerateArray()) Write(item, w);
                    w.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    var text = RedactText(e.GetString() ?? string.Empty, mapping, out var changed);
                    if (changed) count++;
                    w.WriteStringValue(text);
                    break;
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    e.WriteTo(w);
                    break;
                case JsonValueKind.Null:
                    w.WriteNullValue();
                    break;
                default:
                    count++;
                    w.WriteStringValue(Redacted);
                    break;
            }
        }
    }

    /// <summary>
    /// Layer 1 for a whole run's calls: every name through <see cref="RedactToolName"/>, every input through
    /// <see cref="RedactToolInput"/>. One place, because the runner and the report tests both need it and a
    /// second copy of the loop is a second chance to forget one of the two halves.
    /// </summary>
    /// <returns>The redacted calls and how many strings were blanked, for the row's <c>redactedStrings</c>.</returns>
    public static (List<Ap6ToolCall> Calls, int Redacted) RedactCalls(
        IEnumerable<Ap6ToolCall> calls, IReadOnlyCollection<string> offeredTools, Ap6Mapping mapping)
    {
        var result = new List<Ap6ToolCall>();
        var redacted = 0;
        foreach (var c in calls)
        {
            var (input, n) = RedactToolInput(c.Input, mapping);
            var name = RedactToolName(c.Name, offeredTools);
            if (name != c.Name) redacted++;
            redacted += n;
            result.Add(new Ap6ToolCall(name, input));
        }
        return (result, redacted);
    }

    /// <summary>
    /// The shape of a tool name this harness itself offered: a built-in of the two arms
    /// (<see cref="Ap6Scorer.RgArmTools"/>, <see cref="Ap6Scorer.AnswerTool"/>) or one of Shonkor's own MCP
    /// tools. Names are judged as names, not word by word: <c>mcp__shonkor__find_usages</c> is our spelling
    /// of our tool, and allow-listing "usages" as a word would let it through in every customer string too.
    /// </summary>
    public static bool IsOwnToolName(string name) =>
        Ap6Scorer.RgArmTools.Contains(name, StringComparer.Ordinal)
        || name == Ap6Scorer.AnswerTool
        || OwnMcpToolName.IsMatch(name);

    private static readonly Regex OwnMcpToolName =
        new($@"^{Regex.Escape(Ap6Scorer.McpToolPrefix)}[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// A class-C tool-call <b>name</b>, safe to write (#514). The name came out of the stream and was
    /// written into <c>results-C.json</c> raw: neither redacted nor checked, so a name the model invented
    /// would have been published verbatim.
    ///
    /// <para>Two conditions, because the two layers can verify different things. Here the name must also
    /// have been <paramref name="offeredTools"/> — <c>system/init.tools</c>, which is our configuration and
    /// not the model's text — so nothing invented can survive. <see cref="Ap6Corpus.FindResultsLeaks"/>
    /// cannot see that list in the finished file and therefore checks the part that is written down, the
    /// shape. Layer 1 is the tight one; layer 2 is the net under it, as everywhere else here.</para>
    ///
    /// <para>A foreign tool the arm was somehow offered (which voids the run anyway) comes out
    /// <see cref="Redacted"/>: the run's own <c>meta.json</c>, outside both repositories, keeps the name.</para>
    /// </summary>
    public static string RedactToolName(string name, IReadOnlyCollection<string> offeredTools) =>
        IsOwnToolName(name) && offeredTools.Contains(name, StringComparer.Ordinal) ? name : Redacted;

    /// <summary>
    /// The mapping-free part of the redaction: Sitecore item paths, GUIDs and any absolute path under the
    /// projects root (a sibling checkout, a stray drive path — the fixed leak patterns of <see cref="Ap6Corpus.FindLeaks"/>).
    /// Applied to recorded environment text (plugin verify output) even when no mapping is loaded.
    /// </summary>
    public static string RedactPaths(string text)
    {
        var result = text.Replace('\\', '/');
        result = Regex.Replace(result, @"/sitecore/[^\s""'`,;)\]}]*", "<item-path>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = Regex.Replace(result, @"\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?", "<guid>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        result = Regex.Replace(result, @"[A-Za-z]:/+Projects[^\s""'`,;)\]}]*", "<abs-path>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return result;
    }
}
