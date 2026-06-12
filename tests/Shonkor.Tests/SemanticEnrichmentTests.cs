// Licensed to Shonkor under the MIT License.

using System.Net.Http;

using Microsoft.Extensions.Logging.Abstractions;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;
using Shonkor.Web.Services;

namespace Shonkor.Tests;

/// <summary>
/// Tests the bounded-parallel batch enrichment in <see cref="SemanticEnrichmentService.ProcessBatchAsync"/>:
/// the happy path persists every node, a backend outage trips the circuit breaker (return true), and a
/// per-node logic error is skipped without tripping it.
/// </summary>
public class SemanticEnrichmentTests
{
    private sealed class FakeAnalyzer : ISemanticAnalyzer
    {
        private readonly Func<GraphNode, SemanticAnalysisResult> _summarize;
        public int Calls;
        public FakeAnalyzer(Func<GraphNode, SemanticAnalysisResult> summarize) => _summarize = summarize;

        public Task<SemanticAnalysisResult> AnalyzeNodeAsync(GraphNode node, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(_summarize(node));
        }

        public Task<string> GenerateRAGResponseAsync(string query, IReadOnlyList<GraphNode> contextNodes, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeEmbeddings : IEmbeddingService
    {
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new[] { 1f, 0f, 0f });
    }

    private static async Task<SqliteGraphStorageProvider> SeededStorageAsync(int count)
    {
        var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(Enumerable.Range(0, count)
            .Select(i => new GraphNode { Id = $"n{i}", Type = "Class", Name = $"N{i}", Content = $"body {i}" }));
        return storage;
    }

    [Fact]
    public async Task ProcessBatch_HappyPath_EnrichesEveryNode()
    {
        using var storage = await SeededStorageAsync(8);
        var nodes = await storage.GetNodesPendingSemanticAnalysisAsync(100);
        var analyzer = new FakeAnalyzer(n => new SemanticAnalysisResult { Summary = $"summary of {n.Name}" });

        var backendDown = await SemanticEnrichmentService.ProcessBatchAsync(
            nodes, analyzer, new FakeEmbeddings(), storage, maxParallelism: 4, NullLogger.Instance, CancellationToken.None);

        Assert.False(backendDown);
        Assert.Equal(8, analyzer.Calls);
        // Every node now carries its summary -> none remain pending.
        Assert.Empty(await storage.GetNodesPendingSemanticAnalysisAsync(100));
        Assert.Equal("summary of N3", (await storage.GetNodeByIdAsync("n3"))!.Summary);
    }

    [Fact]
    public async Task ProcessBatch_BackendUnavailable_TripsBreaker()
    {
        using var storage = await SeededStorageAsync(6);
        var nodes = await storage.GetNodesPendingSemanticAnalysisAsync(100);
        // Every analyze call fails as if Ollama is unreachable.
        var analyzer = new FakeAnalyzer(_ => throw new HttpRequestException("connection refused"));

        var backendDown = await SemanticEnrichmentService.ProcessBatchAsync(
            nodes, analyzer, new FakeEmbeddings(), storage, maxParallelism: 4, NullLogger.Instance, CancellationToken.None);

        Assert.True(backendDown);
        // Nothing was persisted; all nodes still pending.
        Assert.Equal(6, (await storage.GetNodesPendingSemanticAnalysisAsync(100)).Count);
    }

    [Fact]
    public async Task ProcessBatch_PerNodeError_SkipsWithoutTrippingBreaker()
    {
        using var storage = await SeededStorageAsync(5);
        var nodes = await storage.GetNodesPendingSemanticAnalysisAsync(100);
        // One node fails with a non-backend (logic) error; the rest succeed.
        var analyzer = new FakeAnalyzer(n => n.Id == "n2"
            ? throw new InvalidOperationException("unparseable model output")
            : new SemanticAnalysisResult { Summary = $"ok {n.Name}" });

        var backendDown = await SemanticEnrichmentService.ProcessBatchAsync(
            nodes, analyzer, new FakeEmbeddings(), storage, maxParallelism: 4, NullLogger.Instance, CancellationToken.None);

        Assert.False(backendDown);                       // logic error must NOT trip the breaker
        var stillPending = await storage.GetNodesPendingSemanticAnalysisAsync(100);
        Assert.Single(stillPending);                     // only the failed node remains
        Assert.Equal("n2", stillPending[0].Id);
    }
}
