// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;
using Shonkor.Core.Models;

namespace Shonkor.Tests;

/// <summary>
/// #110: the retrieval benchmark must ignore its OWN ground-truth fixtures (files under bench/golden/),
/// which list every query string verbatim and would otherwise be guaranteed false #1 hits — the
/// self-contamination that made the code-intent numbers look like a doc-vs-code regression.
/// </summary>
public class GoldenFixtureGuardTests
{
    private static GraphNode NodeAt(string path) => new() { Id = path, Name = "x", Type = "File", FilePath = path };

    [Theory]
    [InlineData("C:\\Projects\\Brain\\bench\\golden\\agent-queries.json")]
    [InlineData("/home/ci/Brain/bench/golden/doc-sections.json")]
    [InlineData("bench/golden/negatives.json")]
    public void GoldenFixtureFiles_AreRecognized_OnBothSeparators(string path)
    {
        Assert.True(RetrievalBenchmark.IsGoldenFixture(NodeAt(path)));
    }

    [Theory]
    [InlineData("C:\\Projects\\Brain\\src\\Shonkor.Infrastructure\\Services\\GraphIndexScanner.cs")]
    [InlineData("/home/ci/Brain/bench/RetrievalBenchmark.cs")] // bench/, but NOT bench/golden/
    [InlineData("docs/developer/arc42/06_runtime_view.md")]
    public void RealCodeAndDocNodes_AreNotExcluded(string path)
    {
        Assert.False(RetrievalBenchmark.IsGoldenFixture(NodeAt(path)));
    }

    [Fact]
    public void NodeWithoutAPath_IsNotAFixture()
    {
        Assert.False(RetrievalBenchmark.IsGoldenFixture(new GraphNode { Id = "concept::x", Name = "x", Type = "Concept" }));
    }
}
