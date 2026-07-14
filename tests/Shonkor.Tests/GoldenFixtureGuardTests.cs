// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;
using Shonkor.Core.Models;

namespace Shonkor.Tests;

/// <summary>
/// #110 / #133: the retrieval benchmark must ignore the project's own DEV-PROCESS META prose (golden sets,
/// tickets, reviews, measurement notes) — those describe the code in query vocabulary and would otherwise be
/// circular false hits. Product code (src/) and product docs (docs/) must stay eligible.
/// </summary>
public class GoldenFixtureGuardTests
{
    private static GraphNode NodeAt(string path) => new() { Id = path, Name = "x", Type = "File", FilePath = path };

    [Theory]
    // Golden fixtures — the acute, query-verbatim case (#110).
    [InlineData("C:\\Projects\\Brain\\bench\\golden\\agent-queries.json")]
    [InlineData("bench/golden/doc-sections.json")]
    // Dev-process prose — the diffuse tail (#133).
    [InlineData("C:\\Projects\\Brain\\tickets\\TICKET-201-groundedness-eval.md")]
    [InlineData("review/shonkor-bug-report.md")]
    [InlineData("/home/ci/Brain/bench/code-intent-decontamination.md")] // a measurement note (.md under bench)
    public void DevProcessMetaProse_IsExcludedFromTheEval(string path)
    {
        Assert.True(RetrievalBenchmark.IsEvalMetaNode(NodeAt(path)));
    }

    [Theory]
    // Product code and docs — must stay eligible.
    [InlineData("C:\\Projects\\Brain\\src\\Shonkor.Infrastructure\\Services\\GraphIndexScanner.cs")]
    [InlineData("docs/developer/arc42/06_runtime_view.md")]        // product documentation (doc-intent target)
    [InlineData("docs/user/setup_guide.md")]
    [InlineData("bench/RetrievalBenchmark.cs")]                     // bench SOURCE code is not prose
    public void ProductCodeAndDocs_StayEligible(string path)
    {
        Assert.False(RetrievalBenchmark.IsEvalMetaNode(NodeAt(path)));
    }

    [Fact]
    public void NodeWithoutAPath_IsNotMeta()
    {
        Assert.False(RetrievalBenchmark.IsEvalMetaNode(new GraphNode { Id = "concept::x", Name = "x", Type = "Concept" }));
    }
}
