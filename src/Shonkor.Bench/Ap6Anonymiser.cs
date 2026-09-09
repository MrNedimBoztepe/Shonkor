// Licensed to Shonkor under the MIT License.

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

    /// <summary>Tokens in prose, word-bounded the way <see cref="Ap6Corpus.ContainsWord"/> defines a word (hyphen included).</summary>
    private static readonly Regex TokenInText =
        new(@"(?<![A-Za-z0-9_-])(Rendering|Controller|View|Template|Model|Page)-\d{2,4}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant);

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
