// Licensed to Shonkor under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

using static Shonkor.Web.EndpointHelpers;

namespace Shonkor.Web.Endpoints;

/// <summary>Dynamic plugin management (list/create/delete) and the aggregated node-type catalog.</summary>
public static class PluginEndpoints
{
    public static void MapPluginEndpoints(this WebApplication app)
    {
        // GET /api/plugins - list dynamic plugin files and whether they currently load.
        app.MapGet("/api/plugins", (IConfiguration config, ProjectManager pm) =>
        {
            try
            {
                var pluginsDir = Path.Combine(pm.WorkspacePath, "plugins");
                if (!Directory.Exists(pluginsDir))
                {
                    return Results.Ok(new { Plugins = Array.Empty<object>(), PluginsDirectory = pluginsDir });
                }

                var pluginFiles = Directory.GetFiles(pluginsDir, "*.cs");

                // Compile the whole directory ONCE (only when plugins are enabled); the collectible
                // context is unloaded right after.
                var pluginsEnabled = PluginsEnabled(config);
                var loadedCount = 0;
                if (pluginsEnabled)
                {
                    using var trialLoad = LoadWorkspacePlugins(pm.WorkspacePath);
                    loadedCount = trialLoad.Parsers.Count;
                }

                var anyLoaded = loadedCount > 0;
                var plugins = pluginFiles.Select(file => new
                {
                    FileName = Path.GetFileName(file),
                    Name = Path.GetFileNameWithoutExtension(file),
                    FullPath = file,
                    Status = !pluginsEnabled ? "disabled" : (anyLoaded ? "loaded" : "no-parser"),
                    Error = "",
                    LastModified = File.GetLastWriteTimeUtc(file).ToString("o"),
                    SizeBytes = new FileInfo(file).Length
                }).ToList();

                return Results.Ok(new { Plugins = plugins, PluginsDirectory = pluginsDir, PluginsEnabled = pluginsEnabled });
            }
            catch (Exception ex)
            {
                return Fail("Failed to list plugins.", ex);
            }
        });

        // POST /api/plugins/create - scaffold a boilerplate C# plugin (local-trust, plugins-enabled only).
        app.MapPost("/api/plugins/create", (PluginCreateRequest req, HttpContext context, IConfiguration config, IHostEnvironment env, ProjectManager pm) =>
        {
            try
            {
                // Writing a plugin only makes sense if plugins can run, and authoring is a local-trust action.
                var allowAuthoring = config.GetValue<bool?>("Security:AllowFilesystemBrowse") ?? env.IsDevelopment();
                if (!PluginsEnabled(config) || !allowAuthoring || !context.IsLoopback())
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Extension))
                {
                    return Results.BadRequest("Plugin Name and file Extension are required.");
                }

                var pluginsDir = Path.Combine(pm.WorkspacePath, "plugins");
                Directory.CreateDirectory(pluginsDir);

                // Sanitize name for C# class: PascalCase, no spaces.
                var className = string.Concat(req.Name.Split(' ', '-', '_', '.')
                    .Where(s => s.Length > 0)
                    .Select(s => char.ToUpperInvariant(s[0]) + s[1..])) + "Parser";

                var ext = req.Extension.StartsWith(".") ? req.Extension : "." + req.Extension;
                var filePath = Path.Combine(pluginsDir, $"{className}.cs");

                if (File.Exists(filePath))
                {
                    return Results.BadRequest($"Plugin file '{className}.cs' already exists.");
                }

                File.WriteAllText(filePath, GeneratePluginBoilerplate(className, ext, req.Name));

                return Results.Ok(new
                {
                    Message = $"Plugin '{className}' created successfully at {filePath}.",
                    FileName = $"{className}.cs",
                    FullPath = filePath,
                    Extension = ext
                });
            }
            catch (Exception ex)
            {
                return Fail("Failed to create plugin.", ex);
            }
        });

        // DELETE /api/plugins/{fileName} - delete a plugin source file.
        app.MapDelete("/api/plugins/{fileName}", (string fileName, ProjectManager pm) =>
        {
            try
            {
                var filePath = Path.Combine(pm.WorkspacePath, "plugins", fileName);
                if (!File.Exists(filePath))
                {
                    return Results.NotFound($"Plugin file '{fileName}' not found.");
                }

                File.Delete(filePath);
                return Results.Ok(new { Message = $"Plugin '{fileName}' deleted successfully." });
            }
            catch (Exception ex)
            {
                return Fail("Failed to delete plugin.", ex);
            }
        });

        // GET /api/node-types - aggregate node types across core parsers, dynamic plugins, and system types.
        app.MapGet("/api/node-types", (IConfiguration config, ProjectManager pm, IEnumerable<IFileParser> coreParsers) =>
        {
            try
            {
                var allParsers = new List<IFileParser>(coreParsers);

                using var pluginLoad = PluginsEnabled(config)
                    ? AssemblyPluginLoader.LoadActive(pm.WorkspacePath)
                    : AssemblyPluginLoadResult.Empty;
                allParsers.AddRange(pluginLoad.Parsers);

                var types = allParsers
                    .SelectMany(p => p.NodeTypeDescriptors)
                    .GroupBy(t => t.TypeName)
                    .Select(g => g.First())
                    .ToList();

                // System types not produced by a file parser.
                types.Add(new NodeTypeDescriptor("File", "Documentation", true));
                types.Add(new NodeTypeDescriptor("Task", "Interaction", true));
                types.Add(new NodeTypeDescriptor("Decision", "Interaction", true));
                types.Add(new NodeTypeDescriptor("Milestone", "Interaction", true));
                types.Add(new NodeTypeDescriptor("Question", "Interaction", true));
                types.Add(new NodeTypeDescriptor("HelixModule", "Code", true)); // From CrossTechLinker

                return Results.Ok(new { Types = types });
            }
            catch (Exception ex)
            {
                return Fail("Failed to get node types.", ex);
            }
        });
    }
}
