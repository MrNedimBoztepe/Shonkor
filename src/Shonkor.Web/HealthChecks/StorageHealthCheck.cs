// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web.HealthChecks;

/// <summary>
/// Readiness probe: confirms the service can actually do its job — the project workspace is reachable
/// and writable (the registry and per-project SQLite files live there), and, when at least one project
/// is registered, its graph store opens and answers a trivial query. Liveness ("is the process up?")
/// is covered separately by the check-less /health and /health/live endpoints.
/// </summary>
public sealed class StorageHealthCheck : IHealthCheck
{
    private readonly ProjectManager _projectManager;

    public StorageHealthCheck(ProjectManager projectManager) => _projectManager = projectManager;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var workspace = _projectManager.WorkspacePath;

        // 1. The workspace directory must exist and be writable.
        if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
        {
            return HealthCheckResult.Unhealthy($"Workspace path is not available: '{workspace}'.");
        }

        try
        {
            var probe = Path.Combine(workspace, $".health-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(probe, "ok", cancellationToken).ConfigureAwait(false);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Workspace '{workspace}' is not writable.", ex);
        }

        // 2. If projects exist, prove the active project's graph store opens and answers.
        //    An empty workspace is still "ready" — the app can accept its first project.
        if (_projectManager.GetProjects().Count > 0)
        {
            try
            {
                var storage = await _projectManager.GetActiveStorageProviderAsync(cancellationToken).ConfigureAwait(false);
                _ = await storage.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy("Active project's graph store is not reachable.", ex);
            }

            return HealthCheckResult.Healthy("Workspace writable; graph store reachable.");
        }

        return HealthCheckResult.Healthy("Workspace writable; no projects registered yet.");
    }
}
