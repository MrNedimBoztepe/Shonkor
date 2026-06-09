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
            Relationship = reader.GetString(reader.GetOrdinal("RelationType"))
        };

    /// <summary>
    /// Serializes the node's dynamic properties into a JSON string for the Metadata column,
    /// or returns <see cref="DBNull.Value"/> when there are none.
    /// </summary>
    public static object SerializeMetadata(Dictionary<string, string> properties) =>
        properties.Count > 0 ? JsonSerializer.Serialize(properties) : DBNull.Value;

    /// <summary>Packs a float embedding into a little-endian byte blob, or returns null when absent.</summary>
    public static byte[]? EmbeddingToBytes(float[]? embedding)
    {
        if (embedding == null) return null;
        var bytes = new byte[embedding.Length * 4];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
