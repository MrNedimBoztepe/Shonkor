// Licensed to Shonkor under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using static Shonkor.Web.EndpointHelpers;

namespace Shonkor.Web.Endpoints;

/// <summary>
/// Local filesystem folder browser for the dashboard's project picker. Exposes the host filesystem,
/// so it is restricted to loopback callers and disabled outside Development unless explicitly opted in.
/// </summary>
public static class BrowseEndpoints
{
    public static void MapBrowseEndpoints(this WebApplication app)
    {
        app.MapGet("/api/browse", (string? path, HttpContext context, IHostEnvironment env, IConfiguration config) =>
        {
            var allowBrowse = config.GetValue<bool?>("Security:AllowFilesystemBrowse") ?? env.IsDevelopment();
            if (!allowBrowse || !context.IsLoopback())
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            try
            {
                // Empty path: list logical drives.
                if (string.IsNullOrWhiteSpace(path))
                {
                    return Results.Ok(new
                    {
                        CurrentPath = string.Empty,
                        ParentPath = string.Empty,
                        Folders = new List<string>(),
                        Drives = Directory.GetLogicalDrives().ToList()
                    });
                }

                var targetPath = Path.GetFullPath(path);
                if (!Directory.Exists(targetPath))
                {
                    return Results.BadRequest($"Path does not exist: {targetPath}");
                }

                var folders = Directory.GetDirectories(targetPath)
                                       .Select(Path.GetFileName)
                                       .Where(name => !string.IsNullOrEmpty(name) && !name.StartsWith(".")) // Exclude hidden folders
                                       .OrderBy(name => name)
                                       .ToList();

                return Results.Ok(new
                {
                    CurrentPath = targetPath,
                    ParentPath = Directory.GetParent(targetPath)?.FullName ?? string.Empty,
                    Folders = folders,
                    Drives = new List<string>()
                });
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Problem("Access denied to the specified directory.", statusCode: 403);
            }
            catch (Exception ex)
            {
                return Fail("Failed to browse directory.", ex);
            }
        });
    }
}
