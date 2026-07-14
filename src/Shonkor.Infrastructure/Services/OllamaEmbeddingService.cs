using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;

namespace Shonkor.Infrastructure.Services;

public class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaEmbeddingService> _logger;
    private readonly string _ollamaUrl;
    private readonly string _ollamaModel;
    private readonly string _queryPrefix;
    private readonly string _documentPrefix;

    public OllamaEmbeddingService(HttpClient httpClient, IConfiguration configuration, ILogger<OllamaEmbeddingService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _ollamaUrl = (configuration["EmbeddingService:OllamaUrl"] ?? "http://localhost:11434").TrimEnd('/');
        _ollamaModel = configuration["EmbeddingService:OllamaModel"] ?? "nomic-embed-text";

        // Optional nomic-style task prefixes (TICKET-006). Default OFF: on Shonkor's code corpus the
        // measured retrieval difference was within noise, so prefixing is opt-in rather than forced.
        // Set EmbeddingService:QueryPrefix / :DocumentPrefix to e.g. "search_query: " / "search_document: ".
        _queryPrefix = configuration["EmbeddingService:QueryPrefix"] ?? string.Empty;
        _documentPrefix = configuration["EmbeddingService:DocumentPrefix"] ?? string.Empty;

        // Do NOT set _httpClient.BaseAddress here — mutating a shared HttpClient after construction
        // is not thread-safe and would affect any other service using the same instance.
        // Instead we build the full URL per request (see below).
        _httpClient.Timeout = TimeSpan.FromMinutes(1);
    }

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        => GenerateEmbeddingAsync(text, EmbeddingKind.Document, cancellationToken);

    public async Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingKind kind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<float>();
        }

        var prefix = kind == EmbeddingKind.Query ? _queryPrefix : _documentPrefix;
        var prompt = string.IsNullOrEmpty(prefix) ? text : prefix + text;

        var requestBody = new
        {
            model = _ollamaModel,
            prompt
        };

        var endpoint = $"{_ollamaUrl}/api/embeddings";

        // No retry loop here (#116). Retry, exponential backoff and jitter are Polly's job — see
        // OllamaResilience, which every construction path (Web DI, CLI, bench) wraps its HttpClient in.
        // Two consequences worth naming, because the old hand-rolled loop had to work for them:
        //   - The OllamaResponseException below is raised AFTER a successful 200, so the retry pipeline has
        //     already returned and cannot retry it. Deterministic-failure-is-never-retried is now structural.
        //   - A caller-triggered cancellation propagates out of the pipeline. There is no longer any need to
        //     tell it apart from an HttpClient timeout by inspecting the token.
        var response = await OllamaResilience.Background
            .ExecuteAsync(async ct => await _httpClient.PostAsJsonAsync(endpoint, requestBody, ct).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var embeddingNode = responseJson?["embedding"]?.AsArray();

        if (embeddingNode == null || embeddingNode.Count == 0)
        {
            throw new OllamaResponseException("Ollama returned an empty embedding.");
        }

        var embedding = new float[embeddingNode.Count];
        for (int i = 0; i < embeddingNode.Count; i++)
        {
            embedding[i] = (float)embeddingNode[i]!.GetValue<double>();
        }

        return embedding;
    }
}
