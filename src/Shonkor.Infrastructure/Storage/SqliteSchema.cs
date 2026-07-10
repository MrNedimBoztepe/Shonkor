// Licensed to Shonkor under the MIT License.

using Microsoft.Data.Sqlite;

namespace Shonkor.Infrastructure.Storage;

/// <summary>
/// Owns the SQLite schema for the knowledge graph: table/index/FTS5 creation, the additive column
/// migrations for pre-existing databases, and the FTS-rebuild-on-drift guard. Kept separate from
/// <see cref="SqliteGraphStorageProvider"/> so the DDL is in one auditable place.
/// </summary>
internal static class SqliteSchema
{
    /// <summary>
    /// Creates all tables, indexes, FTS5 virtual table and sync triggers if absent, applies additive
    /// migrations, and rebuilds the FTS index only when it has drifted out of sync with the Nodes table.
    /// </summary>
    public static async Task InitializeAsync(SqliteConnection connection, bool isMemory, CancellationToken cancellationToken)
    {
        // Write-Ahead Logging improves concurrency for file-based databases (no-op for memory).
        if (!isMemory)
        {
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS Nodes (
                Id          TEXT PRIMARY KEY,
                Type        TEXT NOT NULL,
                Name        TEXT NOT NULL,
                Content     TEXT,
                Metadata    TEXT,
                FilePath    TEXT,
                StartLine   INTEGER,
                EndLine     INTEGER,
                ContentHash TEXT,
                Summary     TEXT,
                NeedsSemanticAnalysis INTEGER DEFAULT 1,
                Embedding   BLOB,
                EmbeddingDim   INTEGER,
                EmbeddingModel TEXT
            );
            """,
            cancellationToken).ConfigureAwait(false);

        // Additive migrations for databases created before these columns existed (ignore if present).
        await TryExecuteAsync(connection, "ALTER TABLE Nodes ADD COLUMN Summary TEXT;", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(connection, "ALTER TABLE Nodes ADD COLUMN NeedsSemanticAnalysis INTEGER DEFAULT 1;", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(connection, "ALTER TABLE Nodes ADD COLUMN Embedding BLOB;", cancellationToken).ConfigureAwait(false);
        // TICKET-006: version the embedding so a model/dimension change is detectable (re-embed trigger)
        // instead of the stored vector being silently skipped at query time.
        await TryExecuteAsync(connection, "ALTER TABLE Nodes ADD COLUMN EmbeddingDim INTEGER;", cancellationToken).ConfigureAwait(false);
        await TryExecuteAsync(connection, "ALTER TABLE Nodes ADD COLUMN EmbeddingModel TEXT;", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS Edges (
                SourceId     TEXT NOT NULL,
                TargetId     TEXT NOT NULL,
                RelationType TEXT NOT NULL,
                Provenance   INTEGER NOT NULL DEFAULT 0,
                Properties   TEXT,
                PRIMARY KEY (SourceId, TargetId, RelationType)
            );
            """,
            cancellationToken).ConfigureAwait(false);

        // Additive migration: edges in a graph created before provenance existed read back as 0 (Extracted),
        // matching the prior implicit "all edges are hard facts" semantics until they are re-indexed.
        await TryExecuteAsync(connection, "ALTER TABLE Edges ADD COLUMN Provenance INTEGER NOT NULL DEFAULT 0;", cancellationToken).ConfigureAwait(false);
        // Edge properties (TICKET-207): dynamic parser-specific attributes, persisted as a JSON object.
        await TryExecuteAsync(connection, "ALTER TABLE Edges ADD COLUMN Properties TEXT;", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_edges_source ON Edges(SourceId);", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_edges_target ON Edges(TargetId);", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_nodes_filepath ON Nodes(FilePath);", cancellationToken).ConfigureAwait(false);

        // Reverse index of C# type references (drift Layer 2): which node (and file) references a type by
        // NAME. Derived from each node's `referencedTypes` metadata during upsert (the JSON Metadata column
        // isn't efficiently queryable). Lets a rename/remove in one file relink only the files that actually
        // reference the changed type — bounding incoming-edge maintenance to the referencers, not the repo.
        await ExecuteAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS TypeReferences (
                TypeName TEXT NOT NULL,
                NodeId   TEXT NOT NULL,
                FilePath TEXT,
                PRIMARY KEY (TypeName, NodeId)
            );
            """,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_typerefs_name ON TypeReferences(TypeName);", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_typerefs_file ON TypeReferences(FilePath);", cancellationToken).ConfigureAwait(false);

        // Diagnostics produced by graph post-processors (phase 2). Grouped by Source (the post-processor's
        // name) so a re-run replaces exactly its own rows. Kept out of the graph so issues stay visible
        // without polluting nodes/edges. Severity is the DiagnosticSeverity enum value.
        await ExecuteAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS Diagnostics (
                Source   TEXT NOT NULL,
                Code     TEXT NOT NULL,
                Severity INTEGER NOT NULL,
                Message  TEXT NOT NULL,
                NodeId   TEXT,
                FilePath TEXT
            );
            """,
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS idx_diagnostics_source ON Diagnostics(Source);", cancellationToken).ConfigureAwait(false);

        // Migration (TICKET-211): NodesFts gained a Summary column. An FTS5 virtual table cannot be
        // ALTERed, so a pre-existing index built without Summary is dropped and rebuilt below. The AI
        // summary is often the only place a node's INTENT vocabulary appears, so leaving it out made
        // keyword search blind to exactly the words a human would type.
        var ftsSql = await ScalarStringAsync(connection,
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='NodesFts';", cancellationToken).ConfigureAwait(false);
        var ftsNeedsMigration = ftsSql is not null && !ftsSql.Contains("Summary", StringComparison.OrdinalIgnoreCase);
        if (ftsNeedsMigration)
        {
            await ExecuteAsync(connection, "DROP TABLE NodesFts;", cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection,
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS NodesFts USING fts5(
                Id, Name, Content, Summary,
                content=Nodes,
                content_rowid=rowid
            );
            """,
            cancellationToken).ConfigureAwait(false);

        // Triggers keep the FTS5 index synchronized with the Nodes table during normal operation. They are
        // dropped and recreated unconditionally so their column list can never drift from the table above.
        await ExecuteAsync(connection, "DROP TRIGGER IF EXISTS nodes_ai;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "DROP TRIGGER IF EXISTS nodes_ad;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "DROP TRIGGER IF EXISTS nodes_au;", cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection,
            """
            CREATE TRIGGER nodes_ai AFTER INSERT ON Nodes BEGIN
                INSERT INTO NodesFts(rowid, Id, Name, Content, Summary)
                VALUES (new.rowid, new.Id, new.Name, new.Content, new.Summary);
            END;
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection,
            """
            CREATE TRIGGER nodes_ad AFTER DELETE ON Nodes BEGIN
                INSERT INTO NodesFts(NodesFts, rowid, Id, Name, Content, Summary)
                VALUES ('delete', old.rowid, old.Id, old.Name, old.Content, old.Summary);
            END;
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection,
            """
            CREATE TRIGGER nodes_au AFTER UPDATE ON Nodes BEGIN
                INSERT INTO NodesFts(NodesFts, rowid, Id, Name, Content, Summary)
                VALUES ('delete', old.rowid, old.Id, old.Name, old.Content, old.Summary);
                INSERT INTO NodesFts(rowid, Id, Name, Content, Summary)
                VALUES (new.rowid, new.Id, new.Name, new.Content, new.Summary);
            END;
            """,
            cancellationToken).ConfigureAwait(false);

        // Rebuild the FTS5 index only when it is out of sync with the Nodes table. A full rebuild is
        // O(N) and can take seconds on large databases, so running it unconditionally on every startup
        // is a performance regression. The triggers keep FTS in sync during normal operation; a rebuild
        // is only needed when FTS missed updates (direct DB edits, migration, or first open after the
        // FTS table was added to a pre-existing database) — including the Summary migration above.
        var nodeCount = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM Nodes;", cancellationToken).ConfigureAwait(false);
        var ftsCount = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM NodesFts;", cancellationToken).ConfigureAwait(false);
        if (ftsNeedsMigration || nodeCount != ftsCount)
        {
            await ExecuteAsync(connection, "INSERT INTO NodesFts(NodesFts) VALUES('rebuild');", cancellationToken).ConfigureAwait(false);
        }

        // Stamp a brand-new (empty) graph with the current node-id scheme version, so it isn't later
        // mistaken for a stale legacy graph (which reads user_version 0). A non-empty database keeps its
        // existing version — if that is below the current scheme, the mismatch is the re-index signal.
        if (nodeCount == 0)
        {
            await ExecuteAsync(connection, $"PRAGMA user_version = {Core.Services.CsharpNodeId.SchemeVersion};", cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        try { await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false); }
        catch (SqliteException) { /* Column/object already exists — migration is a no-op. */ }
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? 0 : Convert.ToInt64(result);
    }

    /// <summary>Reads a single text value, or <c>null</c> when the query yields no row (or SQL NULL).</summary>
    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result);
    }
}
