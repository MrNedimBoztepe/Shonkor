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

    public OllamaEmbeddingService(HttpClient httpClient, IConfiguration configuration, ILogger<OllamaEmbeddingService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _ollamaUrl = (configuration["EmbeddingService:OllamaUrl"] ?? "http://localhost:11434").TrimEnd('/');
        _ollamaModel = configuration["EmbeddingService:OllamaModel"] ?? "nomic-embed-text";

        // Do NOT set _httpClient.BaseAddress here — mutating a shared HttpClient after construction
        // is not thread-safe and would affect any other service using the same instance.
        // Instead we build the full URL per request (see below).
        _httpClient.Timeout = TimeSpan.FromMinutes(1);
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<float>();
        }

        var requestBody = new
        {
            model = _ollamaModel,
            prompt = text
        };

        var endpoint = $"{_ollamaUrl}/api/embeddings";

        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(endpoint, requestBody, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
                var embeddingNode = responseJson?["embedding"]?.AsArray();

                if (embeddingNode == null || embeddingNode.Count == 0)
                {
                    throw new Exception("Ollama returned an empty embedding.");
                }

                var embedding = new float[embeddingNode.Count];
                for (int i = 0; i < embeddingNode.Count; i++)
                {
                    embedding[i] = (float)embeddingNode[i]!.GetValue<double>();
                }

                return embedding;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate embedding via Ollama on attempt {Attempt}.", attempt);
                if (attempt == maxRetries)
                {
                    _logger.LogError("Max retries reached for embedding generation.");
                    throw;
                }
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }

        return Array.Empty<float>();
    }
}
