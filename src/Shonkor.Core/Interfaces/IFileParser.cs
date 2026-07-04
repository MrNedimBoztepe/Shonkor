// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Interfaces;

/// <summary>
/// Defines a parser that extracts <see cref="GraphNode"/> and <see cref="GraphEdge"/>
/// instances from the content of a specific file type.
/// </summary>
/// <remarks>
/// Implementations should be registered for each supported file extension
/// (e.g., <c>.cs</c>, <c>.md</c>, <c>.yml</c>, <c>.js</c>).
/// The parser is responsible for decomposing a file's content into meaningful
/// semantic elements and their relationships.
/// </remarks>
public interface IFileParser
{
    /// <summary>
    /// Gets the set of file extensions (including the leading dot) that this parser supports.
    /// </summary>
    /// <example>
    /// A C# parser might return <c>{ ".cs" }</c>, while a YAML parser might return
    /// <c>{ ".yml", ".yaml" }</c>.
    /// </example>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>
    /// The baseline trust tier of the edges this parser produces. Deterministic, language-exact parsers
    /// (e.g. Roslyn) leave the default <see cref="Provenance.Extracted"/>; heuristic parsers (regex,
    /// name-based, or Tree-sitter syntactic patterns without semantic resolution) MUST override this to
    /// <see cref="Provenance.Inferred"/>. The host stamps each produced edge with the more-uncertain of
    /// this default and the edge's own <see cref="GraphEdge.Provenance"/>, so a parser that forgets to tag
    /// an individual edge cannot over-claim, while it can still escalate a specific edge to
    /// <see cref="Provenance.Ambiguous"/>. Default-implemented so existing parsers/plugins stay valid.
    /// </summary>
    Provenance DefaultProvenance => Provenance.Extracted;

    /// <summary>
    /// Declares the node types this parser produces, with metadata for UI filtering.
    /// </summary>
    IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; }

    /// <summary>
    /// Parses the given file content and extracts graph nodes and edges.
    /// </summary>
    /// <param name="filePath">
    /// The absolute or repository-relative path of the file being parsed.
    /// Used to populate <see cref="GraphNode.FilePath"/> and generate unique node identifiers.
    /// </param>
    /// <param name="content">The full textual content of the file to parse.</param>
    /// <returns>
    /// A tuple containing:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>Nodes</c> – the semantic elements extracted from the file.
    ///   </description></item>
    ///   <item><description>
    ///     <c>Edges</c> – the relationships between the extracted nodes.
    ///   </description></item>
    /// </list>
    /// </returns>
    Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(
        string filePath,
        string content);
}
