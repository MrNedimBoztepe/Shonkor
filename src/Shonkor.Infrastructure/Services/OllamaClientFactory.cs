// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// The one place a non-DI host builds an Ollama-backed service (#179).
///
/// <para>
/// There are three construction paths — the web host's typed clients, the CLI (which is also the <b>MCP stdio
/// server</b>), and the bench — and until now they had three different <i>shapes</i>: DI on one side, and
/// <c>new OllamaEmbeddingService(new HttpClient(), config, NullLogger…)</c> hand-assembled five times on the
/// other. That is what made "just put the resilience policy in the DI registration" look like a complete
/// answer: from inside <c>Shonkor.Web</c> the registration <i>is</i> the only construction site you can see.
/// It isn't, and a DI-only policy would have left the CLI and bench with no retry at all
/// (see <see cref="OllamaResilience"/>).
/// </para>
/// <para>
/// The resilience policy travels with the <b>service</b>, not with the <c>HttpClient</c>, so every path here
/// gets it for free and no caller has to remember to attach it. Equally: <b>no caller should attach one</b> —
/// a handler-level policy would nest inside the call-site pipeline and multiply the retries.
/// </para>
/// <para>
/// Configuration is passed in rather than built here: the CLI and bench read the environment, the web host
/// reads its own configuration stack, and this type has no business choosing between them.
/// </para>
/// </summary>
public static class OllamaClientFactory
{
    /// <summary>
    /// An embedding service over its own <see cref="HttpClient"/>, or over <paramref name="httpClient"/> when
    /// the caller already owns one it wants shared (the CLI's MCP path reuses a single client across probes
    /// and requests, and owns its lifetime).
    /// </summary>
    public static OllamaEmbeddingService CreateEmbeddingService(
        IConfiguration configuration,
        ILogger<OllamaEmbeddingService>? logger = null,
        HttpClient? httpClient = null) =>
        new(httpClient ?? new HttpClient(), configuration, logger ?? NullLogger<OllamaEmbeddingService>.Instance);

    /// <summary>The semantic-analysis counterpart, same contract.</summary>
    public static OllamaSemanticAnalyzer CreateSemanticAnalyzer(
        IConfiguration configuration,
        ILogger<OllamaSemanticAnalyzer>? logger = null,
        HttpClient? httpClient = null) =>
        new(httpClient ?? new HttpClient(), configuration, logger ?? NullLogger<OllamaSemanticAnalyzer>.Instance);
}
