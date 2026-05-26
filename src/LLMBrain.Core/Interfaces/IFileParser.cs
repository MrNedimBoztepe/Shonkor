// Licensed to LLMBrain under the MIT License.

using LLMBrain.Core.Models;

namespace LLMBrain.Core.Interfaces;

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
