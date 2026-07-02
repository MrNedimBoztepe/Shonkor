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
using Shonkor.Infrastructure.Services;

using static Shonkor.Web.EndpointHelpers;

namespace Shonkor.Web.Endpoints;

/// <summary>Triggers the file-indexing scanner for a project (with single-scan guard and opt-in plugins).</summary>
public static class IndexEndpoints
{
    public static void MapIndexEndpoints(this WebApplication app)
    {
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

                var targetDir = Path.GetFullPath(request?.Directory ?? project.Path);
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
                    var scanner = new GraphIndexScanner(storage, activeParsers, loggerFactory.CreateLogger("Shonkor.Index"),
                        semanticCsharp: UseSemanticCSharp(project, config), compilationCache: compilationCache,
                        postProcessors: pluginLoad.PostProcessors.Concat(Shonkor.Infrastructure.Services.FirstPartyPostProcessors.Create()));

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
                return Fail("Indexing operation failed.", ex);
            }
        });
    }
}
