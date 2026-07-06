using System.Text.Json;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
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
        await SqliteSchema.InitializeAsync(connection, _isMemory, cancellationToken).ConfigureAwait(false);
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
            INSERT OR REPLACE INTO Nodes (Id, Type, Name, Content, Metadata, FilePath, StartLine, EndLine, ContentHash, Summary, NeedsSemanticAnalysis, Embedding)
            VALUES (@Id, @Type, @Name, @Content, @Metadata, @FilePath, @StartLine, @EndLine, @ContentHash, @Summary, @NeedsSemanticAnalysis, @Embedding);
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
        var pSummary = command.Parameters.Add("@Summary", SqliteType.Text);
        var pNeedsAnalysis = command.Parameters.Add("@NeedsSemanticAnalysis", SqliteType.Integer);
        var pEmbedding = command.Parameters.Add("@Embedding", SqliteType.Blob);

        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

        // Reverse-index maintenance (drift Layer 2): re-sync each node's referencedTypes rows on upsert.
        await using var refDelete = connection.CreateCommand();
        refDelete.CommandText = "DELETE FROM TypeReferences WHERE NodeId = @NodeId;";
        var rdNodeId = refDelete.Parameters.Add("@NodeId", SqliteType.Text);
        await refDelete.PrepareAsync(cancellationToken).ConfigureAwait(false);

        await using var refInsert = connection.CreateCommand();
        refInsert.CommandText = "INSERT OR IGNORE INTO TypeReferences (TypeName, NodeId, FilePath) VALUES (@TypeName, @NodeId, @FilePath);";
        var riTypeName = refInsert.Parameters.Add("@TypeName", SqliteType.Text);
        var riNodeId = refInsert.Parameters.Add("@NodeId", SqliteType.Text);
        var riFilePath = refInsert.Parameters.Add("@FilePath", SqliteType.Text);
        await refInsert.PrepareAsync(cancellationToken).ConfigureAwait(false);

        foreach (var node in nodes)
        {
            pId.Value = node.Id;
            pType.Value = node.Type;
            pName.Value = node.Name;
            pContent.Value = string.IsNullOrEmpty(node.Content) ? DBNull.Value : node.Content;
            pMetadata.Value = SqliteRowMapper.SerializeMetadata(node.Properties);
            pFilePath.Value = (object?)node.FilePath ?? DBNull.Value;
            pStartLine.Value = node.StartLine.HasValue ? node.StartLine.Value : DBNull.Value;
            pEndLine.Value = node.EndLine.HasValue ? node.EndLine.Value : DBNull.Value;
            pContentHash.Value = (object?)node.ContentHash ?? DBNull.Value;
            pSummary.Value = (object?)node.Summary ?? DBNull.Value;
            pNeedsAnalysis.Value = 1;
            pEmbedding.Value = (object?)SqliteRowMapper.EmbeddingToBytes(node.Embedding) ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Refresh the reverse index for this node: drop its old rows, re-add from referencedTypes.
            rdNodeId.Value = node.Id;
            await refDelete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (node.Properties.TryGetValue("referencedTypes", out var refCsv) && !string.IsNullOrWhiteSpace(refCsv))
            {
                riNodeId.Value = node.Id;
                riFilePath.Value = (object?)node.FilePath ?? DBNull.Value;
                foreach (var typeName in refCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    riTypeName.Value = typeName;
                    await refInsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
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
            INSERT OR IGNORE INTO Edges (SourceId, TargetId, RelationType, Provenance)
            VALUES (@SourceId, @TargetId, @RelationType, @Provenance);
            """;

        var pSource = command.Parameters.Add("@SourceId", SqliteType.Text);
        var pTarget = command.Parameters.Add("@TargetId", SqliteType.Text);
        var pRelation = command.Parameters.Add("@RelationType", SqliteType.Text);
        var pProvenance = command.Parameters.Add("@Provenance", SqliteType.Integer);

        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

        foreach (var edge in edges)
        {
            pSource.Value = edge.SourceId;
            pTarget.Value = edge.TargetId;
            pRelation.Value = edge.Relationship;
            pProvenance.Value = (int)edge.Provenance;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int maxResults = 20,
        int offset = 0,
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
                SELECT Id, Type, Name, Content, Metadata, FilePath, StartLine, EndLine, ContentHash, Summary,
                       1.0 AS Score
                FROM Nodes
                {typeClause}
                LIMIT @limit OFFSET @offset;
                """;

            allCmd.Parameters.AddWithValue("@limit", maxResults);
            allCmd.Parameters.AddWithValue("@offset", offset);
            if (!string.IsNullOrWhiteSpace(filterType))
            {
                allCmd.Parameters.AddWithValue("@typeFilter", filterType);
            }

            await using var reader = await allCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add((SqliteRowMapper.ReadNode(reader), 1.0));
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
                       n.StartLine, n.EndLine, n.ContentHash, n.Summary,
                       bm25(NodesFts) AS Score
                FROM NodesFts fts
                JOIN Nodes n ON fts.Id = n.Id
                WHERE NodesFts MATCH @query {typeClause}
                ORDER BY Score
                LIMIT @limit OFFSET @offset;
                """;

            command.Parameters.AddWithValue("@query", trimmedQuery);
            command.Parameters.AddWithValue("@limit", maxResults);
            command.Parameters.AddWithValue("@offset", offset);
            if (!string.IsNullOrWhiteSpace(filterType))
            {
                command.Parameters.AddWithValue("@typeFilter", filterType);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var node = SqliteRowMapper.ReadNode(reader);
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
                SELECT Id, Type, Name, Content, Metadata, FilePath, StartLine, EndLine, ContentHash, Summary,
                       1.0 AS Score
                FROM Nodes
                WHERE (Name LIKE @likeQuery
                   OR Content LIKE @likeQuery
                   OR FilePath LIKE @likeQuery)
                   {typeClause}
                LIMIT @limit OFFSET @offset;
                """;

            command.Parameters.AddWithValue("@likeQuery", $"%{trimmedQuery}%");
            command.Parameters.AddWithValue("@limit", maxResults);
            command.Parameters.AddWithValue("@offset", offset);
            if (!string.IsNullOrWhiteSpace(filterType))
            {
                command.Parameters.AddWithValue("@typeFilter", filterType);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hits.Add((SqliteRowMapper.ReadNode(reader), 1.0));
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

    public async Task<IReadOnlyList<SearchResult>> SearchSemanticAsync(float[] queryEmbedding, int maxResults = 10, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Maintain a bounded max-heap of the top-K hits so we never keep more than
        // maxResults * OVERSCAN embeddings in memory at once, rather than loading every
        // embedding in the database before sorting.
        // For a 768-float embedding (nomic-embed-text) each blob is ~3 KB; with an
        // overscan factor of 4 we keep at most 4*maxResults scores in the heap at any time
        // instead of potentially thousands of full blobs.
        const int overscanFactor = 4;
        var capacity = maxResults * overscanFactor;

        // SortedList keyed by score (ascending) acts as a min-heap of fixed capacity.
        // When full, new items only enter if their score beats the current minimum.
        var heap = new SortedList<double, string>(capacity + 1, Comparer<double>.Default);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Embedding FROM Nodes WHERE Embedding IS NOT NULL;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var blob = reader.GetValue(1) as byte[];

            if (blob == null || blob.Length == 0) continue;

            var floatCount = blob.Length / 4;
            if (floatCount != queryEmbedding.Length) continue; // dimension mismatch — skip

            var nodeEmbedding = new float[floatCount];
            Buffer.BlockCopy(blob, 0, nodeEmbedding, 0, blob.Length);

            var score = (double)System.Numerics.Tensors.TensorPrimitives.CosineSimilarity(queryEmbedding, nodeEmbedding);

            // Only keep if it beats the current worst in the heap, or heap is not yet full.
            if (heap.Count < capacity || score > heap.Keys[0])
            {
                // SortedList requires unique keys; add a tiny tie-breaking suffix to the score.
                var key = score;
                while (heap.ContainsKey(key)) key = Math.BitIncrement(key);
                heap.Add(key, id);

                if (heap.Count > capacity)
                {
                    heap.RemoveAt(0); // evict the lowest-score entry
                }
            }
        }

        if (heap.Count == 0)
        {
            return [];
        }

        // Take the true top-maxResults (highest scores = last entries in ascending SortedList).
        var topHits = heap
            .OrderByDescending(kv => kv.Key)
            .Take(maxResults)
            .Select(kv => (Id: kv.Value, Score: kv.Key))
            .ToList();

        var ids = topHits.Select(h => h.Id).ToList();

        // Fetch full nodes for the final winners only.
        var (nodes, _) = await GetSubgraphAsync(ids, 0, cancellationToken).ConfigureAwait(false);
        var nodesById = nodes.ToDictionary(n => n.Id);

        var finalHits = new List<(GraphNode Node, double Score)>(topHits.Count);
        foreach (var hit in topHits)
        {
            if (nodesById.TryGetValue(hit.Id, out var node))
            {
                finalHits.Add((node, hit.Score));
            }
        }

        return await AttachEdgesAsync(connection, finalHits, cancellationToken).ConfigureAwait(false);
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
                UNION
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
                nodes.Add(SqliteRowMapper.ReadNode(reader));
            }
        }

        // Collect all edges between the discovered nodes.
        var nodeIds = new HashSet<string>(nodes.Select(n => n.Id));
        var edges = await GetEdgesBetweenNodesAsync(connection, nodeIds, cancellationToken).ConfigureAwait(false);

        return (nodes, edges);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<GraphEdge> Edges, IReadOnlyDictionary<string, GraphNode> Neighbours)> GetIncidentEdgesAsync(
        string nodeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 1. Just the node's own edges, in either direction — no neighbourhood materialization.
        var edges = new List<GraphEdge>();
        await using (var edgeCommand = connection.CreateCommand())
        {
            edgeCommand.CommandText =
                """
                SELECT SourceId, TargetId, RelationType, Provenance
                FROM Edges
                WHERE SourceId = @id OR TargetId = @id;
                """;
            edgeCommand.Parameters.AddWithValue("@id", nodeId);

            await using var reader = await edgeCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                edges.Add(SqliteRowMapper.ReadEdge(reader));
            }
        }

        // 2. Fetch the endpoint nodes (both ends, incl. the node itself) in one IN query so the caller
        //    can render names/summaries without N round-trips.
        var endpointIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            endpointIds.Add(e.SourceId);
            endpointIds.Add(e.TargetId);
        }

        var neighbours = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        if (endpointIds.Count > 0)
        {
            await using var nodeCommand = connection.CreateCommand();
            var paramNames = new List<string>(endpointIds.Count);
            var i = 0;
            foreach (var id in endpointIds)
            {
                var paramName = $"@n{i++}";
                paramNames.Add(paramName);
                nodeCommand.Parameters.AddWithValue(paramName, id);
            }

            nodeCommand.CommandText = $"SELECT * FROM Nodes WHERE Id IN ({string.Join(", ", paramNames)});";

            await using var reader = await nodeCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var node = SqliteRowMapper.ReadNode(reader);
                neighbours[node.Id] = node;
            }
        }

        return (edges, neighbours);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphEdge>> GetEdgesByRelationshipAsync(string relationship, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SourceId, TargetId, RelationType, Provenance
            FROM Edges
            WHERE RelationType = @rel;
            """;
        command.Parameters.AddWithValue("@rel", relationship);

        var edges = new List<GraphEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            edges.Add(SqliteRowMapper.ReadEdge(reader));
        }
        return edges;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphEdge>> GetAllEdgesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceId, TargetId, RelationType, Provenance FROM Edges;";

        var edges = new List<GraphEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            edges.Add(SqliteRowMapper.ReadEdge(reader));
        }
        return edges;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNodesWithEmbeddingsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, Type, Name, Content, Metadata, FilePath, StartLine, EndLine, ContentHash, Summary, Embedding
            FROM Nodes
            WHERE Embedding IS NOT NULL
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", limit <= 0 ? int.MaxValue : limit);

        var nodes = new List<GraphNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var embeddingOrdinal = reader.GetOrdinal("Embedding");
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var node = SqliteRowMapper.ReadNode(reader);
            if (!reader.IsDBNull(embeddingOrdinal))
            {
                var blob = (byte[])reader.GetValue(embeddingOrdinal);
                var floats = new float[blob.Length / 4];
                Buffer.BlockCopy(blob, 0, floats, 0, floats.Length * 4);
                node.Embedding = floats;
            }
            nodes.Add(node);
        }
        return nodes;
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

        // Drop the file's reverse-index rows (drift Layer 2).
        await using (var refCmd = connection.CreateCommand())
        {
            refCmd.CommandText = "DELETE FROM TypeReferences WHERE FilePath = @path;";
            refCmd.Parameters.AddWithValue("@path", filePath);
            await refCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Conservative cap on host parameters per statement (SQLite's default limit is 999).</summary>
    private const int MaxSqlParameters = 900;

    /// <inheritdoc />
    public async Task DeleteByFilePathsAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var paths = filePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0) return;

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        static string AddParams(SqliteCommand cmd, IReadOnlyList<string> values, string prefix)
        {
            var names = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                names[i] = $"{prefix}{i}";
                cmd.Parameters.AddWithValue(names[i], values[i]);
            }
            return string.Join(", ", names);
        }

        // 1. Collect every node id belonging to any of the paths (so orphaned edges can be cleaned up).
        var nodeIds = new List<string>();
        foreach (var chunk in paths.Chunk(MaxSqlParameters))
        {
            await using var selectCmd = connection.CreateCommand();
            var paramList = AddParams(selectCmd, chunk, "@p");
            selectCmd.CommandText = $"SELECT Id FROM Nodes WHERE FilePath IN ({paramList});";
            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nodeIds.Add(reader.GetString(0));
            }
        }

        // 2. Delete the nodes by file path (and their reverse-index rows — drift Layer 2).
        foreach (var chunk in paths.Chunk(MaxSqlParameters))
        {
            await using var deleteCmd = connection.CreateCommand();
            var paramList = AddParams(deleteCmd, chunk, "@p");
            deleteCmd.CommandText = $"DELETE FROM Nodes WHERE FilePath IN ({paramList});";
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var refCmd = connection.CreateCommand();
            var refList = AddParams(refCmd, chunk, "@r");
            refCmd.CommandText = $"DELETE FROM TypeReferences WHERE FilePath IN ({refList});";
            await refCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // 3. Delete orphaned edges. The id list is referenced twice (SourceId/TargetId); named
        //    parameters can be reused, so the distinct-parameter count stays at the chunk size.
        foreach (var chunk in nodeIds.Chunk(MaxSqlParameters))
        {
            await using var edgeCmd = connection.CreateCommand();
            var paramList = AddParams(edgeCmd, chunk, "@n");
            edgeCmd.CommandText = $"DELETE FROM Edges WHERE SourceId IN ({paramList}) OR TargetId IN ({paramList});";
            await edgeCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearFileForReindexAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Collect the file's node ids so we can delete only edges that ORIGINATE from them.
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

        await using (var deleteNodes = connection.CreateCommand())
        {
            deleteNodes.CommandText = "DELETE FROM Nodes WHERE FilePath = @path;";
            deleteNodes.Parameters.AddWithValue("@path", filePath);
            await deleteNodes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Drop the file's reverse-index rows; re-parse + upsert repopulates them (drift Layer 2).
        await using (var refCmd = connection.CreateCommand())
        {
            refCmd.CommandText = "DELETE FROM TypeReferences WHERE FilePath = @path;";
            refCmd.Parameters.AddWithValue("@path", filePath);
            await refCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Delete only OUTGOING/internal edges (SourceId in the file). Edges that point INTO the file from
        // other files (incoming references) are preserved: re-parsing recreates the file's symbols with
        // the same ids, so those references stay valid across an edit — instead of being dropped until the
        // next whole-graph cross-tech relink.
        foreach (var chunk in nodeIds.Chunk(MaxSqlParameters))
        {
            await using var edgeCmd = connection.CreateCommand();
            var paramNames = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var p = $"@s{i}";
                paramNames.Add(p);
                edgeCmd.Parameters.AddWithValue(p, chunk[i]);
            }
            edgeCmd.CommandText = $"DELETE FROM Edges WHERE SourceId IN ({string.Join(", ", paramNames)});";
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

        var schemeVersion = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        return new GraphStatistics(
            totalNodes,
            totalEdges,
            nodesByType,
            edgesByRelation,
            schemeVersion,
            CsharpNodeId.SchemeVersion);
    }

    /// <inheritdoc />
    public async Task<int> GetNodeIdSchemeVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetNodeIdSchemeVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // PRAGMA user_version does not accept parameters; version is an int, so inlining is injection-safe.
        command.CommandText = $"PRAGMA user_version = {version};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? 0 : Convert.ToInt32(result);
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
            nodes.Add(SqliteRowMapper.ReadNode(reader));
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
            nodes.Add(SqliteRowMapper.ReadNode(reader));
        }

        return nodes;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphNode>> GetNodesByFilePathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Nodes WHERE FilePath = @path;";
        command.Parameters.AddWithValue("@path", filePath);

        var nodes = new List<GraphNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(SqliteRowMapper.ReadNode(reader));
        }
        return nodes;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<GraphNode>>> GetDefinitionsByNamesAsync(IEnumerable<string> names, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var nameList = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, List<GraphNode>>(StringComparer.Ordinal);
        if (nameList.Count == 0)
        {
            return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<GraphNode>)kv.Value, StringComparer.Ordinal);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Chunk to stay under SQLite's bound-parameter limit (definition types + names share the clause).
        const int chunkSize = 400;
        for (var offset = 0; offset < nameList.Count; offset += chunkSize)
        {
            var chunk = nameList.Skip(offset).Take(chunkSize).ToList();

            await using var command = connection.CreateCommand();
            var paramNames = new List<string>(chunk.Count);
            for (var i = 0; i < chunk.Count; i++)
            {
                var p = $"@n{i}";
                paramNames.Add(p);
                command.Parameters.AddWithValue(p, chunk[i]);
            }

            command.CommandText =
                $"SELECT * FROM Nodes WHERE Type IN ('Class','Interface','Record','Struct','Enum') " +
                $"AND Name IN ({string.Join(", ", paramNames)});";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var node = SqliteRowMapper.ReadNode(reader);
                if (!result.TryGetValue(node.Name, out var list))
                {
                    list = new List<GraphNode>();
                    result[node.Name] = list;
                }
                list.Add(node);
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<GraphNode>)kv.Value, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetReferencingFilePathsAsync(IEnumerable<string> typeNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeNames);

        var names = typeNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (names.Count == 0) return paths.ToList();

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        const int chunkSize = 400;
        for (var offset = 0; offset < names.Count; offset += chunkSize)
        {
            var chunk = names.Skip(offset).Take(chunkSize).ToList();

            await using var command = connection.CreateCommand();
            var paramNames = new List<string>(chunk.Count);
            for (var i = 0; i < chunk.Count; i++)
            {
                var p = $"@n{i}";
                paramNames.Add(p);
                command.Parameters.AddWithValue(p, chunk[i]);
            }
            command.CommandText =
                $"SELECT DISTINCT FilePath FROM TypeReferences WHERE FilePath IS NOT NULL AND TypeName IN ({string.Join(", ", paramNames)});";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                paths.Add(reader.GetString(0));
            }
        }

        return paths.ToList();
    }

    /// <inheritdoc />
    public async Task DeleteOutgoingEdgesByFilePathAsync(string filePath, string relationship, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationship);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // The file's node ids are the edge sources. Delete matching outgoing edges in chunks.
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
        if (nodeIds.Count == 0) return;

        foreach (var chunk in nodeIds.Chunk(MaxSqlParameters))
        {
            await using var edgeCmd = connection.CreateCommand();
            var paramNames = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                var p = $"@s{i}";
                paramNames.Add(p);
                edgeCmd.Parameters.AddWithValue(p, chunk[i]);
            }
            edgeCmd.Parameters.AddWithValue("@rel", relationship);
            edgeCmd.CommandText = $"DELETE FROM Edges WHERE RelationType = @rel AND SourceId IN ({string.Join(", ", paramNames)});";
            await edgeCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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
            return SqliteRowMapper.ReadNode(reader);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task ReplaceDiagnosticsAsync(string source, IEnumerable<GraphDiagnostic> diagnostics, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(diagnostics);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM Diagnostics WHERE Source = @Source;";
            delete.Parameters.AddWithValue("@Source", source);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO Diagnostics (Source, Code, Severity, Message, NodeId, FilePath) VALUES (@Source, @Code, @Severity, @Message, @NodeId, @FilePath);";
        var pSource = insert.Parameters.Add("@Source", SqliteType.Text);
        var pCode = insert.Parameters.Add("@Code", SqliteType.Text);
        var pSeverity = insert.Parameters.Add("@Severity", SqliteType.Integer);
        var pMessage = insert.Parameters.Add("@Message", SqliteType.Text);
        var pNodeId = insert.Parameters.Add("@NodeId", SqliteType.Text);
        var pFilePath = insert.Parameters.Add("@FilePath", SqliteType.Text);
        await insert.PrepareAsync(cancellationToken).ConfigureAwait(false);

        foreach (var d in diagnostics)
        {
            pSource.Value = source;
            pCode.Value = d.Code;
            pSeverity.Value = (int)d.Severity;
            pMessage.Value = d.Message;
            pNodeId.Value = (object?)d.NodeId ?? DBNull.Value;
            pFilePath.Value = (object?)d.FilePath ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GraphDiagnostic>> GetDiagnosticsAsync(DiagnosticSeverity? minSeverity = null, string? code = null, int maxResults = 200, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Code, Severity, Message, NodeId, FilePath
            FROM Diagnostics
            WHERE (@MinSeverity IS NULL OR Severity >= @MinSeverity)
              AND (@Code IS NULL OR Code = @Code)
            ORDER BY Severity DESC
            LIMIT @Max;
            """;
        command.Parameters.AddWithValue("@MinSeverity", minSeverity is null ? DBNull.Value : (int)minSeverity.Value);
        command.Parameters.AddWithValue("@Code", (object?)code ?? DBNull.Value);
        command.Parameters.AddWithValue("@Max", maxResults);

        var result = new List<GraphDiagnostic>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new GraphDiagnostic(
                reader.GetString(0),
                (DiagnosticSeverity)reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return result;
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
            SELECT SourceId, TargetId, RelationType, Provenance
            FROM Edges
            WHERE SourceId IN ({paramList})
               OR TargetId IN ({paramList});
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var edge = SqliteRowMapper.ReadEdge(reader);

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
            SELECT SourceId, TargetId, RelationType, Provenance
            FROM Edges
            WHERE SourceId IN ({paramList})
              AND TargetId IN ({paramList});
            """;

        var edges = new List<GraphEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            edges.Add(SqliteRowMapper.ReadEdge(reader));
        }

        return edges;
    }


    public async Task<IReadOnlyList<GraphNode>> GetNodesPendingSemanticAnalysisAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT Id, Type, Name, Content, Metadata, FilePath, StartLine, EndLine, ContentHash
            FROM Nodes 
            WHERE NeedsSemanticAnalysis = 1 AND Type != 'Concept'
            LIMIT @BatchSize;";
        command.Parameters.AddWithValue("@BatchSize", batchSize);

        var nodes = new List<GraphNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            nodes.Add(SqliteRowMapper.ReadNode(reader));
        }

        return nodes;
    }

    public async Task UpdateNodeSemanticDataAsync(string nodeId, SemanticAnalysisResult result, float[]? embedding = null, string? embeddingModel = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // The concept nodes/edges AND the node's summary update are written in a SINGLE transaction.
        // Previously they were two separate transactions, so a crash in between could leave Concept
        // nodes/edges persisted while the node stayed flagged as pending — an inconsistent partial state.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // 1. Fetch existing properties (to merge in the new benchmark metrics).
        string? existingPropsJson;
        await using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.Transaction = transaction;
            selectCmd.CommandText = "SELECT Metadata FROM Nodes WHERE Id = @Id;";
            selectCmd.Parameters.AddWithValue("@Id", nodeId);
            existingPropsJson = await selectCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        // 2. Promote extracted concepts to first-class Concept nodes + RELATES_TO edges.
        if (result.ExtractedConcepts.Count > 0)
        {
            await using var conceptCmd = connection.CreateCommand();
            conceptCmd.Transaction = transaction;
            conceptCmd.CommandText =
                """
                INSERT OR IGNORE INTO Nodes (Id, Type, Name, Content, Summary, NeedsSemanticAnalysis)
                VALUES (@Id, 'Concept', @Name, @Name, '', 0);
                """;
            var pConceptId = conceptCmd.Parameters.Add("@Id", SqliteType.Text);
            var pConceptName = conceptCmd.Parameters.Add("@Name", SqliteType.Text);
            await conceptCmd.PrepareAsync(cancellationToken).ConfigureAwait(false);

            await using var edgeCmd = connection.CreateCommand();
            edgeCmd.Transaction = transaction;
            edgeCmd.CommandText =
                """
                INSERT OR IGNORE INTO Edges (SourceId, TargetId, RelationType)
                VALUES (@SourceId, @TargetId, 'RELATES_TO');
                """;
            var pSourceId = edgeCmd.Parameters.Add("@SourceId", SqliteType.Text);
            var pTargetId = edgeCmd.Parameters.Add("@TargetId", SqliteType.Text);
            await edgeCmd.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var concept in result.ExtractedConcepts)
            {
                var conceptId = $"concept_{concept.ToLowerInvariant()}";

                pConceptId.Value = conceptId;
                pConceptName.Value = concept;
                await conceptCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                pSourceId.Value = nodeId;
                pTargetId.Value = conceptId;
                await edgeCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // 3. Merge benchmark metrics into the node's metadata and write the summary/embedding.
        var properties = string.IsNullOrEmpty(existingPropsJson)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(existingPropsJson) ?? new Dictionary<string, string>();

        properties["PromptTokens"] = result.PromptTokens.ToString();
        properties["CompletionTokens"] = result.CompletionTokens.ToString();
        properties["LatencyMs"] = result.LatencyMs.ToString();

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE Nodes
                SET Summary = @Summary, Metadata = @Metadata, NeedsSemanticAnalysis = 0,
                    Embedding = @Embedding, EmbeddingDim = @EmbeddingDim, EmbeddingModel = @EmbeddingModel
                WHERE Id = @Id;
                """;
            command.Parameters.AddWithValue("@Summary", (object?)result.Summary ?? DBNull.Value);
            command.Parameters.AddWithValue("@Metadata", JsonSerializer.Serialize(properties));
            command.Parameters.AddWithValue("@Embedding", (object?)SqliteRowMapper.EmbeddingToBytes(embedding) ?? DBNull.Value);
            // TICKET-006: stamp the embedding's dimension AND model so a later change (incl. a same-dim
            // model swap) is detectable by MarkStaleEmbeddingsForReembedAsync.
            command.Parameters.AddWithValue("@EmbeddingDim", embedding is { Length: > 0 } ? embedding.Length : (object)DBNull.Value);
            command.Parameters.AddWithValue("@EmbeddingModel", embedding is { Length: > 0 } && embeddingModel is not null ? embeddingModel : (object)DBNull.Value);
            command.Parameters.AddWithValue("@Id", nodeId);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> MarkStaleEmbeddingsForReembedAsync(int expectedDim, string? expectedModel = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedDim);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Clear the vector and re-flag the node so the enrichment worker re-embeds it under the current model.
        // Stale = a KNOWN dimension that differs, OR (when a model is given) a KNOWN model that differs
        // (catches a same-dimension model swap). Legacy vectors with NULL dim/model metadata are left as-is
        // (the query-time dimension guard still protects the search from mixing dimensions).
        command.CommandText =
            """
            UPDATE Nodes
            SET Embedding = NULL, EmbeddingDim = NULL, EmbeddingModel = NULL, NeedsSemanticAnalysis = 1
            WHERE Embedding IS NOT NULL
              AND (
                    (EmbeddingDim IS NOT NULL AND EmbeddingDim <> @ExpectedDim)
                 OR (@ExpectedModel IS NOT NULL AND EmbeddingModel IS NOT NULL AND EmbeddingModel <> @ExpectedModel)
                  );
            """;
        command.Parameters.AddWithValue("@ExpectedDim", expectedDim);
        command.Parameters.AddWithValue("@ExpectedModel", (object?)expectedModel ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateNodeEmbeddingAsync(string nodeId, float[]? embedding, string? embeddingModel = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Nodes
            SET Embedding = @Embedding, EmbeddingDim = @EmbeddingDim, EmbeddingModel = @EmbeddingModel
            WHERE Id = @Id;
            """;
        command.Parameters.AddWithValue("@Embedding", (object?)SqliteRowMapper.EmbeddingToBytes(embedding) ?? DBNull.Value);
        command.Parameters.AddWithValue("@EmbeddingDim", embedding is { Length: > 0 } ? embedding.Length : (object)DBNull.Value);
        command.Parameters.AddWithValue("@EmbeddingModel", embedding is { Length: > 0 } && embeddingModel is not null ? embeddingModel : (object)DBNull.Value);
        command.Parameters.AddWithValue("@Id", nodeId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    #endregion
}
