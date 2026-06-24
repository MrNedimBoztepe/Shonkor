// Licensed to Shonkor under the MIT License.

using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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
    /// Logs the full exception server-side but returns only a generic message to the client,
    /// so internal paths/stack details are never leaked over the API.
    /// </summary>
    public static IResult Fail(string clientMessage, Exception ex)
    {
        Console.Error.WriteLine($"[API] {clientMessage} :: {ex}");
        return Results.Problem(clientMessage);
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
    /// Whether to use exact semantic C# resolution when indexing <paramref name="project"/>: the
    /// per-project <see cref="Project.SemanticCSharp"/> setting wins; otherwise the global
    /// <c>Indexing:SemanticCSharp</c> default applies.
    /// </summary>
    public static bool UseSemanticCSharp(Project project, IConfiguration config) =>
        project.SemanticCSharp ?? config.GetValue<bool>("Indexing:SemanticCSharp");

}
