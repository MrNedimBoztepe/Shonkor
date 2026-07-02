// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Shonkor.Web.Services;

/// <summary>
/// The dashboard-editable AI/tool settings. A partial update: any <c>null</c> field is left unchanged.
/// Deliberately excludes secrets (API keys, webhook secret) — those stay in user-secrets/env.
/// </summary>
public sealed record AiSettings
{
    public string? SemanticAnalyzerUrl { get; init; }
    public string? SemanticAnalyzerModel { get; init; }
    public string? EmbeddingUrl { get; init; }
    public string? EmbeddingModel { get; init; }
    /// <summary><c>code</c> or <c>summary</c>.</summary>
    public string? EmbeddingSource { get; init; }
    public bool? SemanticCSharp { get; init; }
    public bool? StreamingAnswers { get; init; }
    public int? EnrichmentBatchSize { get; init; }
    public int? EnrichmentMaxParallelism { get; init; }
    /// <summary>Restart-only: registered as a hosted service at startup.</summary>
    public int? DriftReconcileIntervalSeconds { get; init; }
}

/// <summary>
/// Reads the effective AI settings from <see cref="IConfiguration"/> and persists edits to the writable
/// <c>appsettings.Local.json</c> overlay (loaded last, reloadOnChange). The JSON merge is a pure function
/// (<see cref="BuildLocalJson"/>) so it is unit-testable without file IO.
/// </summary>
public static class SettingsStore
{
    /// <summary>Effective current values (config value or the same defaults the services use).</summary>
    public static AiSettings Read(IConfiguration c) => new()
    {
        SemanticAnalyzerUrl = c["SemanticAnalyzer:OllamaUrl"] ?? "http://localhost:11434",
        SemanticAnalyzerModel = c["SemanticAnalyzer:OllamaModel"] ?? "qwen2.5-coder",
        EmbeddingUrl = c["EmbeddingService:OllamaUrl"] ?? "http://localhost:11434",
        EmbeddingModel = c["EmbeddingService:OllamaModel"] ?? "nomic-embed-text",
        EmbeddingSource = (c["Embedding:Source"] ?? "code").ToLowerInvariant(),
        SemanticCSharp = c.GetValue<bool?>("Indexing:SemanticCSharp") ?? true,
        StreamingAnswers = c.GetValue<bool?>("Features:StreamingAnswers") ?? true,
        EnrichmentBatchSize = c.GetValue<int?>("SemanticEnrichment:BatchSize") ?? 16,
        EnrichmentMaxParallelism = c.GetValue<int?>("SemanticEnrichment:MaxParallelism") ?? 4,
        DriftReconcileIntervalSeconds = c.GetValue<int?>("Drift:ReconcileIntervalSeconds") ?? 0
    };

    /// <summary>
    /// Merges the non-null fields of <paramref name="s"/> into <paramref name="existingJson"/> (the current
    /// appsettings.Local.json, or null/empty) and returns the new pretty-printed JSON. Pure — no IO.
    /// </summary>
    public static string BuildLocalJson(string? existingJson, AiSettings s)
    {
        JsonObject root;
        try
        {
            root = (string.IsNullOrWhiteSpace(existingJson) ? null : JsonNode.Parse(existingJson)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        if (s.SemanticAnalyzerUrl is not null) Set(root, "SemanticAnalyzer", "OllamaUrl", s.SemanticAnalyzerUrl);
        if (s.SemanticAnalyzerModel is not null) Set(root, "SemanticAnalyzer", "OllamaModel", s.SemanticAnalyzerModel);
        if (s.EmbeddingUrl is not null) Set(root, "EmbeddingService", "OllamaUrl", s.EmbeddingUrl);
        if (s.EmbeddingModel is not null) Set(root, "EmbeddingService", "OllamaModel", s.EmbeddingModel);
        if (s.EmbeddingSource is not null) Set(root, "Embedding", "Source", s.EmbeddingSource);
        if (s.SemanticCSharp is not null) Set(root, "Indexing", "SemanticCSharp", s.SemanticCSharp.Value);
        if (s.StreamingAnswers is not null) Set(root, "Features", "StreamingAnswers", s.StreamingAnswers.Value);
        if (s.EnrichmentBatchSize is not null) Set(root, "SemanticEnrichment", "BatchSize", s.EnrichmentBatchSize.Value);
        if (s.EnrichmentMaxParallelism is not null) Set(root, "SemanticEnrichment", "MaxParallelism", s.EnrichmentMaxParallelism.Value);
        if (s.DriftReconcileIntervalSeconds is not null) Set(root, "Drift", "ReconcileIntervalSeconds", s.DriftReconcileIntervalSeconds.Value);

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Persists <paramref name="s"/> into the overlay file at <paramref name="localFilePath"/>.</summary>
    public static void Write(string localFilePath, AiSettings s)
    {
        var existing = File.Exists(localFilePath) ? File.ReadAllText(localFilePath) : null;
        File.WriteAllText(localFilePath, BuildLocalJson(existing, s));
    }

    private static void Set(JsonObject root, string section, string key, JsonNode value)
    {
        if (root[section] is not JsonObject obj)
        {
            obj = new JsonObject();
            root[section] = obj;
        }
        obj[key] = value;
    }
}
