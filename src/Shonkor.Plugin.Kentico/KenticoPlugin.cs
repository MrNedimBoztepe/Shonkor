using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Shonkor.Plugin.Kentico;

public sealed class KenticoPlugin : IFileParser
{
    /// <remarks>Regex/convention-based CMS extraction — heuristic, never Extracted (TICKET-207).</remarks>
    public Provenance DefaultProvenance => Provenance.Inferred;

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" }.ToFrozenSet();

    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("KenticoPageType", "CMS", true),
        new NodeTypeDescriptor("KenticoModule", "CMS", true),
        new NodeTypeDescriptor("KenticoFormComponent", "CMS", true)
    };

    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(content);
        var root = syntaxTree.GetCompilationUnitRoot();

        var walker = new KenticoWalker(filePath);
        walker.Visit(root);

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((walker.Nodes.AsReadOnly(), walker.Edges.AsReadOnly()));
    }

    private sealed class KenticoWalker(string filePath) : CSharpSyntaxWalker
    {
        public List<GraphNode> Nodes { get; } = [];
        public List<GraphEdge> Edges { get; } = [];

        public override void VisitAttributeList(AttributeListSyntax node)
        {
            foreach (var attr in node.Attributes)
            {
                var name = attr.Name.ToString();
                if (name == "RegisterModule" || name == "assembly: RegisterModule")
                {
                    Nodes.Add(new GraphNode
                    {
                        Id = $"{filePath}::KenticoModule",
                        Name = "Kentico Module Registration",
                        Type = "KenticoModule",
                        FilePath = filePath,
                        Properties = new Dictionary<string, string>
                        {
                            ["cms"] = "Kentico"
                        }
                    });
                }
            }
            base.VisitAttributeList(node);
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var typeName = node.Identifier.Text;
            var typeNodeId = $"{filePath}::{typeName}";

            var isTreeNode = node.BaseList?.Types.Any(t => t.Type.ToString().Contains("TreeNode")) == true;
            if (isTreeNode)
            {
                Nodes.Add(new GraphNode
                {
                    Id = typeNodeId,
                    Name = typeName,
                    Type = "KenticoPageType",
                    FilePath = filePath,
                    Properties = new Dictionary<string, string>
                    {
                        ["cms"] = "Kentico"
                    }
                });
            }

            var isFormComponent = node.BaseList?.Types.Any(t => t.Type.ToString().Contains("FormComponent")) == true;
            if (isFormComponent)
            {
                Nodes.Add(new GraphNode
                {
                    Id = typeNodeId,
                    Name = typeName,
                    Type = "KenticoFormComponent",
                    FilePath = filePath,
                    Properties = new Dictionary<string, string>
                    {
                        ["cms"] = "Kentico"
                    }
                });
            }

            base.VisitClassDeclaration(node);
        }
    }
}
