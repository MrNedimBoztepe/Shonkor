// Licensed to Shonkor under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web;

/// <summary>
/// Cross-cutting helpers shared by the minimal-API endpoint groups: uniform error responses,
/// per-request tenant/storage resolution, loopback detection, and the (opt-in) dynamic-plugin loading.
/// </summary>
public static class EndpointHelpers
{
    /// <summary>
    /// The logger every endpoint group hands to <see cref="Fail(ILogger, string, Exception)"/> (#256).
    ///
    /// <para>
    /// Resolved once per group at registration time and captured by the endpoint lambdas, which is why this is
    /// not a static field: <c>WebApplicationFactory</c> boots several hosts concurrently under xUnit, and a
    /// static logger would be shared — last writer wins — across hosts that each configured their own.
    /// Resolving from <paramref name="app"/>'s own provider gives each host its own.
    /// </para>
    /// </summary>
    public static ILogger ApiLogger(this WebApplication app) =>
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Shonkor.Api");

    /// <summary>
    /// Logs the full exception server-side but returns only a generic message to the client,
    /// so internal paths/stack details are never leaked over the API.
    ///
    /// <para>
    /// Goes through <see cref="ILogger"/> rather than <c>Console.Error</c> (#256), so these paths honour log
    /// levels and configuration like everything else, arrive structured, and can be routed or silenced by the
    /// host — including a test host that expects the failure and no longer has to print a scary stack trace to
    /// prove it happened.
    /// </para>
    /// </summary>
    public static IResult Fail(ILogger logger, string clientMessage, Exception ex)
    {
        logger.LogError(ex, "[API] {ClientMessage}", clientMessage);
        return Results.Problem(clientMessage);
    }

    /// <summary>
    /// As <see cref="Fail(string, Exception)"/>, but also carries a stable machine-readable failure class in
    /// the problem document's <c>code</c> extension (#228).
    ///
    /// <para>
    /// The generic client message stays exactly as generic — no path, no SQL, no stack. The code adds no
    /// detail, only an identity: it says <i>which kind</i> of failure this was, so a dashboard or an operator
    /// can tell "Ollama is not running" from "your model returns garbage" from "raise the timeout" — remedies
    /// with nothing in common that previously all read "Failed to generate RAG response.".
    /// </para>
    /// </summary>
    public static IResult Fail(ILogger logger, string clientMessage, string code, Exception ex)
    {
        logger.LogError(ex, "[API] {ClientMessage} ({FailureCode})", clientMessage, code);
        return Results.Problem(clientMessage, extensions: new Dictionary<string, object?> { ["code"] = code });
    }

    /// <summary>
    /// Resolves the storage provider for the current request's tenant, taken from the
    /// <c>X-Project-Name</c> header (set authoritatively by <see cref="Middleware.ApiKeyMiddleware"/>),
    /// falling back to the active project when absent.
    /// </summary>
    public static Task<IGraphStorageProvider> GetStorageForRequestAsync(this ProjectManager pm, HttpContext context, CancellationToken ct)
    {
        var projectName = context.Request.Headers["X-Project-Name"].ToString();
        return string.IsNullOrEmpty(projectName)
            ? pm.GetActiveStorageProviderAsync(ct)
            : pm.GetStorageProviderAsync(projectName, ct);
    }

    /// <summary>True when the request originates from the loopback interface (local dashboard).</summary>
    public static bool IsLoopback(this HttpContext context) =>
        context.Connection.RemoteIpAddress != null &&
        IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

    /// <summary>
    /// Global kill-switch for the assembly-plugin system. The real trust gate is now per-plugin activation
    /// (installing a plugin runs nothing), so this defaults to ON; set <c>Security:EnablePlugins=false</c>
    /// to hard-disable loading every plugin regardless of its activation state.
    /// </summary>
    public static bool PluginsEnabled(IConfiguration config) => config.GetValue("Security:EnablePlugins", true);

    /// <summary>
    /// Builds the file-parser list the MCP relay hands to <c>reindex_file</c>. Tenant-locked (SaaS) sessions
    /// get <c>null</c> — the tenant's files are not on this server, so reindex_file stays disabled and cannot
    /// wrongly clear a file's graph. A trusted-local session gets the base parsers PLUS the active plugin
    /// parsers.
    /// <para>
    /// Merging the plugins is NOT optional: since #292 the JS/TS parser lives in the <c>shonkor-typescript</c>
    /// plugin rather than the host's DI parser list. An un-merged list would parse <c>.ts/.tsx/.js/.jsx</c>
    /// with no parser, so <c>reindex_file</c> would produce zero nodes and then DELETE the file's existing
    /// JSComponent/IMPORTS nodes (<c>ScanFileAsync</c> clears a file with no matching parser) — silent data
    /// loss over the HTTP relay. This helper is the single, tested place that construction lives.
    /// </para>
    /// </summary>
    public static IEnumerable<IFileParser>? BuildRelayFileParsers(
        bool isTenantLocked,
        IEnumerable<IFileParser>? baseParsers,
        IReadOnlyList<IFileParser> activePluginParsers)
    {
        if (isTenantLocked) return null;
        var merged = new List<IFileParser>(baseParsers ?? Enumerable.Empty<IFileParser>());
        merged.AddRange(activePluginParsers);
        return merged;
    }

    /// <summary>
    /// Whether to use exact semantic C# resolution when indexing <paramref name="project"/>: the
    /// per-project <see cref="Project.SemanticCSharp"/> setting wins; otherwise the global
    /// <c>Indexing:SemanticCSharp</c> setting applies, which itself defaults to ON when unset (semantic
    /// resolution is what earns EXTRACTED provenance). Set the global or per-project value to false to opt out.
    /// </summary>
    public static bool UseSemanticCSharp(Project project, IConfiguration config) =>
        project.SemanticCSharp ?? config.GetValue("Indexing:SemanticCSharp", true);

}
