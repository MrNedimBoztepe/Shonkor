using System.Collections.Frozen;

using LLMBrain.Core.Interfaces;
using LLMBrain.Core.Models;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LLMBrain.Core.Services;

/// <summary>
/// Parses C# source files using the Roslyn syntax tree API to extract
/// type declarations, method declarations, inheritance relationships,
/// and Optimizely-specific <c>[ContentType]</c> attributes.
/// </summary>
public sealed class RoslynAstParser : IFileParser
{
    /// <inheritdoc />
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" }.ToFrozenSet();

    /// <inheritdoc />
    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(
        string filePath,
        string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var syntaxTree = CSharpSyntaxTree.ParseText(content);
        var root = syntaxTree.GetCompilationUnitRoot();

        var walker = new CSharpDeclarationWalker(filePath);
        walker.Visit(root);

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>(
            (walker.Nodes.AsReadOnly(), walker.Edges.AsReadOnly()));
    }

    /// <summary>
    /// A <see cref="CSharpSyntaxWalker"/> that visits class, interface, record, and method declarations
    /// to build knowledge graph nodes and edges.
    /// </summary>
    private sealed class CSharpDeclarationWalker(string filePath) : CSharpSyntaxWalker
    {
        private const string ContentTypeAttribute = "ContentType";
        private const string ContainsRelationship = "CONTAINS";

        private string? _currentTypeNodeId;

        /// <summary>
        /// Gets the collected nodes from the syntax walk.
        /// </summary>
        public List<GraphNode> Nodes { get; } = [];

        /// <summary>
        /// Gets the collected edges from the syntax walk.
        /// </summary>
        public List<GraphEdge> Edges { get; } = [];

        /// <inheritdoc />
        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var typeNodeId = VisitTypeDeclaration(node, node.Identifier, node.BaseList, "Class");
            var prev = _currentTypeNodeId;
            _currentTypeNodeId = typeNodeId;
            base.VisitClassDeclaration(node);
            _currentTypeNodeId = prev;
        }

        /// <inheritdoc />
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            var typeNodeId = VisitTypeDeclaration(node, node.Identifier, node.BaseList, "Interface");
            var prev = _currentTypeNodeId;
            _currentTypeNodeId = typeNodeId;
            base.VisitInterfaceDeclaration(node);
            _currentTypeNodeId = prev;
        }

        /// <inheritdoc />
        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            var typeNodeId = VisitTypeDeclaration(node, node.Identifier, node.BaseList, "Record");
            var prev = _currentTypeNodeId;
            _currentTypeNodeId = typeNodeId;
            base.VisitRecordDeclaration(node);
            _currentTypeNodeId = prev;
        }

        /// <inheritdoc />
        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var methodName = node.Identifier.Text;
            var parentName = _currentTypeNodeId is not null ? _currentTypeNodeId.Split("::").Last() : "global";
            var methodNodeId = $"{filePath}::{parentName}::{methodName}";

            Nodes.Add(new GraphNode
            {
                Id = methodNodeId,
                Name = methodName,
                Type = "Method",
                Properties = new Dictionary<string, string>
                {
                    ["filePath"] = filePath,
                    ["returnType"] = node.ReturnType.ToString(),
                    ["lineNumber"] = node.GetLocation().GetLineSpan().StartLinePosition.Line.ToString()
                }
            });

            if (_currentTypeNodeId is not null)
            {
                Edges.Add(new GraphEdge
                {
                    SourceId = _currentTypeNodeId,
                    TargetId = methodNodeId,
                    Relationship = ContainsRelationship
                });
            }

            base.VisitMethodDeclaration(node);
        }

        /// <summary>
        /// Shared logic for visiting class, interface, and record declarations.
        /// Creates a type node, a CONTAINS edge from the file, and inheritance edges for base types.
        /// Detects the Optimizely <c>[ContentType]</c> attribute to override the node type.
        /// </summary>
        private string VisitTypeDeclaration(
            TypeDeclarationSyntax node,
            SyntaxToken identifier,
            BaseListSyntax? baseList,
            string defaultType)
        {
            var typeName = identifier.Text;
            var typeNodeId = $"{filePath}::{typeName}";

            var nodeType = HasContentTypeAttribute(node) ? "OptiBlockType" : defaultType;

            Nodes.Add(new GraphNode
            {
                Id = typeNodeId,
                Name = typeName,
                Type = nodeType,
                Properties = new Dictionary<string, string>
                {
                    ["filePath"] = filePath,
                    ["lineNumber"] = node.GetLocation().GetLineSpan().StartLinePosition.Line.ToString()
                }
            });

            // Edge from file to type: CONTAINS
            Edges.Add(new GraphEdge
            {
                SourceId = filePath,
                TargetId = typeNodeId,
                Relationship = ContainsRelationship
            });

            // Edges for base types and implemented interfaces
            if (baseList is not null)
            {
                foreach (var baseType in baseList.Types)
                {
                    var baseTypeName = baseType.Type.ToString();
                    var relationship = IsLikelyInterface(baseTypeName) ? "IMPLEMENTS" : "EXTENDS";

                    Edges.Add(new GraphEdge
                    {
                        SourceId = typeNodeId,
                        TargetId = baseTypeName,
                        Relationship = relationship
                    });
                }
            }

            return typeNodeId;
        }

        /// <summary>
        /// Determines whether the given type declaration has a <c>[ContentType]</c> attribute,
        /// indicating an Optimizely block/page type.
        /// </summary>
        private static bool HasContentTypeAttribute(MemberDeclarationSyntax node) =>
            node.AttributeLists
                .SelectMany(al => al.Attributes)
                .Any(attr =>
                {
                    var name = attr.Name.ToString();
                    return name is ContentTypeAttribute or $"{ContentTypeAttribute}Attribute";
                });

        /// <summary>
        /// Uses a naming convention heuristic to determine if a base type name refers to an interface.
        /// Interface names conventionally start with 'I' followed by an uppercase letter.
        /// </summary>
        private static bool IsLikelyInterface(string typeName) =>
            typeName.Length >= 2 && typeName[0] == 'I' && char.IsUpper(typeName[1]);
    }
}
