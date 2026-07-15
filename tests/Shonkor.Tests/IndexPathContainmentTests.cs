// Licensed to Shonkor under the MIT License.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// <c>/api/index</c> may narrow a scan to a subdirectory but must not let an untrusted <c>Directory</c> escape
/// the project (CodeQL <c>cs/path-injection</c>).
///
/// <para>
/// Without the containment check an authenticated caller could point <c>Directory</c> at any existing path on
/// the server — <c>/etc</c>, another tenant's checkout, the temp root — and have its source pulled into their
/// graph, then read it back through <c>/api/rag</c>. The base is <c>project.Path</c>, which is trusted (set at
/// project creation); the target must equal it or lie provably inside it.
/// </para>
/// <para>
/// The escaping directory below <b>exists</b> (it is the temp root, an ancestor of the project path). That is
/// deliberate: a non-existent path would already be rejected by the <c>Directory.Exists</c> check, so it could
/// not tell the guard apart from that check. An existing-but-outside path is refused <i>only</i> by the
/// containment guard — drop the guard and this test goes green on a 200/500 instead of the 400 it asserts.
/// </para>
/// </summary>
public class IndexPathContainmentTests
{
    private const string ApiKey = "test-key";

    private sealed class AppFactory(string workspace) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ProjectManager>();
                services.AddSingleton(_ => new ProjectManager(workspace));
                foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
                {
                    services.Remove(hosted);
                }
            });
        }
    }

    /// <summary>A workspace holding one project whose Path is a real subdirectory of the workspace.</summary>
    private static async Task<(string Workspace, string ProjectPath)> WorkspaceWithProjectAsync()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_idx_{Guid.NewGuid():N}");
        var projectPath = Path.Combine(ws, "proj");
        Directory.CreateDirectory(projectPath);

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[]
            {
                new { Name = "P", Path = projectPath, DatabasePath = Path.Combine(ws, "g.db"),
                      OrganizationId = "", RepositoryUrl = "", ApiKey = TokenHasher.Hash(ApiKey) }
            },
            ActiveProjectName = "P"
        };
        await File.WriteAllTextAsync(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));
        return (ws, projectPath);
    }

    [Fact]
    public async Task ADirectoryOutsideTheProject_IsRejected_EvenWhenItExists()
    {
        var (ws, projectPath) = await WorkspaceWithProjectAsync();
        await using var factory = new AppFactory(ws);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);

        // The temp root exists and is an ANCESTOR of the project path — i.e. outside it. The only thing that can
        // refuse an existing directory here is the containment guard.
        var outside = Path.GetTempPath();
        Assert.True(Directory.Exists(outside));
        Assert.False(string.Equals(Path.GetFullPath(outside), Path.GetFullPath(projectPath), StringComparison.Ordinal));

        var res = await client.PostAsJsonAsync("/api/index", new { Directory = outside });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("within the project", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASubdirectoryOfTheProject_IsNotRejectedByTheContainmentCheck()
    {
        var (ws, projectPath) = await WorkspaceWithProjectAsync();
        var sub = Path.Combine(projectPath, "src");
        Directory.CreateDirectory(sub);

        await using var factory = new AppFactory(ws);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", ApiKey);

        var res = await client.PostAsJsonAsync("/api/index", new { Directory = sub });

        // A path inside the project clears the guard: whatever the scan then does, it is NOT the containment 400.
        var body = await res.Content.ReadAsStringAsync();
        Assert.False(res.StatusCode == HttpStatusCode.BadRequest && body.Contains("within the project", StringComparison.OrdinalIgnoreCase));
    }
}
