// Licensed to Shonkor under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shonkor.Bench;

/// <summary>
/// One task of the AP6 part 1 corpus (#466): a query, the mechanically derived answer key, and where that
/// key came from. Extends the <c>{id, query, expected[]}</c> shape of <c>bench/golden/agent-queries.json</c>
/// with a two-part key (files + symbols) and provenance.
/// </summary>
/// <param name="SchemaVersion">Always 1 for this shape.</param>
/// <param name="Id">Unique task id, e.g. <c>B-04</c>.</param>
/// <param name="Class"><c>A</c> local navigation, <c>B</c> whole-program C#, <c>C</c> cross-tech CMS.</param>
/// <param name="Corpus">Project name from <c>projects.json</c> — a name, never a path.</param>
/// <param name="Query">The question an arm receives. Must not contain the key (see <see cref="SeedInKey"/>).</param>
/// <param name="Key">What a correct answer names.</param>
/// <param name="KeySource">How the key was derived without consulting the graph.</param>
/// <param name="SeedInKey">True when the seed is unavoidably part of the key (the pilot's flag).</param>
/// <param name="Expectation">The policy column, fixed in the corpus: <c>rg</c> / <c>graph</c> / <c>graph-only</c>.</param>
internal sealed record Ap6Task(
    int? SchemaVersion,
    string? Id,
    string? Class,
    string? Corpus,
    string? Query,
    Ap6Key? Key,
    Ap6KeySource? KeySource,
    bool SeedInKey,
    string? Expectation);

/// <summary>The answer key — the same shape the harness will enforce on an arm's answer.</summary>
/// <param name="Files">Repo-relative paths with <c>/</c> (class A/B) or anonymised tokens (class C).</param>
/// <param name="Symbols"><c>TypeName</c> or <c>TypeName.Member</c> — no namespace, arity or span.</param>
internal sealed record Ap6Key(List<string>? Files, List<string>? Symbols);

/// <summary>Provenance of a key.</summary>
/// <param name="Method"><c>merge-commit</c> (class A/B) or <c>unicorn-yml</c> (class C).</param>
/// <param name="Ref">The commit SHA the key was read from (Brain merge or corpus revision).</param>
/// <param name="Rule">The mechanical rule the script applied, including the plausibility value it measured.</param>
internal sealed record Ap6KeySource(string? Method, string? Ref, string? Rule);

/// <summary>
/// Loads, validates and leak-checks <c>bench/golden/ap6/tasks.json</c> (#466).
///
/// <para>
/// Two independent checks on purpose. <see cref="Validate"/> answers "is this a corpus the harness can
/// score" (counts, shapes, class/expectation policy, seed-in-key). <see cref="FindLeaks"/> answers "did
/// customer data get in" — fixed patterns for the things that are always wrong (corpus paths, item paths,
/// GUIDs), a structural rule for class C (tokens only), and a hashed deny-list so the customer's own words
/// can be checked for without ever being written down in this repository.
/// </para>
/// </summary>
internal static class Ap6Corpus
{
    public const int ExpectedTaskCount = 30;
    public const int TasksPerClass = 10;

    private static readonly string[] Classes = ["A", "B", "C"];

    /// <summary>Class C keys and seeds are anonymised tokens of exactly this shape — nothing else.</summary>
    public static readonly Regex AnonymisedToken =
        new(@"^(Rendering|Controller|View|Template|Model|Page)-\d{2,4}$", RegexOptions.CultureInvariant);

    private static readonly Regex GitSha = new(@"^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant);

    /// <summary>Identifier-ish words, for the hashed deny-list; hyphenated tokens split into their parts.</summary>
    private static readonly Regex Word = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);

