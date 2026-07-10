// Licensed to Shonkor under the MIT License.

using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

using Shonkor.Infrastructure.Services;
using Shonkor.Web.Middleware;

namespace Shonkor.Tests;

/// <summary>
/// Tests for the security behaviour of <see cref="ApiKeyMiddleware"/>: the loopback bypass is limited
/// to Development AND non-SaaS paths, /api/rag is always authenticated, webhooks are exempt, and a valid
/// key injects the authoritative tenant into both the request header and the unforgeable HttpContext.Items.
/// </summary>
public class ApiKeyMiddlewareTests
{
    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Shonkor.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static ProjectManager NewProjectManager()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_mwtest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        return new ProjectManager(ws);
    }

    private static async Task<(int Status, bool NextCalled, DefaultHttpContext Ctx)> RunAsync(
        string env, IPAddress? remoteIp, string path, string? apiKey,
        Dictionary<string, string?>? config = null, ProjectManager? pm = null)
    {
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new ApiKeyMiddleware(next);

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = remoteIp;
        if (apiKey != null) ctx.Request.Headers["X-API-Key"] = apiKey;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build();

        await middleware.InvokeAsync(ctx, configuration, new FakeEnv { EnvironmentName = env }, pm ?? NewProjectManager());
        return (ctx.Response.StatusCode, nextCalled, ctx);
    }

    [Fact]
    public async Task Production_NoKey_Returns401()
    {
        var (status, nextCalled, _) = await RunAsync("Production", IPAddress.Loopback, "/api/stats", apiKey: null);
        Assert.Equal(401, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Development_Loopback_NonSaaS_BypassesAuth()
    {
        var (_, nextCalled, _) = await RunAsync("Development", IPAddress.Loopback, "/api/stats", apiKey: null);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Development_Loopback_RagEndpoint_StillRequiresKey()
    {
        // The loopback bypass must NOT apply to /api/rag/* — those are always authenticated.
        var (status, nextCalled, _) = await RunAsync("Development", IPAddress.Loopback, "/api/rag/query", apiKey: null);
        Assert.Equal(401, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Development_Loopback_McpRelay_StillRequiresKey()
    {
        // TICKET-209 AC3: /api/mcp is agent-facing and must always be authenticated, so an SSRF that
        // reaches the relay from localhost cannot drive the MCP file tools. The local edit loop uses
        // in-process stdio, not this relay, so nothing legitimate breaks.
        var (status, nextCalled, _) = await RunAsync("Development", IPAddress.Loopback, "/api/mcp/relay", apiKey: null);
        Assert.Equal(401, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Production_AllowLocalBypassFlag_IsIgnored_Returns401()
    {
        // TICKET-209 AC3 / H9: the flag must NOT re-enable the loopback bypass outside Development —
        // behind a reverse proxy every request is loopback, so honoring it would disable auth entirely.
        var cfg = new Dictionary<string, string?> { ["Security:AllowLocalBypass"] = "true" };
        var (status, nextCalled, _) = await RunAsync("Production", IPAddress.Loopback, "/api/stats", apiKey: null, config: cfg);
        Assert.Equal(401, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Development_AllowLocalBypassFalse_DisablesBypass_Returns401()
    {
        // The flag can still turn the bypass OFF in Development (to exercise auth locally).
        var cfg = new Dictionary<string, string?> { ["Security:AllowLocalBypass"] = "false" };
        var (status, nextCalled, _) = await RunAsync("Development", IPAddress.Loopback, "/api/stats", apiKey: null, config: cfg);
        Assert.Equal(401, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task LocalDashboardAskEndpoint_IsBypassedLocally()
    {
        // /api/ask is the dashboard's own AI chat (NOT under /api/rag), so loopback dev bypasses it.
        var (_, nextCalled, _) = await RunAsync("Development", IPAddress.Loopback, "/api/ask", apiKey: null);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Webhooks_AlwaysBypassApiKey()
    {
        var (_, nextCalled, _) = await RunAsync("Production", IPAddress.Parse("203.0.113.5"), "/api/webhooks/github/push", apiKey: null);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task HealthEndpoint_IsPublic()
    {
        // /health must stay reachable without a key (container/k8s probes), even in Production.
        var (_, nextCalled, _) = await RunAsync("Production", IPAddress.Parse("203.0.113.5"), "/health", apiKey: null);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Production_WrongKey_Returns401()
    {
        var cfg = new Dictionary<string, string?> { ["ApiKeys:valid-key"] = "ProjA" };
        var (status, nextCalled, _) = await RunAsync("Production", IPAddress.Parse("203.0.113.5"), "/api/stats", apiKey: "wrong", config: cfg);
        Assert.Equal(401, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Production_ValidKey_InjectsTenantAndAuthenticatedItem()
    {
        var cfg = new Dictionary<string, string?> { ["ApiKeys:valid-key"] = "ProjA" };
        var (_, nextCalled, ctx) = await RunAsync("Production", IPAddress.Parse("203.0.113.5"), "/api/stats", apiKey: "valid-key", config: cfg);

        Assert.True(nextCalled);
        // Authoritative tenant injected for downstream endpoints…
        Assert.Equal("ProjA", ctx.Request.Headers["X-Project-Name"].ToString());
        // …and recorded unforgeably in HttpContext.Items (used by the MCP relay for tenant locking).
        Assert.Equal("ProjA", ctx.Items["AuthenticatedProjectName"] as string);
    }
}
