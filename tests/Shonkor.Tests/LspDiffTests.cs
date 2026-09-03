// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;
using Shonkor.Core.Models;

namespace Shonkor.Tests;

/// <summary>
/// The pure half of the LSP diff (#467): seed selection, anchor search, location→node mapping, verdicts
/// and gap buckets — everything that decides WHICH graph node a server answer stands for, pinned without a
/// language server. The identity rules under test are the ticket's: line containment only (never offsets),
/// paths through <c>FilePaths.Comparer</c>, and "two candidates on one line → unmappable, never guessed".
/// </summary>
public class LspDiffTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "shonkor-lsp-diff-tests");

    private static string In(string file) => Path.Combine(Root, file);

    private static GraphNode Node(string id, string type, string name, string file, int start, int end) =>
        new() { Id = id, Type = type, Name = name, FilePath = In(file), StartLine = start, EndLine = end };

    private static GraphEdge Edge(string s, string t, string rel, ProvenanceReason reason = ProvenanceReason.SemanticSymbol) =>
        new() { SourceId = s, TargetId = t, Relationship = rel, Reason = reason };

    private static DocumentSymbol Symbol(string name, int rangeStart, int rangeEnd, int selectionLine) => new()
    {
        Name = name,
        Range = new LspRange { Start = new LspPosition { Line = rangeStart }, End = new LspPosition { Line = rangeEnd } },
        SelectionRange = new LspRange { Start = new LspPosition { Line = selectionLine, Character = 4 }, End = new LspPosition { Line = selectionLine, Character = 8 } }
    };

    /// <summary>A.cs: class A (1-30) with methods A.M (5-10) and A.N (12-20); B.cs: class B with a nested C and a ctor.</summary>
    private static List<GraphNode> Corpus() =>
    [
        Node("A.cs::A", "Class", "A", "A.cs", 1, 30),
        Node("A.cs::A::M", "Method", "M", "A.cs", 5, 10),
        Node("A.cs::A::N", "Method", "N", "A.cs", 12, 20),
        Node("A.cs::A::P", "Property", "P", "A.cs", 22, 22),
        Node("A.cs::A::Q", "Property", "Q", "A.cs", 22, 22),
        Node("B.cs::B", "Class", "B", "B.cs", 1, 40),
        Node("B.cs::B::ctor", "Constructor", "B", "B.cs", 3, 6),
        Node("B.cs::B::C", "Class", "C", "B.cs", 10, 30),
        Node("B.cs::B::C::Run", "Method", "Run", "B.cs", 12, 18),
        Node("B.cs::B::Size", "Property", "Size", "B.cs", 35, 35)
    ];

    private static Dictionary<string, List<GraphNode>> ByFile() => LspDiff.GroupByFile(Corpus(), Root);

    private static string UriOf(string file) => new Uri(In(file)).AbsoluteUri;

    // ---- SelectSeeds --------------------------------------------------------------------------------------------

    [Fact]
    public void SelectSeeds_RanksFilesByDistinctPairs_AndBreaksTiesOrdinallyByPath()
    {
        var nodes = Corpus().ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = new[]
        {
            Edge("A.cs::A::M", "B.cs::B", "REFERENCES_TYPE"),
            Edge("A.cs::A::M", "B.cs::B", "REFERENCES_TYPE"), // duplicate — one pair
            Edge("A.cs::A::N", "B.cs::B::C::Run", "CALLS"),
            Edge("B.cs::B::C::Run", "A.cs::A::M", "CALLS"),
            Edge("B.cs::B::C::Run", "A.cs::A", "INSTANTIATES"),
            Edge("B.cs::B::Size", "A.cs::A::P", "OVERRIDES", ProvenanceReason.SyntacticHeritage) // not semantic — ignored
        };

        var selection = LspDiff.SelectSeeds(nodes, edges, Root, top: 5);

        Assert.Equal(5, selection.SemanticEdges);
        Assert.Equal(0, selection.UnspecifiedEdges);
        Assert.Equal([In("A.cs"), In("B.cs")], selection.Seeds.Select(s => s.File));
        Assert.Equal([2, 2], selection.Seeds.Select(s => s.Pairs));
    }

    [Fact]
    public void SelectSeeds_CountsUnspecifiedEdges_SoAPreAp1GraphIsRefused()
    {
        var nodes = Corpus().ToDictionary(n => n.Id, StringComparer.Ordinal);
        var selection = LspDiff.SelectSeeds(nodes,
            [Edge("A.cs::A::M", "B.cs::B", "REFERENCES_TYPE", ProvenanceReason.Unspecified), Edge("A.cs::A::N", "B.cs::B", "REFERENCES_TYPE")],
            Root);

        Assert.Equal(1, selection.UnspecifiedEdges);
        Assert.Equal(1, selection.SemanticEdges);
    }

    [Fact]
    public void SelectSeeds_TakesOnlyTheTopN()
    {
        var nodes = Corpus().ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = new[]
        {
            Edge("A.cs::A::M", "B.cs::B", "REFERENCES_TYPE"),
            Edge("A.cs::A::N", "B.cs::B", "REFERENCES_TYPE"),
            Edge("B.cs::B::C::Run", "A.cs::A::M", "CALLS")
        };

        var one = Assert.Single(LspDiff.SelectSeeds(nodes, edges, Root, top: 1).Seeds);
        Assert.Equal(In("A.cs"), one.File);
    }

    // ---- FindAnchor ---------------------------------------------------------------------------------------------

    [Fact]
    public void FindAnchor_MatchesNameAndContainment_AndReturnsTheSelectionStart()
    {
        var symbols = new[] { Symbol("A", 0, 29, 0), Symbol("M(int)", 4, 9, 4), Symbol("N()", 11, 19, 11) };

        var anchor = LspDiff.FindAnchor(Corpus().Single(n => n.Id == "A.cs::A::N"), symbols);

        Assert.True(anchor.Found);
        Assert.Equal(11, anchor.Position!.Line);
        Assert.Equal(4, anchor.Position.Character);
    }

    [Fact]
    public void FindAnchor_ConstructorNodeCarriesTheTypeName_AndMatchesTheSymbolNamedAfterIt()
    {
        // Roslyn names the constructor symbol `B(int)`; RoslynAstParser names the node after the identifier: `B`.
        var symbols = new[] { Symbol("B", 0, 39, 0), Symbol("B(int)", 2, 5, 2), Symbol("C", 9, 29, 9) };

        var anchor = LspDiff.FindAnchor(Corpus().Single(n => n.Id == "B.cs::B::ctor"), symbols);

        Assert.True(anchor.Found);
        Assert.Equal(2, anchor.Position!.Line);
    }

    [Fact]
    public void FindAnchor_TwoSymbolsOnOneLine_IsUnmappable_NeverAGuess()
    {
        var symbols = new[] { Symbol("P", 21, 21, 21), Symbol("P", 21, 21, 21) };

        var anchor = LspDiff.FindAnchor(Corpus().Single(n => n.Id == "A.cs::A::P"), symbols);

        Assert.False(anchor.Found);
        Assert.Equal("ambiguous line", anchor.Failure);
    }

    [Fact]
    public void FindAnchor_NoSymbolWithThatNameInsideTheSpan_IsUnmappable()
    {
        var symbols = new[] { Symbol("A", 0, 29, 0), Symbol("M(int)", 4, 9, 4) };

        var anchor = LspDiff.FindAnchor(Corpus().Single(n => n.Id == "A.cs::A::N"), symbols);

        Assert.False(anchor.Found);
        Assert.Equal("no symbol", anchor.Failure);
    }

    // ---- MapToNode ----------------------------------------------------------------------------------------------

    [Fact]
    public void MapToNode_ReferencesType_PicksTheInnermostEnclosingType_LikeTheLinker()
    {
        // Line 15 (1-based) is inside nested C inside B; SemanticCsharpLinker attributes to the NEAREST TypeDeclaration.
        var mapped = LspDiff.MapToNode(UriOf("B.cs"), 14, "REFERENCES_TYPE", ByFile());

        Assert.True(mapped.IsOk);
        Assert.Equal("B.cs::B::C", mapped.NodeId);
    }

    [Fact]
    public void MapToNode_Calls_PicksTheMethod_AndMarksAConstructorBodyAsLinkerScope()
    {
        var byFile = ByFile();

        var inMethod = LspDiff.MapToNode(UriOf("A.cs"), 7, "CALLS", byFile);
        var inCtor = LspDiff.MapToNode(UriOf("B.cs"), 4, "CALLS", byFile);
        var inTypeOnly = LspDiff.MapToNode(UriOf("A.cs"), 27, "CALLS", byFile);

        Assert.Equal("A.cs::A::M", inMethod.NodeId);
        Assert.Equal("linker-scope", inCtor.Status);
        Assert.Equal("B.cs::B::ctor", inCtor.NodeId);
        Assert.Equal("no-node", inTypeOnly.Status);
    }

    [Fact]
    public void MapToNode_Instantiates_FallsBackFromMethodToType()
    {
        var byFile = ByFile();

        Assert.Equal("A.cs::A::M", LspDiff.MapToNode(UriOf("A.cs"), 7, "INSTANTIATES", byFile).NodeId);
        Assert.Equal("B.cs::B", LspDiff.MapToNode(UriOf("B.cs"), 4, "INSTANTIATES", byFile).NodeId); // ctor body → enclosing type
    }

    [Fact]
    public void MapToNode_Overrides_PicksTheInnermostMember_IncludingProperties()
    {
        Assert.Equal("B.cs::B::Size", LspDiff.MapToNode(UriOf("B.cs"), 34, "OVERRIDES", ByFile()).NodeId);
        Assert.Equal("B.cs::B::C::Run", LspDiff.MapToNode(UriOf("B.cs"), 13, "IMPLEMENTS_MEMBER", ByFile()).NodeId);
    }

    [Fact]
    public void MapToNode_TwoMembersOnOneLine_IsAmbiguous_NeverAGuess()
    {
        var mapped = LspDiff.MapToNode(UriOf("A.cs"), 21, "OVERRIDES", ByFile());

        Assert.Equal("ambiguous", mapped.Status);
        Assert.Null(mapped.NodeId);
    }

    [Fact]
    public void MapToNode_FileNotInGraph_IsReportedAsSuch()
    {
        Assert.Equal("file-not-in-graph", LspDiff.MapToNode(UriOf("Z.cs"), 0, "CALLS", ByFile()).Status);
    }

    [Fact]
    public void MapToNode_ComparesTheUriPath_ByThePlatformsRules()
    {
        // `file:///…/a.CS` against a node at `…/A.cs`: the same file on Windows, a different one on Linux —
        // exactly what FilePaths.Comparer encodes, and why the mapping must go through it.
        var mapped = LspDiff.MapToNode(new Uri(In("a.CS")).AbsoluteUri, 7, "CALLS", ByFile());

        if (OperatingSystem.IsWindows()) Assert.Equal("A.cs::A::M", mapped.NodeId);
        else Assert.Equal("file-not-in-graph", mapped.Status);
    }

    [Fact]
    public void FileOf_DecodesAFileUri_ToTheLocalPath()
    {
        Assert.Equal(Path.GetFullPath(In("A.cs")), LspDiff.FileOf(UriOf("A.cs")));
        Assert.Equal(Path.GetFullPath(In("A.cs")), LspDiff.FileOf(In("A.cs")));
    }

    // ---- Classify -----------------------------------------------------------------------------------------------

    [Fact]
    public void Classify_ThreeOutcomes_NeverFolded()
    {
        var pair = new EdgePair("A.cs::A::M", "B.cs::B::C::Run", "CALLS");
        var source = Corpus().Single(n => n.Id == pair.SourceId);
        var lsp = new HashSet<string>(StringComparer.Ordinal) { "A.cs::A::M" };

        Assert.Equal(LspOutcome.Confirmed, LspDiff.Classify(pair, anchorFound: true, source, lsp));
        Assert.Equal(LspOutcome.Contradicted, LspDiff.Classify(pair, anchorFound: true, source, new HashSet<string>(StringComparer.Ordinal) { "A.cs::A::N" }));
        Assert.Equal(LspOutcome.Unmappable, LspDiff.Classify(pair, anchorFound: false, source, lsp));
        Assert.Equal(LspOutcome.Unmappable, LspDiff.Classify(pair, anchorFound: true, source: null, lsp));
    }

    // ---- Bucket -------------------------------------------------------------------------------------------------

    [Fact]
    public void Bucket_NamesTheCause_ForEveryKindOfServerOnlyAnswer()
    {
        var ok = MappedSource.Ok("A.cs::A::M");

        Assert.Equal(GapBucket.Generated, LspDiff.Bucket(new Uri(Path.Combine(Root, "obj", "Debug", "X.g.cs")).AbsoluteUri, Root, new MappedSource(null, "file-not-in-graph")));
        Assert.Equal(GapBucket.Generated, LspDiff.Bucket(new Uri(Path.Combine(Root, "Form1.Designer.cs")).AbsoluteUri, Root, ok));
        Assert.Equal(GapBucket.External, LspDiff.Bucket(new Uri(Path.Combine(Path.GetTempPath(), "elsewhere", "X.cs")).AbsoluteUri, Root, new MappedSource(null, "file-not-in-graph")));
        Assert.Equal(GapBucket.Unmappable, LspDiff.Bucket(UriOf("A.cs"), Root, new MappedSource(null, "no-node")));
        Assert.Equal(GapBucket.Unmappable, LspDiff.Bucket(UriOf("A.cs"), Root, new MappedSource(null, "ambiguous")));
        Assert.Equal(GapBucket.LinkerScope, LspDiff.Bucket(UriOf("B.cs"), Root, new MappedSource("B.cs::B::ctor", "linker-scope")));
        Assert.Equal(GapBucket.Other, LspDiff.Bucket(UriOf("A.cs"), Root, ok));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Foo", "Foo")]
    [InlineData("Foo(int, string)", "Foo")]
    [InlineData("Foo<T>", "Foo")]
    [InlineData("IBar.Baz()", "Baz")]
    public void BareName_StripsParameterListTypeArgumentsAndExplicitInterfacePrefix(string symbol, string expected) =>
        Assert.Equal(expected, LspDiff.BareName(symbol));

    [Fact]
    public void Percentile_IsNearestRank_LikeTheSearchLatencyReport()
    {
        double[] xs = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.Equal(5, LspDiff.Percentile(xs, 50));
        Assert.Equal(10, LspDiff.Percentile(xs, 95));
        Assert.Equal(0, LspDiff.Percentile([], 50));
    }
}
