// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// <see cref="Ap6Anonymiser"/> (#473) over the invented mapping of <see cref="Ap6Fixtures.Mapping"/>:
/// tokens → names for the prompt, names/paths → tokens for scoring, redaction of recorded arguments.
/// No customer data anywhere in this file — the mapping is fictional.
/// </summary>
public class Ap6AnonymiserTests
{
    private static readonly Ap6Mapping Mapping = Ap6Fixtures.Mapping();

    /// <summary>
    /// The string <b>values</b> of a JSON document, keys excluded — what the redaction judges and what
    /// <see cref="Ap6Corpus.FindResultsLeaks"/> checks. A key is the tool's schema, written by us; asserting
    /// over the raw text would judge "query" and "command" as if a corpus had named them.
    /// </summary>
    private static IEnumerable<string> StringValues(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return Walk(doc.RootElement).ToList();

        static IEnumerable<string> Walk(System.Text.Json.JsonElement e)
        {
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String: yield return e.GetString()!; break;
                case System.Text.Json.JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject()) foreach (var s in Walk(p.Value)) yield return s;
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) foreach (var s in Walk(item)) yield return s;
                    break;
            }
        }
    }

    // ---------- query → prompt ----------

    [Fact]
    public void ResolveQuery_ReplacesEveryTokenWithItsName()
    {
        var q = "Which controller renders Rendering-01 through View-01, and which model (Model-01) does it use?";

        var resolved = Ap6Anonymiser.ResolveQuery(q, Mapping);

        Assert.Equal("Which controller renders Hero Banner through Index.cshtml, and which model (HeroModel) does it use?", resolved);
    }

    [Fact]
    public void ResolveQuery_ThrowsOnATokenWithoutAnEntry()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Ap6Anonymiser.ResolveQuery("Where is Rendering-99 used?", Mapping));
        Assert.Contains("Rendering-99", ex.Message);
    }

    [Fact]
    public void ResolveQuery_LeavesNonTokensAlone()
    {
        Assert.Equal("Model-View-Controller is a pattern; Page-1 is not a token, nor is XController-01.",
            Ap6Anonymiser.ResolveQuery("Model-View-Controller is a pattern; Page-1 is not a token, nor is XController-01.", Mapping));
    }

    [Fact]
    public void TokensIn_ListsDistinctTokensInOrder()
    {
        Assert.Equal(["View-01", "Controller-02", "Model-01"],
            Ap6Anonymiser.TokensIn("View-01 → Controller-02 → View-01 → Model-01"));
        Assert.Empty(Ap6Anonymiser.TokensIn("nothing here"));
    }

    // ---------- answer → tokens ----------

    [Fact]
    public void ToTokens_MapsFilesByPath_AndCountsUnmappedOnes()
    {
        var t = Ap6Anonymiser.ToTokens(
            ["src/Feature/Hero/HeroController.cs", "src/Feature/Hero/Views/Hero/Index.cshtml", "src/Vendor/Thing.cs"],
            [], Mapping);

        Assert.Equal(["Controller-01", "View-01"], t.Files);
        Assert.Equal(1, t.UnmappedFiles);
        Assert.Empty(t.Symbols);
    }

    [Fact]
    public void ToTokens_ComparesPathsByThePlatformRule()
    {
        var t = Ap6Anonymiser.ToTokens(["SRC/feature/hero/herocontroller.CS"], [], Mapping);

        if (OperatingSystem.IsWindows()) Assert.Equal(["Controller-01"], t.Files);
        else Assert.Equal(1, t.UnmappedFiles);
    }

    [Fact]
    public void ToTokens_MapsAUniqueTypeName_ByLastOrSecondLastSegment()
    {
        var t = Ap6Anonymiser.ToTokens([], ["NavController", "Acme.Feature.Hero.Models.HeroModel", "HeroModel.Render"], Mapping);

        Assert.Equal(["Controller-03", "Model-01"], t.Symbols);
        Assert.Equal(0, t.UnmappedSymbols);
        Assert.Equal(0, t.AmbiguousSymbols);
    }

    [Fact]
    public void ToTokens_ATemplateSharingAModelsName_DoesNotMakeTheModelAmbiguous()
    {
        // Precondition and scorer agree on what a name can mean (#505 follow-up): only controller/model entries
        // are candidates for an answer symbol, so a same-named template item is not a second hit.
        var mapping = Ap6Fixtures.Mapping();
        mapping.Entries!["Template-01"] = new Ap6MappingEntry("template", "serialization/Templates/HeroModel.yml", "HeroModel", null);

        var t = Ap6Anonymiser.ToTokens([], ["HeroModel"], mapping);

        Assert.Equal(["Model-01"], t.Symbols);
        Assert.Equal(0, t.AmbiguousSymbols);
        Assert.Equal(0, t.UnmappedSymbols);
    }

    [Fact]
    public void ToTokens_ASharedSimpleName_IsAmbiguousWithoutAFile_AndResolvedByOne()
    {
        var alone = Ap6Anonymiser.ToTokens([], ["HeroController"], Mapping);
        Assert.Empty(alone.Symbols);
        Assert.Equal(1, alone.AmbiguousSymbols);

        var withFile = Ap6Anonymiser.ToTokens(["src/Feature/Legacy/HeroController.cs"], ["HeroController"], Mapping);
        Assert.Equal(["Controller-02"], withFile.Symbols);
        Assert.Equal(0, withFile.AmbiguousSymbols);

        // Both candidate files in the answer: still nothing to decide by.
        var both = Ap6Anonymiser.ToTokens(["src/Feature/Legacy/HeroController.cs", "src/Feature/Hero/HeroController.cs"], ["HeroController"], Mapping);
        Assert.Empty(both.Symbols);
        Assert.Equal(1, both.AmbiguousSymbols);
    }

    [Fact]
    public void ToTokens_CountsUnknownSymbols_AndDeduplicates()
    {
        var t = Ap6Anonymiser.ToTokens([], ["Nope", "NavController", "Acme.Feature.Nav.NavController"], Mapping);

        Assert.Equal(["Controller-03"], t.Symbols);
        Assert.Equal(1, t.UnmappedSymbols);
    }

    // ---------- redaction ----------

    [Fact]
    public void RedactArgument_ReplacesPathsNamesRootAndDenyWords()
    {
        var input = "{\"query\":\"HeroController in C:\\\\Corpora\\\\Acme\\\\src/Feature/Hero/HeroController.cs for AcmeSite by acme\"}";

        var redacted = Ap6Anonymiser.RedactArgument(input, Mapping);

        Assert.DoesNotContain("HeroController", redacted);
        Assert.DoesNotContain("Corpora", redacted);
        Assert.DoesNotContain("acme", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<corpus>", redacted);
        Assert.Contains("Controller-01", redacted);
        Assert.Contains("<redacted>", redacted);
    }

    [Fact]
    public void RedactArgument_PrefersTheLongestLiteral()
    {
        // The full name contains the simple name; the full name must win, so the token is not split.
        var redacted = Ap6Anonymiser.RedactArgument("Acme.Feature.Nav.NavController and NavController", Mapping);

        Assert.Equal("Controller-03 and Controller-03", redacted);
    }

    [Fact]
    public void RelativiseArgument_MakesPathsUnderTheRootRelative_AndRedactsTheRest()
    {
        // The rg arm's Read input as it sits in stream.jsonl: JSON-escaped backslashes, drive letter in any case.
        const string input = @"{""file_path"":""C:\\Projects\\Brain\\src\\X.cs"",""other"":""c:/projects/brain/docs/adr/1.md"",""stray"":""C:\\Projects\\Other\\y.cs""}";

        var result = Ap6Anonymiser.RelativiseArgument(input, @"C:\Projects\Brain");

        Assert.Contains(@"""file_path"":""src/X.cs""", result);
        Assert.Contains(@"""other"":""docs/adr/1.md""", result);
        Assert.Contains(@"""stray"":""<abs-path>""", result);
        Assert.Empty(Ap6Corpus.FindLeaks(result, null));
    }

    [Fact]
    public void RelativiseArgument_WithoutARoot_StillRedactsProjectPaths()
    {
        Assert.Equal(@"{""p"":""<abs-path>""}", Ap6Anonymiser.RelativiseArgument(@"{""p"":""C:\\Projects\\Brain\\src\\X.cs""}", string.Empty));
    }

    [Fact]
    public void RedactPaths_ReplacesItemPathsGuidsAndProjectPaths()
    {
        var text = "/sitecore/content/Site/Home {1A2B3C4D-1111-2222-3333-444455556666} C:\\Projects\\Some-Customer\\src\\A.cs";

        var redacted = Ap6Anonymiser.RedactPaths(text);

        Assert.Equal("<item-path> <guid> <abs-path>", redacted);
    }

    // ---------- #511: redact by default ----------

    /// <summary>
    /// The case #511 was opened for. "Kestrelbrook" is in no mapping entry, is no deny word, is no GUID, is
    /// under no corpus root and is not a Sitecore item path — every subtractive rule the old redaction had
    /// says "nothing to do here", and the first smoke run wrote three such names into <c>results-C.json</c>.
    /// Default deny means it does not need to be recognised to be removed.
    /// </summary>
    [Fact]
    public void RedactToolInput_RemovesAnIdentifierThatIsInNeitherTheMappingNorTheDenyWords()
    {
        const string identifier = "Kestrelbrook";
        var input = $$"""{"query":"which controller renders the {{identifier}} teaser"}""";

        var (json, redacted) = Ap6Anonymiser.RedactToolInput(input, "mcp__shonkor__locate", Mapping);

        Assert.DoesNotContain(identifier, json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, redacted);
        Assert.All(StringValues(json), v => Assert.Empty(Ap6Anonymiser.DisallowedWords(v)));
    }

    /// <summary>Every free-text field of every tool the arms actually call goes through it — not a list of the ones we thought of.</summary>
    [Theory]
    [InlineData("mcp__shonkor__locate", """{"query":"Kestrelbrook teaser"}""")]
    [InlineData("mcp__shonkor__search_graph", """{"query":"Kestrelbrook"}""")]
    [InlineData("mcp__shonkor__generate_capsule", """{"query":"the Kestrelbrook rendering"}""")]
    [InlineData("mcp__shonkor__get_source", """{"symbol":"KestrelbrookController"}""")]
    [InlineData("mcp__shonkor__find_usages", """{"symbol":"Kestrelbrook"}""")]
    [InlineData("mcp__shonkor__outline", """{"path":"src/Feature/Kestrelbrook/Thing.cs"}""")]
    [InlineData("mcp__shonkor__get_subgraph", """{"seeds":["@/src/Feature/Kestrelbrook/Thing.cs::Kestrelbrook"],"hops":2}""")]
    [InlineData("Bash", """{"command":"rg -rn Kestrelbrook src","description":"find Kestrelbrook"}""")]
    [InlineData("StructuredOutput", """{"schemaVersion":1,"files":["src/Feature/Kestrelbrook/Thing.cs"],"symbols":["Kestrelbrook"]}""")]
    public void RedactToolInput_CoversEveryFreeTextField(string tool, string input)
    {
        var (json, redacted) = Ap6Anonymiser.RedactToolInput(input, tool, Mapping);

        Assert.DoesNotContain("Kestrelbrook", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(redacted > 0);
        Assert.All(StringValues(json), v => Assert.Empty(Ap6Anonymiser.DisallowedWords(v)));
    }

    /// <summary>
    /// A relative corpus path carries the customer's folder names just as an absolute one does, and
    /// <see cref="Ap6Anonymiser.RedactPaths"/> only ever caught the absolute form — so
    /// <c>src/Feature/Kestrelbrook/Thing.cs</c> used to walk straight through. Structure survives, names do not.
    /// </summary>
    [Fact]
    public void RedactToolInput_RedactsRelativeCorpusPaths_KeepingTheirShape()
    {
        var (json, _) = Ap6Anonymiser.RedactToolInput("""{"path":"src/Feature/Kestrelbrook/Thing.cs"}""", "mcp__shonkor__outline", Mapping);

        Assert.Equal("""{"path":"src/<redacted>/<redacted>/<redacted>.cs"}""", json);
    }

    /// <summary>Numbers, booleans, nulls and the schema keys around them name nobody, so they are not touched — a row with every value blanked would be unreadable for nothing.</summary>
    [Fact]
    public void RedactToolInput_LeavesNonStringsAndKeysAlone()
    {
        var (json, redacted) = Ap6Anonymiser.RedactToolInput("""{"hops":3,"verbose":true,"note":null,"limit":25}""", "mcp__shonkor__get_subgraph", Mapping);

        Assert.Equal("""{"hops":3,"verbose":true,"note":null,"limit":25}""", json);
        Assert.Equal(0, redacted);
    }

    /// <summary>What the mapping does know still becomes its token: redaction by default does not throw away the reading the tokens exist for.</summary>
    [Fact]
    public void RedactToolInput_KeepsMappedEntriesAsTokens_AndPlaceholders()
    {
        var input = """{"seeds":["@/src/Feature/Hero/HeroController.cs","{1A2B3C4D-1111-2222-3333-444455556666}"],"query":"NavController"}""";

        var (json, _) = Ap6Anonymiser.RedactToolInput(input, "mcp__shonkor__get_subgraph", Mapping);

        Assert.Contains("Controller-01", json, StringComparison.Ordinal);
        Assert.Contains("Controller-03", json, StringComparison.Ordinal);
        Assert.Contains("guid", json, StringComparison.Ordinal);
        Assert.All(StringValues(json), v => Assert.Empty(Ap6Anonymiser.DisallowedWords(v)));
    }

    /// <summary>Input that is not JSON is the case a subtractive redaction would have had to guess at; it is replaced whole.</summary>
    [Fact]
    public void RedactToolInput_ReplacesUnparseableInputWholesale()
    {
        var (json, redacted) = Ap6Anonymiser.RedactToolInput("{not json at all", "Bash", Mapping);

        Assert.Equal("\"<redacted>\"", json);
        Assert.Equal(1, redacted);
    }

    /// <summary>
    /// <see cref="Ap6Anonymiser.RedactArgument"/> is also applied to Brain's own prose (the plugin verify
    /// output, the mapping path in an error message), where exact equality is pinned. #511 adds a layer on
    /// top of it and must not have changed it.
    /// </summary>
    [Fact]
    public void RedactArgument_IsUnchangedByTheNewLayer()
    {
        Assert.Equal("Controller-03 and Controller-03", Ap6Anonymiser.RedactArgument("Acme.Feature.Nav.NavController and NavController", Mapping));
    }

    // ---------- the allow-list, and the invariant it is kept under ----------

    [Fact]
    public void AllowedWords_AreSortedWithinTheirGroups_AndDistinct()
    {
        Assert.Equal(Ap6Anonymiser.AllowedWords.Length, Ap6Anonymiser.AllowedWords.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(Ap6Anonymiser.AllowedWords, w => Assert.Matches("^[A-Za-z][A-Za-z0-9]*$", w));
    }

    /// <summary>
    /// The list may never contain a customer word. The deny words are the ones we know of, and they are held
    /// as hashes so they are never written down here — the same digests <see cref="Ap6CorpusTests"/> embeds.
    /// </summary>
    [Fact]
    public void NoAllowedWord_HashesToADenyWord()
    {
        var deny = Ap6CorpusTests.DenyWordHashes;

        Assert.All(Ap6Anonymiser.AllowedWords, w => Assert.DoesNotContain(Ap6Corpus.Sha256Hex(w.ToLowerInvariant()), deny));
    }

    /// <summary>
    /// Nor may it contain a class-C key: a key is an anonymised token, and a token that is also allow-listed
    /// vocabulary would make the redaction unable to tell the two apart.
    /// </summary>
    [Fact]
    public void NoAllowedWord_IsAClassCKey()
    {
        var keys = Ap6Corpus.Load(RepoPaths.File("bench", "golden", "ap6", "tasks.json"))
            .Where(t => t.Class == "C")
            .SelectMany(t => (t.Key?.Files ?? []).Concat(t.Key?.Symbols ?? []))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(keys);
        Assert.All(Ap6Anonymiser.AllowedWords, w => Assert.DoesNotContain(w, keys));
    }

    /// <summary>
    /// The answer schema is the one vocabulary a value may legitimately echo, and it is a file that can
    /// change. Rather than reading it at run time, the list is checked against it here: change the schema
    /// and this fails, which is where a missing word should surface.
    /// </summary>
    [Fact]
    public void AllowedWords_CoverTheAnswerSchema()
    {
        var schema = File.ReadAllText(RepoPaths.File("bench", "golden", "ap6", "answer-schema.json"));
        var words = System.Text.RegularExpressions.Regex.Matches(schema, "[A-Za-z_][A-Za-z0-9_]*").Select(m => m.Value).Distinct(StringComparer.Ordinal);

        Assert.All(words, w => Assert.Empty(Ap6Anonymiser.DisallowedWords(w)));
    }

    /// <summary>Tokens and placeholders are recognised whole, not read as the words they are spelled with.</summary>
    [Theory]
    [InlineData("Controller-003")]
    [InlineData("Rendering-01")]
    [InlineData("<corpus>")]
    [InlineData("<guid>")]
    [InlineData("<item-path>")]
    [InlineData("<abs-path>")]
    [InlineData("<redacted>")]
    public void TokensAndPlaceholders_AreAllowedWhole(string text) => Assert.True(Ap6Anonymiser.IsAllowedString(text));
}
