// Licensed to Shonkor under the MIT License.

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shonkor.Infrastructure.Services;

using static Shonkor.Web.EndpointHelpers;

namespace Shonkor.Web.Endpoints;

/// <summary>Multi-project registry management: list/add/delete, active-project switching, and per-project config.</summary>
public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        // GET /api/projects - list all registered projects + the active one.
        app.MapGet("/api/projects", (ProjectManager pm) =>
            Results.Ok(new { Projects = pm.GetProjects(), ActiveProject = pm.GetActiveProjectName() }));

        // POST /api/projects - register a new project.
        app.MapPost("/api/projects", (Project newProject, ProjectManager pm) =>
        {
            if (string.IsNullOrWhiteSpace(newProject.Name) || string.IsNullOrWhiteSpace(newProject.Path))
            {
                return Results.BadRequest("Project Name and Path are required.");
            }

            try
            {
                pm.AddProject(newProject.Name, newProject.Path, newProject.DatabasePath);
                return Results.Ok(new { Message = $"Project '{newProject.Name}' registered successfully." });
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return Fail("Failed to add project.", ex);
            }
        });

        // DELETE /api/projects/{name} - deregister a project.
        app.MapDelete("/api/projects/{name}", (string name, ProjectManager pm) =>
        {
            try
            {
                pm.DeleteProject(name);
                return Results.Ok(new { Message = $"Project '{name}' removed successfully." });
            }
            catch (Exception ex)
            {
                return Fail("Failed to delete project.", ex);
            }
        });

        // POST /api/projects/active - switch the active project.
        app.MapPost("/api/projects/active", (ActiveProjectRequest req, ProjectManager pm) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
            {
                return Results.BadRequest("Project name is required.");
            }

            try
            {
                pm.SetActiveProject(req.Name);
                return Results.Ok(new { Message = $"Active project set to '{req.Name}'." });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return Fail("Failed to switch active project.", ex);
            }
        });

        // POST /api/projects/{name}/semantic - set/clear the per-project semantic-C# override.
        app.MapPost("/api/projects/{name}/semantic", (string name, SemanticRequest req, ProjectManager pm) =>
        {
            try
            {
                pm.SetProjectSemantic(name, req.SemanticCSharp);
                var label = req.SemanticCSharp switch { true => "semantic", false => "syntactic", _ => "default" };
                return Results.Ok(new { Message = $"Project '{name}' set to {label} indexing.", SemanticCSharp = req.SemanticCSharp });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return Fail("Failed to update semantic setting.", ex);
            }
        });

        // GET /api/projects/{name}/config - read a project's shonkor.json config.
        app.MapGet("/api/projects/{name}/config", (string name, ProjectManager pm) =>
        {
            try
            {
                return Results.Ok(pm.GetProjectConfig(name));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return Fail("Failed to retrieve config.", ex);
            }
        });

        // POST /api/projects/{name}/config - persist a project's shonkor.json config.
        app.MapPost("/api/projects/{name}/config", (string name, WebConfig newConfig, ProjectManager pm) =>
        {
            try
            {
                pm.SaveProjectConfig(name, newConfig);
                return Results.Ok(new { Message = $"Configuration for '{name}' saved successfully." });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (Exception ex)
            {
                return Fail("Failed to save config.", ex);
            }
        });
    }
}
