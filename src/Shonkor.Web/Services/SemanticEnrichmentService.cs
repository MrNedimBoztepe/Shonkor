using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web.Services;

/// <summary>
/// A background worker that asynchronously enriches the knowledge graph with semantic metadata.
/// It polls the database for nodes marked for analysis and delegates them to the ISemanticAnalyzer.
/// </summary>
public class SemanticEnrichmentService : BackgroundService
{
    private readonly ProjectManager _projectManager;
    private readonly ISemanticAnalyzer _analyzer;
    private readonly ILogger<SemanticEnrichmentService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 10;

    public SemanticEnrichmentService(
        ProjectManager projectManager, 
        ISemanticAnalyzer analyzer, 
        ILogger<SemanticEnrichmentService> logger)
    {
        _projectManager = projectManager;
        _analyzer = analyzer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Semantic Enrichment Service is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingNodesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while processing semantic enrichment.");
            }

            // Wait before next poll
            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation("Semantic Enrichment Service is stopping.");
    }

    private async Task ProcessPendingNodesAsync(CancellationToken cancellationToken)
    {
        var projects = _projectManager.GetProjects();
        if (projects.Count == 0) return;

        foreach (var project in projects)
        {
            var storage = _projectManager.GetStorageProvider(project.Name);
            
            // Fetch a batch of nodes that need analysis
            var pendingNodes = await storage.GetNodesPendingSemanticAnalysisAsync(BatchSize, cancellationToken);
            
            if (pendingNodes.Count > 0)
            {
                _logger.LogInformation("Found {Count} nodes pending semantic analysis in project '{Project}'.", pendingNodes.Count, project.Name);
                
                foreach (var node in pendingNodes)
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    _logger.LogDebug("Analyzing node {Id}...", node.Id);
                    
                    try
                    {
                        var result = await _analyzer.AnalyzeNodeAsync(node, cancellationToken);
                        
                        // Update the node's semantic summary in the DB
                        await storage.UpdateNodeSemanticDataAsync(node.Id, result.Summary, cancellationToken);

                        // If the analyzer returned extracted concepts, we could optionally 
                        // create new Concept nodes and BELONGS_TO_CONCEPT edges here.
                        // For now, Phase 2 focuses on just persisting the Summary.
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to analyze node {Id}. Skipping for now.", node.Id);
                    }
                }
                
                _logger.LogInformation("Finished processing batch for project '{Project}'.", project.Name);
            }
        }
    }
}
