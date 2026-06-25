using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugin.Sitecore;

/// <summary>
/// Sitecore configuration / patch parser (App_Config Include *.config). Models what each config file
/// wires up, so a developer can answer "what runs in httpRequestBegin?" or "where is IFoo registered?":
///   config --REGISTERS_PROCESSOR(pipeline)--> clrType
///   config --REGISTERS_SERVICE(serviceType,lifetime)--> clrType   (DI implementation)
///   config --REGISTERS_CONFIGURATOR--> clrType
///   config --HANDLES_EVENT(event)--> clrType
///
/// Referenced CLR types become <c>clrtype:{FullName}</c> anchor nodes. They are NOT linked to the actual
/// C# class nodes here: C# ids are <c>{filePath}::{TypeName}</c> (file-based, namespace-free), so resolving
/// a config's <c>Namespace.Class, Assembly</c> to its declaring file needs a graph-aware second pass
/// (see F3/F8 in docs/projects/sitecore-plugin-gaps.md). The anchor nodes make the config graph queryable
/// now and give that future pass a stable target to rewire.
/// </summary>
public sealed class SitecoreConfigPlugin : IFileParser
{
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".config" }.ToFrozenSet();

    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("SitecoreConfig", "CMS", true),
        new NodeTypeDescriptor("ClrType", "Code", false)
    };

    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        // Fast guard: only Sitecore configs carry a <sitecore> section (skips web.config-style files).
        if (!content.Contains("<sitecore", StringComparison.OrdinalIgnoreCase))
            return Done(nodes, edges);

        XDocument xml;
        try { xml = XDocument.Parse(content); }
        catch { return Done(nodes, edges); }

        var sitecore = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "sitecore");
        if (sitecore == null) return Done(nodes, edges);

        var configId = filePath;
        var declaredClrTypes = new HashSet<string>(StringComparer.Ordinal);

        void Register(string? fullType, string relationship, IDictionary<string, string>? props = null)
        {
            if (string.IsNullOrWhiteSpace(fullType)) return;
            var typeId = ClrTypeId(fullType);
            if (declaredClrTypes.Add(typeId))
            {
                nodes.Add(new GraphNode
                {
                    Id = typeId,
                    Type = "ClrType",
                    Name = SimpleName(fullType),
                    Properties = new Dictionary<string, string> { ["clrType"] = fullType }
                });
            }
            edges.Add(new GraphEdge
            {
                SourceId = configId,
                TargetId = typeId,
                Relationship = relationship,
                Properties = props is null ? new Dictionary<string, string>() : new Dictionary<string, string>(props)
            });
        }

        // Pipelines: <pipelines><{pipelineName}><processor type="..." />
        foreach (var pipeline in sitecore.Elements().Where(e => e.Name.LocalName == "pipelines").Elements())
        {
            var pipelineName = pipeline.Name.LocalName;
            foreach (var processor in pipeline.Elements().Where(e => e.Name.LocalName == "processor"))
            {
                Register(TypeAttr(processor, "type"), "REGISTERS_PROCESSOR",
                    new Dictionary<string, string> { ["pipeline"] = pipelineName });
            }
        }

        // DI: <services><register serviceType implementationType lifetime /> and <configurator type />
        foreach (var entry in sitecore.Elements().Where(e => e.Name.LocalName == "services").Elements())
        {
            switch (entry.Name.LocalName)
            {
                case "register":
                    var impl = TypeAttr(entry, "implementationType") ?? TypeAttr(entry, "concreteType");
                    Register(impl, "REGISTERS_SERVICE", new Dictionary<string, string>
                    {
                        ["serviceType"] = TypeAttr(entry, "serviceType") ?? string.Empty,
                        ["lifetime"] = (string?)entry.Attribute("lifetime") ?? string.Empty
                    });
                    break;
                case "configurator":
                    Register(TypeAttr(entry, "type"), "REGISTERS_CONFIGURATOR");
                    break;
            }
        }

        // Events: <events><event name="..."><handler type="..." />
        foreach (var evt in sitecore.Elements().Where(e => e.Name.LocalName == "events").Elements().Where(e => e.Name.LocalName == "event"))
        {
            var eventName = (string?)evt.Attribute("name") ?? string.Empty;
            foreach (var handler in evt.Elements().Where(e => e.Name.LocalName == "handler"))
            {
                Register(TypeAttr(handler, "type"), "HANDLES_EVENT",
                    new Dictionary<string, string> { ["event"] = eventName });
            }
        }

        // Settings: recorded as metadata on the config node (not graph edges).
        var settingNames = sitecore.Elements().Where(e => e.Name.LocalName == "settings")
            .Elements().Where(e => e.Name.LocalName == "setting")
            .Select(s => (string?)s.Attribute("name"))
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();

        var props = new Dictionary<string, string> { ["cms"] = "Sitecore" };
        if (settingNames.Count > 0)
        {
            props["settingCount"] = settingNames.Count.ToString();
            props["settings"] = string.Join(",", settingNames.Take(50));
        }

        // Config node first so it reads as the primary node for the file.
        nodes.Insert(0, new GraphNode
        {
            Id = configId,
            Type = "SitecoreConfig",
            Name = Path.GetFileName(filePath),
            FilePath = filePath,
            Properties = props
        });

        return Done(nodes, edges);
    }

    private static Task<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)> Done(List<GraphNode> nodes, List<GraphEdge> edges)
        => Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, edges));

    /// <summary>Reads a Sitecore type attribute ("Namespace.Class, Assembly") and returns just the type name.</summary>
    private static string? TypeAttr(XElement el, string attr)
    {
        var v = (string?)el.Attribute(attr);
        if (string.IsNullOrWhiteSpace(v)) return null;
        return v.Split(',')[0].Trim();
    }

    private static string SimpleName(string fullType)
    {
        var i = fullType.LastIndexOf('.');
        return i >= 0 && i < fullType.Length - 1 ? fullType[(i + 1)..] : fullType;
    }

    private static string ClrTypeId(string fullType) => $"clrtype:{fullType}";
}
