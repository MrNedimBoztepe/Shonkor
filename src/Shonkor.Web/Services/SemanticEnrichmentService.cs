using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
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

    // Tunable via config (SemanticEnrichment:BatchSize / :MaxParallelism). Client-side parallelism is
    // safe: Ollama serializes internally when it can't parallelize, so concurrency never costs throughput.
    private readonly int _batchSize;
    private readonly int _maxParallelism;

    // TICKET-002: what text gets embedded. "code" (default) embeds a structured code document
    // (type + name + signature + bounded body), which measured markedly better on intent retrieval
    // than embedding the 1-sentence summary ("summary"). See review/results.md.
    private readonly string _embeddingSource;

    private int _consecutiveServiceFailures;
    // TICKET-006: reconcile stored embeddings against the current model's dimension exactly once per
    // process (after the backend is first reachable), so a model change re-embeds instead of silently
    // dropping stale-dimension vectors from the vector search.
    private bool _embeddingsReconciled;

    public SemanticEnrichmentService(
        ProjectManager projectManager,
        IServiceScopeFactory scopeFactory,
        ILogger<SemanticEnrichmentService> logger,
        IConfiguration configuration)
    {
        _projectManager = projectManager;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _batchSize = Math.Max(1, configuration.GetValue<int?>("SemanticEnrichment:BatchSize") ?? 16);
        _maxParallelism = Math.Max(1, configuration.GetValue<int?>("SemanticEnrichment:MaxParallelism") ?? 4);
        _embeddingSource = (configuration["Embedding:Source"] ?? "code").Trim().ToLowerInvariant();
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

        // One-time re-embed reconciliation: probe the current embedding dimension, then flag any stored
        // vectors of a different dimension for re-embedding (TICKET-006). Guarded by a probe so a dead
        // backend simply defers this to a later cycle.
        if (!_embeddingsReconciled)
        {
            try
            {
                var probe = await embeddingService.GenerateEmbeddingAsync("dimension probe", cancellationToken);
                if (probe is { Length: > 0 })
                {
                    foreach (var project in projects)
                    {
                        var st = await _projectManager.GetStorageProviderAsync(project.Name, cancellationToken);
                        var flagged = await st.MarkStaleEmbeddingsForReembedAsync(probe.Length, cancellationToken);
                        if (flagged > 0)
                        {
                            _logger.LogInformation(
                                "Flagged {Count} embedding(s) with a stale dimension for re-embedding in project '{Project}'.",
                                flagged, project.Name);
                        }
                    }
                    _embeddingsReconciled = true;
                }
            }
            catch (Exception ex) when (IsBackendUnavailable(ex))
            {
                // Backend not reachable yet — leave _embeddingsReconciled false and retry next cycle.
            }
        }

        foreach (var project in projects)
        {
            var storage = await _projectManager.GetStorageProviderAsync(project.Name, cancellationToken);

            var pendingNodes = await storage.GetNodesPendingSemanticAnalysisAsync(_batchSize, cancellationToken);
            if (pendingNodes.Count == 0) continue;

            _logger.LogInformation(
                "Found {Count} nodes pending semantic analysis in project '{Project}'.",
                pendingNodes.Count, project.Name);

            var backendDown = await ProcessBatchAsync(
                pendingNodes, analyzer, embeddingService, storage, _maxParallelism, _logger, _embeddingSource, cancellationToken)
                .ConfigureAwait(false);

            if (backendDown) return true;
            if (cancellationToken.IsCancellationRequested) return false;

            _logger.LogInformation("Finished processing batch for project '{Project}'.", project.Name);
        }

        return false;
    }

    /// <summary>
    /// Enriches a batch of nodes with bounded parallelism: each node is summarized, its summary embedded,
    /// and the result persisted. The analyze→embed pair for one node stays sequential (the embedding
    /// needs the summary), but separate nodes run concurrently up to <paramref name="maxParallelism"/>.
    /// Returns <c>true</c> when the backend looks unreachable (so the caller can trip the circuit breaker).
    /// Internal for testing.
    /// </summary>
    internal static async Task<bool> ProcessBatchAsync(
        IReadOnlyList<GraphNode> nodes,
        ISemanticAnalyzer analyzer,
        IEmbeddingService embeddingService,
        ISemanticGraphStore storage,
        int maxParallelism,
        ILogger logger,
        string embeddingSource,
        CancellationToken cancellationToken)
    {
        using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var backendDown = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, maxParallelism),
            CancellationToken = cycleCts.Token
        };

        try
        {
            await Parallel.ForEachAsync(nodes, options, async (node, ct) =>
            {
                try
                {
                    var result = await analyzer.AnalyzeNodeAsync(node, ct);

                    // TICKET-002: embed a structured code document by default (markedly better intent
                    // retrieval than embedding the summary). Configurable via Embedding:Source.
                    var embeddingText = Shonkor.Core.Services.EmbeddingTextBuilder.Build(node, result.Summary, embeddingSource);
                    float[]? embedding = null;
                    if (!string.IsNullOrWhiteSpace(embeddingText))
                    {
                        embedding = await embeddingService.GenerateEmbeddingAsync(embeddingText, EmbeddingKind.Document, ct);
                    }

                    await storage.UpdateNodeSemanticDataAsync(node.Id, result, embedding, ct);
                }
                catch (OperationCanceledException)
                {
                    // Sibling tripped the breaker (or the app is stopping). Stop quietly.
                }
                catch (Exception ex) when (IsBackendUnavailable(ex))
                {
                    // The backend is down — trip the breaker and cancel the rest of this batch rather
                    // than grinding through it; the caller applies exponential backoff.
                    if (Interlocked.Exchange(ref backendDown, 1) == 0)
                    {
                        logger.LogWarning(ex,
                            "Semantic backend unreachable while analyzing node {Id}; aborting this cycle.", node.Id);
                        cycleCts.Cancel();
                    }
                }
                catch (Exception ex)
                {
                    // Per-node logic error (e.g. the model returned unparseable output). Skip this node;
                    // it does not indicate the backend is down, so don't trip the breaker.
                    logger.LogWarning(ex, "Failed to analyze node {Id}. Skipping for now.", node.Id);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Parallel.ForEachAsync surfaces the cancellation once the batch unwinds — either the
            // breaker tripped (handled via backendDown below) or the app is stopping.
        }

        return backendDown == 1;
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
