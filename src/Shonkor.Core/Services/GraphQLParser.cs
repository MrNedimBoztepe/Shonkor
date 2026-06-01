using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// Parses GraphQL query files (.graphql, .gql) to extract queries, mutations, and fragments.
/// Detects references to Sitecore templates (e.g. "... on Promo") to link queries to content structures.
/// </summary>
public sealed partial class GraphQLParser : IFileParser
{
    /// <inheritdoc />
    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".graphql", ".gql" }.ToFrozenSet();

    /// <inheritdoc />
    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new[]
    {
        new NodeTypeDescriptor("GraphQLQuery", "Code", true),
        new NodeTypeDescriptor("GraphQLFragment", "Code", true)
    };

    [GeneratedRegex(@"query\s+([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex QueryRegex();

    [GeneratedRegex(@"mutation\s+([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MutationRegex();

    [GeneratedRegex(@"fragment\s+([A-Za-z0-9_]+)\s+on\s+([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FragmentRegex();

    [GeneratedRegex(@"\.\.\.\s+on\s+([A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineFragmentRegex();

    /// <inheritdoc />
    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(
        string filePath,
        string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(content);

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        var fileNodeId = filePath.ToLowerInvariant();

        // 1. Find all query names
        var queries = QueryRegex().Matches(content);
        foreach (Match match in queries)
        {
            var queryName = match.Groups[1].Value;
            var nodeId = $"{fileNodeId}::query::{queryName.ToLowerInvariant()}";

            nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = queryName,
                Type = "GraphQLQuery",
                Properties = new Dictionary<string, string>
                {
                    ["filePath"] = filePath,
                    ["queryType"] = "Query"
                }
            });

            // Connect query to the containing file
            edges.Add(new GraphEdge
            {
                SourceId = nodeId,
                TargetId = fileNodeId,
                Relationship = "DEFINED_IN"
            });
        }

        // 2. Find all mutation names
        var mutations = MutationRegex().Matches(content);
        foreach (Match match in mutations)
        {
            var mutationName = match.Groups[1].Value;
            var nodeId = $"{fileNodeId}::mutation::{mutationName.ToLowerInvariant()}";

            nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = mutationName,
                Type = "GraphQLQuery",
                Properties = new Dictionary<string, string>
                {
                    ["filePath"] = filePath,
                    ["queryType"] = "Mutation"
                }
            });

            edges.Add(new GraphEdge
            {
                SourceId = nodeId,
                TargetId = fileNodeId,
                Relationship = "DEFINED_IN"
            });
        }

        // 3. Find fragments (e.g. fragment PromoFields on Promo)
        var fragments = FragmentRegex().Matches(content);
        foreach (Match match in fragments)
        {
            var fragmentName = match.Groups[1].Value;
            var targetTemplate = match.Groups[2].Value;
            var nodeId = $"{fileNodeId}::fragment::{fragmentName.ToLowerInvariant()}";

            nodes.Add(new GraphNode
            {
                Id = nodeId,
                Name = fragmentName,
                Type = "GraphQLFragment",
                Properties = new Dictionary<string, string>
                {
                    ["filePath"] = filePath,
                    ["targetTemplate"] = targetTemplate
                }
            });

            edges.Add(new GraphEdge
            {
                SourceId = nodeId,
                TargetId = fileNodeId,
                Relationship = "DEFINED_IN"
            });
        }

        // 4. Find all inline fragment template references (e.g. "... on Promo")
        var inlineFragments = InlineFragmentRegex().Matches(content);
        var referencedTemplates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in inlineFragments)
        {
            referencedTemplates.Add(match.Groups[1].Value);
        }

        if (referencedTemplates.Count > 0)
        {
            var refString = string.Join(",", referencedTemplates);
            foreach (var node in nodes)
            {
                if (node.Type == "GraphQLQuery" || node.Type == "GraphQLFragment")
                {
                    node.Properties["referencedTemplates"] = refString;
                }
            }
        }

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>(
            (nodes.AsReadOnly(), edges.AsReadOnly()));
    }
}
