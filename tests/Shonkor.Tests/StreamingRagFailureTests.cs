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
/// A streaming answer must never end quietly (#227) — the streaming counterpart of #225.
///
/// <para>
/// #225 stopped the <b>blocking</b> RAG path handing a backend failure back as though it were the model's
/// answer. The streaming path had the same class of bug, and it hid better, because most of it was already
/// handled: a reset, a mid-body death and a 503 all produced a <c>500</c> with a visible marker.
/// </para>
/// <para>
/// One did not. A backend that <b>hung</b> produced a <b>200 OK with an empty body</b> — the user saw a
/// successful, blank answer. The cause is the same confusion #215 found in the retry classifier: an
/// <c>HttpClient</c> timeout arrives as a <see cref="TaskCanceledException"/>, the endpoint's
/// "client disconnected" arm caught it, and swallowed it. <b>A timeout is not a cancellation. It only looks
/// like one.</b>
/// </para>
/// <para>
/// The tests below pin every way a stream can fail, and what the user is left looking at in each case —
/// because the property that matters is not "an exception was raised somewhere", it is "the person reading the
/// dashboard can tell this answer is not an answer".
/// </para>
/// </summary>
[Collection(ExpectedServerErrorsCollection.Name)]
public class StreamingRagFailureTests
{
    private const string ApiKey = "test-key";
    private const string NodeId = "src::TokenHasher";

    private sealed class AppFactory(string ollamaUrl, string workspace) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("SemanticAnalyzer:OllamaUrl", ollamaUrl);
            builder.UseSetting("SemanticAnalyzer:TimeoutSeconds", "2"); // so a hung backend fails in test time

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

