using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Shonkor.Infrastructure.Services;
using System.Security.Cryptography;
using System.Text;

namespace Shonkor.Web.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin");

        // Organizations
        group.MapGet("/orgs", ([FromServices] ProjectManager projectManager) =>
        {
            return Results.Ok(projectManager.GetOrganizations());
        });

        group.MapPost("/orgs", ([FromBody] CreateOrganizationRequest request, [FromServices] ProjectManager projectManager) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Organization name is required." });

            var org = new Organization { Name = request.Name };
            projectManager.AddOrganization(org);
            return Results.Ok(org);
        });

        // Users
        group.MapGet("/users/{orgId}", (string orgId, [FromServices] ProjectManager projectManager) =>
        {
            return Results.Ok(projectManager.GetUsersByOrganization(orgId).Select(u => new 
            {
                u.Id,
                u.OrganizationId,
                u.Username,
                u.GitHubUsername
                // Do not return ApiToken in GET requests for security
            }));
        });

        group.MapPost("/users", ([FromBody] CreateUserRequest request, [FromServices] ProjectManager projectManager) =>
        {
            if (string.IsNullOrWhiteSpace(request.OrganizationId))
                return Results.BadRequest(new { error = "OrganizationId is required." });
            if (string.IsNullOrWhiteSpace(request.Username))
                return Results.BadRequest(new { error = "Username is required." });

            var org = projectManager.GetOrganization(request.OrganizationId);
            if (org == null)
                return Results.NotFound(new { error = "Organization not found." });

            // Generate a secure personal access token
            var token = "sk_" + GenerateSecureToken(32);

            var user = new User
            {
                OrganizationId = request.OrganizationId,
                Username = request.Username,
                GitHubUsername = request.GitHubUsername ?? string.Empty,
                ApiToken = token
            };

            projectManager.AddUser(user);

            // We only return the ApiToken on creation
            return Results.Ok(user);
        });

        group.MapDelete("/users/{id}", (string id, [FromServices] ProjectManager projectManager) =>
        {
            var deleted = projectManager.DeleteUser(id);
            if (deleted)
                return Results.Ok(new { success = true });
            
            return Results.NotFound(new { error = "User not found." });
        });
    }

    private static string GenerateSecureToken(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var result = new StringBuilder(length);
        using (var rng = RandomNumberGenerator.Create())
        {
            var bytes = new byte[length];
            rng.GetBytes(bytes);
            foreach (var b in bytes)
            {
                result.Append(chars[b % chars.Length]);
            }
        }
        return result.ToString();
    }
}

public record CreateOrganizationRequest(string Name);
public record CreateUserRequest(string OrganizationId, string Username, string? GitHubUsername);