    /// <summary>
    /// Fixed leak patterns — always wrong in the corpus regardless of what the mapping file says: the corpus
    /// folder, any absolute path under the projects root, a Sitecore item path, any GUID (item / template ids).
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] FixedLeakPatterns =
    [
        ("corpus folder name", new Regex("sitecoremum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("absolute projects path", new Regex(@"C:[/\\]+Projects", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("Sitecore item path", new Regex("/sitecore/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        ("GUID", new Regex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static List<Ap6Task> Load(string path) => Parse(File.ReadAllText(path));

    public static List<Ap6Task> Parse(string jsonText) =>
        JsonSerializer.Deserialize<List<Ap6Task>>(jsonText, JsonOptions) ?? [];

    /// <summary>Every way the corpus fails its own contract; empty when it is scoreable. Never throws.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<Ap6Task> tasks)
    {
        var problems = new List<string>();

        if (tasks.Count != ExpectedTaskCount)
            problems.Add($"expected {ExpectedTaskCount} tasks, found {tasks.Count}");
        foreach (var cls in Classes)
        {
            var n = tasks.Count(t => t.Class == cls);
            if (n != TasksPerClass) problems.Add($"class {cls}: expected {TasksPerClass} tasks, found {n}");
        }
        foreach (var dup in tasks.Where(t => t.Id is not null).GroupBy(t => t.Id).Where(g => g.Count() > 1))
            problems.Add($"duplicate id '{dup.Key}'");

        for (var i = 0; i < tasks.Count; i++)
            ValidateTask(tasks[i], i, problems);

        return problems;
    }

    private static void ValidateTask(Ap6Task t, int index, List<string> problems)
    {
        var label = string.IsNullOrEmpty(t.Id) ? $"task #{index}" : t.Id;
        void Problem(string message) => problems.Add($"{label}: {message}");

        if (t.SchemaVersion != 1) Problem($"schemaVersion must be 1, was {t.SchemaVersion?.ToString() ?? "missing"}");
        if (string.IsNullOrWhiteSpace(t.Id)) Problem("missing id");
        if (string.IsNullOrWhiteSpace(t.Class)) Problem("missing class");
        else if (!Classes.Contains(t.Class)) Problem($"class must be A, B or C, was '{t.Class}'");
        if (string.IsNullOrWhiteSpace(t.Corpus)) Problem("missing corpus");
        if (string.IsNullOrWhiteSpace(t.Query)) Problem("missing query");
        if (string.IsNullOrWhiteSpace(t.Expectation)) Problem("missing expectation");

        if (t.Key is null) Problem("missing key");
        else
        {
            if (t.Key.Files is null || t.Key.Files.Count == 0) Problem("key.files is empty");
            if (t.Key.Symbols is null || t.Key.Symbols.Count == 0) Problem("key.symbols is empty");
        }

        if (t.KeySource is null) Problem("missing keySource");
        else
        {
            if (string.IsNullOrWhiteSpace(t.KeySource.Method)) Problem("missing keySource.method");
            if (string.IsNullOrWhiteSpace(t.KeySource.Ref)) Problem("missing keySource.ref");
        }

        // Policy columns are fixed per class — a task that disagrees with them is a different corpus.
        var (corpus, expectation, method) = t.Class switch
        {
            "A" => ("Brain", "rg", "merge-commit"),
            "B" => ("Brain", "graph", "merge-commit"),
            "C" => ("Corpus-A", "graph-only", "unicorn-yml"),
            _ => (null, null, null),
        };
        if (corpus is not null)
        {
            if (t.Corpus is not null && t.Corpus != corpus) Problem($"class {t.Class} must use corpus '{corpus}', was '{t.Corpus}'");
            if (t.Expectation is not null && t.Expectation != expectation) Problem($"class {t.Class} must expect '{expectation}', was '{t.Expectation}'");
            if (t.KeySource?.Method is not null && t.KeySource.Method != method) Problem($"class {t.Class} must use keySource.method '{method}', was '{t.KeySource.Method}'");
            if (t.Class is "A" or "B" && t.KeySource?.Ref is { } sha && !GitSha.IsMatch(sha)) Problem($"keySource.ref must be a 7-40 hex commit sha, was '{sha}'");
        }

        // Seed-in-key: the query must not hand an arm the answer. Where that is unavoidable the task says so.
        if (t.Query is not null && t.Key is not null && !t.SeedInKey)
        {
            foreach (var member in KeyMembers(t.Key))
            {
                if (ContainsWord(t.Query, member))
                    Problem($"query contains key member '{member}' but seedInKey is false");
            }
        }
    }

    /// <summary>The words of a key an arm could match on: symbol names, member names, file stems.</summary>
    private static IEnumerable<string> KeyMembers(Ap6Key key)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in key.Symbols ?? [])
        {
            if (seen.Add(s)) yield return s;
            foreach (var part in s.Split('.', StringSplitOptions.RemoveEmptyEntries))
                if (seen.Add(part)) yield return part;
        }
        foreach (var f in key.Files ?? [])
        {
            var name = f.Split('/')[^1];
            if (seen.Add(name)) yield return name;
            var stem = name.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (stem is not null && seen.Add(stem)) yield return stem;
        }
    }

    /// <summary>Whole-word containment; hyphens count as word characters so <c>Rendering-07</c> is one word.</summary>
    internal static bool ContainsWord(string text, string word) =>
        Regex.IsMatch(text, $@"(?<![A-Za-z0-9_-]){Regex.Escape(word)}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant);

    /// <summary>
    /// Everything in the corpus text that must not be there. <paramref name="denyWordHashes"/> are SHA-256
    /// hex digests of lower-cased customer words; every identifier-like word of the whole file (paths,
    /// symbols and query prose alike) is hashed and compared, so the words themselves never appear in the
    /// repository. The hash protects against an accident, not against a dictionary attack — that is enough
    /// for a list whose only job is to stop a paste.
    /// </summary>
    public static IReadOnlyList<string> FindLeaks(string jsonText, IReadOnlyCollection<string>? denyWordHashes = null)
    {
        var leaks = new List<string>();
        var lines = jsonText.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (name, pattern) in FixedLeakPatterns)
                if (pattern.IsMatch(lines[i])) leaks.Add($"line {i + 1}: {name}");

            if (denyWordHashes is { Count: > 0 })
            {
                foreach (Match m in Word.Matches(lines[i]))
                    if (denyWordHashes.Contains(Sha256Hex(m.Value.ToLowerInvariant())))
                        leaks.Add($"line {i + 1}: deny-listed word");
            }
        }

        // Structural rule: class C keys are tokens and nothing else — a real path or type name is a leak
        // even when no fixed pattern recognises it.
        List<Ap6Task> tasks;
        try { tasks = Parse(jsonText); }
        catch (JsonException) { return leaks; }
        foreach (var t in tasks.Where(t => t.Class == "C"))
        {
            var label = t.Id ?? "(no id)";
            foreach (var f in t.Key?.Files ?? [])
                if (!AnonymisedToken.IsMatch(f)) leaks.Add($"{label}: class C key file '{f}' is not an anonymised token");
            foreach (var s in t.Key?.Symbols ?? [])
                if (!AnonymisedToken.IsMatch(s)) leaks.Add($"{label}: class C key symbol '{s}' is not an anonymised token");
        }
        return leaks;
    }

    /// <summary>Lower-case hex SHA-256 of a UTF-8 string — the deny-list's word form.</summary>
    public static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
