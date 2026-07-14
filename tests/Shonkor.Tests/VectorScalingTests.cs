// Licensed to Shonkor under the MIT License.

using Microsoft.Data.Sqlite;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-215 Stage 1: L2-normalization on write, dot-product scoring (== cosine, exactness preserved),
/// the similarity floor, the zero-copy read, the two-branch subgraph CTE (identical result set), and the
/// one-time normalization migration for pre-existing un-normalized vectors.
/// </summary>
public class VectorScalingTests
{
    private static string NewDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_vec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "g.db");
    }

    private static GraphNode Node(string id, float[] embedding) => new()
    {
        Id = id, Name = id, Type = "Class", Content = $"class {id} {{}}", Embedding = embedding
    };

    // ---- VectorMath ---------------------------------------------------------------------------------

    [Fact]
    public void NormalizeL2_MakesUnitLength_AndIsIdempotent()
    {
        var v = new[] { 3f, 4f, 0f }; // magnitude 5
        VectorMath.NormalizeL2(v);
        Assert.True(VectorMath.IsUnitLength(v));
        Assert.Equal(0.6f, v[0], 3);
        Assert.Equal(0.8f, v[1], 3);

        var again = (float[])v.Clone();
        VectorMath.NormalizeL2(again); // normalizing a unit vector is a no-op
        Assert.Equal(v, again);
    }

    [Fact]
    public void NormalizeL2_LeavesZeroVectorUnchanged()
    {
        var v = new float[4];
        VectorMath.NormalizeL2(v);
        Assert.All(v, x => Assert.Equal(0f, x));
        Assert.False(VectorMath.IsUnitLength(v));
    }

    // ---- Zero-copy read: endianness guard (#129) ----------------------------------------------------

    [Fact]
    public void AsFloats_ReinterpretsTheBlob_AsTheSameFloatsThatWerePacked()
    {
        // The read side must be the exact inverse of the BlockCopy packing (SqliteRowMapper.EmbeddingToBytes)
        // on this (little-endian) host — that is the contract the zero-copy semantic-search read depends on.
        var vector = new[] { 1f, -2.5f, 3.25f, 0f };
        var blob = new byte[vector.Length * 4];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

        var readBack = VectorMath.AsFloats(blob).ToArray();

        Assert.Equal(vector, readBack);
    }

    [Fact]
    public void AsFloats_OnABigEndianHost_FailsLoud_RatherThanReturningSilentlyWrongScores()
    {
        // The stored blob is native-endian; a big-endian host reading it would reinterpret every float with
        // swapped bytes and return garbage similarities with no crash. The guard turns that silent-wrong into
        // a loud PlatformNotSupportedException. The endianness is a parameter here only so this branch is
        // reachable on a little-endian test host — the production call passes BitConverter.IsLittleEndian.
        var blob = new byte[8];

        var ex = Assert.Throws<PlatformNotSupportedException>(() =>
        {
            _ = VectorMath.AsFloats(blob, littleEndian: false);
        });
        Assert.Contains("little-endian", ex.Message);

        // ...and the same blob is read without throwing when the platform IS little-endian.
        var readOk = Record.Exception(() =>
        {
            _ = VectorMath.AsFloats(blob, littleEndian: true).Length;
        });
        Assert.Null(readOk);
    }

    [Fact]
    public void EmbeddingToBytes_ThenAsFloats_RoundTripsTheNormalizedVector()
    {
        // End-to-end write→read: the packing normalizes, so the bytes read back must be the UNIT vector, and
        // AsFloats must recover it exactly — pinning that the two halves of the storage format agree.
        var expected = VectorMath.NormalizedCopy(new[] { 3f, 4f, 0f }); // -> {0.6, 0.8, 0}

        var blob = SqliteRowMapper.EmbeddingToBytes(new[] { 3f, 4f, 0f })!;
        var readBack = VectorMath.AsFloats(blob).ToArray();

        Assert.Equal(expected, readBack);
    }

    // ---- Scoring: dot == cosine, exactness preserved ------------------------------------------------

    [Fact]
    public async Task Scoring_PreservesCosineRanking_AndReportsUnitScoreForAlignedVector()
    {
        using var storage = new SqliteGraphStorageProvider(NewDbPath());
        await storage.InitializeAsync();
        // Un-normalized on purpose: the write path must normalize them, so a scaled duplicate ranks equal.
        await storage.UpsertNodesAsync(new[]
        {
            Node("aligned", new[] { 2f, 0f, 0f }),        // same direction as the query, different magnitude
            Node("half", new[] { 1f, 1f, 0f }),           // 45° off
            Node("orthogonal", new[] { 0f, 5f, 0f })      // 90° off → cosine 0
        });

        var hits = await storage.SearchSemanticAsync(new[] { 1f, 0f, 0f }, maxResults: 10);

        // aligned (cosine 1) first; half (cosine ~0.707) second; orthogonal (cosine 0) excluded by score<=0.
        Assert.Equal("aligned", hits[0].Node.Id);
        Assert.Equal(1.0, hits[0].Score, precision: 3);       // magnitude was normalized away
        Assert.Equal("half", hits[1].Node.Id);
        Assert.Equal(0.707, hits[1].Score, precision: 2);
        Assert.DoesNotContain(hits, h => h.Node.Id == "orthogonal");
    }

    [Fact]
    public async Task SimilarityFloor_DropsWeakHits()
    {
        using var storage = new SqliteGraphStorageProvider(NewDbPath());
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            Node("strong", new[] { 1f, 0f, 0f }),         // cosine 1.0
            Node("weak", new[] { 3f, 1f, 0f })            // cosine ~0.949 → keep at 0.5, drop at 0.98
        });

        Assert.Equal(2, (await storage.SearchSemanticAsync(new[] { 1f, 0f, 0f }, 10, 0.5)).Count);
        var strict = await storage.SearchSemanticAsync(new[] { 1f, 0f, 0f }, 10, 0.98);
        Assert.Single(strict);
        Assert.Equal("strong", strict[0].Node.Id);
    }

    // ---- Migration ----------------------------------------------------------------------------------

    [Fact]
    public async Task Migration_NormalizesPreExistingUnNormalizedVectors_Once()
    {
        var dbPath = NewDbPath();
        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[] { Node("n", new[] { 1f, 0f, 0f }) });
        }

        // Forcibly write a NON-normalized blob straight into the row, simulating a pre-TICKET-215 DB, and
        // clear the migration flag so the next open re-runs it.
        var raw = new byte[3 * 4];
        Buffer.BlockCopy(new[] { 10f, 0f, 0f }, 0, raw, 0, raw.Length); // magnitude 10, not 1
        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Nodes SET Embedding=@e WHERE Id='n'; DELETE FROM Meta WHERE Key='embeddingsNormalized';";
            cmd.Parameters.AddWithValue("@e", raw);
            await cmd.ExecuteNonQueryAsync();
        }

        // Re-opening runs the one-time migration, normalizing the stored vector in place.
        using (var migrated = new SqliteGraphStorageProvider(dbPath))
        {
            await migrated.InitializeAsync();
            var hit = Assert.Single(await migrated.SearchSemanticAsync(new[] { 1f, 0f, 0f }, 10));
            Assert.Equal("n", hit.Node.Id);
            Assert.Equal(1.0, hit.Score, precision: 3); // would be 10.0 if the migration hadn't run
        }
    }

    // ---- Subgraph CTE equivalence -------------------------------------------------------------------

    [Fact]
    public async Task SubgraphCte_TwoBranch_ReturnsBothDirections_Undirected()
    {
        using var storage = new SqliteGraphStorageProvider(NewDbPath());
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            Node("center", new[] { 1f, 0f }),
            Node("out", new[] { 1f, 0f }),   // center -> out (center is the SOURCE)
            Node("in", new[] { 1f, 0f })     // in -> center (center is the TARGET)
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "center", TargetId = "out", Relationship = "REFERENCES_TYPE" },
            new GraphEdge { SourceId = "in", TargetId = "center", Relationship = "REFERENCES_TYPE" }
        });

        var (nodes, _) = await storage.GetSubgraphAsync(new[] { "center" }, 1);
        var ids = nodes.Select(n => n.Id).ToHashSet();

        // Undirected traversal must reach BOTH the outgoing and the incoming neighbour.
        Assert.Contains("out", ids);   // via the SourceId = center branch
        Assert.Contains("in", ids);    // via the TargetId = center branch
        Assert.Contains("center", ids);
    }
}
