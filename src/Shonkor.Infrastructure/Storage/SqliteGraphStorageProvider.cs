using System.Text.Json;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Microsoft.Data.Sqlite;

namespace Shonkor.Infrastructure.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IGraphStorageProvider"/> using FTS5
/// for full-text search and recursive CTEs for graph traversal.
/// </summary>
/// <remarks>
/// Thread-safety: a fresh <see cref="SqliteConnection"/> is opened for every operation.
/// For file-based databases this draws from the built-in connection pool, so concurrent
/// requests no longer share a single (non-thread-safe) connection object.
/// For in-memory databases a uniquely-named shared-cache database is used, anchored by a
/// long-lived keep-alive connection so the data survives between per-operation connections.
/// </remarks>
public sealed class SqliteGraphStorageProvider : IGraphStorageProvider, IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;
    private readonly bool _isMemory;

    /// <summary>
    /// Initializes a new instance of <see cref="SqliteGraphStorageProvider"/>
    /// with the specified database file path.
    /// </summary>
    /// <param name="dbPath">
    /// The file system path for the SQLite database.
    /// Use <c>":memory:"</c> for in-memory databases.
    /// </param>
    public SqliteGraphStorageProvider(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        _isMemory = dbPath == ":memory:";

        if (_isMemory)
        {
            // A uniquely-named, shared-cache in-memory database. Multiple connections that use
            // the same name + shared cache observe the same data. A keep-alive connection holds
            // the database alive for the lifetime of this provider.
            var name = $"shonkor_mem_{Guid.NewGuid():N}";
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = name,
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();
        }
        else
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Default,
                Pooling = true
            }.ToString();
        }
    }

    /// <summary>
    /// Opens a fresh connection for a single operation. The caller owns and disposes it.
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Wait up to 5s on a locked database instead of failing instantly with SQLITE_BUSY.
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Write-Ahead Logging improves concurrency for file-based databases (no-op for memory).
        if (!_isMemory)
        {
            await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection,
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
                ContentHash TEXT
            );
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection,
            """
            CREATE TABLE IF NOT EXISTS Edges (
                SourceId     TEXT NOT NULL,
                TargetId     TEXT NOT NULL,
                RelationType TEXT NOT NULL,
                PRIMARY KEY (SourceId, TargetId, RelationType)
            );
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection,
            "CREATE INDEX IF NOT EXISTS idx_edges_source ON Edges(SourceId);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection,
            "CREATE INDEX IF NOT EXISTS idx_edges_target ON Edges(TargetId);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection,
            "CREATE INDEX IF NOT EXISTS idx_nodes_filepath ON Nodes(FilePath);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection,
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS NodesFts USING fts5(
                Id, Name, Content,
                content=Nodes,
                content_rowid=rowid
            );
            """,
            cancellationToken).ConfigureAwait(false);

        // Triggers to keep the FTS5 index synchronized with the Nodes table.
        await ExecuteNonQueryAsync(connection,
            """
            CREATE TRIGGER IF NOT EXISTS nodes_ai AFTER INSERT ON Nodes BEGIN
                INSERT INTO NodesFts(rowid, Id, Name, Content)
                VALUES (new.rowid, new.Id, new.Name, new.Content);
            END;
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection,
            """
            CREATE TRIGGER IF NOT EXISTS nodes_ad AFTER DELETE ON Nodes BEGIN
                INSERT INTO NodesFts(NodesFts, rowid, Id, Name, Content)
                VALUES ('delete', old.rowid, old.Id, old.Name, old.Content);
            END;
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection,
            """
            CREATE TRIGGER IF NOT EXISTS nodes_au AFTER UPDATE ON Nodes BEGIN
                INSERT INTO NodesFts(NodesFts, rowid, Id, Name, Content)
                VALUES ('delete', old.rowid, old.Id, old.Name, old.Content);
                INSERT INTO NodesFts(rowid, Id, Name, Content)
                VALUES (new.rowid, new.Id, new.Name, new.Content);
            END;
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection,
            "INSERT INTO NodesFts(NodesFts) VALUES('rebuild');",
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertNodesAsync(IEnumerable<GraphNode> nodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO Nodes (Id, Type, Name, Content, Metadata, FilePath, StartLine, EndLine, ContentHash)
            VALUES (@Id, @Type, @Name, @Content, @Metadata, @FilePath, @StartLine, @EndLine, @ContentHash);
            """;

        var pId = command.Parameters.Add("@Id", SqliteType.Text);
        var pType = command.Parameters.Add("@Type", SqliteType.Text);
        var pName = command.Parameters.Add("@Name", SqliteType.Text);
        var pContent = command.Parameters.Add("@Content", SqliteType.Text);
        var pMetadata = command.Parameters.Add("@Metadata", SqliteType.Text);
        var pFilePath = command.Parameters.Add("@FilePath", SqliteType.Text);
        var pStartLine = command.Parameters.Add("@StartLine", SqliteType.Integer);
        var pEndLine = command.Parameters.Add("@EndLine", SqliteType.Integer);
        var pContentHash = command.Parameters.Add("@ContentHash", SqliteType.Text);

        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

        foreach (var node in nodes)
        {
            pId.Value = node.Id;
            pType.Value = node.Type;
            pName.Value = node.Name;
            pContent.Value = string.IsNullOrEmpty(node.Content) ? DBNull.Value : node.Content;
            pMetadata.Value = SerializeMetadata(node.Properties);
            pFilePath.Value = (object?)node.FilePath ?? DBNull.Value;
            pStartLine.Value = node.StartLine.HasValue ? node.StartLine.Value : DBNull.Value;
            pEndLine.Value = node.EndLine.HasValue ? node.EndLine.Value : DBNull.Value;
            pContentHash.Value = (object?)node.ContentHash ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edges);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO Edges (SourceId, TargetId, RelationType)
            VALUES (@SourceId, @TargetId, @RelationType);
            """;

        var pSource = command.Parameters.Add("@SourceId", SqliteType.Text);
        var pTarget = command.Parameters.Add("@TargetId", SqliteType.Text);
        var pRelation = command.Parameters.Add("@RelationType", SqliteType.Text);

        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

        foreach (var edge in edges)
        {
            pSource.Value = edge.SourceId;
            pTarget.Value = edge.TargetId;
            pRelation.Value = edge.Relationship;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int maxResults = 20,
        string? filterType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Collect (node, score) pairs first, then batch-load related edges in a single query
        // to avoid the previous N+1 edge lookup (one extra query per result row).
        var hits = new List<(GraphNode Node, double Score)>();

        var trimmedQuery = query.Trim();
        if (trimmedQuery == "*" || trimmedQuery.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            await using var allCmd = connection.CreateCommand();
            var typeClause = string.IsNullOrWhiteSpace(filterType) ? "" : "WHERE Type = @typeFilter";
            allCmd.CommandText =
                $"""
                SELECT Id, Type, Name, Content, Metadata, FilePath, StartLine, EndLine, ContentHash,
                       1.0 AS Score
                FROM Nodes
                {typeClause}
                LIMIT @limit;
                """;

            allCmd.Parameters.AddWithValue("@limit", maxResults);
            if (!string.IsNullOrWhiteSpace(filterType))
            {
                allCmd.Parameters.AddWithValue("@typeFilter", filterType);
            }

            await using var reader = await allCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add((ReadNode(reader), 1.0));
            }

            return await AttachEdgesAsync(connection, hits, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Attempt standard high-performance FTS5 full-text MATCH
            await using var command = connection.CreateCommand();
            var typeClause = string.IsNullOrWhiteSpace(filterType) ? "" : "AND n.Type = @typeFilter";
            command.CommandText =
                $"""
                SELECT n.Id, n.Type, n.Name, n.Content, n.Metadata, n.FilePath,
                       n.StartLine, n.EndLine, n.ContentHash,
                       bm25(NodesFts) AS Score
                FROM NodesFts fts
                JOIN Nodes n ON fts.Id = n.Id
                WHERE NodesFts MATCH @query {typeClause}
                ORDER BY Score
                LIMIT @limit;
                """;

            command.Parameters.AddWithValue("@query", trimmedQuery);
            command.Parameters.AddWithValue("@limit", maxResults);
            if (!string.IsNullOrWhiteSpace(filterType))
            {
                command.Parameters.AddWithValue("@typeFilter", filterType);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var node = ReadNode(reader);
                var score = reader.GetDouble(reader.GetOrdinal("Score"));

                // BM25 returns negative values (lower = better); normalize to a positive magnitude.
                hits.Add((node, Math.Abs(score)));
            }
        }
        catch (SqliteException)
        {
            // Fallback: If FTS5 throws a syntax error (e.g. from colons, slashes, or special operators), use a robust LIKE query
            hits.Clear();
            await using var command = connection.CreateCommand();
            var typeClause = string.IsNullOrWhiteSpace(filterType) ? "" : "AND Type = @typeFilter";
            command.CommandText =
                $"""
                SELECT Id, Type, Name, Content, Metadata, FilePath, StartLine, EndLine, ContentHash,
                       1.0 AS Score
                FROM Nodes
                WHERE (Name LIKE @likeQuery
                   OR Content LIKE @likeQuery
                   OR FilePath LIKE @likeQuery)
                   {typeClause}
                LIMIT @limit;
                """;

            command.Parameters.AddWithValue("@likeQuery", $"%{query}%");
            command.Parameters.AddWithValue("@limit", maxResults);
            if (!string.IsNullOrWhiteSpace(filterType))
            {
                command.Parameters.AddWithValue("@typeFilter", filterType);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add((ReadNode(reader), 1.0));
            }
        }

        return await AttachEdgesAsync(connection, hits, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Batch-loads the edges touching all hit nodes in a single query and assembles the
    /// final <see cref="SearchResult"/> list, preserving the input ordering.
    /// </summary>
    private async Task<IReadOnlyList<SearchResult>> AttachEdgesAsync(
        SqliteConnection connection,
        List<(GraphNode Node, double Score)> hits,
        CancellationToken cancellationToken)
    {
        if (hits.Count == 0)
        {
            return [];
        }

        var nodeIds = hits.Select(h => h.Node.Id).ToList();
        var edgesByNode = await GetRelatedEdgesForNodesAsync(connection, nodeIds, cancellationToken).ConfigureAwait(false);

        var results = new List<SearchResult>(hits.Count);
        foreach (var (node, score) in hits)
        {
            var related = edgesByNode.TryGetValue(node.Id, out var list)
                ? (IReadOnlyList<GraphEdge>)list
                : [];
            results.Add(new SearchResult(node, score, related));
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> GetSubgraphAsync(
        IEnumerable<string> seedNodeIds,
        int maxHops = 2,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(seedNodeIds);
        ArgumentOutOfRangeException.ThrowIfNegative(maxHops);

        var seeds = seedNodeIds.ToList();
        if (seeds.Count == 0)
        {
            return ([], []);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Build parameterized seed list for the recursive CTE.
        var seedParams = new List<string>();
        await using var command = connection.CreateCommand();

        for (var i = 0; i < seeds.Count; i++)
        {
            var paramName = $"@seed{i}";
            seedParams.Add(paramName);
            command.Parameters.AddWithValue(paramName, seeds[i]);
        }

        var seedList = string.Join(", ", seedParams);

        // Use MIN(Depth) aggregation so each node is expanded from its *shortest* path,
        // which makes the hop-limit deterministic regardless of edge iteration order.
        command.CommandText =
            $"""
            WITH RECURSIVE Subgraph(Id, Depth) AS (
                SELECT Id, 0 FROM Nodes WHERE Id IN ({seedList})
                UNION ALL
                SELECT CASE WHEN e.SourceId = s.Id THEN e.TargetId ELSE e.SourceId END, s.Depth + 1
                FROM Edges e
                JOIN Subgraph s ON (e.SourceId = s.Id OR e.TargetId = s.Id)
                WHERE s.Depth < @hops
            )
            SELECT DISTINCT n.*
            FROM Nodes n
            JOIN (SELECT Id, MIN(Depth) AS Depth FROM Subgraph GROUP BY Id) s ON n.Id = s.Id;
            """;

        command.Parameters.AddWithValue("@hops", maxHops);

        var nodes = new List<GraphNode>();

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nodes.Add(ReadNode(reader));
            }
        }

        // Collect all edges between the discovered nodes.
        var nodeIds = new HashSet<string>(nodes.Select(n => n.Id));
        var edges = await GetEdgesBetweenNodesAsync(connection, nodeIds, cancellationToken).ConfigureAwait(false);

        return (nodes, edges);
    }

    /// <inheritdoc />
    public async Task DeleteByFilePathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Collect affected node IDs before deletion for edge cleanup.
        var nodeIds = new List<string>();
        await using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT Id FROM Nodes WHERE FilePath = @path;";
            selectCmd.Parameters.AddWithValue("@path", filePath);

            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nodeIds.Add(reader.GetString(0));
            }
        }

        // Delete the nodes.
        await using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.CommandText = "DELETE FROM Nodes WHERE FilePath = @path;";
            deleteCmd.Parameters.AddWithValue("@path", filePath);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Delete orphaned edges referencing the deleted nodes.
        if (nodeIds.Count > 0)
        {
            var paramNames = new List<string>();
            await using var edgeCmd = connection.CreateCommand();

            for (var i = 0; i < nodeIds.Count; i++)
            {
                var paramName = $"@nodeId{i}";
                paramNames.Add(paramName);
                edgeCmd.Parameters.AddWithValue(paramName, nodeIds[i]);
            }

            var paramList = string.Join(", ", paramNames);
            edgeCmd.CommandText =
                $"""
                DELETE FROM Edges
                WHERE SourceId IN ({paramList})
                   OR TargetId IN ({paramList});
                """;

            await edgeCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAllIndexedFilePathsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var filePaths = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Nodes WHERE Type = 'File';";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            filePaths.Add(reader.GetString(0));
        }

        return filePaths;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);

        var ids = fileIds.Distinct().ToList();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            return hashes;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Query in chunks to stay well under SQLite's bound-parameter limit.
        const int chunkSize = 500;
        for (var offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var chunk = ids.Skip(offset).Take(chunkSize).ToList();

            await using var command = connection.CreateCommand();
            var paramNames = new List<string>(chunk.Count);
            for (var i = 0; i < chunk.Count; i++)
            {
                var paramName = $"@id{i}";
                paramNames.Add(paramName);
                command.Parameters.AddWithValue(paramName, chunk[i]);
            }

            command.CommandText =
                $"""
                SELECT Id, ContentHash
                FROM Nodes
                WHERE Type = 'File' AND ContentHash IS NOT NULL AND Id IN ({string.Join(", ", paramNames)});
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hashes[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return hashes;
    }

    /// <inheritdoc />
    public async Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var totalNodes = await ExecuteScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM Nodes;", cancellationToken).ConfigureAwait(false);

        var totalEdges = await ExecuteScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM Edges;", cancellationToken).ConfigureAwait(false);

        var nodesByType = await GetGroupedCountsAsync(connection,
            "SELECT Type, COUNT(*) FROM Nodes GROUP BY Type;", cancellationToken).ConfigureAwait(false);

        var edgesByRelation = await GetGroupedCountsAsync(connection,
            "SELECT RelationType, COUNT(*) FROM Edges GROUP BY RelationType;", cancellationToken).ConfigureAwait(false);

        return new GraphStatistics(
            totalNodes,
            totalEdges,
            nodesByType,
            edgesByRelation);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetAllNodesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var nodes = new List<GraphNode>();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Nodes;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(ReadNode(reader));
        }

        return nodes;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNodesByTypesAsync(IEnumerable<string> types, CancellationToken cancellationToken = default)
    {
        var nodes = new List<GraphNode>();
        var typeList = types?.ToList();

        if (typeList == null || typeList.Count == 0)
        {
            return nodes;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        var paramNames = new List<string>();
        for (int i = 0; i < typeList.Count; i++)
        {
            var pName = $"@type{i}";
            paramNames.Add(pName);
            command.Parameters.AddWithValue(pName, typeList[i]);
        }

        var inClause = string.Join(", ", paramNames);
        command.CommandText = $"SELECT * FROM Nodes WHERE Type IN ({inClause});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(ReadNode(reader));
        }

        return nodes;
    }

    /// <inheritdoc />
    public async Task<GraphNode?> GetNodeByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Nodes WHERE Id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReadNode(reader);
        }

        return null;
    }

    /// <inheritdoc cref="IDisposable.Dispose"/>
    public void Dispose()
    {
        _keepAlive?.Dispose();
    }

    #region Private Helpers

    /// <summary>
    /// Executes a non-query SQL statement against the given connection.
    /// </summary>
    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a scalar SQL query and returns the result cast to <typeparamref name="T"/>.
    /// </summary>
    private static async Task<T> ExecuteScalarAsync<T>(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (T)(result ?? throw new InvalidOperationException("Scalar query returned null."));
    }

    /// <summary>
    /// Executes a grouped COUNT query and returns the results as a dictionary.
    /// </summary>
    private static async Task<Dictionary<string, int>> GetGroupedCountsAsync(
        SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = reader.GetString(0);
            var count = reader.GetInt32(1);
            counts[key] = count;
        }

        return counts;
    }

    /// <summary>
    /// Retrieves, in a single query, all edges touching any of the specified node IDs,
    /// grouped per node ID. Replaces the previous per-node N+1 lookup.
    /// </summary>
    private static async Task<Dictionary<string, List<GraphEdge>>> GetRelatedEdgesForNodesAsync(
        SqliteConnection connection, IReadOnlyList<string> nodeIds, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, List<GraphEdge>>();
        if (nodeIds.Count == 0)
        {
            return map;
        }

        var idSet = new HashSet<string>(nodeIds);

        var paramNames = new List<string>(nodeIds.Count);
        await using var command = connection.CreateCommand();
        for (var i = 0; i < nodeIds.Count; i++)
        {
            var paramName = $"@id{i}";
            paramNames.Add(paramName);
            command.Parameters.AddWithValue(paramName, nodeIds[i]);
        }

        var paramList = string.Join(", ", paramNames);
        command.CommandText =
            $"""
            SELECT SourceId, TargetId, RelationType
            FROM Edges
            WHERE SourceId IN ({paramList})
               OR TargetId IN ({paramList});
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var edge = ReadEdge(reader);

            // An edge is "related" to both endpoints that are part of the queried set.
            if (idSet.Contains(edge.SourceId))
            {
                AddToMap(map, edge.SourceId, edge);
            }
            if (edge.TargetId != edge.SourceId && idSet.Contains(edge.TargetId))
            {
                AddToMap(map, edge.TargetId, edge);
            }
        }

        return map;

        static void AddToMap(Dictionary<string, List<GraphEdge>> map, string key, GraphEdge edge)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<GraphEdge>();
                map[key] = list;
            }
            list.Add(edge);
        }
    }

    /// <summary>
    /// Retrieves all edges where both source and target are in the given set of node IDs.
    /// </summary>
    private static async Task<IReadOnlyList<GraphEdge>> GetEdgesBetweenNodesAsync(
        SqliteConnection connection, HashSet<string> nodeIds, CancellationToken cancellationToken)
    {
        if (nodeIds.Count == 0)
        {
            return [];
        }

        var paramNames = new List<string>();
        await using var command = connection.CreateCommand();

        var i = 0;
        foreach (var id in nodeIds)
        {
            var paramName = $"@id{i++}";
            paramNames.Add(paramName);
            command.Parameters.AddWithValue(paramName, id);
        }

        var paramList = string.Join(", ", paramNames);
        command.CommandText =
            $"""
            SELECT SourceId, TargetId, RelationType
            FROM Edges
            WHERE SourceId IN ({paramList})
              AND TargetId IN ({paramList});
            """;

        var edges = new List<GraphEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            edges.Add(ReadEdge(reader));
        }

        return edges;
    }

    /// <summary>
    /// Reads a <see cref="GraphNode"/> from the current row of a data reader, mapping the
    /// dedicated columns to typed properties and the JSON metadata blob back to <see cref="GraphNode.Properties"/>.
    /// </summary>
    private static GraphNode ReadNode(SqliteDataReader reader)
    {
        string? GetStringOrNull(string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        int? GetIntOrNull(string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        }

        var properties = new Dictionary<string, string>();
        var metadataJson = GetStringOrNull("Metadata");
        if (metadataJson is not null)
        {
            var extra = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
            if (extra is not null)
            {
                foreach (var kvp in extra)
                {
                    properties[kvp.Key] = kvp.Value;
                }
            }
        }

        return new GraphNode
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Type = reader.GetString(reader.GetOrdinal("Type")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
            Content = GetStringOrNull("Content") ?? string.Empty,
            FilePath = GetStringOrNull("FilePath"),
            StartLine = GetIntOrNull("StartLine"),
            EndLine = GetIntOrNull("EndLine"),
            ContentHash = GetStringOrNull("ContentHash"),
            Properties = properties
        };
    }

    /// <summary>
    /// Reads a <see cref="GraphEdge"/> from the current row of a data reader.
    /// </summary>
    private static GraphEdge ReadEdge(SqliteDataReader reader) =>
        new()
        {
            SourceId = reader.GetString(reader.GetOrdinal("SourceId")),
            TargetId = reader.GetString(reader.GetOrdinal("TargetId")),
            Relationship = reader.GetString(reader.GetOrdinal("RelationType"))
        };

    /// <summary>
    /// Serializes the node's dynamic properties into a JSON string for the Metadata column,
    /// or returns <see cref="DBNull.Value"/> when there are none.
    /// </summary>
    private static object SerializeMetadata(Dictionary<string, string> properties) =>
        properties.Count > 0 ? JsonSerializer.Serialize(properties) : DBNull.Value;

    #endregion
}
