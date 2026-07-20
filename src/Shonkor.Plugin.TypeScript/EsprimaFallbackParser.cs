// Licensed to Shonkor under the MIT License.

using Esprima;
using Esprima.Ast;

using Shonkor.Core.Models;

namespace Shonkor.Plugin.TypeScript;

/// <summary>
/// The private, in-plugin degradation parser. This is the same Esprima tolerant parse the host used to run
/// in-process (the former <c>Shonkor.Core.Services.JavaScriptParser</c>): when the Node sidecar is
/// unavailable or times out, the adapter delegates here so JS/TS indexing continues with the prior
/// behaviour (AC#3) instead of stopping. Emitting the "degraded" diagnostic is the adapter's job; this
/// type only reproduces the old node/edge shape.
/// </summary>
internal static class EsprimaFallbackParser
{
    private static readonly string[] ProbeExtensions = [".ts", ".tsx", ".js", ".jsx"];

    /// <summary>
    /// Parses <paramref name="content"/> into the JSComponent/IMPORTS shape. Advanced TS syntax that
    /// Esprima cannot tolerate still yields the component node (only its imports are dropped), matching the
    /// prior host behaviour.
    /// </summary>
    public static (List<GraphNode> Nodes, List<GraphEdge> Edges) Parse(string filePath, string content)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        var componentName = Path.GetFileNameWithoutExtension(filePath);
        var componentNodeId = $"{filePath}::{componentName}";

        var properties = new Dictionary<string, string>();
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

        edges.Add(new GraphEdge
        {
            SourceId = filePath,
            TargetId = componentNodeId,
            Relationship = "CONTAINS"
        });

        ParseImports(filePath, content, componentNodeId, edges);
        return (nodes, edges);
    }

    private static void ParseImports(string filePath, string content, string componentNodeId, List<GraphEdge> edges)
    {
        Esprima.JavaScriptParser parser = new(new ParserOptions { Tolerant = true });

        Program program;
        try
        {
            program = parser.ParseModule(content);
        }
        catch (ParserException)
        {
            // Prior behaviour: advanced/unsupported syntax is skipped here. The adapter has already surfaced
            // a diagnostic explaining the fallback, so this is not a silent drop of the whole file.
            return;
        }

        foreach (var statement in program.Body)
        {
            if (statement is not ImportDeclaration importDecl) continue;

            var source = importDecl.Source.Value as string;
            if (string.IsNullOrWhiteSpace(source)) continue;

            edges.Add(new GraphEdge
            {
                SourceId = componentNodeId,
                TargetId = ResolveImportPath(filePath, source),
                Relationship = "IMPORTS",
                Properties = new Dictionary<string, string> { ["rawSource"] = source }
            });
        }
    }

    private static string ResolveImportPath(string filePath, string importSource)
    {
        if (!importSource.StartsWith('.')) return importSource;

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
