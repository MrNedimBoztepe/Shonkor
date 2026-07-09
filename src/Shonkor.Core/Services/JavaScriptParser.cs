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
    /// <remarks>Name-based import extraction without cross-file type resolution — heuristic, not proven.</remarks>
    public Provenance DefaultProvenance => Provenance.Inferred;

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
        // {file}::{name}, matching the other parsers. Node ids are case-sensitive in storage: the old
        // lowercased id matched nothing on Windows paths (every IMPORTS edge dangled) and collided with
        // the scanner's File node on all-lowercase paths (nondeterministically destroying its ContentHash).
        var componentNodeId = $"{filePath}::{componentName}";

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

        // Connect the component to the scanner's File node (id = the file's full path).
        edges.Add(new GraphEdge
        {
            SourceId = filePath,
            TargetId = componentNodeId,
            Relationship = "CONTAINS"
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
                TargetId = resolvedImportPath,
                Relationship = "IMPORTS",
                Properties = new Dictionary<string, string>
                {
                    ["rawSource"] = source
                }
            });
        }
    }

    /// <summary>Extensions probed when resolving an extensionless relative import (ES/TS convention).</summary>
    private static readonly string[] ProbeExtensions = [".ts", ".tsx", ".js", ".jsx"];

    /// <summary>
    /// Resolves a relative import path against the directory of the importing file, probing the usual
    /// extensionless conventions (<c>./Button</c> → <c>Button.tsx</c>, <c>./components</c> →
    /// <c>components/index.ts</c>) so the IMPORTS edge targets the imported file's actual File node id.
    /// Non-relative imports (package names) are returned as-is. Falls back to the extensionless full
    /// path when nothing matches on disk (e.g. no filesystem access).
    /// </summary>
    private static string ResolveImportPath(string filePath, string importSource)
    {
        if (!importSource.StartsWith('.'))
        {
            return importSource;
        }

        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var basePath = Path.GetFullPath(Path.Combine(directory, importSource));

        try
        {
            if (File.Exists(basePath)) return basePath;
            foreach (var ext in ProbeExtensions)
            {
                if (File.Exists(basePath + ext)) return basePath + ext;
            }
            foreach (var ext in ProbeExtensions)
            {
                var index = Path.Combine(basePath, "index" + ext);
                if (File.Exists(index)) return index;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Probing is best-effort; fall through to the unresolved base path.
        }

        return basePath;
    }
}
