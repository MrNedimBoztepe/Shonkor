// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Eval-corpus hygiene is a LOUD gate, not just a silent filter (#136).
/// <para>
/// #132 filters index-excluded meta files (<c>bench/golden</c>, <c>tickets</c>, <c>review</c>) out of the
/// ranked results at measurement time. That keeps today's numbers correct — but if the <c>shonkor.json</c>
/// exclude regresses and those files get indexed again, the filter quietly patches over it and the
/// re-contamination is invisible. So the benchmark now also <b>records</b> any such file that surfaces in
/// retrieval and fails with a non-zero exit, naming it. This tests that detection end-to-end through
/// <c>RetrievalBenchmark.RunAsync</c>, and the classifier that draws the line.
/// </para>
/// </summary>
public class EvalContaminationGateTests
{
    private static SqliteGraphStorageProvider NewStorage()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_contam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new SqliteGraphStorageProvider(Path.Combine(dir, "g.db"));
    }

    // ---- the classifier that decides "must never be in the graph" vs "guard-only" -------------------

    [Theory]
    [InlineData("bench/golden/agent-queries.json", true)]  // index-excluded → contamination if it appears
    [InlineData("tickets/TICKET-215-vector-scaling.md", true)]
    [InlineData("review/roadmap.md", true)]
    [InlineData("bench/vector-scaling-measurement.md", false)] // guard-only: legitimately indexed
    [InlineData("src/Shonkor.Bench/RetrievalBenchmark.cs", false)] // product source: eligible
    [InlineData("docs/user/setup_guide.md", false)]              // product docs: eligible
    public void IsIndexExcludedMeta_DrawsTheLineAtDirectoriesThatMustNotBeIndexed(string path, bool expected)
    {
        var node = new GraphNode { Id = path, Name = "x", Type = "File", FilePath = path };
        Assert.Equal(expected, RetrievalBenchmark.IsIndexExcludedMeta(node));
    }

    // ---- the gate fires end-to-end when a golden fixture is in the graph -----------------------------

    [Fact]
    public async Task RunAsync_FlagsAContaminatedGraph_NamingTheFixture()
    {
        using var storage = NewStorage();
        await storage.InitializeAsync();

        // The exclude has "regressed": a golden fixture is in the graph, and its content contains the query
        // words so FTS surfaces it (exactly the circular hit #110 was about).
        const string fixturePath = "bench/golden/agent-queries.json";
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "src::TokenHasher", Name = "TokenHasher", Type = "Class",
                            FilePath = "src/Security/TokenHasher.cs", Content = "class TokenHasher hashes api tokens" },
            new GraphNode { Id = fixturePath, Name = "agent-queries", Type = "File",
                            FilePath = fixturePath, Content = "how are api tokens hashed and stored" }
        });
        var all = await storage.GetAllNodesAsync();

        var cases = new List<bench::Shonkor.Bench.GoldenCase>
        {
            new() { Id = "q", Query = "how are api tokens hashed", Expected = ["TokenHasher"] }
        };

        var (_, _, _, contamination) = await RetrievalBenchmark.RunAsync(storage, all, k: 10, TextWriter.Null, cases);

        Assert.Contains("bench/golden/agent-queries.json", contamination);
    }

    [Fact]
    public async Task RunAsync_OnACleanGraph_ReportsNoContamination()
    {
        using var storage = NewStorage();
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "src::TokenHasher", Name = "TokenHasher", Type = "Class",
                            FilePath = "src/Security/TokenHasher.cs", Content = "class TokenHasher hashes api tokens" }
        });
        var all = await storage.GetAllNodesAsync();

        var cases = new List<bench::Shonkor.Bench.GoldenCase>
        {
            new() { Id = "q", Query = "how are api tokens hashed", Expected = ["TokenHasher"] }
        };

        var (_, _, _, contamination) = await RetrievalBenchmark.RunAsync(storage, all, k: 10, TextWriter.Null, cases);

        Assert.Empty(contamination);
    }

    [Fact]
    public async Task RunAsync_DoesNotFlag_TheGuardOnlyBenchMarkdown()
    {
        // A bench measurement note is legitimately indexed and only eval-filtered — it must NOT trip the
        // contamination gate even when it surfaces in results, or every real run would fail.
        using var storage = NewStorage();
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "note", Name = "note", Type = "MarkdownSection",
                            FilePath = "bench/vector-scaling-measurement.md", Content = "how are api tokens hashed" }
        });
        var all = await storage.GetAllNodesAsync();

        var cases = new List<bench::Shonkor.Bench.GoldenCase>
        {
            new() { Id = "q", Query = "how are api tokens hashed", Expected = ["Whatever"] }
        };

        var (_, _, _, contamination) = await RetrievalBenchmark.RunAsync(storage, all, k: 10, TextWriter.Null, cases);

        Assert.Empty(contamination);
    }
}
