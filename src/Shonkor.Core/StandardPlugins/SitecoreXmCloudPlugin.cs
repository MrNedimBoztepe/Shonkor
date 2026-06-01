using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugins;

public sealed class SitecoreXmCloudPlugin : IFileParser
{
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".tsx", ".jsx", ".ts", ".js", ".json" }.ToFrozenSet();

    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext is ".tsx" or ".jsx")
        {
            if (content.Contains("withSitecoreContext") || content.Contains("@sitecore-jss"))
            {
                var componentName = Path.GetFileNameWithoutExtension(filePath);
                var nodeId = $"{filePath}::{componentName}";

                nodes.Add(new GraphNode
                {
                    Id = nodeId,
                    Name = componentName,
                    Type = "XmCloudComponent",
                    Properties = new Dictionary<string, string>
                    {
                        ["filePath"] = filePath,
                        ["cms"] = "Sitecore XM Cloud"
                    }
                });
            }
        }
        else if (ext == ".json")
        {
            if (content.Contains("sitecore") && content.Contains("route"))
            {
                nodes.Add(new GraphNode
                {
                    Id = filePath,
                    Name = Path.GetFileName(filePath),
                    Type = "XmCloudRouteData",
                    Properties = new Dictionary<string, string>
                    {
                        ["filePath"] = filePath,
                        ["cms"] = "Sitecore XM Cloud"
                    }
                });
            }
        }

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, edges));
    }
}
