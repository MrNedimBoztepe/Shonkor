// Licensed to Shonkor under the MIT License.

using Microsoft.Data.Sqlite;

using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// #399: the one-time repair of edges holding a trust tier their relationship may not hold. 1,354 such
/// edges were measured across four real graphs, and none of them is reachable through the normal write
/// path — the persistence merge keeps the MIN provenance on conflict, so trust only ratchets up.
///
/// <para>
/// These tests use a temp FILE database rather than <c>:memory:</c> because the repair has to be observed
/// across provider instances: the flag is set on the first initialize of a fresh graph (correctly — a fresh
/// graph has nothing to repair), so a legacy graph is simulated by seeding violations and clearing the flag.
/// </para>
/// </summary>
public sealed class ProvenanceRepairMigrationTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shonkor-repair-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in _paths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

    private static async Task WithRawConnectionAsync(string path, Func<SqliteConnection, Task> work)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        await conn.OpenAsync();
        await work(conn);
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Seeds one edge per (relationship, tier) pair and re-arms the one-time gate.</summary>
    private static async Task SeedLegacyGraphAsync(string path, params (string Rel, Provenance Tier)[] edges)
    {
        await WithRawConnectionAsync(path, async conn =>
        {
            var i = 0;
            foreach (var (rel, tier) in edges)
            {
                var s = $"n{i}s";
                var t = $"n{i}t";
                i++;
                await ExecAsync(conn, $"INSERT OR IGNORE INTO Nodes (Id, Type, Name) VALUES ('{s}', 'T', '{s}'), ('{t}', 'T', '{t}');");
                await ExecAsync(conn,
                    $"INSERT INTO Edges (SourceId, TargetId, RelationType, Provenance) VALUES ('{s}', '{t}', '{rel}', {(int)tier});");
            }
            await ExecAsync(conn, $"DELETE FROM Meta WHERE Key = '{SqliteSchema.ProvenanceRepairMetaKey}';");
        });
    }

    private static async Task<List<GraphEdge>> EdgesOfAsync(string path)
    {
        using var provider = new SqliteGraphStorageProvider(path);
        return (await provider.GetAllEdgesAsync()).ToList();
    }

    private static async Task<List<string>> RepairDiagnosticsAsync(string path)
    {
        var messages = new List<string>();
        await WithRawConnectionAsync(path, async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Message FROM Diagnostics WHERE Source = '{SqliteSchema.ProvenanceRepairDiagnosticSource}';";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) messages.Add(r.GetString(0));
        });
        return messages;
    }

    /// <summary>
    /// The repair itself: every family whose producer is identifiable from <c>(RelationType, Provenance)</c>
    /// is moved onto the tier that producer would assign today, and the families whose producer is NOT
    /// identifiable are left exactly as they are.
    /// </summary>
    [Fact]
    public async Task Repair_MovesViolatingFamilies_AndLeavesTheUnidentifiableOnesAlone()
    {
        var path = NewDbPath();
        using (var provider = new SqliteGraphStorageProvider(path)) await provider.InitializeAsync();

        await SeedLegacyGraphAsync(path,
            ("RELATES_TO", Provenance.Extracted),            // LLM concept link claiming a compiler fact
            ("BELONGS_TO_MODULE", Provenance.Extracted),     // path-based Helix membership
            ("REGISTERS_PROCESSOR", Provenance.Extracted),   // patched Sitecore config
            ("DEFINES_COMPONENT", Provenance.Extracted),     // XM Cloud component
            ("RESOLVES_TO", Provenance.Extracted),           // resolver output, two legitimate tiers
            ("IMPLEMENTS", Provenance.Extracted),            // producer NOT identifiable -- #402
            ("EXTENDS", Provenance.Extracted),               // producer NOT identifiable -- #402
            ("CALLS", Provenance.Extracted));                // legitimately Extracted -- must not move

        using (var provider = new SqliteGraphStorageProvider(path)) await provider.InitializeAsync();

        var byRel = (await EdgesOfAsync(path)).ToDictionary(e => e.Relationship, e => e.Provenance, StringComparer.Ordinal);

        Assert.Equal(Provenance.Inferred, byRel["RELATES_TO"]);
        Assert.Equal(Provenance.Inferred, byRel["BELONGS_TO_MODULE"]);
        Assert.Equal(Provenance.Inferred, byRel["REGISTERS_PROCESSOR"]);
        Assert.Equal(Provenance.Inferred, byRel["DEFINES_COMPONENT"]);

        // The weaker of the two legitimate tiers: a migration cannot recompute the candidate count that
        // decides between Inferred and Ambiguous, so it understates rather than overstates.
        Assert.Equal(Provenance.Ambiguous, byRel["RESOLVES_TO"]);

        // Untouched: repairing these would have to guess which producer wrote them.
        Assert.Equal(Provenance.Extracted, byRel["IMPLEMENTS"]);
        Assert.Equal(Provenance.Extracted, byRel["EXTENDS"]);

        // And a legitimately Extracted family is not collateral damage.
        Assert.Equal(Provenance.Extracted, byRel["CALLS"]);
    }

    /// <summary>
    /// The repair does what the runtime write path cannot. <c>UpsertEdgesAsync</c> merges with
    /// <c>MIN(excluded, existing)</c>, so offering the correct weaker tier through it leaves the wrong
    /// stronger one in place — which is precisely why this is a migration and not a re-index.
    /// </summary>
    [Fact]
    public async Task Repair_AchievesWhatAnUpsertCannot()
    {
        var path = NewDbPath();
        using (var provider = new SqliteGraphStorageProvider(path)) await provider.InitializeAsync();
        await SeedLegacyGraphAsync(path, ("RELATES_TO", Provenance.Extracted));

        // First: prove the normal path is powerless here.
        using (var provider = new SqliteGraphStorageProvider(path))
        {
            var existing = Assert.Single(await provider.GetAllEdgesAsync());
            await provider.UpsertEdgesAsync(new[] { existing with { Provenance = Provenance.Inferred } });
            Assert.Equal(Provenance.Extracted, Assert.Single(await provider.GetAllEdgesAsync()).Provenance);
        }

        // The upsert above ran through InitializeAsync-free construction, so the gate is still cleared.
        using (var provider = new SqliteGraphStorageProvider(path)) await provider.InitializeAsync();

        Assert.Equal(Provenance.Inferred, Assert.Single(await EdgesOfAsync(path)).Provenance);
    }

    /// <summary>
    /// Idempotence, tested on the predicate rather than on the gate: with the gate re-armed and nothing left
    /// to repair, a second run must change nothing and say nothing. A migration that keeps "repairing"
    /// already-correct rows would make its own audit trail worthless.
    /// </summary>
    [Fact]
    public async Task Repair_IsIdempotent_EvenWithTheGateReArmed()
    {
        var path = NewDbPath();
        using (var provider = new SqliteGraphStorageProvider(path)) await provider.InitializeAsync();
        await SeedLegacyGraphAsync(path,
            ("RELATES_TO", Provenance.Extracted),
            ("BELONGS_TO_MODULE", Provenance.Extracted));

        using (var provider = new SqliteGraphStorageProvider(path)) await provider.InitializeAsync();
        var afterFirst = await RepairDiagnosticsAsync(path);
        Assert.Equal(2, afterFirst.Count);

        // Re-arm and run again: the predicate now matches nothing.
        await WithRawConnectionAsync(path, conn =>
            ExecAsync(conn, $"DELETE FROM Meta WHERE Key = '{SqliteSchema.ProvenanceRepairMetaKey}';"));
        using (var provider = new SqliteGraphStorageProvider(path)) await provider.InitializeAsync();

        Assert.Equal(afterFirst.Count, (await RepairDiagnosticsAsync(path)).Count);
        var (violations, _) = ProvenanceInvariant.Check(await EdgesOfAsync(path));
        Assert.Empty(violations);
    }

    /// <summary>
    /// The change has to be auditable after the fact, not merely to have happened: each repaired family
    /// leaves a diagnostic naming its count and its producer, queryable through <c>get_diagnostics</c>.
    /// </summary>
    [Fact]
    public async Task Repair_LeavesAnAuditTrailPerFamily()
    {
        var path = NewDbPath();
        using (var provider = new SqliteGraphStorageProvider(path)) await provider.InitializeAsync();
        await SeedLegacyGraphAsync(path, ("RELATES_TO", Provenance.Extracted));

        using (var provider = new SqliteGraphStorageProvider(path)) await provider.InitializeAsync();

        var message = Assert.Single(await RepairDiagnosticsAsync(path));
        Assert.Contains("RELATES_TO", message);
        Assert.Contains("inferred", message);
        Assert.Contains("LLM concept promotion", message); // the producer, so the row explains itself
    }

    /// <summary>
    /// A fresh graph is flagged as repaired without doing anything, and — the part that matters — without
    /// leaving an audit row. A migration that announces itself on every new database is noise.
    /// </summary>
    [Fact]
    public async Task FreshGraph_IsGatedWithoutAnyAuditRow()
    {
        var path = NewDbPath();
        using (var provider = new SqliteGraphStorageProvider(path)) await provider.InitializeAsync();

        Assert.Empty(await RepairDiagnosticsAsync(path));
    }
}
