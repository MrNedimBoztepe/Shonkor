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

        try
        {
            _logger.LogDebug("Sending node {NodeId} to Ollama ({Model}) for analysis...", node.Id, _ollamaModel);
            
            var response = await _httpClient.PostAsJsonAsync("/api/generate", requestBody, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
            var responseText = responseJson?["response"]?.ToString();

            if (string.IsNullOrWhiteSpace(responseText))
            {
                throw new Exception("Ollama returned an empty response.");
            }

            // Parse the JSON returned by the model
            var parsedResult = JsonSerializer.Deserialize<SemanticAnalysisResult>(responseText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsedResult == null || string.IsNullOrWhiteSpace(parsedResult.Summary))
            {
                throw new Exception("Failed to deserialize Ollama JSON response or Summary was empty.");
            }

            return parsedResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze node {NodeId} via Ollama. Re-throwing so it can be retried later.", node.Id);
            throw;
        }
    }
}
