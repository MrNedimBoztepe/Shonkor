using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web.Services;

/// <summary>
/// Drift Layer 3: a background worker that periodically reconciles each project's graph against its
/// working tree, catching out-of-band edits (git pull, branch switch, external editor) that never went
/// through <c>reindex_file</c>. Each cycle re-indexes only the drifted files surgically
/// (<see cref="GraphIndexScanner.ReconcileDriftAsync"/> → <c>ScanFileAsync</c> with Layer 1+2 scoped
/// relink), not a whole-tree rescan.
/// </summary>
/// <remarks>
/// Disabled by default — it hashes the candidate files each cycle, so it's opt-in via
/// <c>Drift:ReconcileIntervalSeconds</c> (a positive value enables it). Parsers are injected directly
/// (the registered singleton set); plugins are loaded per cycle only when <c>Security:EnablePlugins</c> is
/// set, matching the indexer's parser set so plugin-parsed files aren't mistaken for deletions. Semantic
/// <c>CALLS</c> resolution is NOT refreshed here (it's a whole-graph/compilation concern — see the
/// incremental-semantic-relink follow-up).
/// </remarks>
public sealed class DriftReconciliationService : BackgroundService
{
    private readonly ProjectManager _projectManager;
    private readonly IEnumerable<IFileParser> _parsers;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DriftReconciliationService> _logger;
    private readonly SemanticCompilationCache _compilationCache;
    private readonly TimeSpan _interval;

    public DriftReconciliationService(
        ProjectManager projectManager,
        IEnumerable<IFileParser> parsers,
        IConfiguration configuration,
        SemanticCompilationCache compilationCache,
        ILogger<DriftReconciliationService> logger)
    {
        _projectManager = projectManager;
        _parsers = parsers;
        _configuration = configuration;
        _compilationCache = compilationCache;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue<int?>("Drift:ReconcileIntervalSeconds") ?? 0));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_interval <= TimeSpan.Zero)
        {
            _logger.LogInformation(
                "Drift reconciliation is disabled (set Drift:ReconcileIntervalSeconds > 0 to enable).");
            return;
        }

        _logger.LogInformation("Drift Reconciliation Service starting; interval {Interval}.", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAllProjectsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Drift reconciliation cycle failed.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Drift Reconciliation Service stopping.");
    }

    private async Task ReconcileAllProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = _projectManager.GetProjects();
        if (projects.Count == 0) return;

        var enablePlugins = _configuration.GetValue("Security:EnablePlugins", true);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(project.Path) || !Directory.Exists(project.Path)) continue;

            // Per-project semantic override wins over the global default.
            var semanticCsharp = EndpointHelpers.UseSemanticCSharp(project, _configuration);

            // Skip if a scan/reconcile is already in progress for this project (avoids overlapping writes).
            if (!_projectManager.TryBeginScan(project.Name)) continue;
            try
            {
                var storage = await _projectManager.GetStorageProviderAsync(project.Name, cancellationToken).ConfigureAwait(false);

                var activeParsers = new List<IFileParser>(_parsers);
                using var pluginLoad = enablePlugins
                    ? AssemblyPluginLoader.LoadActive(_projectManager.WorkspacePath)
                    : AssemblyPluginLoadResult.Empty;
                activeParsers.AddRange(pluginLoad.Parsers);

                var scanner = new GraphIndexScanner(storage, activeParsers, _logger, semanticCsharp, _compilationCache,
                    postProcessors: pluginLoad.PostProcessors.Concat(Shonkor.Infrastructure.Services.FirstPartyPostProcessors.Create()));
                var projectConfig = _projectManager.GetProjectConfig(project.Name);

                var result = await scanner.ReconcileDriftAsync(project.Path, projectConfig.ExcludePatterns, cancellationToken).ConfigureAwait(false);
                if (result.FilesScanned > 0)
                {
                    _logger.LogInformation(
                        "Drift reconciled for '{Project}': {Files} file(s), {Nodes} node(s) updated.",
                        project.Name, result.FilesScanned, result.NodesCreated);
                }
            }
            finally
            {
                _projectManager.EndScan(project.Name);
            }
        }
    }
}
