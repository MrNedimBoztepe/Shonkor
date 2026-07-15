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
using Shonkor.Core.Services;

namespace Shonkor.Infrastructure.Services;

public class OllamaSemanticAnalyzer : ISemanticAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaSemanticAnalyzer> _logger;
    private readonly string _ollamaUrl;
    private readonly string _ollamaModel;

    /// <summary>
    /// How long a streamed answer may go with NO new token before the backend is judged wedged (#230).
    /// <c>HttpClient.Timeout</c> bounds only the pre-header phase under <c>ResponseHeadersRead</c>; body reads
    /// are otherwise unbounded, so a model that emits a few tokens and then stalls hangs the request forever.
    /// Reset on every token, so a slow-but-alive model is never killed — only a stall is. Config:
    /// <c>SemanticAnalyzer:StreamIdleTimeoutSeconds</c> (default 120s; ≤ 0 disables the guard).
    /// </summary>
    private readonly TimeSpan _streamIdleTimeout;

    public OllamaSemanticAnalyzer(HttpClient httpClient, IConfiguration configuration, ILogger<OllamaSemanticAnalyzer> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _ollamaUrl = (configuration["SemanticAnalyzer:OllamaUrl"] ?? "http://localhost:11434").TrimEnd('/');
        _ollamaModel = configuration["SemanticAnalyzer:OllamaModel"] ?? "qwen2.5-coder";

        var idleSeconds = configuration.GetValue<double?>("SemanticAnalyzer:StreamIdleTimeoutSeconds") ?? 120;
        _streamIdleTimeout = idleSeconds > 0 ? TimeSpan.FromSeconds(idleSeconds) : Timeout.InfiniteTimeSpan;

        // Do NOT set _httpClient.BaseAddress here — mutating a shared HttpClient after construction
        // is not thread-safe and would affect any other service using the same instance.
        // Instead we build the full URL per request (see below).
        // Local LLM generation can take a bit — but "a bit" is not the same on every machine, and this was
        // hard-coded at two minutes while also silently overwriting any timeout the caller had configured
        // (#215). It is now SemanticAnalyzer:TimeoutSeconds, still defaulting to two minutes: a big model on
        // slow hardware can be given longer, and someone who wants RAG to fail fast can ask for less.
        OllamaClientFactory.ApplyTimeout(_httpClient, configuration, "SemanticAnalyzer", TimeSpan.FromMinutes(2));
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
        You are a software-architecture expert. Analyze the following code node and return exactly TWO things in JSON format:
        1. "Summary": A concise 1-sentence profile (max 200 characters) of the business purpose of this file/class/method. No technical details like "This is a C# class", but WHAT it does.
        2. "ExtractedConcepts": An array of 1-3 abstract architecture concepts (e.g. "Authentication", "Data Access", "UI Component").

        Node Name: {{node.Name}}
        Node Type: {{node.Type}}

        Code:
        ```
        {{contentToAnalyze}}
        ```

        Reply EXCLUSIVELY with valid JSON, without any surrounding Markdown formatting. Format:
        { "Summary": "...", "ExtractedConcepts": ["...", "..."] }
        """;

        var requestBody = new
        {
            model = _ollamaModel,
            prompt = prompt,
            stream = false,
            format = "json" // Tell Ollama to enforce JSON output (supported in recent versions)
        };

        var endpoint = $"{_ollamaUrl}/api/generate";

        _logger.LogDebug("Sending node {NodeId} to Ollama ({Model}) for analysis...", node.Id, _ollamaModel);

        // Background work: Polly retries transient failures with exponential backoff + jitter (#116). The
        // OllamaResponseException raised below comes AFTER a 200, so the pipeline has already returned and
        // cannot retry it — deterministic failures are excluded by construction, not by a rule.
        {
            {
                var response = await OllamaResilience.Background
                    .ExecuteAsync(async ct => await _httpClient.PostAsJsonAsync(endpoint, requestBody, ct).ConfigureAwait(false), cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
                var responseText = responseJson?["response"]?.ToString();

                if (string.IsNullOrWhiteSpace(responseText))
                {
                    throw new OllamaResponseException("Ollama returned an empty response.");
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
                    throw new OllamaResponseException("Failed to deserialize the Ollama JSON response, or the Summary was empty.");
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
        }
    }

    public Task<string> GenerateRAGResponseAsync(string query, IReadOnlyList<GraphNode> contextNodes, CancellationToken cancellationToken = default)
        => GenerateRAGResponseAsync(query, contextNodes, RagPromptOptions.Default, cancellationToken);

    public async Task<string> GenerateRAGResponseAsync(string query, IReadOnlyList<GraphNode> contextNodes, RagPromptOptions options, CancellationToken cancellationToken = default)
    {
        // Budget the context to the model window (TICKET-205), then build from the plan so validation and
        // logging see exactly the nodes the model saw.
        var plan = RagPromptBuilder.PlanContext(contextNodes, options);
        var prompt = RagPromptBuilder.Build(query, plan, options);

        var requestBody = new
        {
            model = _ollamaModel,
            prompt = prompt,
            stream = false,
            // temperature=0 + fixed seed → reproducible answers for the same context (TICKET-005
            // determinism). num_ctx sizes the window so the prompt isn't silently truncated (TICKET-205).
            options = new { temperature = 0, seed = 42, num_ctx = options.NumCtx }
        };

        var ragEndpoint = $"{_ollamaUrl}/api/generate";
        // Flatten line breaks out of the untrusted query before logging: a raw newline lets a caller forge a
        // fake second log line (cs/log-forging). ReplaceLineEndings(" ") is the same flattening RagPromptBuilder
        // and AnswersBenchmark already use.
        _logger.LogInformation("Generating RAG response via Ollama ({Model}) for query: {Query}", _ollamaModel, query.ReplaceLineEndings(" "));

        // A RAG generation is BLOCKING and can take minutes. Retrying it repeatedly could keep a caller
        // waiting for an answer that will not come. So it uses the BLOCKING pipeline (#116): at most ONE
        // retry, and only when the request never reached a responding backend (connection refused / DNS) — a
        // cheap, likely-fixable failure. A timeout or a 5xx mid-generation is NOT retried; the caller gets
        // the error promptly. This is why the policy cannot live on the shared HttpClient's handler: the
        // background path on that same client must retry exactly the failures this one must not.
        //
        // ONLY THE HEADERS ARE READ INSIDE THE PIPELINE (#221). That is what makes "never re-run a generation
        // that already started" true rather than merely intended:
        //
        //   * an exception INSIDE the pipeline now means we never got a response at all — the only thing the
        //     blocking policy may retry;
        //   * reading the BODY happens after the pipeline has returned, so a connection that dies mid-answer
        //     CANNOT be retried. Not by rule — by construction, the same mechanism that already makes an
        //     unusable payload unretriable (#222).
        //
        // #218 tried to draw that line from the exception instead, using HttpRequestException.HttpRequestError.
        // That was wrong, and the Windows-only measurement behind it hid the fact: on Linux a mid-body death
        // reports ConnectionError — identical to a refused connection — so the blocking path re-ran a
        // minutes-long generation on the platform this actually ships on. Measured on Ubuntu 24.04:
        //
        //     failure                Windows          Linux
        //     connection refused     ConnectionError  ConnectionError
        //     reset before response  Unknown          ConnectionError
        //     200 then dies mid-body Unknown          ConnectionError   <-- indistinguishable
        //
        // The exception cannot tell us whether a response arrived. The call site can, so it does.
        {
            {
                using var response = await OllamaResilience.Blocking
                    .ExecuteAsync(async ct =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, ragEndpoint)
                        {
                            Content = JsonContent.Create(requestBody)
                        };
                        return await _httpClient
                            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                            .ConfigureAwait(false);
                    }, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken).ConfigureAwait(false);
                var responseText = responseJson?["response"]?.ToString();

                // A 200 OK with no usable answer in it is a BACKEND FAILURE, and is now reported as one (#225).
                //
                // It used to `return "No answer could be generated."` — a string, handed back through the same
                // channel as a real answer and rendered to the user as though the model had considered the
                // question and declined. It had not: the payload was unusable (a misconfigured model, a wrong
                // response schema, a broken Ollama build). Nothing was logged as a failure and nothing was
                // measured, so a backend malfunctioning on EVERY query presented as a system that politely
                // refuses to answer anything.
                //
                // The codebase already has a real "no answer" path, and it is nothing like this one:
                // GroundingPrep.NoEvidence abstains DETERMINISTICALLY, without calling the LLM at all, and says
                // so (`grounded = false`). Conflating a broken backend with a considered abstention is what made
                // the failure invisible.
                //
                // So this now throws, exactly as AnalyzeNodeAsync does for the identical condition — the three
                // Ollama call paths finally agree on what a 200-with-garbage means. The endpoints already turn
                // it into a logged 500 (EndpointHelpers.Fail → Results.Problem), which is what an infrastructure
                // failure should look like.
                if (string.IsNullOrWhiteSpace(responseText))
                {
                    throw new OllamaResponseException(
                        "Ollama returned a 200 OK with no usable answer. The backend is misconfigured or " +
                        "malfunctioning — this is not the model declining to answer.");
                }

                WarnIfPromptTruncated(responseJson, options.NumCtx);

                // Grounding safety net (TICKET-206): flag any citation the model invented (a source not in
                // the context) so an ungrounded reference is visible, not silently trusted.
                var validNames = RagPromptBuilder.ValidCitationNames(plan.Nodes);
                return CitationValidator.AnnotateInvalid(responseText, validNames, options.Language);
            }
        }
    }

    /// <summary>
    /// Streams the grounded RAG answer token-by-token from Ollama (<c>stream=true</c>, NDJSON), so the UI
    /// shows first tokens immediately instead of waiting for the whole generation (TICKET-104). Uses the same
    /// grounded prompt as <see cref="GenerateRAGResponseAsync"/>. If the stream ends before Ollama's terminal
    /// <c>done</c> line (e.g. the backend is killed mid-answer), a truncation marker is emitted so the answer
    /// isn't silently presented as complete.
    ///
    /// <para>
    /// <b>The retry is scoped to the pre-header send (#224 established why that is safe; #243 acted on it).</b>
    /// The send uses <c>HttpCompletionOption.ResponseHeadersRead</c>, so a pipeline around it can only ever see
    /// failures from <i>before the headers arrived</i> — where nothing has been yielded and a restart is
    /// indistinguishable from a first attempt. A failure <i>after</i> bytes are flowing happens once the send
    /// has already returned, so it is unretriable <b>by construction</b>, exactly like the deterministic
    /// failures of #222 and the mid-body deaths of #221. The completion option is the safety mechanism, not the
    /// absence of a pipeline — so a pipeline that only touches the pre-header phase is safe to add, and it gives
    /// the user-facing streaming path the same cheap connection recovery the blocking path always had.
    /// </para>
    /// <para>
    /// The property <c>StreamingNoRetryTests</c> pins still holds: <b>a token is never emitted twice, and the
    /// backend is never asked to generate the same answer again.</b> The pipeline is
    /// <c>OllamaResilience.Blocking</c> — one retry, <i>connection failures only</i> — so a 5xx is a response the
    /// pipeline returns and <c>EnsureSuccessStatusCode</c> throws outside it (not retried, matching the blocking
    /// path), and a body-phase failure is out of the pipeline's reach.
    /// </para>
    /// </summary>
    public IAsyncEnumerable<string> StreamRAGResponseAsync(
        string query,
        IReadOnlyList<GraphNode> contextNodes,
        CancellationToken cancellationToken = default)
        => StreamRAGResponseAsync(query, contextNodes, RagPromptOptions.Default, cancellationToken);

    public async IAsyncEnumerable<string> StreamRAGResponseAsync(
        string query,
        IReadOnlyList<GraphNode> contextNodes,
        RagPromptOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var plan = RagPromptBuilder.PlanContext(contextNodes, options);
        var requestBody = new
        {
            model = _ollamaModel,
            prompt = RagPromptBuilder.Build(query, plan, options),
            stream = true,
            options = new { temperature = 0, seed = 42, num_ctx = options.NumCtx }
        };

        // Retry ONLY the pre-header send, and only for a genuine connection failure (#243). This is now safe
        // to reason about because of #224: ResponseHeadersRead returns the moment the headers arrive, so a
        // failure the pipeline can see is always one from BEFORE any byte was yielded — nothing has streamed,
        // so restarting is indistinguishable from a first attempt. A failure while reading the BODY happens
        // after ExecuteAsync has already returned, and is therefore never retried, which is the property
        // StreamingNoRetryTests pins. So the user-facing streaming path finally gets the same cheap recovery
        // the blocking path always had — a backend that was merely not listening yet no longer fails the answer
        // outright — without any risk of a replayed token. Uses OllamaResilience.Blocking (one retry, connect
        // errors only): a 5xx is a response, so the pipeline returns it and EnsureSuccessStatusCode below throws
        // it OUTSIDE the retry, exactly as the blocking path treats a mid-generation 5xx.
        //
        // The request is built INSIDE the callback because an HttpRequestMessage can be sent only once; each
        // attempt needs a fresh one.
        using var response = await OllamaResilience.Blocking.ExecuteAsync(async ct =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_ollamaUrl}/api/generate")
            {
                Content = JsonContent.Create(requestBody)
            };
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        // Idle-timeout guard for the body (#230): under ResponseHeadersRead, HttpClient.Timeout stops applying
        // once headers are in, so a backend that emits a few tokens and then stalls would hang the request
        // forever. This linked source is armed ONLY around each read and disarmed the instant a line arrives,
        // so it times the backend's silence, not the consumer's backpressure at `yield` — a slow-but-alive
        // model keeps resetting it, and only a wedged backend (no token for the whole window) trips it.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Buffer the full answer alongside streaming it, so a citation-validation footer can be appended
        // once the whole answer is known (a single [Name @ …] can span two chunks) — TICKET-206.
        var full = new System.Text.StringBuilder();

        // Ollama streams one JSON object per line: {"response":"…","done":false} … {"done":true}.
        var completed = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line;
            idleCts.CancelAfter(_streamIdleTimeout); // arm the idle window for this read
            try
            {
                line = await reader.ReadLineAsync(idleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The window elapsed with no new token AND the client did not disconnect → the backend stalled
                // mid-answer. Surface it as a failure — it joins the mid-stream failure path #227 fixed (a 500
                // if nothing was yielded yet, otherwise the endpoint's "answer incomplete" marker) — instead of
                // hanging forever. The `when` guard keeps a genuine client disconnect on its quiet path (#227).
                throw new OllamaResponseException(
                    $"The Ollama stream went idle for over {_streamIdleTimeout.TotalSeconds:0}s mid-answer — the backend appears wedged (no further tokens, no completion).");
            }
            idleCts.CancelAfter(Timeout.InfiniteTimeSpan); // disarm: a token arrived; don't count processing/yield time
            if (line is null)
            {
                break; // end of stream
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? token = null;
            var done = false;
            JsonObject? obj = null;
            try
            {
                obj = JsonNode.Parse(line)?.AsObject();
                token = obj?["response"]?.ToString();
                done = obj?["done"]?.GetValue<bool>() ?? false;
            }
            catch (JsonException)
            {
                // Skip a malformed line rather than aborting the whole stream.
                continue;
            }

            if (!string.IsNullOrEmpty(token))
            {
                full.Append(token);
                yield return token;
            }
            if (done)
            {
                WarnIfPromptTruncated(obj, options.NumCtx); // the terminal line carries prompt_eval_count
                completed = true;
                break;
            }
        }

        // A stream that ran to done=true having emitted NOTHING is a backend malfunction, not a blank answer —
        // the same rule the blocking path now applies (#225), and for the same reason: an empty answer is
        // indistinguishable from a broken model, so presenting it as an answer hides the failure. Nothing has
        // been yielded at this point, so the response has not started and the endpoint can still return a
        // proper 500 rather than a successful, empty one (#227).
        if (completed && full.Length == 0)
        {
            throw new OllamaResponseException(
                "Ollama streamed a complete response containing no tokens. The backend is misconfigured or " +
                "malfunctioning — this is not the model declining to answer.");
        }

        if (completed)
        {
            // Append the invalid-citation footer (if any) after the model's own text — never rewrite it.
            var validNames = RagPromptBuilder.ValidCitationNames(plan.Nodes);
            var report = CitationValidator.Validate(full.ToString(), validNames);
            if (report.HasInvalidCitations)
            {
                // Fixed marker — ALWAYS English, identical to CitationValidator's footer so app.js strips it
                // consistently across the streaming and blocking paths (regardless of answer language).
                var sb = new System.Text.StringBuilder("\n\n");
                sb.AppendLine("> ⚠ **Unsupported sources:** the following cited sources are NOT in the provided context and are therefore unverified:");
                foreach (var name in report.InvalidCitations) sb.AppendLine($"> - {name}");
                yield return sb.ToString();
            }
        }
        else
        {
            // Stream ended without Ollama's terminal done=true — the backend was cut off mid-answer.
            // Emit a marker so the caller/UI doesn't present a truncated answer as complete.
            yield return "\n\n_… [Answer incomplete — connection to the model was interrupted]_";
        }
    }

    /// <summary>
    /// Detects a silently truncated prompt (TICKET-205): if Ollama actually consumed as many prompt tokens
    /// as the whole window (<c>prompt_eval_count ≥ num_ctx</c>), the front of the prompt was dropped. Logged
    /// as a warning so an operator sees it instead of it being swallowed. Best-effort — some responses omit
    /// the count.
    /// </summary>
    private void WarnIfPromptTruncated(JsonObject? responseJson, int numCtx)
    {
        if (responseJson?["prompt_eval_count"] is not { } node) return;
        int promptTokens;
        try { promptTokens = node.GetValue<int>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return; }

        if (promptTokens >= numCtx)
        {
            _logger.LogWarning(
                "RAG prompt likely truncated by the model: prompt_eval_count={PromptTokens} reached num_ctx={NumCtx}. " +
                "Reduce context nodes or raise SemanticAnalyzer:NumCtx.", promptTokens, numCtx);
        }
    }
}
