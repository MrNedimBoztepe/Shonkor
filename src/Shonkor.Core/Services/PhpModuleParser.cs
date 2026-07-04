using System.Collections.Frozen;
using System.Text.RegularExpressions;

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// Parses PHP source files and Smarty template files (<c>.tpl</c>) to extract
/// OXID eShop module structures including class declarations, metadata extensions,
/// and template block overrides.
/// </summary>
public sealed partial class PhpModuleParser : IFileParser
{
    /// <summary>
    /// Matches PHP class declarations with optional <c>extends</c> clause.
    /// Captures the class name and the base class name.
    /// </summary>
    /// <example><c>class MyModule extends OxidBaseClass</c></example>
    [GeneratedRegex(@"^\s*class\s+(\w+)\s+extends\s+(\w+)", RegexOptions.Multiline)]
    private static partial Regex PhpClassExtendsPattern();

    /// <summary>
    /// Matches entries in the OXID metadata <c>'extend'</c> array.
    /// Captures the core class (key) and the module class (value).
    /// </summary>
    /// <example><c>'OxidCoreClass' => 'Module\MyExtension'</c></example>
    [GeneratedRegex(@"['""](\w+)['""]\s*=>\s*['""]([^'""]+)['""]")]
    private static partial Regex MetadataExtendPattern();

    /// <summary>
    /// Matches Smarty template block declarations.
    /// Captures the block name from <c>[{block name="..."}]</c> syntax.
    /// </summary>
    [GeneratedRegex(@"\[\{block\s+name\s*=\s*""([^""]+)""\s*\}\]")]
    private static partial Regex SmartyBlockPattern();

    private const string MetadataFileName = "metadata.php";

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".php", ".tpl" }.ToFrozenSet();

    /// <inheritdoc />
    /// <remarks>Regex-based OXID/Smarty extraction — its module/template edges are heuristic, not proven.</remarks>
    public Provenance DefaultProvenance => Provenance.Inferred;

    /// <inheritdoc />
    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("OxidModule", "Code", true),
        new NodeTypeDescriptor("SmartyTemplate", "CMS", false)
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

        var extension = Path.GetExtension(filePath);

        if (extension.Equals(".tpl", StringComparison.OrdinalIgnoreCase))
        {
            ParseSmartyTemplate(filePath, content, nodes, edges);
        }
        else if (extension.Equals(".php", StringComparison.OrdinalIgnoreCase))
        {
            ParsePhpFile(filePath, content, nodes, edges);
        }

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>(
            (nodes.AsReadOnly(), edges.AsReadOnly()));
    }

    /// <summary>
    /// Parses a PHP file to extract class declarations and, for metadata files,
    /// the <c>'extend'</c> array mapping core classes to module extensions.
    /// </summary>
    private static void ParsePhpFile(
        string filePath,
        string content,
        List<GraphNode> nodes,
        List<GraphEdge> edges)
    {
        ParseClassDeclarations(filePath, content, nodes, edges);

        var fileName = Path.GetFileName(filePath);
        if (fileName.Equals(MetadataFileName, StringComparison.OrdinalIgnoreCase))
        {
            ParseMetadataExtensions(filePath, content, edges);
        }
    }

    /// <summary>
    /// Detects PHP class declarations with <c>extends</c> clauses and creates
    /// <c>OxidModule</c> nodes with <c>EXTENDS</c> edges to the base class.
    /// </summary>
    private static void ParseClassDeclarations(
        string filePath,
        string content,
        List<GraphNode> nodes,
        List<GraphEdge> edges)
    {
        foreach (Match match in PhpClassExtendsPattern().Matches(content))
        {
            var className = match.Groups[1].Value;
            var baseClassName = match.Groups[2].Value;
            var classNodeId = $"{filePath}::{className}";

            nodes.Add(new GraphNode
            {
                Id = classNodeId,
                Name = className,
                Type = "OxidModule",
                FilePath = filePath,
                Properties = new Dictionary<string, string>
                {
                    ["baseClass"] = baseClassName
                }
            });

            edges.Add(new GraphEdge
            {
                SourceId = classNodeId,
                TargetId = baseClassName,
                Relationship = "EXTENDS"
            });
        }
    }

    /// <summary>
    /// Parses the OXID <c>metadata.php</c> file to extract the <c>'extend'</c> array,
    /// which maps core OXID classes to their module-level extensions.
    /// Creates <c>EXTENDS</c> edges from the module class to the core class.
    /// </summary>
    private static void ParseMetadataExtensions(
        string filePath,
        string content,
        List<GraphEdge> edges)
    {
        foreach (Match match in MetadataExtendPattern().Matches(content))
        {
            var coreClass = match.Groups[1].Value;
            var moduleClass = match.Groups[2].Value;

            edges.Add(new GraphEdge
            {
                SourceId = $"{filePath}::{moduleClass}",
                TargetId = coreClass,
                Relationship = "EXTENDS",
                Properties = new Dictionary<string, string>
                {
                    ["source"] = "metadata",
                    ["filePath"] = filePath
                }
            });
        }
    }

    /// <summary>
    /// Parses Smarty template files to detect <c>[{block name="..."}]</c> declarations.
    /// Creates a node for the template file and <c>OVERRIDES_BLOCK</c> edges for each block.
    /// </summary>
    private static void ParseSmartyTemplate(
        string filePath,
        string content,
        List<GraphNode> nodes,
        List<GraphEdge> edges)
    {
        var templateName = Path.GetFileNameWithoutExtension(filePath);
        var templateNodeId = $"{filePath}::{templateName}";

        nodes.Add(new GraphNode
        {
            Id = templateNodeId,
            Name = templateName,
            Type = "SmartyTemplate",
            FilePath = filePath,
            Properties = new Dictionary<string, string>()
        });

        foreach (Match match in SmartyBlockPattern().Matches(content))
        {
            var blockName = match.Groups[1].Value;

            edges.Add(new GraphEdge
            {
                SourceId = templateNodeId,
                TargetId = blockName,
                Relationship = "OVERRIDES_BLOCK",
                Properties = new Dictionary<string, string>
                {
                    ["blockName"] = blockName
                }
            });
        }
    }
}
