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
        // The local dashboard may skip the API key, but ONLY in Development. In any non-Development
        // environment the loopback bypass is disabled, because behind a reverse proxy (nginx/IIS/ngrok)
        // RemoteIpAddress is typically 127.0.0.1 for EVERY request, which would otherwise disable auth
        // entirely. Operators can still opt back in via Security:AllowLocalBypass = true.
        var allowLocalBypass = configuration.GetValue<bool?>("Security:AllowLocalBypass") ?? env.IsDevelopment();

        var isLocal = context.Connection.RemoteIpAddress != null &&
                      IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

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

        // 1. Check dynamic projects (constant-time comparison to avoid timing attacks)
        var matchingProject = projects.FirstOrDefault(p => !string.IsNullOrEmpty(p.ApiKey) && FixedTimeEquals(p.ApiKey, presentedKey));
        if (matchingProject != null)
        {
            projectName = matchingProject.Name;
        }
        // 2. Check fallback appsettings.json (constant-time)
        else if (fallbackApiKeys != null)
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
