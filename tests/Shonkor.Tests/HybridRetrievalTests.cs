// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// #122: the single hybrid-retrieval entry point shared by the MCP tools and the web endpoints. FTS + vector
/// fused by RRF; FTS-only fallback when there is no embedding backend or it fails, so a query never
/// hard-fails on a missing/slow backend.
/// </summary>
public class HybridRetrievalTests
{
    private static string NewDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_hybrid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "g.db");
    }

    /// <summary>Returns a fixed vector for any input, so a chosen node ranks first in the vector arm.</summary>
    private sealed class StubEmbeddingService : IEmbeddingService
    {
        private readonly float[]? _vector;
        public StubEmbeddingService(float[]? vector) => _vector = vector;
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
            => Task.FromResult(_vector ?? Array.Empty<float>());
        public Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingKind kind, CancellationToken ct = default)
            => Task.FromResult(_vector ?? Array.Empty<float>());
    }

    /// <summary>An embedding service that always throws — models a down/slow backend.</summary>
    private sealed class ThrowingEmbeddingService : IEmbeddingService
    {
        public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default) => throw new HttpRequestException("down");
        public Task<float[]> GenerateEmbeddingAsync(string text, EmbeddingKind kind, CancellationToken ct = default) => throw new HttpRequestException("down");
    }

    private static async Task<SqliteGraphStorageProvider> SeedAsync()
    {
        var storage = new SqliteGraphStorageProvider(NewDbPath());
        await storage.InitializeAsync();
        await storage.UpsertNodesAsync(new[]
        {
            // "Widget" matches the keyword; "Vectorish" is aligned with the stub query vector [1,0,0].
            new GraphNode { Id = "kw::Widget", Name = "Widget", Type = "Class", Content = "class Widget { }", Embedding = new[] { 0f, 1f, 0f } },
            new GraphNode { Id = "vec::Vectorish", Name = "Vectorish", Type = "Class", Content = "class Vectorish { }", Embedding = new[] { 1f, 0f, 0f } }
        });
        return storage;
    }

    [Fact]
    public async Task NullEmbeddingService_FallsBackToFtsOnly()
    {
        using var storage = await SeedAsync();

        var hits = await HybridRetrieval.SearchAsync(storage, embeddingService: null, "Widget", 10);

        Assert.Contains(hits, h => h.Node.Id == "kw::Widget"); // keyword hit present
        Assert.DoesNotContain(hits, h => h.Node.Id == "vec::Vectorish"); // no vector arm → no vector-only hit
    }

    [Fact]
    public async Task EmbeddingBackendThrows_DegradesToFtsOnly_NoThrow()
    {
        using var storage = await SeedAsync();

        var hits = await HybridRetrieval.SearchAsync(storage, new ThrowingEmbeddingService(), "Widget", 10);

        Assert.Contains(hits, h => h.Node.Id == "kw::Widget");
        Assert.DoesNotContain(hits, h => h.Node.Id == "vec::Vectorish");
    }

    [Fact]
    public async Task WithBackend_FusesKeywordAndVectorArms()
    {
        using var storage = await SeedAsync();
        // Query "Widget" (keyword) with a vector aligned to Vectorish → RRF should surface BOTH.
        var hits = await HybridRetrieval.SearchAsync(storage, new StubEmbeddingService(new[] { 1f, 0f, 0f }), "Widget", 10);

        Assert.Contains(hits, h => h.Node.Id == "kw::Widget");    // from FTS
        Assert.Contains(hits, h => h.Node.Id == "vec::Vectorish"); // from vector similarity
    }

    [Fact]
    public async Task McpContextAndSharedHelper_ProduceTheSameSeeds()
    {
        // The MCP path (ctx.HybridSearchAsync) now delegates to HybridRetrieval — same seeds by construction,
        // which is what the dashboard/tool parity in #122 depends on.
        using var storage = await SeedAsync();
        var emb = new StubEmbeddingService(new[] { 1f, 0f, 0f });

        var direct = await HybridRetrieval.SearchAsync(storage, emb, "Widget", 5);
        var viaContext = await new McpToolContextProbe(emb).HybridSearchAsync(storage, "Widget", 5);

        Assert.Equal(direct.Select(h => h.Node.Id), viaContext.Select(h => h.Node.Id));
    }

    /// <summary>Minimal shim to exercise McpToolContext.HybridSearchAsync with a stub embedding service.</summary>
    private sealed class McpToolContextProbe
    {
        private readonly Shonkor.Infrastructure.Services.Mcp.McpToolContext _ctx;
        public McpToolContextProbe(IEmbeddingService emb)
        {
            var ws = Path.Combine(Path.GetTempPath(), $"shonkor_probe_{Guid.NewGuid():N}");
            Directory.CreateDirectory(ws);
            _ctx = new Shonkor.Infrastructure.Services.Mcp.McpToolContext(
                new Shonkor.Infrastructure.Services.ProjectManager(ws),
                new ContextCapsuleSynthesizer(), null, false, emb, null, null);
        }
        public Task<IReadOnlyList<SearchResult>> HybridSearchAsync(SqliteGraphStorageProvider storage, string query, int count)
            => _ctx.HybridSearchAsync(storage, query, count);
    }
}
