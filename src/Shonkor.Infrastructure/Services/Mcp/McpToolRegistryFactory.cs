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
    };
}
