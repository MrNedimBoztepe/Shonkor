// Licensed to Shonkor under the MIT License.

using Shonkor.Infrastructure.Services.Mcp.Tools;

namespace Shonkor.Infrastructure.Services.Mcp;

/// <summary>
/// Builds the default set of <see cref="IMcpTool"/> implementations the server exposes. Tools are being
/// migrated here group by group from the legacy switch in McpRequestHandler; any tool not yet listed here
/// is still served by that switch as a fallback, so the surface stays complete throughout the migration.
/// </summary>
public static class McpToolRegistryFactory
{
    public static IReadOnlyList<IMcpTool> CreateTools() => new IMcpTool[]
    {
        // Group 1 — memory & stats
        new GetStatsTool(),
        new VerifyExistsTool(),
        new GetOpenThreadsTool(),
        new RecordTool(),

        // Group 2 — find
        new SearchGraphTool(),
        new LocateTool(),
        new SearchSemanticTool(),
        new SearchHybridTool(),

        // Group 3 — read
        new SignatureTool(),
        new GetSourceTool(),
        new OutlineTool(),
        new GetSubgraphTool(),
        new GenerateCapsuleTool(),
        new ArchitectureTool(),

        // Group 4 — analyze
        new ReferencesTool(),
        new FindUsagesTool(),
        new CallHierarchyTool(),
        new ImplementationsOfTool(),
        new FindPathTool(),

        // Group 5 — edit loop
        new ReindexFileTool(),
        new CheckEditTool(),
        new FreshnessTool(),
        new RelatedTestsTool(),
        new EditPlanTool(),
        new RenamePlanTool(),
        new ReviewTool(),

        // Group 6 — meta
        new OrientTool(),
        new SetProjectTool(),
        new GetDiagnosticsTool(),
    };
}
