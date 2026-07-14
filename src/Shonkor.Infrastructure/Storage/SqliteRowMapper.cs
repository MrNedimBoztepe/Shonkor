// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Shonkor.Core.Models;

namespace Shonkor.Infrastructure.Storage;

/// <summary>
/// Maps between <see cref="SqliteDataReader"/> rows and the domain models (<see cref="GraphNode"/>,
/// <see cref="GraphEdge"/>), including the JSON metadata blob and the float-embedding BLOB packing.
/// </summary>
internal static class SqliteRowMapper
{
    /// <summary>
    /// Reads a <see cref="GraphNode"/> from the current row, mapping dedicated columns to typed
    /// properties and the JSON metadata blob back into <see cref="GraphNode.Properties"/>.
    /// </summary>
    public static GraphNode ReadNode(SqliteDataReader reader)
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

        // Summary may not be present in all queries (e.g. GetNodesPendingSemanticAnalysisAsync).
        string? summary = null;
        try { summary = GetStringOrNull("Summary"); } catch { /* column not in result set */ }

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
            Summary = summary,
            Properties = properties
        };
    }

    /// <summary>Reads a <see cref="GraphEdge"/> from the current row of a data reader.</summary>
    public static GraphEdge ReadEdge(SqliteDataReader reader) =>
        new()
        {
            SourceId = reader.GetString(reader.GetOrdinal("SourceId")),
            TargetId = reader.GetString(reader.GetOrdinal("TargetId")),
            Relationship = reader.GetString(reader.GetOrdinal("RelationType")),
            Provenance = ReadProvenance(reader),
            Properties = ReadEdgeProperties(reader)
        };

    /// <summary>
    /// Materializes the edge's JSON properties (TICKET-207) tolerantly: a query that doesn't select the
    /// Properties column, a NULL, or unparseable JSON yields an empty dictionary.
    /// </summary>
    private static Dictionary<string, string> ReadEdgeProperties(SqliteDataReader reader)
    {
        int ordinal;
        try { ordinal = reader.GetOrdinal("Properties"); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return new Dictionary<string, string>(); // query didn't select the column
        }
        if (reader.IsDBNull(ordinal)) return new Dictionary<string, string>();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(ordinal)) ?? new(); }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }

    /// <summary>
    /// Reads the edge's provenance tier tolerantly: a query that does not select the Provenance column
    /// (or a legacy NULL) maps to <see cref="Provenance.Extracted"/>, preserving the prior semantics.
    /// </summary>
    private static Provenance ReadProvenance(SqliteDataReader reader)
    {
        int ordinal;
        try { ordinal = reader.GetOrdinal("Provenance"); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return Provenance.Extracted;
        }
        return reader.IsDBNull(ordinal) ? Provenance.Extracted : (Provenance)reader.GetInt32(ordinal);
    }

    /// <summary>
    /// Serializes the node's dynamic properties into a JSON string for the Metadata column,
    /// or returns <see cref="DBNull.Value"/> when there are none.
    /// </summary>
    public static object SerializeMetadata(Dictionary<string, string> properties) =>
        properties.Count > 0 ? JsonSerializer.Serialize(properties) : DBNull.Value;

    /// <summary>
    /// Packs a float embedding into a native-endian byte blob, or returns null when absent. The vector is
    /// L2-normalized first (TICKET-215): every stored embedding is unit-length, so the semantic search hot
    /// path can score with a dot product instead of recomputing each vector's magnitude for cosine. This is
    /// the single storage write choke point, so normalization holds regardless of which caller wrote it.
    /// <para>
    /// <see cref="Buffer.BlockCopy(Array,int,Array,int,int)"/> writes the float bytes in the host's native
    /// order — the read side (<see cref="VectorMath.AsFloats"/>) reinterprets them the same way, so a
    /// round-trip is self-consistent on any single host but the on-disk blob is NOT portable across hosts of
    /// opposite endianness. Shonkor supports little-endian platforms only; see #129 for the read-side guard.
    /// </para>
    /// </summary>
    public static byte[]? EmbeddingToBytes(float[]? embedding)
    {
        if (embedding == null) return null;
        // Normalize a COPY so a caller reusing the array (e.g. to store elsewhere) isn't mutated underneath.
        var normalized = VectorMath.NormalizedCopy(embedding);
        var bytes = new byte[normalized.Length * 4];
        Buffer.BlockCopy(normalized, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
