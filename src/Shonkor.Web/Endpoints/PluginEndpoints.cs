// Licensed to Shonkor under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

using static Shonkor.Web.EndpointHelpers;

namespace Shonkor.Web.Endpoints;

/// <summary>
/// Assembly-plugin management: list installed plugins, install one from an uploaded ZIP (inert),
/// activate/deactivate, and uninstall — plus the aggregated node-type catalog. Installing or changing a
/// plugin's state is a local-trust action (loopback only). Installation never loads code; only activation
/// does (see <see cref="PluginRegistry"/> / <see cref="AssemblyPluginLoader"/>).
/// </summary>
public static class PluginEndpoints
{
    public static void MapPluginEndpoints(this WebApplication app)
    {
        // GET /api/plugins - list installed plugins with their lifecycle state.
        app.MapGet("/api/plugins", (ProjectManager pm) =>
        {
            try
            {
                var registry = new PluginRegistry(pm.WorkspacePath);
                var plugins = registry.List().Select(p => new
                {
                    p.Manifest.Id,
                    p.Manifest.Name,
                    p.Manifest.Version,
                    p.Manifest.Description,
                    State = p.State.ToString(),
                    Active = p.State == PluginState.Active,
                    Extensions = p.Manifest.TargetExtensions,
                    p.InstalledAtUtc,
                    p.Error
                }).ToList();
                return Results.Ok(new { Plugins = plugins, PluginsDirectory = Path.Combine(pm.WorkspacePath, "plugins") });
            }
            catch (Exception ex)
            {
                return Fail("Failed to list plugins.", ex);
            }
        });

        // POST /api/plugins/install - upload a plugin ZIP (multipart field 'package'). Inert until activated.
        app.MapPost("/api/plugins/install", async (HttpContext context, ProjectManager pm) =>
        {
            if (!context.IsLoopback())
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            if (!context.Request.HasFormContentType)
            {
                return Results.BadRequest("Upload the plugin ZIP as multipart/form-data (field 'package').");
            }

            var form = await context.Request.ReadFormAsync();
            var file = form.Files["package"] ?? form.Files.FirstOrDefault();
            if (file == null || file.Length == 0)
            {
                return Results.BadRequest("No plugin ZIP uploaded.");
            }

            var temp = Path.Combine(Path.GetTempPath(), $"shonkor_plugin_{Guid.NewGuid():N}.zip");
            try
            {
                await using (var fs = File.Create(temp))
                {
                    await file.CopyToAsync(fs);
                }
                var result = new PluginRegistry(pm.WorkspacePath).InstallFromZip(temp);
                return result.Success
                    ? Results.Ok(new { result.Message, Plugin = result.Plugin })
                    : Results.BadRequest(result.Message);
            }
            catch (Exception ex)
            {
                return Fail("Failed to install plugin.", ex);
            }
            finally
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        });

        // POST /api/plugins/{id}/activate - load this plugin on subsequent indexing.
        app.MapPost("/api/plugins/{id}/activate", (string id, HttpContext context, ProjectManager pm) =>
            RunTransition(context, pm, r => r.Activate(id)));

        // POST /api/plugins/{id}/deactivate - stop loading this plugin.
        app.MapPost("/api/plugins/{id}/deactivate", (string id, HttpContext context, ProjectManager pm) =>
            RunTransition(context, pm, r => r.Deactivate(id)));

        // DELETE /api/plugins/{id} - uninstall (remove files + registry entry).
        app.MapDelete("/api/plugins/{id}", (string id, HttpContext context, ProjectManager pm) =>
            RunTransition(context, pm, r => r.Uninstall(id)));

        // GET /api/node-types - aggregate node types across core parsers, active plugins, and system types.
        app.MapGet("/api/node-types", (ProjectManager pm, IEnumerable<IFileParser> coreParsers) =>
        {
            try
            {
                var allParsers = new List<IFileParser>(coreParsers);

                using var pluginLoad = AssemblyPluginLoader.LoadActive(pm.WorkspacePath);
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

    private static IResult RunTransition(HttpContext context, ProjectManager pm, Func<PluginRegistry, PluginOperationResult> op)
    {
        if (!context.IsLoopback())
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        try
        {
            var result = op(new PluginRegistry(pm.WorkspacePath));
            return result.Success ? Results.Ok(new { result.Message }) : Results.BadRequest(result.Message);
        }
        catch (Exception ex)
        {
            return Fail("Plugin operation failed.", ex);
        }
    }
}