    private static async Task<string> WorkspaceWithANodeAsync()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_stream_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode
                {
                    Id = NodeId, Name = "TokenHasher", Type = "Class",
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
                new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "",
                      RepositoryUrl = "", ApiKey = TokenHasher.Hash(ApiKey) }
            },
            ActiveProjectName = "P"
        };
        await File.WriteAllTextAsync(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));
        return ws;
    }

    private static async Task<(HttpStatusCode Status, string Body)> AskStreamAsync(string ollamaUrl)
    {
        var ws = await WorkspaceWithANodeAsync();
        await using var factory = new AppFactory(ollamaUrl, ws);
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);

        var res = await client.PostAsJsonAsync("/api/ask/stream",
            new { Query = "how are tokens hashed?", NodeIds = new[] { NodeId } });

        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    /// <summary>Parses the NDJSON body into its frames (#231): one JSON object per line.</summary>
    private static List<JsonElement> Frames(string body) =>
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

    /// <summary>The answer text: the concatenation of the <c>token</c> frames, and nothing else.</summary>
    private static string Answer(string body) =>
        string.Concat(Frames(body)
            .Where(f => f.TryGetProperty("token", out _))
            .Select(f => f.GetProperty("token").GetString()));

    /// <summary>
    /// Asserts the stream ended in a failure and returns the machine-readable class (#228).
    ///
    /// <para>
    /// This is what replaced <c>Assert.Contains("Error streaming the answer", body)</c>. That assertion could
    /// not tell an error apart from a model that happened to write those words, and it pinned English prose as
    /// the API contract — which is what #231 exists to end. A frame the model cannot author is the contract now.
    /// </para>
    /// </summary>
    private static string FailureCode(string body)
    {
        var frames = Frames(body);

        var error = Assert.Single(frames, f => f.TryGetProperty("error", out _));
        var done = Assert.Single(frames, f => f.TryGetProperty("done", out _));

        // A failure is never dressed as a finished answer: the terminal frame says so on its own.
        Assert.False(done.GetProperty("complete").GetBoolean());

        return error.GetProperty("error").GetProperty("code").GetString()!;
    }

    /// <summary>
    /// <b>The bug.</b> Before #227 this was a <c>200 OK</c> with an empty body: Ollama hung, the
    /// <c>HttpClient</c> timeout fired, the <c>TaskCanceledException</c> was caught by the "client disconnected"
    /// arm, and the user was served a blank answer as a success.
    /// </summary>
    [Fact]
    public async Task AHungBackend_IsAFailure_NotAnEmptyAnswerServedWithA200()
    {
        using var backend = FakeOllamaBackend.ThatNeverResponds();

        ExpectedError.Emit("streaming /api/ask: a hung backend must fail, not serve a blank 200 — asserted below (#236)");
        var (status, body) = await AskStreamAsync(backend.Url);

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.NotEqual(string.Empty, body);
        // ...and it says WHICH failure (#228): the request outlived its budget. "Raise the timeout / use a
        // smaller model" — a remedy nothing else on this page shares.
        Assert.Equal("backend_timeout", FailureCode(body));
    }

    /// <summary>
    /// The streaming counterpart of #225's rule: a stream that runs to <c>done=true</c> having emitted no
    /// tokens is a malfunctioning backend, not a model with nothing to say.
    /// </summary>
    [Fact]
    public async Task AStreamThatCompletesWithNoTokens_IsAFailure_NotABlankAnswer()
    {
        // Valid NDJSON, terminal line, zero content — the shape a misconfigured model produces.
        using var backend = FakeOllamaBackend.ThatAnswers("""{"response":"","done":true}""" + "\n");

        ExpectedError.Emit("streaming /api/ask: a zero-token completion is a backend malfunction, asserted below as a 500 (#236)");
        var (status, body) = await AskStreamAsync(backend.Url);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        // The backend answered 200 and streamed a well-formed terminal line — it is not unreachable and not
        // timing out. It is answering with something unusable, and retrying gets the same nothing (#222).
        Assert.Equal("backend_unusable_response", FailureCode(body));
        Assert.Equal(string.Empty, Answer(body)); // no token frame was ever passed off as an answer
    }

    [Fact]
    public async Task ABackendThatDiesMidStream_SurfacesAsAFailure()
    {
        using var backend = new MisbehavingBackend(MisbehavingBackend.Mode.DieMidBody);

        ExpectedError.Emit("streaming /api/ask: a backend that dies mid-body surfaces as a 500 — asserted below (#236)");
        var (status, body) = await AskStreamAsync(backend.Url);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("backend_error", FailureCode(body));
    }

    [Fact]
    public async Task ABackendThatRefusesToAnswer_SurfacesAsAFailure()
    {
        using var backend = new FakeOllamaBackend(HttpStatusCode.ServiceUnavailable);

        ExpectedError.Emit("streaming /api/ask: a 503 from the backend surfaces as a 500 — asserted below (#236)");
        var (status, body) = await AskStreamAsync(backend.Url);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        // Something IS listening and it said no — distinct from backend_unreachable, and the remedy differs:
        // read the backend's own logs, do not go looking for a dead process.
        Assert.Equal("backend_error", FailureCode(body));
    }

    /// <summary>
    /// The case that gives the others their point, and the one most likely to mislead: the backend <b>does</b>
    /// answer, streams real tokens, and then dies <i>without</i> Ollama's terminal <c>done=true</c> line. The
    /// tokens already sent are a perfectly readable paragraph — which is exactly why an unmarked truncation is a
    /// correctness bug the reader cannot see. The answer must arrive carrying its own incompleteness.
    /// </summary>
    /// <remarks>
    /// This test is where #231 is easiest to see. It used to assert <c>Contains("incomplete", body)</c> — and the
    /// marker it matched was English prose appended to the model's own text. Two things were wrong with that: the
    /// text was the API contract, so it could not be reworded or translated; and <b>a model that merely wrote the
    /// word "incomplete" satisfied it</b>, which means the signal was forgeable by the very thing it was
    /// describing. Now completeness rides on the terminal frame, where the model has no way to reach it, and the
    /// token frames carry the model's words and nothing but.
    /// </remarks>
    [Fact]
    public async Task APartialAnswer_ArrivesMarkedAsIncomplete_SoItCannotBeReadAsComplete()
    {
        // Two real tokens, then a clean end of stream with no done=true line.
        using var backend = FakeOllamaBackend.ThatAnswers(
            """{"response":"Tokens are hashed ","done":false}""" + "\n" +
            """{"response":"with SHA-256.","done":false}""" + "\n");

        var (status, body) = await AskStreamAsync(backend.Url);

        // The tokens the model DID produce are kept — they are real, and throwing them away helps nobody.
        // Note this is the ANSWER, not the raw body: the assertion can no longer be satisfied by a control
        // signal that happens to contain the text.
        Assert.Equal("Tokens are hashed with SHA-256.", Answer(body));

        // ...but the stream says, in a frame beside the answer, that it is not finished.
        var done = Assert.Single(Frames(body), f => f.TryGetProperty("done", out _));
        Assert.False(done.GetProperty("complete").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, status); // bytes were already on the wire; the frame carries the news
    }
}
