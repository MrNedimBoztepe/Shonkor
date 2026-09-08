// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// <see cref="Ap6Preconditions"/> (#473): the rules that stop a run before the first prompt — revision
/// equalities, dirty trees, key files, and the class-C token/mapping consistency the #466 review added.
/// The world is handed in, so nothing here touches git or a database.
/// </summary>
public class Ap6PreconditionsTests
{
    private const string BrainHead = "de44654380032c1766d089d859c7e3c86ac79a74";
    private const string CorpusRev = "9d7f9ce";

    private static List<Ap6Task> Ab() => [Ap6Fixtures.Task("A-01", "A"), Ap6Fixtures.Task("B-01", "B")];

    private static List<Ap6Task> C(string token = "Controller-03", string? query = null, string? @ref = null) =>
        [Ap6Fixtures.Task("C1-01", "C", query ?? $"Which view does {token} render?", files: [token], symbols: [token], @ref: @ref)];

    [Fact]
    public void AHealthyWorld_HasNoProblems()
    {
        var problems = Ap6Preconditions.Check(Ab().Concat(C()).ToList(), Ap6Fixtures.Mapping(), Ap6Fixtures.HealthyWorld());
        Assert.Empty(problems);
    }

    [Fact]
    public void ClassAB_BrainGraphNotAtHead_IsAProblem()
    {
        var world = Ap6Fixtures.HealthyWorld() with { BrainIndexedRevision = "0000000000000000000000000000000000000000" };

        var problems = Ap6Preconditions.Check(Ab(), null, world);

        var p = Assert.Single(problems);
        Assert.Contains("Brain graph indexed", p);
        Assert.Contains("re-index", p);
    }

    [Fact]
    public void ClassAB_NoIndexedRevision_OrNoHead_IsAProblem()
    {
        Assert.Contains(Ap6Preconditions.Check(Ab(), null, Ap6Fixtures.HealthyWorld() with { BrainIndexedRevision = null }), p => p.Contains("no indexedRevision"));
        Assert.Contains(Ap6Preconditions.Check(Ab(), null, Ap6Fixtures.HealthyWorld() with { BrainHead = null }), p => p.Contains("HEAD not readable"));
    }

    [Fact]
    public void ClassAB_DirtyCsFiles_AreAProblem_NamingSome()
    {
        var world = Ap6Fixtures.HealthyWorld() with { BrainDirtyCsFiles = ["src/A.cs", "src/B.cs"] };

        var p = Assert.Single(Ap6Preconditions.Check(Ab(), null, world));

        Assert.Contains("2 .cs file(s)", p);
        Assert.Contains("src/A.cs", p);
    }

