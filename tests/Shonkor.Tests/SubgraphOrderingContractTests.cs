// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// The ordering contract of <see cref="Shonkor.Core.Interfaces.IGraphSearch.GetSubgraphAsync"/> (#170).
/// <para>
/// <c>get_subgraph verbose</c>'s size cap (#117) drops a <b>tail prefix</b> of the node list to stay under
/// <c>maxChars</c> while keeping the JSON parseable. That is only correct if the list is ordered
/// <b>nearest-first</b> — otherwise the cap silently evicts the <i>seeds</i> and keeps distant hub nodes,
/// producing valid-but-useless output with no failing signal (the exact #157 failure class).
/// </para>
/// <para>
/// The order used to be an accident of SQLite row order — the query had no <c>ORDER BY</c> at all. It is now
/// an explicit <c>ORDER BY Depth, Id</c> in the CTE, and this test enforces it: a future change to that query
/// that breaks nearest-first ordering fails here, loudly, instead of degrading the cap in silence.
/// </para>
/// </summary>
public class SubgraphOrderingContractTests
{
    private static SqliteGraphStorageProvider NewStorage()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_subg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new SqliteGraphStorageProvider(Path.Combine(dir, "g.db"));
    }

    /// <summary>
    /// A line graph seed → a → b → c → d, so hop distance from the seed is unambiguous and strictly
    /// increasing. Node ids are chosen so that <b>alphabetical</b> order (a plausible accidental row order)
    /// would NOT match hop order — that is what makes the test able to catch a broken ordering.
    /// </summary>
    private static async Task<SqliteGraphStorageProvider> SeedLineAsync()
    {
        var storage = NewStorage();
        await storage.InitializeAsync();
        // ids deliberately NOT in hop order alphabetically: seed="m_seed", then z1,a2,z3,a4 by increasing hop.
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "m_seed", Name = "Seed", Type = "Class", Content = "x" },
            new GraphNode { Id = "z1",     Name = "Hop1", Type = "Class", Content = "x" },
            new GraphNode { Id = "a2",     Name = "Hop2", Type = "Class", Content = "x" },
            new GraphNode { Id = "z3",     Name = "Hop3", Type = "Class", Content = "x" },
            new GraphNode { Id = "a4",     Name = "Hop4", Type = "Class", Content = "x" }
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "m_seed", TargetId = "z1", Relationship = "CALLS" },
            new GraphEdge { SourceId = "z1",     TargetId = "a2", Relationship = "CALLS" },
            new GraphEdge { SourceId = "a2",     TargetId = "z3", Relationship = "CALLS" },
            new GraphEdge { SourceId = "z3",     TargetId = "a4", Relationship = "CALLS" }
        });
        return storage;
    }

    [Fact]
    public async Task GetSubgraphAsync_ReturnsNodesNearestFirst()
    {
        using var storage = await SeedLineAsync();

        var (nodes, _) = await storage.GetSubgraphAsync(new[] { "m_seed" }, 4);

        // Seed first, then strictly by hop distance. If the query loses its ORDER BY (or an "optimisation"
        // returns rows by rowid / alphabetically), this sequence breaks and the cap would evict the wrong end.
        Assert.Equal(new[] { "m_seed", "z1", "a2", "z3", "a4" }, nodes.Select(n => n.Id).ToArray());
    }

    [Fact]
    public async Task Truncation_KeepsTheSeed_DropsTheFurthest()
    {
        // The behaviour the ordering exists to protect: a prefix (Take k) keeps the nearest k, INCLUDING the
        // seed, and drops the furthest. This is what #117's tail-drop cap does; if the order were wrong, the
        // seed — the one node the caller definitely wants — could be the first thing dropped.
        using var storage = await SeedLineAsync();
        var (nodes, _) = await storage.GetSubgraphAsync(new[] { "m_seed" }, 4);

        var keptNearestTwo = nodes.Take(2).Select(n => n.Id).ToList();
        Assert.Contains("m_seed", keptNearestTwo);   // the seed survives a tight cap
        Assert.Contains("z1", keptNearestTwo);        // and its nearest neighbour
        Assert.DoesNotContain("a4", keptNearestTwo);  // the furthest node is what's dropped
    }

    [Fact]
    public async Task TiesAtTheSameHop_AreOrderedDeterministicallyById()
    {
        // Two nodes at the same hop distance must come back in a stable, documented order (by Id), so the
        // truncation boundary is reproducible rather than whatever the engine happens to yield this run.
        using var storage = NewStorage();
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            new GraphNode { Id = "seed", Name = "S", Type = "Class", Content = "x" },
            new GraphNode { Id = "hop_b", Name = "B", Type = "Class", Content = "x" },
            new GraphNode { Id = "hop_a", Name = "A", Type = "Class", Content = "x" }
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "seed", TargetId = "hop_b", Relationship = "CALLS" },
            new GraphEdge { SourceId = "seed", TargetId = "hop_a", Relationship = "CALLS" }
        });

        var (nodes, _) = await storage.GetSubgraphAsync(new[] { "seed" }, 1);

        Assert.Equal(new[] { "seed", "hop_a", "hop_b" }, nodes.Select(n => n.Id).ToArray()); // hop_a before hop_b
    }
}
