using System.Collections.Frozen;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Shonkor.Core.Services;

/// <summary>
/// Parses C# source files using the Roslyn syntax tree API to extract
/// type declarations, method declarations, and inheritance relationships.
/// </summary>
public sealed class RoslynAstParser : IFileParser
{
    /// <inheritdoc />
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs" }.ToFrozenSet();

    /// <inheritdoc />
    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("Class", "Code", true),
        new NodeTypeDescriptor("Interface", "Code", true),
        new NodeTypeDescriptor("Record", "Code", true),
        new NodeTypeDescriptor("Method", "Code", true),
        new NodeTypeDescriptor("Enum", "Code", true),
        new NodeTypeDescriptor("Struct", "Code", true),
        new NodeTypeDescriptor("Property", "Code", false),
        new NodeTypeDescriptor("Constructor", "Code", false)
    };

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
            var typeNodeId = VisitTypeDeclaration(node, node.Identifier, node.BaseList, "Class", node.Modifiers);
            var prev = _currentTypeNodeId;
            _currentTypeNodeId = typeNodeId;
            base.VisitClassDeclaration(node);
            _currentTypeNodeId = prev;
        }

        /// <inheritdoc />
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            var typeNodeId = VisitTypeDeclaration(node, node.Identifier, node.BaseList, "Interface", node.Modifiers);
            var prev = _currentTypeNodeId;
            _currentTypeNodeId = typeNodeId;
            base.VisitInterfaceDeclaration(node);
            _currentTypeNodeId = prev;
        }

        /// <inheritdoc />
        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            var typeNodeId = VisitTypeDeclaration(node, node.Identifier, node.BaseList, "Record", node.Modifiers);
            var prev = _currentTypeNodeId;
            _currentTypeNodeId = typeNodeId;
            base.VisitRecordDeclaration(node);
            _currentTypeNodeId = prev;
        }

        /// <inheritdoc />
        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            var typeNodeId = VisitTypeDeclaration(node, node.Identifier, node.BaseList, "Struct", node.Modifiers);
            var prev = _currentTypeNodeId;
            _currentTypeNodeId = typeNodeId;
            base.VisitStructDeclaration(node);
            _currentTypeNodeId = prev;
        }

        /// <inheritdoc />
        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            var typeName = node.Identifier.Text;
            var typeNodeId = CsharpNodeId.ForType(filePath, TypeChainOf(node));

            var members = string.Join(", ", node.Members.Select(m => m.Identifier.Text));
            var span = node.GetLocation().GetLineSpan();

            Nodes.Add(new GraphNode
            {
                Id = typeNodeId,
                Name = typeName,
                Type = "Enum",
                Content = $"Enum Members: {members}",
                FilePath = filePath,
                StartLine = span.StartLinePosition.Line + 1,
                EndLine = span.EndLinePosition.Line + 1,
                Properties = new Dictionary<string, string>
                {
                    ["modifiers"] = node.Modifiers.ToString()
                }
            });

            Edges.Add(new GraphEdge
            {
                SourceId = filePath,
                TargetId = typeNodeId,
                Relationship = ContainsRelationship
            });

            var prev = _currentTypeNodeId;
            _currentTypeNodeId = typeNodeId;
            base.VisitEnumDeclaration(node);
            _currentTypeNodeId = prev;
        }

        /// <inheritdoc />
        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var methodName = node.Identifier.Text;
            var parentName = _currentTypeNodeId is not null ? _currentTypeNodeId.Split("::").Last() : "global";
            var arity = node.ParameterList.Parameters.Count;
            var methodNodeId = CsharpNodeId.ForMethod(filePath, parentName, methodName, arity, MethodOverloadSpan(node, methodName, arity));
            var span = node.GetLocation().GetLineSpan();

            Nodes.Add(new GraphNode
            {
                Id = methodNodeId,
                Name = methodName,
                Type = "Method",
                Content = GetTruncatedContent(node),
                FilePath = filePath,
                StartLine = span.StartLinePosition.Line + 1,
                EndLine = span.EndLinePosition.Line + 1,
                Properties = new Dictionary<string, string>
                {
                    ["returnType"] = node.ReturnType.ToString(),
                    ["modifiers"] = node.Modifiers.ToString(),
                    ["parameters"] = node.ParameterList.Parameters.ToString()
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

        /// <inheritdoc />
        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var propertyName = node.Identifier.Text;
            var parentName = _currentTypeNodeId is not null ? _currentTypeNodeId.Split("::").Last() : "global";
            var propertyNodeId = CsharpNodeId.ForMember(filePath, parentName, propertyName);
            var span = node.GetLocation().GetLineSpan();

            Nodes.Add(new GraphNode
            {
                Id = propertyNodeId,
                Name = propertyName,
                Type = "Property",
                FilePath = filePath,
                StartLine = span.StartLinePosition.Line + 1,
                EndLine = span.EndLinePosition.Line + 1,
                Properties = new Dictionary<string, string>
                {
                    ["returnType"] = node.Type.ToString(),
                    ["modifiers"] = node.Modifiers.ToString()
                }
            });

            if (_currentTypeNodeId is not null)
            {
                Edges.Add(new GraphEdge
                {
                    SourceId = _currentTypeNodeId,
                    TargetId = propertyNodeId,
                    Relationship = ContainsRelationship
                });
            }

            base.VisitPropertyDeclaration(node);
        }

        /// <inheritdoc />
        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var constructorName = node.Identifier.Text;
            var parentName = _currentTypeNodeId is not null ? _currentTypeNodeId.Split("::").Last() : "global";
            var ctorArity = node.ParameterList.Parameters.Count;
            var constructorNodeId = CsharpNodeId.ForMethod(filePath, parentName, "Constructor", ctorArity, ConstructorOverloadSpan(node, ctorArity));
            var span = node.GetLocation().GetLineSpan();

            Nodes.Add(new GraphNode
            {
                Id = constructorNodeId,
                Name = constructorName,
                Type = "Constructor",
                Content = GetTruncatedContent(node),
                FilePath = filePath,
                StartLine = span.StartLinePosition.Line + 1,
                EndLine = span.EndLinePosition.Line + 1,
                Properties = new Dictionary<string, string>
                {
                    ["modifiers"] = node.Modifiers.ToString(),
                    ["parameters"] = node.ParameterList.Parameters.ToString()
                }
            });

            if (_currentTypeNodeId is not null)
            {
                Edges.Add(new GraphEdge
                {
                    SourceId = _currentTypeNodeId,
                    TargetId = constructorNodeId,
                    Relationship = ContainsRelationship
                });
            }

            base.VisitConstructorDeclaration(node);
        }

        /// <summary>
        /// Returns the declaration's source offset when the enclosing type declares more than one method
        /// with the same name and arity (a same-arity overload group), so their node ids can be made
        /// distinct; otherwise <c>null</c> (the common, non-overloaded case keeps a stable name#arity id).
        /// The semantic linker derives the identical offset from the resolved symbol's declaring syntax.
        /// </summary>
        private static int? MethodOverloadSpan(MethodDeclarationSyntax node, string name, int arity)
        {
            if (node.Parent is not TypeDeclarationSyntax type) return null;
            var siblings = 0;
            foreach (var member in type.Members)
            {
                if (member is MethodDeclarationSyntax m
                    && m.Identifier.Text == name
                    && m.ParameterList.Parameters.Count == arity)
                {
                    siblings++;
                    if (siblings > 1) return node.Span.Start;
                }
            }
            return null;
        }

        /// <summary>Constructor counterpart of <see cref="MethodOverloadSpan"/> (overloads share the type's name, so only arity discriminates).</summary>
        private static int? ConstructorOverloadSpan(ConstructorDeclarationSyntax node, int arity)
        {
            if (node.Parent is not TypeDeclarationSyntax type) return null;
            var siblings = 0;
            foreach (var member in type.Members)
            {
                if (member is ConstructorDeclarationSyntax c
                    && c.ParameterList.Parameters.Count == arity)
                {
                    siblings++;
                    if (siblings > 1) return node.Span.Start;
                }
            }
            return null;
        }

        /// <summary>
        /// Shared logic for visiting class, interface, and record declarations.
        /// Creates a type node, a CONTAINS edge from the file, and inheritance edges for base types.
        /// </summary>
        private string VisitTypeDeclaration(
            TypeDeclarationSyntax node,
            SyntaxToken identifier,
            BaseListSyntax? baseList,
            string defaultType,
            SyntaxTokenList modifiers)
        {
            var typeName = identifier.Text;
            var typeNodeId = CsharpNodeId.ForType(filePath, TypeChainOf(node));

            // Collect the names of all types this declaration references (field/property/parameter/
            // return/object-creation/base types). These are resolved post-scan into REFERENCES_TYPE
            // edges by the linker, enabling "who uses type X?" impact traversal.
            var referencedTypes = CollectReferencedTypeNames(node, typeName);

            var properties = new Dictionary<string, string>
            {
                ["modifiers"] = modifiers.ToString()
            };
            if (referencedTypes.Count > 0)
            {
                properties["referencedTypes"] = string.Join(",", referencedTypes);
            }

            var span = node.GetLocation().GetLineSpan();
            Nodes.Add(new GraphNode
            {
                Id = typeNodeId,
                Name = typeName,
                Type = defaultType,
                FilePath = filePath,
                StartLine = span.StartLinePosition.Line + 1,
                EndLine = span.EndLinePosition.Line + 1,
                Properties = properties
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
        /// Collects the distinct simple names of all types referenced within a type declaration
        /// (base types, field/property types, method return &amp; parameter types, local declarations,
        /// object creations, and generic type arguments), excluding the declaring type itself.
        /// Nested type declarations are not descended into (they collect their own references).
        /// </summary>
        private static List<string> CollectReferencedTypeNames(TypeDeclarationSyntax declaration, string selfName)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            // Base types / implemented interfaces.
            if (declaration.BaseList is not null)
            {
                foreach (var baseType in declaration.BaseList.Types)
                {
                    CollectFromTypeSyntax(baseType.Type, names);
                }
            }

            // Walk members but do not descend into nested type declarations.
            foreach (var descendant in declaration.DescendantNodes(
                         n => !(n is TypeDeclarationSyntax tds && tds != declaration)))
            {
                switch (descendant)
                {
                    case PropertyDeclarationSyntax p:
                        CollectFromTypeSyntax(p.Type, names);
                        break;
                    case FieldDeclarationSyntax f:
                        CollectFromTypeSyntax(f.Declaration.Type, names);
                        break;
                    case MethodDeclarationSyntax m:
                        CollectFromTypeSyntax(m.ReturnType, names);
                        break;
                    case ParameterSyntax pa when pa.Type is not null:
                        CollectFromTypeSyntax(pa.Type, names);
                        break;
                    case ObjectCreationExpressionSyntax oc:
                        CollectFromTypeSyntax(oc.Type, names);
                        break;
                    case VariableDeclarationSyntax v:
                        CollectFromTypeSyntax(v.Type, names);
                        break;
                }
            }

            names.Remove(selfName);
            return names.ToList();
        }

        /// <summary>
        /// Extracts the simple type name(s) from a <see cref="TypeSyntax"/>, recursing through
        /// nullable/array/pointer/tuple wrappers and generic type arguments. Predefined types
        /// (int, string, …) and <c>var</c>/<c>void</c> are ignored.
        /// </summary>
        private static void CollectFromTypeSyntax(TypeSyntax? type, HashSet<string> acc)
        {
            switch (type)
            {
                case null:
                case PredefinedTypeSyntax:
                    break;
                case IdentifierNameSyntax id:
                    var name = id.Identifier.Text;
                    if (name is not ("var" or "void"))
                    {
                        acc.Add(name);
                    }
                    break;
                case QualifiedNameSyntax q:
                    CollectFromTypeSyntax(q.Right, acc);
                    break;
                case AliasQualifiedNameSyntax a:
                    CollectFromTypeSyntax(a.Name, acc);
                    break;
                case GenericNameSyntax g:
                    acc.Add(g.Identifier.Text);
                    foreach (var arg in g.TypeArgumentList.Arguments)
                    {
                        CollectFromTypeSyntax(arg, acc);
                    }
                    break;
                case NullableTypeSyntax n:
                    CollectFromTypeSyntax(n.ElementType, acc);
                    break;
                case ArrayTypeSyntax ar:
                    CollectFromTypeSyntax(ar.ElementType, acc);
                    break;
                case PointerTypeSyntax p:
                    CollectFromTypeSyntax(p.ElementType, acc);
                    break;
                case TupleTypeSyntax tup:
                    foreach (var el in tup.Elements)
                    {
                        CollectFromTypeSyntax(el.Type, acc);
                    }
                    break;
            }
        }

        private static string GetTruncatedContent(SyntaxNode node)
        {
            var content = node.ToFullString().Trim();
            if (content.Length > 500)
            {
                content = content.Substring(0, 500) + "...";
            }
            return content;
        }

        /// <summary>
        /// Uses a naming convention heuristic to determine if a base type name refers to an interface.
        /// Interface names conventionally start with 'I' followed by an uppercase letter.
        /// </summary>
        private static bool IsLikelyInterface(string typeName) =>
            typeName.Length >= 2 && typeName[0] == 'I' && char.IsUpper(typeName[1]);

        /// <summary>
        /// Builds the type's full chain within its file — <c>{Namespace}.{Outer}+{Nested}</c> with a
        /// CLR-style backtick arity suffix on generic types — by walking the declaration's ancestors.
        /// <see cref="RoslynSemantics"/> derives the identical chain from the resolved symbol
        /// (<c>MetadataName</c> per segment), so syntactic and semantic node ids match.
        /// </summary>
        private static string TypeChainOf(BaseTypeDeclarationSyntax node)
        {
            var segments = new List<string>();
            var namespaces = new List<string>();
            for (SyntaxNode? current = node; current != null; current = current.Parent)
            {
                switch (current)
                {
                    case TypeDeclarationSyntax t:
                        var arity = t.TypeParameterList?.Parameters.Count ?? 0;
                        segments.Insert(0, arity > 0 ? $"{t.Identifier.Text}`{arity}" : t.Identifier.Text);
                        break;
                    case EnumDeclarationSyntax e:
                        segments.Insert(0, e.Identifier.Text);
                        break;
                    case BaseNamespaceDeclarationSyntax ns:
                        namespaces.Insert(0, ns.Name.ToString());
                        break;
                }
            }
            var chain = string.Join("+", segments);
            return namespaces.Count > 0 ? $"{string.Join(".", namespaces)}.{chain}" : chain;
        }
    }
}
