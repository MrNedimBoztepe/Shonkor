using System.Text.Json;
using LLMBrain.Core.Interfaces;
using LLMBrain.Core.Models;
using Microsoft.Data.Sqlite;

namespace LLMBrain.Infrastructure.Storage;

/// <summary>
/// SQLite-backed implementation of <see cref="IGraphStorageProvider"/> using FTS5
/// for full-text search and recursive CTEs for graph traversal.
/// </summary>
public sealed class SqliteGraphStorageProvider : IGraphStorageProvider, IDisposable
{
    private readonly SqliteConnection _connection;

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

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = dbPath == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        _connection = new SqliteConnection(connectionString);
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
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

        await ExecuteNonQueryAsync(
            """
            CREATE TABLE IF NOT EXISTS Edges (
                SourceId     TEXT NOT NULL,
                TargetId     TEXT NOT NULL,
                RelationType TEXT NOT NULL,
                PRIMARY KEY (SourceId, TargetId, RelationType)
            );
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            "CREATE INDEX IF NOT EXISTS idx_edges_source ON Edges(SourceId);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            "CREATE INDEX IF NOT EXISTS idx_edges_target ON Edges(TargetId);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            "CREATE INDEX IF NOT EXISTS idx_nodes_filepath ON Nodes(FilePath);",
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS NodesFts USING fts5(
                Id, Name, Content,
                content=Nodes,
                content_rowid=rowid
            );
            """,
            cancellationToken).ConfigureAwait(false);

        // Triggers to keep the FTS5 index synchronized with the Nodes table.
        await ExecuteNonQueryAsync(
            """
            CREATE TRIGGER IF NOT EXISTS nodes_ai AFTER INSERT ON Nodes BEGIN
                INSERT INTO NodesFts(rowid, Id, Name, Content)
                VALUES (new.rowid, new.Id, new.Name, new.Content);
            END;
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            """
            CREATE TRIGGER IF NOT EXISTS nodes_ad AFTER DELETE ON Nodes BEGIN
                INSERT INTO NodesFts(NodesFts, rowid, Id, Name, Content)
                VALUES ('delete', old.rowid, old.Id, old.Name, old.Content);
            END;
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            """
            CREATE TRIGGER IF NOT EXISTS nodes_au AFTER UPDATE ON Nodes BEGIN
                INSERT INTO NodesFts(NodesFts, rowid, Id, Name, Content)
                VALUES ('delete', old.rowid, old.Id, old.Name, old.Content);
                INSERT INTO NodesFts(rowid, Id, Name, Content)
                VALUES (new.rowid, new.Id, new.Name, new.Content);
            END;
            """,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertNodesAsync(IEnumerable<GraphNode> nodes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = _connection.CreateCommand();
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
            pContent.Value = GetPropertyOrDbNull(node, "Content");
            pMetadata.Value = SerializeMetadata(node.Properties);
            pFilePath.Value = GetPropertyOrDbNull(node, "FilePath");
            pStartLine.Value = GetIntPropertyOrDbNull(node, "StartLine");
            pEndLine.Value = GetIntPropertyOrDbNull(node, "EndLine");
            pContentHash.Value = GetPropertyOrDbNull(node, "ContentHash");

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edges);

        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = _connection.CreateCommand();
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        // Query the FTS5 index with BM25 scoring, then join with the Nodes table for full data.
        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT n.Id, n.Type, n.Name, n.Content, n.Metadata, n.FilePath,
                   n.StartLine, n.EndLine, n.ContentHash,
                   bm25(NodesFts) AS Score
            FROM NodesFts fts
            JOIN Nodes n ON fts.Id = n.Id
            WHERE NodesFts MATCH @query
            ORDER BY Score
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("@query", query);
        command.Parameters.AddWithValue("@limit", maxResults);

        var results = new List<SearchResult>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var node = ReadNode(reader);
            var score = reader.GetDouble(reader.GetOrdinal("Score"));

            // BM25 returns negative values (lower = better); normalize to [0, 1] range.
            var normalizedScore = Math.Abs(score);

            var relatedEdges = await GetRelatedEdgesAsync(node.Id, cancellationToken).ConfigureAwait(false);
            results.Add(new SearchResult(node, normalizedScore, relatedEdges));
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

        // Build parameterized seed list for the recursive CTE.
        var seedParams = new List<string>();
        await using var command = _connection.CreateCommand();

        for (var i = 0; i < seeds.Count; i++)
        {
            var paramName = $"@seed{i}";
            seedParams.Add(paramName);
            command.Parameters.AddWithValue(paramName, seeds[i]);
        }

        var seedList = string.Join(", ", seedParams);

        command.CommandText =
            $"""
            WITH RECURSIVE Subgraph(Id, Depth) AS (
                SELECT Id, 0 FROM Nodes WHERE Id IN ({seedList})
                UNION
                SELECT CASE WHEN e.SourceId = s.Id THEN e.TargetId ELSE e.SourceId END, s.Depth + 1
                FROM Edges e
                JOIN Subgraph s ON (e.SourceId = s.Id OR e.TargetId = s.Id)
                WHERE s.Depth < @hops
            )
            SELECT DISTINCT n.*
            FROM Nodes n
            JOIN Subgraph s ON n.Id = s.Id;
            """;

        command.Parameters.AddWithValue("@hops", maxHops);

        var nodes = new List<GraphNode>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(ReadNode(reader));
        }

