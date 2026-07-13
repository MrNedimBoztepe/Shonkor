// Licensed to Shonkor under the MIT License.

using Microsoft.Data.Sqlite;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-211: the AI Summary is often the only place a node's INTENT vocabulary appears, so it must be
/// searchable. Covers the new FTS column and the migration of a pre-existing index that lacks it.
/// </summary>
public class FtsSummaryTests
{
    private static string NewDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_fts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "g.db");
    }

    private static GraphNode Node(string id, string name, string content, string? summary) => new()
    {
        Id = id, Name = name, Type = "Class", Content = content, Summary = summary
    };

    [Fact]
    public async Task Search_FindsVocabularyThatExistsOnlyInTheSummary()
    {
        var dbPath = NewDbPath();
        using var storage = new SqliteGraphStorageProvider(dbPath);
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            // "reconciliation" appears in neither the name nor the body — only in the summary.
            Node("a::1", "Widget", "class Widget { }", "Orchestrates the nightly reconciliation of ledgers."),
            Node("a::2", "Gadget", "class Gadget { }", "Unrelated helper.")
        });

        var hits = await storage.SearchAsync("reconciliation", 10);

        Assert.Contains(hits, h => h.Node.Id == "a::1");
        Assert.DoesNotContain(hits, h => h.Node.Id == "a::2");
    }

    [Fact]
    public async Task Search_StillFindsNameAndContent()
    {
        var dbPath = NewDbPath();
        using var storage = new SqliteGraphStorageProvider(dbPath);
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[] { Node("b::1", "Widget", "class Widget { }", null) });

        Assert.Contains(await storage.SearchAsync("Widget", 10), h => h.Node.Id == "b::1");
    }

    [Fact]
    public async Task UpdatingASummary_ReindexesIt()
    {
        var dbPath = NewDbPath();
        using var storage = new SqliteGraphStorageProvider(dbPath);
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[] { Node("c::1", "Thing", "class Thing { }", null) });
        Assert.Empty(await storage.SearchAsync("telemetry", 10));

        // Re-upsert with a summary: the AFTER UPDATE trigger must refresh the FTS row.
        await storage.UpsertNodesAsync(new[] { Node("c::1", "Thing", "class Thing { }", "Emits telemetry.") });

        Assert.Contains(await storage.SearchAsync("telemetry", 10), h => h.Node.Id == "c::1");
    }

    [Fact]
    public async Task LegacyIndexWithoutSummary_IsMigratedAndRebuilt()
    {
        var dbPath = NewDbPath();

        // Build a graph, then forcibly regress its FTS index to the pre-TICKET-211 shape (no Summary),
        // exactly as an existing database on disk would look.
        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[] { Node("d::1", "Ledger", "class Ledger { }", "Handles idempotent settlement.") });
        }

        await using (var raw = new SqliteConnection($"Data Source={dbPath}"))
        {
            await raw.OpenAsync();
            foreach (var sql in new[]
            {
                "DROP TRIGGER IF EXISTS nodes_ai;", "DROP TRIGGER IF EXISTS nodes_ad;", "DROP TRIGGER IF EXISTS nodes_au;",
                "DROP TABLE NodesFts;",
                "CREATE VIRTUAL TABLE NodesFts USING fts5(Id, Name, Content, content=Nodes, content_rowid=rowid);",
                "INSERT INTO NodesFts(NodesFts) VALUES('rebuild');"
            })
            {
                await using var cmd = raw.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // Re-opening must detect the missing column, recreate the index and rebuild it from Nodes.
        using (var migrated = new SqliteGraphStorageProvider(dbPath))
        {
            await migrated.InitializeAsync();

            var hits = await migrated.SearchAsync("settlement", 10);
            Assert.Contains(hits, h => h.Node.Id == "d::1");
            // The pre-existing rows survived the rebuild.
            Assert.Contains(await migrated.SearchAsync("Ledger", 10), h => h.Node.Id == "d::1");
        }
    }

    [Fact]
    public async Task MigrationIsIdempotent_SecondOpenDoesNotRebuildAgain()
    {
        var dbPath = NewDbPath();
        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[] { Node("e::1", "Alpha", "class Alpha { }", "Beta gamma.") });
        }

        using var reopened = new SqliteGraphStorageProvider(dbPath);
        await reopened.InitializeAsync();
        await reopened.InitializeAsync(); // opening twice must not corrupt the index

        Assert.Contains(await reopened.SearchAsync("gamma", 10), h => h.Node.Id == "e::1");
        Assert.Contains(await reopened.SearchAsync("Alpha", 10), h => h.Node.Id == "e::1");
    }
}
