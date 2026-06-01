using System.Collections.Frozen;

using Esprima;
using Esprima.Ast;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// Parses JavaScript and TypeScript files using the Esprima parser to extract
/// ES module import declarations and represent the file as a <c>JSComponent</c> node.
/// </summary>
/// <remarks>
/// TypeScript-specific syntax (type annotations, generics) is handled via
/// <see cref="ParserOptions.Tolerant"/> mode, which allows parsing to continue
/// even when encountering unsupported constructs.
/// </remarks>
public sealed class JavaScriptParser : IFileParser
{
    /// <inheritdoc />
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".js", ".ts", ".jsx", ".tsx" }.ToFrozenSet();

    /// <inheritdoc />
    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("JSComponent", "Code", true)
    };

    /// <inheritdoc />
    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(
        string filePath,
        string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        var componentName = Path.GetFileNameWithoutExtension(filePath);
        var componentNodeId = filePath.ToLowerInvariant();

        var properties = new Dictionary<string, string>();

        // Sitecore JSS/Next.js signature detection
        if (content.Contains("@sitecore-jss/sitecore-jss-nextjs", StringComparison.Ordinal))
        {
            properties["isSitecoreJSS"] = "true";
        }

        if (content.Contains("withDatasourceCheck", StringComparison.Ordinal))
        {
            properties["withDatasourceCheck"] = "true";
        }

        nodes.Add(new GraphNode
        {
            Id = componentNodeId,
            Name = componentName,
            Type = "JSComponent",
            FilePath = filePath,
            Properties = properties
        });

        ParseImports(filePath, content, componentNodeId, edges);

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>(
            (nodes.AsReadOnly(), edges.AsReadOnly()));
    }

    /// <summary>
    /// Parses the JavaScript content and extracts <see cref="ImportDeclaration"/> nodes
    /// from the program body, creating <c>IMPORTS</c> edges from the component to each import source.
    /// </summary>
    private static void ParseImports(
        string filePath,
        string content,
        string componentNodeId,
        List<GraphEdge> edges)
    {
        Esprima.JavaScriptParser parser = new(new ParserOptions { Tolerant = true });

        Program program;
        try
        {
            program = parser.ParseModule(content);
        }
        catch (ParserException)
        {
            // Gracefully skip files that cannot be parsed (e.g., advanced TypeScript syntax)
            return;
        }

        foreach (var statement in program.Body)
        {
            if (statement is not ImportDeclaration importDecl)
            {
                continue;
            }

            var source = importDecl.Source.Value as string;
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var resolvedImportPath = ResolveImportPath(filePath, source);

            edges.Add(new GraphEdge
            {
                SourceId = componentNodeId,
                TargetId = resolvedImportPath.ToLowerInvariant(),
                Relationship = "IMPORTS",
                Properties = new Dictionary<string, string>
                {
                    ["rawSource"] = source
                }
            });
        }
    }

    /// <summary>
    /// Resolves a relative import path against the directory of the importing file.
    /// Non-relative imports (e.g., package names) are returned as-is.
    /// </summary>
    private static string ResolveImportPath(string filePath, string importSource)
    {
        if (!importSource.StartsWith('.'))
        {
            return importSource;
        }

        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        return Path.GetFullPath(Path.Combine(directory, importSource));
    }
}
