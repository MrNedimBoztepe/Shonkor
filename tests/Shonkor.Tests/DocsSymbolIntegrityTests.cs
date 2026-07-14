// Licensed to Shonkor under the MIT License.

using System.Text.RegularExpressions;

namespace Shonkor.Tests;

/// <summary>
/// #159: documentation must not name a type that does not exist.
/// <para>
/// arc42 chapter 5 described <c>PluginLoader</c> — <b>a class that had been deleted</b> — as compiling C#
/// plugins at runtime, i.e. it advertised an RCE surface that no longer existed, and nobody noticed until a
/// human read the chapter against the code. It also named <c>StandardPlugins</c>, <c>McpServer</c>,
/// <c>Shonkor.Eval</c> and <c>Shonkor.Benchmarks</c>, none of which exist either.
/// </para>
/// <para>
/// Shonkor ships <c>verify_exists</c> — an anti-hallucination check that a named symbol is real. We point it
/// at LLM output but never at our own prose. This test is that tool, aimed inward.
/// </para>
/// <para>
/// <b>Scope, stated honestly:</b> this catches <i>dead symbols</i> only. It would NOT have caught the other
/// half of the same bug — the docs claiming <c>Security:EnablePlugins</c> defaults to OFF when it defaults to
/// ON. Wrong semantics about a symbol that exists still needs a human.
/// </para>
/// </summary>
public class DocsSymbolIntegrityTests
{
    /// <summary>
    /// Backtick-quoted names that could be a Shonkor code identifier: PascalCase with at least one lowercase
    /// letter. The lowercase requirement deliberately excludes ALL-CAPS tokens (`CALLS`, `IMPLEMENTS`,
    /// `EXTRACTED`) — those are edge/provenance names in the graph's vocabulary, not C# types.
    /// </summary>
    private static readonly Regex Candidate = new(@"`([A-Z][A-Za-z0-9]*[a-z][A-Za-z0-9]*)`", RegexOptions.Compiled);

    /// <summary>Type declarations — the primary ground truth.</summary>
    private static readonly Regex TypeDecl = new(
        @"\b(?:class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>
    /// Member declarations (properties, methods, fields, consts). Docs legitimately name members —
    /// `StartLine`, `ExecuteAsync`, `FindProjectByPath` — and those are real symbols, so the guard resolves
    /// them rather than forcing them into an allowlist, where a genuinely dead name could then hide.
    /// <para>
    /// The access modifier is <b>optional</b> on purpose: interface members carry none
    /// (<c>Task&lt;…&gt; GetDefinitionsByNamesAsync(…);</c>), and an earlier version of this guard reported
    /// that very method as dead when it is declared in <c>IGraphStore</c>.
    /// </para>
    /// </summary>
    private static readonly Regex MemberDecl = new(
        @"(?:(?:public|internal|protected|private|static|readonly|const|async|override|virtual|abstract|sealed|partial|required|extern|new)\s+)*[\w<>,\[\]\?\.]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:[({=;]|=>)",
        RegexOptions.Compiled);

    /// <summary>Enum members (`Fresh`, `Stale`) — declared without a type or a modifier.</summary>
    private static readonly Regex EnumBlock = new(
        @"\benum\s+[A-Za-z_]\w*[^{]*\{([^}]*)\}", RegexOptions.Compiled);

    /// <summary>
    /// Positional record parameters — e.g. `Changed` / `New` in
    /// <c>record DriftReport(IReadOnlyList&lt;string&gt; Changed, … New, … Deleted)</c>. They are public
    /// properties the docs name, but they are followed by <c>,</c> or <c>)</c>, so <see cref="MemberDecl"/>
    /// misses them.
    /// </summary>
    private static readonly Regex RecordParams = new(
        @"\brecord\s+[A-Za-z_]\w*\s*\(([^)]*)\)", RegexOptions.Compiled);

    private static HashSet<string> DeclaredSymbols()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(RepoPaths.File("src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            foreach (Match m in TypeDecl.Matches(text)) declared.Add(m.Groups[1].Value);
            foreach (Match m in MemberDecl.Matches(text)) declared.Add(m.Groups[1].Value);
            foreach (Match m in EnumBlock.Matches(text))
                foreach (var member in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    // Each chunk carries the member's XML doc comment ahead of it — the member is the last
                    // non-comment, non-attribute line. Splitting on ',' alone yields "/// <summary>…\n Fresh".
                    var name = member.Split('\n')
                        .Select(l => l.Trim())
                        .LastOrDefault(l => l.Length > 0 && !l.StartsWith("//") && !l.StartsWith('['))
                        ?.Split('=')[0].Trim();
                    if (!string.IsNullOrEmpty(name) && char.IsLetter(name[0])) declared.Add(name);
                }

            foreach (Match m in RecordParams.Matches(text))
                foreach (var param in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    // "IReadOnlyList<string> Changed" → "Changed". A generic's own comma splits the chunk,
                    // but each real parameter still ends its own chunk, so the names survive.
                    var name = param.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Split('=')[0].Trim();
                    if (!string.IsNullOrEmpty(name) && char.IsLetter(name[0])) declared.Add(name);
                }
        }
        return declared;
    }

    /// <summary>Type declarations only — used by the self-check, which must not be satisfied by a member.</summary>
    private static HashSet<string> DeclaredTypes()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(RepoPaths.File("src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in TypeDecl.Matches(File.ReadAllText(file)))
                declared.Add(m.Groups[1].Value);
        }
        return declared;
    }

    /// <summary>
    /// Identifiers the docs legitimately name that are NOT Shonkor types: BCL/third-party types, config keys,
    /// product/tool names, and — deliberately — removed types the docs mention *because* they were removed.
    /// </summary>
    private static HashSet<string> Allowlist() => new(
        File.ReadAllLines(RepoPaths.File("docs", "symbol-allowlist.txt"))
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0),
        StringComparer.Ordinal);

    [Fact]
    public void Docs_DoNotNameTypesThatDoNotExist()
    {
        var declared = DeclaredSymbols();
        var allowed = Allowlist();

        var docs = Directory.EnumerateFiles(RepoPaths.File("docs"), "*.md", SearchOption.AllDirectories)
            .Append(RepoPaths.File("README.md"))
            .ToList();

        var dead = new List<string>();
        foreach (var file in docs)
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in Candidate.Matches(lines[i]))
                {
                    var name = m.Groups[1].Value;
                    if (declared.Contains(name) || allowed.Contains(name)) continue;
                    dead.Add($"{relative}:{i + 1}  `{name}`");
                }
            }
        }

        Assert.True(dead.Count == 0,
            "Documentation names types that do not exist in src/ (this is how arc42 came to describe a removed " +
            "RCE surface as live). Either fix the doc, or — if the name is an external type or a deliberate " +
            "historical reference — add it to docs/symbol-allowlist.txt with a reason:\n  " +
            string.Join("\n  ", dead.Distinct()));
    }

    [Fact]
    public void Guard_ActuallyDetectsADeadSymbol()
    {
        // A guard that cannot fail is not a guard. PluginLoader is the exact class that was deleted while the
        // docs kept describing it — if this assert ever breaks, the detection logic has silently stopped working.
        var declared = DeclaredTypes();
        Assert.DoesNotContain("PluginLoader", declared);
        Assert.Contains("AssemblyPluginLoader", declared);        // ...and its real replacement IS found
        Assert.Contains("HybridRetrieval", declared);             // the building block #153 documented
    }
}
