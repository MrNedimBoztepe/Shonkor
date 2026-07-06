// Licensed to Shonkor under the MIT License.

using System.Text.Json.Nodes;
using Shonkor.Web.Services;

namespace Shonkor.Tests;

/// <summary>Tests the pure JSON-merge that backs the dashboard's /api/settings writes.</summary>
public class SettingsStoreTests
{
    [Fact]
    public void PartialUpdate_SetsProvidedKeys_AsCorrectJsonTypes()
    {
        var json = SettingsStore.BuildLocalJson(null, new AiSettings
        {
            EmbeddingModel = "nomic-embed-text",
            SemanticCSharp = false,
            EnrichmentMaxParallelism = 2
        });

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("nomic-embed-text", (string?)root["EmbeddingService"]!["OllamaModel"]);
        Assert.False((bool)root["Indexing"]!["SemanticCSharp"]!);          // JSON bool, not string
        Assert.Equal(2, (int)root["SemanticEnrichment"]!["MaxParallelism"]!); // JSON number
        // Untouched fields are absent (partial update).
        Assert.Null(root["Features"]);
    }

    [Fact]
    public void Merge_PreservesExistingUnrelatedKeys_AndOverwritesProvidedOnes()
    {
        var existing = """
        { "SemanticAnalyzer": { "OllamaUrl": "http://old:1", "OllamaModel": "keep-me" },
          "Features": { "StreamingAnswers": true } }
        """;

        var json = SettingsStore.BuildLocalJson(existing, new AiSettings { SemanticAnalyzerUrl = "http://new:2" });
        var root = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("http://new:2", (string?)root["SemanticAnalyzer"]!["OllamaUrl"]);   // overwritten
        Assert.Equal("keep-me", (string?)root["SemanticAnalyzer"]!["OllamaModel"]);       // preserved sibling
        Assert.True((bool)root["Features"]!["StreamingAnswers"]!);                        // preserved section
    }

    [Fact]
    public void MalformedExistingJson_IsReplaced_NotThrown()
    {
        var json = SettingsStore.BuildLocalJson("{ not valid json", new AiSettings { EmbeddingSource = "code" });
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("code", (string?)root["Embedding"]!["Source"]);
    }
}
