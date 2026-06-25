using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web.Endpoints;

public static class WebhookEndpoints
{
    // Valid GitHub repository names: letters, digits, '.', '_', '-'. No path separators or '..'.
    private static readonly Regex SafeRepoName = new(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled);

    /// <summary>
    /// Reads the raw request body and verifies GitHub's HMAC-SHA256 signature
    /// (X-Hub-Signature-256) against the configured secret. Returns the raw bytes on success.
    /// </summary>
    private static async Task<(bool Ok, byte[] Body)> ReadAndVerifyAsync(HttpContext context, IConfiguration config, CancellationToken ct)
    {
        var secret = config["GitHub:WebhookSecret"];

        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms, ct);
        var body = ms.ToArray();

        // If no secret is configured the endpoint refuses to process (fail closed),
        // rather than silently accepting unauthenticated webhooks.
        if (string.IsNullOrEmpty(secret))
        {
            return (false, body);
        }

        if (!context.Request.Headers.TryGetValue("X-Hub-Signature-256", out var sigHeader))
        {
            return (false, body);
        }

        var signature = sigHeader.ToString();
        const string prefix = "sha256=";
        if (!signature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return (false, body);
        }

        var expected = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
        var provided = signature[prefix.Length..];

        var ok = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(provided));

        return (ok, body);
    }

    /// <summary>
    /// Validates an untrusted repository name and resolves a contained tenant directory,
    /// guaranteeing the result stays inside <paramref name="rootDir"/> (no path traversal).
    /// </summary>
    private static bool TryResolveTenantPath(string rootDir, string repoName, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(repoName) || !SafeRepoName.IsMatch(repoName))
        {
            return false;
        }

        var fullRoot = Path.GetFullPath(rootDir);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, repoName));

        // Ensure the resolved path is strictly within the root.
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        resolved = candidate;
        return true;
    }

    /// <summary>
    /// Extracts the distinct set of changed file paths (added/modified/removed) from a GitHub push payload's
    /// <c>commits[]</c>. Returns an empty list when the payload has none (the caller then falls back to a full
    /// scan). Malformed payloads yield an empty list rather than throwing.
    /// </summary>
    private static List<string> ExtractChangedFiles(byte[] body)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("commits", out var commits) || commits.ValueKind != JsonValueKind.Array)
            {
                return new List<string>();
            }

            foreach (var commit in commits.EnumerateArray())
            {
                foreach (var field in new[] { "added", "modified", "removed" })
                {
                    if (commit.TryGetProperty(field, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var path in arr.EnumerateArray())
                        {
                            var value = path.GetString();
                            if (!string.IsNullOrWhiteSpace(value)) files.Add(value);
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not a parseable push payload — fall back to a full scan.
        }

        return files.ToList();
    }

    public static void MapWebhookEndpoints(this WebApplication app)
    {
        // POST /api/webhooks/github/install
        // Triggered when the GitHub App is installed in an organization.
        // Auto-provisions Tenants (Projects) for all selected repositories.
        app.MapPost("/api/webhooks/github/install", async (HttpContext context, IConfiguration config, ProjectManager pm, CancellationToken ct) =>
        {
            try
            {
                var (ok, body) = await ReadAndVerifyAsync(context, config, ct);
                if (!ok)
                {
                    return Results.Unauthorized();
                }

                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                var action = root.GetProperty("action").GetString();
                if (action == "created" || action == "added")
                {
                    var repos = action == "created" 
                        ? root.GetProperty("repositories").EnumerateArray() 
                        : root.GetProperty("repositories_added").EnumerateArray();
                    
                    var newKeys = new List<object>();

                    var tenantRoot = config["SaaS:TenantRoot"] ?? Path.Combine(AppContext.BaseDirectory, "tenants");

                    foreach (var repo in repos)
                    {
                        var repoName = repo.GetProperty("name").GetString()!;
                        var fullName = repo.GetProperty("full_name").GetString()!;

                        // Validate the untrusted repo name and resolve a directory that is provably
                        // contained within the tenant root (prevents path traversal via "../").
                        if (!TryResolveTenantPath(tenantRoot, repoName, out var tenantPath))
                        {
                            return Results.BadRequest($"Invalid repository name: '{repoName}'.");
                        }

                        if (!Directory.Exists(tenantPath))
                        {
                            Directory.CreateDirectory(tenantPath);
                        }

                        // Generate a secure API Key for this specific repo (Tenant)
                        var apiKey = "sk-" + Guid.NewGuid().ToString("N");

                        // Auto-provision the project
                        pm.AddProject(fullName, tenantPath, "", apiKey);

                        newKeys.Add(new { Repository = fullName, ApiKey = apiKey });
                    }

                    return Results.Ok(new 
                    { 
                        Message = "SaaS Onboarding successful. Tenants provisioned.",
                        Provisioned = newKeys
                    });
                }
                
                return Results.Ok(new { Message = $"Ignored action {action}" });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] GitHub Install Webhook failed. :: {ex}");
                return Results.Problem("GitHub Install Webhook failed.");
            }
        });

        // POST /api/webhooks/github/push
        // Triggered by GitHub when code is pushed.
        app.MapPost("/api/webhooks/github/push", async (HttpContext context, IConfiguration config, ProjectManager pm, IEnumerable<IFileParser> parsers, SemanticCompilationCache compilationCache, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            try
            {
                var (ok, body) = await ReadAndVerifyAsync(context, config, ct);
                if (!ok)
                {
                    return Results.Unauthorized();
                }

                var projectName = context.Request.Headers["X-Project-Name"].ToString();
                var project = string.IsNullOrWhiteSpace(projectName)
                    ? pm.GetActiveProject()
                    : pm.GetProjects().FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

                if (project == null)
                {
                    return Results.BadRequest("Project not configured.");
                }

                // Drift Layer 4: if the push payload names the changed files, reconcile only those (surgical,
                // via ScanFileAsync with scoped relink) instead of hashing the whole tree.
                var changedFiles = ExtractChangedFiles(body);

                // In a real SaaS the working tree would be updated (git pull / clone) before this runs.
                // We reconcile the named changed files against the local tree; if the payload lists none,
                // we fall back to a full incremental scan.

                // Skip if a scan for this project is already in progress (avoids overlapping scans).
                if (!pm.TryBeginScan(project.Name))
                {
                    return Results.Ok(new { Message = "A scan is already in progress for this project; push ignored." });
                }

                // Fire and forget the background scanning so the webhook responds quickly (GitHub timeouts)
                var webhookLogger = loggerFactory.CreateLogger("Shonkor.Webhook");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var storage = await pm.GetStorageProviderAsync(project.Name, ct);

                        // Load the workspace's ACTIVE plugins (pre-built assemblies; install is inert).
                        var activeParsers = new List<IFileParser>(parsers);
                        using var pluginLoad = config.GetValue("Security:EnablePlugins", true)
                            ? AssemblyPluginLoader.LoadActive(pm.WorkspacePath)
                            : AssemblyPluginLoadResult.Empty;
                        activeParsers.AddRange(pluginLoad.Parsers);

                        var scanner = new GraphIndexScanner(storage, activeParsers, webhookLogger,
                            semanticCsharp: EndpointHelpers.UseSemanticCSharp(project, config), compilationCache: compilationCache,
                            postProcessors: pluginLoad.PostProcessors);
                        var projectConfig = pm.GetProjectConfig(project.Name);

                        GraphIndexScanner.IndexResult result;
                        if (changedFiles.Count > 0)
                        {
                            webhookLogger.LogInformation("Push names {Count} changed file(s); reconciling them for project: {Project}", changedFiles.Count, project.Name);
                            result = await scanner.ReconcilePathsAsync(project.Path, changedFiles, CancellationToken.None);
                        }
                        else
                        {
                            webhookLogger.LogInformation("Starting background incremental index for push event on project: {Project}", project.Name);
                            result = await scanner.ScanDirectoryAsync(project.Path, projectConfig.ExcludePatterns, CancellationToken.None);
                        }
                        webhookLogger.LogInformation("Index complete: {Files} files scanned, {Nodes} nodes created/updated.", result.FilesScanned, result.NodesCreated);
                    }
                    catch (Exception ex)
                    {
                        webhookLogger.LogError(ex, "Background incremental index failed for project: {Project}", project.Name);
                    }
                    finally
                    {
                        pm.EndScan(project.Name);
                    }
                });

                return Results.Ok(new { Message = "Push webhook received. Background incremental indexing started." });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] Push webhook failed. :: {ex}");
                return Results.Problem("Webhook failed.");
            }
        });

        // POST /api/webhooks/github/pr
        // Triggered by GitHub when a PR is opened. Analyzes impact.
        app.MapPost("/api/webhooks/github/pr", async (HttpContext context, IConfiguration config, ProjectManager pm, ContextCapsuleSynthesizer synthesizer, CancellationToken ct) =>
        {
            try
            {
                var (ok, _) = await ReadAndVerifyAsync(context, config, ct);
                if (!ok)
                {
                    return Results.Unauthorized();
                }

                var projectName = context.Request.Headers["X-Project-Name"].ToString();
                var project = string.IsNullOrWhiteSpace(projectName)
                    ? pm.GetActiveProject()
                    : pm.GetProjects().FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));

                if (project == null)
                {
                    return Results.BadRequest("Project not configured.");
                }

                // In a real scenario, we read `pull_request.changed_files` from the JSON payload.
                // For this phase, we mock "changed files" to traverse the graph and return an Impact Report.
                var storage = await pm.GetStorageProviderAsync(project.Name, ct);
                
                // Example simulation: Search for a core term to simulate changed files
                var search = await storage.SearchAsync("Controller", 3);
                var seeds = search.Select(s => s.Node.Id).ToList();

                if (seeds.Count == 0) return Results.Ok(new { Impact = "No relevant changed files found to analyze." });

                // Trace 1-2 hops out to see what this PR breaks/affects
                var (nodes, edges) = await storage.GetSubgraphAsync(seeds, 2);
                var impactMarkdown = synthesizer.Synthesize(nodes, edges);

                return Results.Ok(new 
                { 
                    Message = "PR Webhook received. Simulated Impact Analysis completed.",
                    AnalyzedSeeds = seeds,
                    ImpactReport = impactMarkdown
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[API] PR webhook failed. :: {ex}");
                return Results.Problem("PR Webhook failed.");
            }
        });
    }
}
