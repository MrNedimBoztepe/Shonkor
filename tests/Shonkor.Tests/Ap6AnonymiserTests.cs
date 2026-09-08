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
    public void RedactPaths_ReplacesItemPathsGuidsAndProjectPaths()
    {
        var text = "/sitecore/content/Site/Home {1A2B3C4D-1111-2222-3333-444455556666} C:\\Projects\\Some-Customer\\src\\A.cs";

        var redacted = Ap6Anonymiser.RedactPaths(text);

        Assert.Equal("<item-path> <guid> <abs-path>", redacted);
    }
}
