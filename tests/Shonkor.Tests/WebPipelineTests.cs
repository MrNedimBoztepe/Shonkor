// Licensed to Shonkor under the MIT License.

using System.Linq;
using System.Net;
using System.Text;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Shonkor.Infrastructure.Services;
using Shonkor.Web.Services;

namespace Shonkor.Tests;

/// <summary>
/// End-to-end pipeline tests booting the real Web app via <see cref="WebApplicationFactory{TEntryPoint}"/>:
/// the health probe is public, static assets are served before the API-key gate, and the auth gate
/// (incl. the /api/rag SaaS guard) is wired into the actual request pipeline.
/// </summary>
public class WebPipelineTests : IClassFixture<WebPipelineTests.AppFactory>
{
    private readonly HttpClient _client;

    public WebPipelineTests(AppFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public void UseSemanticCSharp_PerProjectOverridesGlobalDefault()
    {
        var globalOff = new Microsoft.Extensions.Configuration.ConfigurationManager();
        globalOff["Indexing:SemanticCSharp"] = "false";
        var globalOn = new Microsoft.Extensions.Configuration.ConfigurationManager();
        globalOn["Indexing:SemanticCSharp"] = "true";

        // Per-project setting wins, regardless of the global default.
        Assert.True(Shonkor.Web.EndpointHelpers.UseSemanticCSharp(new Project { SemanticCSharp = true }, globalOff));
        Assert.False(Shonkor.Web.EndpointHelpers.UseSemanticCSharp(new Project { SemanticCSharp = false }, globalOn));

        // Unset (null) falls back to the global default.
        Assert.False(Shonkor.Web.EndpointHelpers.UseSemanticCSharp(new Project { SemanticCSharp = null }, globalOff));
        Assert.True(Shonkor.Web.EndpointHelpers.UseSemanticCSharp(new Project { SemanticCSharp = null }, globalOn));

        // Neither per-project nor global set → semantic resolution is ON by default (Phase 1.1).
        var globalUnset = new Microsoft.Extensions.Configuration.ConfigurationManager();
        Assert.True(Shonkor.Web.EndpointHelpers.UseSemanticCSharp(new Project { SemanticCSharp = null }, globalUnset));
    }

    [Fact]
    public async Task Health_IsPublic()
    {
        var res = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task LivenessProbe_IsPublic_AndHealthy()
    {
        // /health/live runs no dependency checks — up as soon as the process serves.
        var res = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ReadinessProbe_IsPublic_AndHealthy_OnWritableWorkspace()
    {
        // The factory roots a writable temp workspace with no projects, so readiness is healthy.
        var res = await _client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Healthy", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Root_RedirectsToAtlas_AndAtlasServesShell_WithoutKey()
    {
        // The bare root now 302-redirects to the ATLAS UI; the redirect itself needs no API key.
        var redirect = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/atlas/", redirect.Headers.Location?.OriginalString);

        // The ATLAS shell is a static asset served before the API-key middleware, so it loads publicly.
        var atlas = await _client.GetAsync("/atlas/");
        Assert.Equal(HttpStatusCode.OK, atlas.StatusCode);
    }

    [Fact]
    public async Task ApiEndpoint_WithoutKey_Returns401()
    {
        var res = await _client.GetAsync("/api/stats");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task NodeReferencesEndpoint_WithoutKey_Returns401()
    {
        // The impact/dependencies endpoint is a normal /api/* surface — gated like the rest.
        var res = await _client.GetAsync("/api/node/references?id=anything");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task RagSaaSEndpoint_WithoutKey_Returns401()
    {
        var res = await _client.PostAsync("/api/rag/query",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"shonkor_itest_{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_workspace);
            // Production so the loopback bypass never applies — the auth gate is always enforced.
            builder.UseEnvironment("Production");

            builder.ConfigureServices(services =>
            {
                // Root the registry at a throwaway temp workspace instead of the developer's real one.
                services.RemoveAll<ProjectManager>();
                services.AddSingleton(_ => new ProjectManager(_workspace));

                // Drop the Ollama-polling background worker so tests don't make network calls.
                var enrichment = services.FirstOrDefault(d => d.ImplementationType == typeof(SemanticEnrichmentService));
                if (enrichment != null) services.Remove(enrichment);
            });
        }
    }
}
