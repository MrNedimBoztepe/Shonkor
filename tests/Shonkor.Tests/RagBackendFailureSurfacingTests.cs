// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// What the <b>user</b> sees when the Ollama backend answers <c>200 OK</c> with an unusable payload (#225).
///
/// <para>
/// This is the half of the bug that mattered. <c>GenerateRAGResponseAsync</c> used to <i>return</i>
/// <c>"No answer could be generated."</c> — a string, travelling back through the same channel as a real
/// answer, arriving at <c>/api/ask</c> as a <b>200 OK</b> and rendering in the dashboard as though the model had
/// weighed the question and declined. It had not. The backend was broken, and a backend broken on <i>every</i>
/// query presented as a system that politely refuses to answer anything.
/// </para>
/// <para>
/// The codebase already had a real "no answer" path, and it is nothing like that one: <c>GroundingPrep</c>
/// abstains <b>deterministically, without calling the LLM at all</b>, and says so (<c>grounded = false</c>).
/// Conflating a broken backend with a considered abstention is exactly what made the failure invisible — so
/// both are asserted here, side by side, because the whole point is that they must not look alike.
/// </para>
/// </summary>
public class RagBackendFailureSurfacingTests
{
    private sealed class AppFactory(string ollamaUrl, string workspace) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Development: the loopback bypass applies, so these tests exercise the RAG failure path rather
            // than re-testing the auth gate (which WebPipelineTests already covers).
            builder.UseEnvironment("Development");
            builder.UseSetting("SemanticAnalyzer:OllamaUrl", ollamaUrl);
            builder.UseSetting("SemanticAnalyzer:TimeoutSeconds", "10");
            // Engage the relevance floor, so the deterministic-abstention path below is genuinely exercised.
            // It only applies to nodes the caller supplied a score for, so the unscored request is unaffected.
            builder.UseSetting("Rag:MinRelevanceScore", "0.5");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ProjectManager>();
                services.AddSingleton(_ => new ProjectManager(workspace));

                foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                {
                    services.Remove(hosted);
                }
            });
        }
    }

    /// <summary>
    /// The API key the tests present. The loopback bypass cannot be used here: under <c>TestServer</c> there is
    /// no remote IP, so <c>ApiKeyMiddleware</c> never sees a loopback address and correctly refuses. Presenting
    /// a real key is closer to production anyway — the request under test goes through the whole gate.
    /// </summary>
    private const string ApiKey = "test-key";

    /// <summary>A workspace with one project holding one node, so /api/ask has real context to ground in.</summary>
    private static async Task<string> WorkspaceWithANodeAsync(string nodeId)
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_rag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode
                {
                    Id = nodeId, Name = "TokenHasher", Type = "Class",
                    FilePath = "src/Security/TokenHasher.cs", Content = "class TokenHasher { }"
                }
            });
        }

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[]
            {
                new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = TokenHasher.Hash(ApiKey) }
            },
            ActiveProjectName = "P"
        };
        await File.WriteAllTextAsync(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));
        return ws;
    }

    [Fact]
    public async Task ABrokenBackend_SurfacesAsAFailure_NotAsAnAnswerTheUserWillBelieve()
    {
        const string nodeId = "src::TokenHasher";
        // 200 OK, valid JSON, no "response" field: the backend answered and the payload is unusable.
        using var backend = FakeOllamaBackend.ThatAnswers("{}");
        var ws = await WorkspaceWithANodeAsync(nodeId);
        await using var factory = new AppFactory(backend.Url, ws);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);

        var res = await client.PostAsJsonAsync("/api/ask", new { Query = "how are tokens hashed?", NodeIds = new[] { nodeId } });

        // THE POINT: not a 200 carrying prose the user reads as the model's answer.
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);

        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("No answer could be generated", body, StringComparison.OrdinalIgnoreCase);

        // ...and the backend was hit once, not retried — a deterministic failure (#222) on the path a human waits on.
        Assert.Equal(1, backend.Requests);
    }

    /// <summary>
    /// The contrast that gives the test above its meaning. A genuine "I cannot answer from this evidence" is a
    /// <b>200</b>, carries the abstention marker, and is explicitly flagged <c>grounded = false</c> — and it
    /// never touches the backend at all. If a broken backend ever produced <i>this</i> shape again, the bug
    /// would be back.
    /// </summary>
    [Fact]
    public async Task ADeliberateAbstention_IsStillA200_AndNeverCallsTheBackend()
    {
        const string nodeId = "src::TokenHasher";
        using var backend = FakeOllamaBackend.ThatAnswers("{}");
        var ws = await WorkspaceWithANodeAsync(nodeId);
        await using var factory = new AppFactory(backend.Url, ws);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);

        // The only supplied score (0.01) is far below the configured floor (0.5), so nothing clears it and the
        // request abstains deterministically — no LLM call at all.
        var res = await client.PostAsJsonAsync("/api/ask", new
        {
            Query = "how are tokens hashed?",
            NodeIds = new[] { nodeId },
            Scores = new[] { 0.01 }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"grounded\":false", body.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);

        // ...and it never spent a backend call. THIS is what a real "I cannot answer" looks like: a 200, an
        // explicit grounded=false, and no LLM involved. A broken backend must never be able to imitate it.
        Assert.Equal(0, backend.Requests);
    }
}