        // Collect all edges between the discovered nodes.
        var nodeIds = new HashSet<string>(nodes.Select(n => n.Id));
        var edges = await GetEdgesBetweenNodesAsync(nodeIds, cancellationToken).ConfigureAwait(false);

        return (nodes, edges);
    }

    /// <inheritdoc />
    public async Task DeleteByFilePathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Collect affected node IDs before deletion for edge cleanup.
        var nodeIds = new List<string>();
        await using (var selectCmd = _connection.CreateCommand())
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
        await using (var deleteCmd = _connection.CreateCommand())
        {
            deleteCmd.CommandText = "DELETE FROM Nodes WHERE FilePath = @path;";
            deleteCmd.Parameters.AddWithValue("@path", filePath);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Delete orphaned edges referencing the deleted nodes.
        if (nodeIds.Count > 0)
        {
            var paramNames = new List<string>();
            await using var edgeCmd = _connection.CreateCommand();

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
    public async Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var totalNodes = await ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Nodes;", cancellationToken).ConfigureAwait(false);

        var totalEdges = await ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM Edges;", cancellationToken).ConfigureAwait(false);

        var nodesByType = await GetGroupedCountsAsync(
            "SELECT Type, COUNT(*) FROM Nodes GROUP BY Type;", cancellationToken).ConfigureAwait(false);

        var edgesByRelation = await GetGroupedCountsAsync(
            "SELECT RelationType, COUNT(*) FROM Edges GROUP BY RelationType;", cancellationToken).ConfigureAwait(false);

        return new GraphStatistics(
            (int)totalNodes,
            (int)totalEdges,
            nodesByType,
            edgesByRelation);
    }

    /// <inheritdoc cref="IDisposable.Dispose"/>
    public void Dispose()
    {
        _connection.Dispose();
    }

    #region Private Helpers

    /// <summary>
    /// Executes a non-query SQL statement against the connection.
    /// </summary>
    private async Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a scalar SQL query and returns the result cast to <typeparamref name="T"/>.
    /// </summary>
    private async Task<T> ExecuteScalarAsync<T>(string sql, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return (T)(result ?? throw new InvalidOperationException("Scalar query returned null."));
    }

    /// <summary>
    /// Executes a grouped COUNT query and returns the results as a dictionary.
    /// </summary>
    private async Task<Dictionary<string, int>> GetGroupedCountsAsync(
        string sql, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>();

        await using var command = _connection.CreateCommand();
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
    /// Retrieves all edges connected to the specified node.
    /// </summary>
    private async Task<IReadOnlyList<GraphEdge>> GetRelatedEdgesAsync(
        string nodeId, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT SourceId, TargetId, RelationType
            FROM Edges
            WHERE SourceId = @nodeId OR TargetId = @nodeId;
            """;

        command.Parameters.AddWithValue("@nodeId", nodeId);

        var edges = new List<GraphEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            edges.Add(ReadEdge(reader));
        }

        return edges;
    }

    /// <summary>
    /// Retrieves all edges where both source and target are in the given set of node IDs.
    /// </summary>
    private async Task<IReadOnlyList<GraphEdge>> GetEdgesBetweenNodesAsync(
        HashSet<string> nodeIds, CancellationToken cancellationToken)
    {
        if (nodeIds.Count == 0)
        {
            return [];
        }

        var paramNames = new List<string>();
        await using var command = _connection.CreateCommand();

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
    /// Reads a <see cref="GraphNode"/> from the current row of a data reader.
    /// Maps DB columns back to the <see cref="GraphNode.Properties"/> dictionary.
    /// </summary>
    private static GraphNode ReadNode(SqliteDataReader reader)
    {
        var properties = new Dictionary<string, string>();

        var content = reader.IsDBNull(reader.GetOrdinal("Content"))
            ? null
            : reader.GetString(reader.GetOrdinal("Content"));
        if (content is not null) properties["Content"] = content;

        var filePath = reader.IsDBNull(reader.GetOrdinal("FilePath"))
            ? null
            : reader.GetString(reader.GetOrdinal("FilePath"));
        if (filePath is not null) properties["FilePath"] = filePath;

        var startLine = reader.IsDBNull(reader.GetOrdinal("StartLine"))
            ? (int?)null
            : reader.GetInt32(reader.GetOrdinal("StartLine"));
        if (startLine.HasValue) properties["StartLine"] = startLine.Value.ToString();

        var endLine = reader.IsDBNull(reader.GetOrdinal("EndLine"))
            ? (int?)null
            : reader.GetInt32(reader.GetOrdinal("EndLine"));
        if (endLine.HasValue) properties["EndLine"] = endLine.Value.ToString();

        var contentHash = reader.IsDBNull(reader.GetOrdinal("ContentHash"))
            ? null
            : reader.GetString(reader.GetOrdinal("ContentHash"));
        if (contentHash is not null) properties["ContentHash"] = contentHash;

        // Merge any additional metadata stored as JSON.
        var metadataJson = reader.IsDBNull(reader.GetOrdinal("Metadata"))
            ? null
            : reader.GetString(reader.GetOrdinal("Metadata"));
        if (metadataJson is not null)
        {
            var extraProperties = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
            if (extraProperties is not null)
            {
                foreach (var kvp in extraProperties)
                {
                    properties.TryAdd(kvp.Key, kvp.Value);
                }
            }
        }

        return new GraphNode
        {
            Id = reader.GetString(reader.GetOrdinal("Id")),
            Type = reader.GetString(reader.GetOrdinal("Type")),
            Name = reader.GetString(reader.GetOrdinal("Name")),
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
    /// Retrieves a string property from the node's <see cref="GraphNode.Properties"/> dictionary,
    /// or returns <see cref="DBNull.Value"/> if the key is missing.
    /// </summary>
    private static object GetPropertyOrDbNull(GraphNode node, string key) =>
        node.Properties.TryGetValue(key, out var value) ? value : DBNull.Value;

    /// <summary>
    /// Retrieves an integer property from the node's <see cref="GraphNode.Properties"/> dictionary,
    /// or returns <see cref="DBNull.Value"/> if the key is missing or not a valid integer.
    /// </summary>
    private static object GetIntPropertyOrDbNull(GraphNode node, string key) =>
        node.Properties.TryGetValue(key, out var value) && int.TryParse(value, out var intValue)
            ? intValue
            : DBNull.Value;

    /// <summary>
    /// Serializes the node's properties (excluding well-known DB columns) into a JSON string
    /// for storage in the Metadata column.
    /// </summary>
    private static object SerializeMetadata(Dictionary<string, string> properties)
    {
        // Filter out properties that already have dedicated columns.
        var wellKnownKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "Content", "FilePath", "StartLine", "EndLine", "ContentHash"
        };

        var metadata = properties
            .Where(kvp => !wellKnownKeys.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return metadata.Count > 0
            ? JsonSerializer.Serialize(metadata)
            : DBNull.Value;
    }

    #endregion
}
