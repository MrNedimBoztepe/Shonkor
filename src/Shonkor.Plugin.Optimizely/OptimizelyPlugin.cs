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

namespace Shonkor.Plugin.Optimizely;

public sealed class OptimizelyPlugin : IFileParser
{
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" }.ToFrozenSet();

    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("OptiPageType", "CMS", true),
        new NodeTypeDescriptor("OptiBlockType", "CMS", true),
        new NodeTypeDescriptor("OptiProperty", "CMS", true)
    };

    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(content);
        var root = syntaxTree.GetCompilationUnitRoot();

        var walker = new OptimizelyWalker(filePath);
        walker.Visit(root);

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((walker.Nodes.AsReadOnly(), walker.Edges.AsReadOnly()));
    }

    private sealed class OptimizelyWalker(string filePath) : CSharpSyntaxWalker
    {
        public List<GraphNode> Nodes { get; } = [];
        public List<GraphEdge> Edges { get; } = [];

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var isContentType = node.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(attr => attr.Name.ToString() == "ContentType" || attr.Name.ToString() == "ContentTypeAttribute");

            if (isContentType)
            {
                var typeName = node.Identifier.Text;
                var typeNodeId = $"{filePath}::{typeName}";
                
                var isPage = node.BaseList?.Types.Any(t => t.Type.ToString().Contains("PageData")) == true;
                var nodeType = isPage ? "OptiPageType" : "OptiBlockType";

                Nodes.Add(new GraphNode
                {
                    Id = typeNodeId,
                    Name = typeName,
                    Type = nodeType,
                    FilePath = filePath,
                    Properties = new Dictionary<string, string>
                    {
                        ["cms"] = "Optimizely"
                    }
                });

                // Link properties to the content type
                foreach (var member in node.Members.OfType<PropertyDeclarationSyntax>())
                {
                    var isDisplay = member.AttributeLists.SelectMany(al => al.Attributes)
                        .Any(a => a.Name.ToString() == "Display" || a.Name.ToString() == "DisplayAttribute");
                    
                    if (isDisplay)
                    {
                        var propName = member.Identifier.Text;
                        var propId = $"{typeNodeId}::{propName}";
                        Nodes.Add(new GraphNode
                        {
                            Id = propId,
                            Name = propName,
                            Type = "OptiProperty",
                            Properties = new Dictionary<string, string>
                            {
                                ["type"] = member.Type.ToString()
                            }
                        });
                        Edges.Add(new GraphEdge
                        {
                            SourceId = typeNodeId,
                            TargetId = propId,
                            Relationship = "HAS_PROPERTY"
                        });
                    }
                }
            }
            base.VisitClassDeclaration(node);
        }
    }
}