    [Fact]
    public void ClassAB_AKeyFileMissingAtHead_IsAProblem_NamingTheTask()
    {
        var world = Ap6Fixtures.HealthyWorld() with { BrainKeyFileExists = f => f != "src/X/Foo.cs" };

        var problems = Ap6Preconditions.Check(Ab(), null, world);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("A-01:") && p.Contains("src/X/Foo.cs"));
        Assert.Contains(problems, p => p.StartsWith("B-01:"));
    }

    [Fact]
    public void ClassAB_Only_IgnoresTheCorpusWorld()
    {
        var world = Ap6Fixtures.HealthyWorld() with { CorpusHead = null, CorpusIndexedRevision = null, CorpusDirtyFiles = ["a.yml"] };
        Assert.Empty(Ap6Preconditions.Check(Ab(), null, world));
    }

    [Fact]
    public void ClassC_WithoutAMapping_IsASingleProblem()
    {
        var p = Assert.Single(Ap6Preconditions.Check(C(), null, Ap6Fixtures.HealthyWorld()));
        Assert.Contains("mapping file not loaded", p);
    }

    [Fact]
    public void ClassC_KeySourceRefMustEqualTheMappingRevision()
    {
        var problems = Ap6Preconditions.Check(C(@ref: "abc1234"), Ap6Fixtures.Mapping(CorpusRev), Ap6Fixtures.HealthyWorld());

        var p = Assert.Single(problems);
        Assert.StartsWith("C1-01:", p);
        Assert.Contains("keySource.ref 'abc1234' != mapping.corpusRevision '9d7f9ce'", p);
    }

    [Fact]
    public void ClassC_MappingWithoutARevision_IsAProblem()
    {
        var mapping = Ap6Fixtures.Mapping() with { CorpusRevision = null };
        Assert.Contains(Ap6Preconditions.Check(C(), mapping, Ap6Fixtures.HealthyWorld()), p => p.Contains("corpusRevision missing"));
    }

    [Fact]
    public void ClassC_CheckoutAndGraphMustBeAtTheMappingRevision()
    {
        var movedCheckout = Ap6Fixtures.HealthyWorld() with { CorpusHead = "1111111" };
        var p1 = Assert.Single(Ap6Preconditions.Check(C(), Ap6Fixtures.Mapping(), movedCheckout));
        Assert.Contains("corpus checkout is at '1111111'", p1);

        var staleGraph = Ap6Fixtures.HealthyWorld() with { CorpusIndexedRevision = "2222222" };
        var p2 = Assert.Single(Ap6Preconditions.Check(C(), Ap6Fixtures.Mapping(), staleGraph));
        Assert.Contains("corpus graph indexed '2222222'", p2);

        var noGraph = Ap6Fixtures.HealthyWorld() with { CorpusIndexedRevision = null };
        Assert.Contains(Ap6Preconditions.Check(C(), Ap6Fixtures.Mapping(), noGraph), p => p.Contains("corpus graph: no indexedRevision"));
    }

    [Fact]
    public void ClassC_ModifiedTrackedFiles_AreAProblem()
    {
        var world = Ap6Fixtures.HealthyWorld() with { CorpusDirtyFiles = ["serialization/Renderings/X.yml"] };

        var p = Assert.Single(Ap6Preconditions.Check(C(), Ap6Fixtures.Mapping(), world));

        Assert.Contains("1 tracked", p);
        Assert.Contains("serialization/Renderings/X.yml", p);
    }

    [Fact]
    public void ClassC_ATokenWithoutAnEntry_IsAProblem_InKeyAndInQuery()
    {
        var problems = Ap6Preconditions.CheckTokens(C("Controller-77"), Ap6Fixtures.Mapping());

        Assert.Contains(problems, p => p == "C1-01: token 'Controller-77' has no mapping entry");
        // The token is in the key files, the key symbols and the query — reported per occurrence, so the count is 3.
        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void ClassC_ANameSharedWithinItsKind_IsAmbiguous()
    {
        var problems = Ap6Preconditions.CheckTokens(C("Controller-01"), Ap6Fixtures.Mapping());

        var p = Assert.Single(problems);
        Assert.Contains("Controller-01", p);
        Assert.Contains("Controller-02", p);
        Assert.Contains("ambiguous within its kind", p);
    }

    [Fact]
    public void ClassC_ASharedNameAmongKeyFiles_IsNotAmbiguous()
    {
        // Views are read back by path, and every Sitecore site has dozens of Index.cshtml — only key symbols need a unique name.
        var mapping = Ap6Fixtures.Mapping();
        mapping.Entries!["View-02"] = new Ap6MappingEntry("view", "src/Feature/Nav/Views/Nav/Index.cshtml", "Index.cshtml", null);
        var tasks = new List<Ap6Task> { Ap6Fixtures.Task("C1-01", "C", "Which view does Controller-03 render?", files: ["View-01"], symbols: ["Controller-03"]) };

        Assert.Empty(Ap6Preconditions.CheckTokens(tasks, mapping));
    }

    [Fact]
    public void ClassC_TheSameNameInAnotherKind_IsNotAmbiguous()
    {
        // A view and a model may share a name — the kind keeps them apart.
        var mapping = Ap6Fixtures.Mapping();
        mapping.Entries!["Model-02"] = new Ap6MappingEntry("model", "src/Feature/Hero/Models/Index.cs", "Index.cshtml", null);

        Assert.Empty(Ap6Preconditions.CheckTokens(C("View-01"), mapping));
    }

    [Fact]
    public void ClassC_AnEntryWithoutAName_IsAProblem()
    {
        var mapping = Ap6Fixtures.Mapping();
        mapping.Entries!["Controller-03"] = new Ap6MappingEntry("controller", "src/Feature/Nav/NavController.cs", null, null);

        Assert.Contains(Ap6Preconditions.CheckTokens(C("Controller-03"), mapping), p => p.Contains("has no name"));
    }

    [Fact]
    public void ClassC_TokensInTheQueryAreCheckedToo()
    {
        var tasks = C("Controller-03", query: "How does Controller-03 reach Rendering-42?");

        Assert.Contains(Ap6Preconditions.CheckTokens(tasks, Ap6Fixtures.Mapping()), p => p.Contains("'Rendering-42' has no mapping entry"));
    }
}
