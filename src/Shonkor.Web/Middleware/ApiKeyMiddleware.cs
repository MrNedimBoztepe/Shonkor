using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Shonkor.Web.Middleware;

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private const string APIKEYNAME = "X-API-Key";

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IConfiguration configuration, IHostEnvironment env, Shonkor.Infrastructure.Services.ProjectManager pm)
    {
        // The local dashboard may skip the API key, but ONLY in Development AND only for non-SaaS
        // endpoints. In any non-Development environment the loopback bypass is disabled, because
        // behind a reverse proxy (nginx/IIS/ngrok) RemoteIpAddress is typically 127.0.0.1 for EVERY
        // request, which would otherwise disable auth entirely. Operators can still opt back in via
        // Security:AllowLocalBypass = true.
        var allowLocalBypass = configuration.GetValue<bool?>("Security:AllowLocalBypass") ?? env.IsDevelopment();

        var isLocal = context.Connection.RemoteIpAddress != null &&
                      IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

        // /api/rag endpoints are SaaS-facing and must always be authenticated, even locally,
        // so that auth can be tested in Development and SSRF from localhost is not a free pass.
        var isSaaSEndpoint = context.Request.Path.StartsWithSegments("/api/rag");

        if (allowLocalBypass && isLocal && !isSaaSEndpoint)
        {
            await _next(context);
            return;
        }

        // Webhooks authenticate via HMAC signature (verified inside the endpoint), not via API key.
        if (context.Request.Path.StartsWithSegments("/api/webhooks"))
        {
            await _next(context);
            return;
        }

        // Health probe must stay public so container/k8s liveness/readiness checks work.
        if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(APIKEYNAME, out var extractedApiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API Key was not provided in X-API-Key header.");
            return;
        }

        // In a SaaS environment, API keys are stored in the Project/Tenant database (projects.json).
        // For local testing, we still support appsettings.json fallback.
        var fallbackApiKeys = configuration.GetSection("ApiKeys").Get<Dictionary<string, string>>();
        var projects = pm.GetProjects();

        string? projectName = null;
        var presentedKey = extractedApiKey.ToString();

        // 1a. Check legacy project-level ApiKey (pre-migration projects). Stored hashed -> hash & compare.
        var matchingProject = projects.FirstOrDefault(p => Shonkor.Infrastructure.Services.TokenHasher.Verify(presentedKey, p.ApiKey));
        if (matchingProject != null)
        {
            projectName = matchingProject.Name;
        }
        // 1b. Check new User-level ApiToken (post-migration; constant-time comparison).
        //     After the Organization/User migration, Project.ApiKey is cleared and the secret lives
        //     in User.ApiToken. We resolve the owning project via the user's OrganizationId.
        else
        {
            var matchingUser = pm.GetUserByTokenConstantTime(presentedKey);
            if (matchingUser != null)
            {
                // Find the first project that belongs to the user's organization.
                var userProject = projects.FirstOrDefault(p => p.OrganizationId == matchingUser.OrganizationId);
                projectName = userProject?.Name;
            }
        }
        // 2. Check fallback appsettings.json (constant-time)
        if (projectName == null && fallbackApiKeys != null)
        {
            var match = fallbackApiKeys.FirstOrDefault(kvp => FixedTimeEquals(kvp.Key, presentedKey));
            if (!string.IsNullOrEmpty(match.Key))
            {
                projectName = match.Value;
            }
        }

        if (string.IsNullOrWhiteSpace(projectName))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized client: Invalid API Key.");
            return;
        }

        // Automatically inject the correct tenant (Project Name) into the request
        // so downstream endpoints (which use ProjectManager) use the correct SQLite DB.
        context.Request.Headers["X-Project-Name"] = projectName;

        // Also record the authenticated tenant in HttpContext.Items. Unlike the request header
        // (which a client could set), Items is server-side only and cannot be forged. Endpoints
        // that must enforce tenant isolation (e.g. the MCP relay) read this to bind the request
        // to exactly the authorized tenant.
        context.Items["AuthenticatedProjectName"] = projectName;

        await _next(context);
    }

    /// <summary>
    /// Constant-time string comparison to prevent timing side-channels on key/secret checks.
    /// </summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
