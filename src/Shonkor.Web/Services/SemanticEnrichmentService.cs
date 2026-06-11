using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web.Services;

/// <summary>
/// A background worker that asynchronously enriches the knowledge graph with semantic metadata.
/// It polls the database for nodes marked for analysis and delegates them to the ISemanticAnalyzer.
/// </summary>
/// <remarks>
/// The analyzer and embedding services are resolved from a per-cycle DI scope rather than captured in
/// the constructor. They are registered as typed <c>HttpClient</c> clients (transient); capturing them
/// in this singleton would pin a single <c>HttpMessageHandler</c> for the app's lifetime and defeat
/// <see cref="IHttpClientFactory"/>'s handler rotation (DNS refresh). A circuit breaker backs off
/// exponentially while the semantic backend (Ollama) is unreachable, so a dead backend is not hammered
/// on every poll.
/// </remarks>
public class SemanticEnrichmentService : BackgroundService
{
    private readonly ProjectManager _projectManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SemanticEnrichmentService> _logger;

    private static readonly TimeSpan BasePollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    private const int BatchSize = 10;

    private int _consecutiveServiceFailures;

    public SemanticEnrichmentService(
        ProjectManager projectManager,
        IServiceScopeFactory scopeFactory,
        ILogger<SemanticEnrichmentService> logger)
    {
        _projectManager = projectManager;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Semantic Enrichment Service is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var backendUnavailable = false;
            try
            {
                backendUnavailable = await ProcessPendingNodesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while processing semantic enrichment.");
            }

            // Circuit breaker: while the backend is unreachable, back off exponentially instead of
            // re-polling every 30s (which would otherwise busy-loop against a dead Ollama). Reset to
            // the normal cadence as soon as it responds again.
            TimeSpan delay;
            if (backendUnavailable)
            {
                _consecutiveServiceFailures++;
                delay = ComputeBackoff(_consecutiveServiceFailures);
                _logger.LogWarning(
                    "Semantic backend unreachable; backing off for {Delay} before retrying (consecutive failure #{Count}).",
                    delay, _consecutiveServiceFailures);
            }
            else
            {
                if (_consecutiveServiceFailures > 0)
                {
                    _logger.LogInformation("Semantic backend reachable again; resuming normal poll cadence.");
                }
                _consecutiveServiceFailures = 0;
                delay = BasePollInterval;
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Semantic Enrichment Service is stopping.");
    }

    /// <summary>
    /// Processes one batch per project. Returns <c>true</c> when the semantic backend appears
    /// unreachable (so the caller can trip the circuit breaker); <c>false</c> on normal completion.
    /// </summary>
    private async Task<bool> ProcessPendingNodesAsync(CancellationToken cancellationToken)
    {
        var projects = _projectManager.GetProjects();
        if (projects.Count == 0) return false;

        // Resolve the HTTP-backed services from a fresh scope each cycle so the HttpClientFactory
        // controls their lifetime/handler rotation instead of them being captured by this singleton.
        using var scope = _scopeFactory.CreateScope();
        var analyzer = scope.ServiceProvider.GetRequiredService<ISemanticAnalyzer>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

        foreach (var project in projects)
        {
            var storage = await _projectManager.GetStorageProviderAsync(project.Name, cancellationToken);

            var pendingNodes = await storage.GetNodesPendingSemanticAnalysisAsync(BatchSize, cancellationToken);
            if (pendingNodes.Count == 0) continue;

            _logger.LogInformation(
                "Found {Count} nodes pending semantic analysis in project '{Project}'.",
                pendingNodes.Count, project.Name);

            foreach (var node in pendingNodes)
            {
                if (cancellationToken.IsCancellationRequested) return false;

                try
                {
                    var result = await analyzer.AnalyzeNodeAsync(node, cancellationToken);

                    float[]? embedding = null;
                    if (!string.IsNullOrWhiteSpace(result.Summary))
                    {
                        embedding = await embeddingService.GenerateEmbeddingAsync(result.Summary, cancellationToken);
                    }

                    await storage.UpdateNodeSemanticDataAsync(node.Id, result, embedding, cancellationToken);
                }
                catch (Exception ex) when (IsBackendUnavailable(ex) && !cancellationToken.IsCancellationRequested)
                {
                    // The backend itself is down/unreachable — abort the whole cycle and let the
                    // circuit breaker apply backoff rather than grinding through the rest of the batch.
                    _logger.LogWarning(ex,
                        "Semantic backend unreachable while analyzing node {Id}; aborting this cycle.", node.Id);
                    return true;
                }
                catch (Exception ex)
                {
                    // Per-node logic error (e.g. the model returned unparseable output). Skip this node;
                    // it does not indicate the backend is down, so don't trip the breaker.
                    _logger.LogWarning(ex, "Failed to analyze node {Id}. Skipping for now.", node.Id);
                }
            }

            _logger.LogInformation("Finished processing batch for project '{Project}'.", project.Name);
        }

        return false;
    }

    /// <summary>Exponential backoff: 30s, 60s, 120s, … capped at <see cref="MaxBackoff"/>.</summary>
    private static TimeSpan ComputeBackoff(int consecutiveFailures)
    {
        var exponent = Math.Min(consecutiveFailures - 1, 10); // cap exponent to avoid overflow
        var seconds = BasePollInterval.TotalSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    /// <summary>
    /// True when the exception indicates the HTTP backend is unreachable or timed out
    /// (connection refused, DNS failure, request timeout), as opposed to a per-node logic error.
    /// </summary>
    private static bool IsBackendUnavailable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException) return true;
            // HttpClient request timeout surfaces as TaskCanceledException (with no caller cancellation).
            if (current is TaskCanceledException) return true;
        }
        return false;
    }
}
