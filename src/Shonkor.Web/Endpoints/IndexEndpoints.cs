// Licensed to Shonkor under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;

using static Shonkor.Web.EndpointHelpers;

namespace Shonkor.Web.Endpoints;

/// <summary>Triggers the file-indexing scanner for a project (with single-scan guard and opt-in plugins).</summary>
public static class IndexEndpoints
{
    public static void MapIndexEndpoints(this WebApplication app)
    {
        // Resolved once here and captured by the lambdas below (#256) — see EndpointHelpers.ApiLogger.
        var log = app.ApiLogger();
        // POST /api/index - scan and (re)index a project's directory into the graph.
        app.MapPost("/api/index", async (IndexRequest? request, HttpContext context, IConfiguration config, ProjectManager pm, IEnumerable<IFileParser> parsers, SemanticCompilationCache compilationCache, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            try
            {
                var projectName = context.Request.Headers["X-Project-Name"].ToString();
                var project = string.IsNullOrWhiteSpace(projectName)
                    ? pm.GetActiveProject()
                    : pm.GetProjects().FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

                if (project == null)
                {
                    return Results.BadRequest("No project configured.");
                }

                // The caller may narrow the scan to a subdirectory, but NOT escape the project. `Directory` is
                // untrusted input; without this an authenticated tenant could point it at any path on the server
                // (e.g. "/etc", "C:\\") and read arbitrary source into their graph, then retrieve it via /api/rag.
                // So the target must be the project root itself or something provably inside it (#code-scanning
                // cs/path-injection). project.Path is trusted (set at project creation), so it is the base.
                var projectRoot = Path.GetFullPath(project.Path);
                var targetDir = Path.GetFullPath(request?.Directory ?? project.Path);
                if (!FilePaths.AreEqual(targetDir, projectRoot) && !FilePaths.TryGetRelative(targetDir, projectRoot, out _))
                {
                    return Results.BadRequest("The index directory must be within the project's own path.");
                }

                if (!Directory.Exists(targetDir))
                {
                    return Results.BadRequest($"Target directory does not exist: {targetDir}");
                }

                var projectConfig = pm.GetProjectConfig(project.Name);
                var exclusions = request?.ExcludePatterns ?? projectConfig.ExcludePatterns;

                // Prevent concurrent scans of the same project (duplicate work / racey deletes).
                if (!pm.TryBeginScan(project.Name))
                {
                    return Results.Conflict($"An index scan is already running for project '{project.Name}'.");
                }

                try
                {
                    // Load the workspace's ACTIVE plugins (pre-built assemblies; no compilation). Installation
                    // is inert — only plugins the user explicitly activated load here.
                    var activeParsers = new List<IFileParser>(parsers);
                    using var pluginLoad = PluginsEnabled(config)
                        ? AssemblyPluginLoader.LoadActive(pm.WorkspacePath)
                        : AssemblyPluginLoadResult.Empty;
                    activeParsers.AddRange(pluginLoad.Parsers);

                    var storage = await pm.GetStorageProviderAsync(project.Name, ct);
                    // Per-project config the post-processors may use (e.g. the Sitecore clrtype resolver's
                    // user-declared external/third-party namespace prefixes).
                    var ppContext = new Shonkor.Core.Models.GraphPostProcessorContext
                    {
                        ExternalTypePrefixes = project.ExternalTypePrefixes ?? new List<string>()
                    };
                    var scanner = new GraphIndexScanner(storage, activeParsers, loggerFactory.CreateLogger("Shonkor.Index"),
                        semanticCsharp: UseSemanticCSharp(project, config), compilationCache: compilationCache,
                        postProcessors: pluginLoad.PostProcessors, postProcessorContext: ppContext);

                    var result = await scanner.ScanDirectoryAsync(targetDir, exclusions, ct);
                    var stats = await storage.GetStatisticsAsync(ct);

                    return Results.Ok(new
                    {
                        Message = "Indexing completed successfully.",
                        Result = result,
                        Stats = stats
                    });
                }
                finally
                {
                    pm.EndScan(project.Name);
                }
            }
            catch (Exception ex)
            {
                return Fail(log, "Indexing operation failed.", ex);
            }
        });
    }
}
