using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Infrastructure.Services;

public class OllamaSemanticAnalyzer : ISemanticAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaSemanticAnalyzer> _logger;
    private readonly string _ollamaUrl;
    private readonly string _ollamaModel;

    public OllamaSemanticAnalyzer(HttpClient httpClient, IConfiguration configuration, ILogger<OllamaSemanticAnalyzer> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        _ollamaUrl = configuration["SemanticAnalyzer:OllamaUrl"] ?? "http://localhost:11434";
        _ollamaModel = configuration["SemanticAnalyzer:OllamaModel"] ?? "qwen2.5-coder";

        _httpClient.BaseAddress = new Uri(_ollamaUrl);
        _httpClient.Timeout = TimeSpan.FromMinutes(2); // Local LLM generation can take a bit
    }

    public async Task<SemanticAnalysisResult> AnalyzeNodeAsync(GraphNode node, CancellationToken cancellationToken = default)
    {
        // For very long files, we might want to truncate the content to fit the context window
        var contentToAnalyze = node.Content;
        if (contentToAnalyze.Length > 8000)
        {
            contentToAnalyze = contentToAnalyze[..8000]; // rudimentary truncation, ideally we'd use a tokenizer
        }

        var prompt = $$"""
        Du bist ein Software-Architektur-Experte. Analysiere den folgenden Code-Knoten und gib genau ZWEI Dinge im JSON-Format zurück:
        1. "Summary": Ein prägnanter 1-Satz Steckbrief (max 200 Zeichen) über den fachlichen Geschäftszweck dieser Datei/Klasse/Methode. Keine technischen Details wie "Das ist eine C# Klasse", sondern WAS sie tut.
        2. "ExtractedConcepts": Ein Array von 1-3 abstrakten Architektur-Konzepten (z.B. "Authentication", "Data Access", "UI Component").

        Knoten Name: {{node.Name}}
        Knoten Typ: {{node.Type}}

        Code:
        ```
        {{contentToAnalyze}}
        ```

        Antworte AUSSCHLIESSLICH mit gültigem JSON, ohne Markdown Formatierung drumherum. Format:
        { "Summary": "...", "ExtractedConcepts": ["...", "..."] }
        """;

        var requestBody = new
        {
            model = _ollamaModel,
            prompt = prompt,
            stream = false,
            format = "json" // Tell Ollama to enforce JSON output (supported in recent versions)
        };

        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogDebug("Sending node {NodeId} to Ollama ({Model}) for analysis... (Attempt {Attempt})", node.Id, _ollamaModel, attempt);
                
                var response = await _httpClient.PostAsJsonAsync("/api/generate", requestBody, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
                var responseText = responseJson?["response"]?.ToString();

                if (string.IsNullOrWhiteSpace(responseText))
                {
                    throw new Exception("Ollama returned an empty response.");
                }

                // Parse the JSON returned by the model
                responseText = responseText.Trim();
                if (responseText.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                {
                    responseText = responseText.Substring(7).Trim();
                }
                else if (responseText.StartsWith("```", StringComparison.OrdinalIgnoreCase))
                {
                    responseText = responseText.Substring(3).Trim();
                }
                if (responseText.EndsWith("```", StringComparison.OrdinalIgnoreCase))
                {
                    responseText = responseText.Substring(0, responseText.Length - 3).Trim();
                }

                var parsedResult = JsonSerializer.Deserialize<SemanticAnalysisResult>(responseText, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (parsedResult == null || string.IsNullOrWhiteSpace(parsedResult.Summary))
                {
                    throw new Exception("Failed to deserialize Ollama JSON response or Summary was empty.");
                }
                
                // Extract metrics
                var promptTokens = responseJson?["prompt_eval_count"]?.GetValue<int>() ?? 0;
                var completionTokens = responseJson?["eval_count"]?.GetValue<int>() ?? 0;
                var durationNs = responseJson?["eval_duration"]?.GetValue<long>() ?? 0;

                return parsedResult with
                {
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    LatencyMs = durationNs / 1_000_000
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze node {NodeId} via Ollama on attempt {Attempt}.", node.Id, attempt);
                if (attempt == maxRetries)
                {
                    _logger.LogError("Max retries reached for node {NodeId}.", node.Id);
                    throw;
                }
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }
        
        throw new Exception("Failed to analyze node after retries.");
    }

    public async Task<string> GenerateRAGResponseAsync(string query, IReadOnlyList<GraphNode> contextNodes, CancellationToken cancellationToken = default)
    {
        var contextBuilder = new System.Text.StringBuilder();
        foreach (var node in contextNodes)
        {
            contextBuilder.AppendLine($"--- KNOTEN: {node.Name} ({node.Type}) ---");
            contextBuilder.AppendLine($"ZUSAMMENFASSUNG: {node.Summary}");
            if (!string.IsNullOrWhiteSpace(node.Content))
            {
                var contentToInclude = node.Content.Length > 2000 ? node.Content[..2000] + " ... [TRUNCATED]" : node.Content;
                contextBuilder.AppendLine($"CODE:\n{contentToInclude}");
            }
            contextBuilder.AppendLine();
        }

        var prompt = $$"""
        Du bist Shonkor, ein intelligenter KI-Softwarearchitekt. Beantworte die folgende Frage des Nutzers PRÄZISE und AUSSCHLIESSLICH basierend auf dem bereitgestellten Code-Kontext aus dem Projektgraphen.
        Wenn die Antwort nicht im bereitgestellten Kontext enthalten ist, sage deutlich, dass du es basierend auf den aktuellen Graphen-Daten nicht weißt. Erfinde keine APIs oder Funktionen, die nicht im Kontext stehen.

        NUTZERFRAGE:
        {{query}}

        VERFÜGBARER KONTEXT:
        {{contextBuilder.ToString()}}

        Antworte in klarem Markdown (auf Deutsch).
        """;

        var requestBody = new
        {
            model = _ollamaModel,
            prompt = prompt,
            stream = false
        };

        _logger.LogInformation("Generating RAG response via Ollama ({Model}) for query: {Query}", _ollamaModel, query);
        
        var response = await _httpClient.PostAsJsonAsync("/api/generate", requestBody, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
        var responseText = responseJson?["response"]?.ToString();

        return responseText ?? "Es konnte keine Antwort generiert werden.";
    }
}
