// Licensed to Shonkor under the MIT License.

namespace Shonkor.Infrastructure.Services.Mcp;

/// <summary>
/// The set of <see cref="IMcpTool"/> implementations the server exposes. Resolves a tool by name for
/// <c>tools/call</c> and produces the capability-filtered schema list for <c>tools/list</c>.
/// </summary>
public sealed class McpToolRegistry
{
    private readonly IReadOnlyList<IMcpTool> _tools;
    private readonly Dictionary<string, IMcpTool> _byName;

    public McpToolRegistry(IEnumerable<IMcpTool> tools)
    {
        _tools = tools.ToList();
        _byName = _tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    /// <summary>Returns the tool with the given name, or null if none is registered.</summary>
    public IMcpTool? Find(string? name) =>
        !string.IsNullOrEmpty(name) && _byName.TryGetValue(name, out var tool) ? tool : null;

    /// <summary>The schemas to advertise in <c>tools/list</c>, filtered by each tool's availability gate.</summary>
    public IReadOnlyList<object> GetSchemas(McpToolContext ctx) =>
        _tools.Where(t => t.IsAvailable(ctx)).Select(t => t.GetSchema()).ToList();

    /// <summary>All registered tool names (for diagnostics/tests).</summary>
    public IEnumerable<string> Names => _byName.Keys;
}
