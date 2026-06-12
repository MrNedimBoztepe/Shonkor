// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// One hop along a discovered path. <see cref="Relation"/> and <see cref="Forward"/> describe the edge
/// connecting the PREVIOUS step to this one; for the first (source) step <see cref="Relation"/> is null.
/// <see cref="Forward"/> is true when the real edge points previous→this (rendered <c>--REL--&gt;</c>),
/// false when it points this→previous (rendered <c>&lt;--REL--</c>).
/// </summary>
public sealed record PathStep(GraphNode Node, string? Relation, bool Forward);

/// <summary>
/// Finds the shortest connection between two graph nodes. Edges are treated as undirected for
/// connectivity, but each hop remembers the real edge direction and relation so the path can be
/// rendered faithfully. Shared by the MCP <c>find_path</c> tool and the dashboard's path endpoint.
/// </summary>
public static class GraphPathFinder
{
    /// <summary>
    /// Returns the shortest path from <paramref name="fromId"/> to <paramref name="toId"/> as an ordered
    /// list (first element is the source, with a null relation), or <c>null</c> if the target is not
    /// reachable within <paramref name="maxHops"/>. When the two ids are equal, a single-step path is
    /// returned.
    /// </summary>
    public static async Task<IReadOnlyList<PathStep>?> FindPathAsync(
        IGraphSearch storage, string fromId, string toId, int maxHops = 5, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toId);

        // Fetch the neighbourhood once, then BFS in-memory.
        var (nodes, edges) = await storage.GetSubgraphAsync(new[] { fromId }, maxHops, cancellationToken).ConfigureAwait(false);
        var nodeById = nodes.ToDictionary(n => n.Id);

        var adjacency = new Dictionary<string, List<(string Next, string Rel, bool Forward)>>();
        void AddEdge(string a, string b, string rel, bool forward)
        {
            if (!adjacency.TryGetValue(a, out var list)) adjacency[a] = list = new();
            list.Add((b, rel, forward));
        }
        foreach (var e in edges)
        {
            AddEdge(e.SourceId, e.TargetId, e.Relationship, true);
            AddEdge(e.TargetId, e.SourceId, e.Relationship, false);
        }

        var prev = new Dictionary<string, (string From, string Rel, bool Forward)>();
        var visited = new HashSet<string> { fromId };
        var queue = new Queue<string>();
        queue.Enqueue(fromId);

        var found = false;
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur == toId) { found = true; break; }
            if (!adjacency.TryGetValue(cur, out var neighbours)) continue;
            foreach (var (next, rel, forward) in neighbours)
            {
                if (visited.Add(next))
                {
                    prev[next] = (cur, rel, forward);
                    queue.Enqueue(next);
                }
            }
        }

        if (!found) return null;

        // Reconstruct target<-…<-source, then reverse to read source→target.
        var ids = new List<string>();
        for (var n = toId; n != fromId; n = prev[n].From) ids.Add(n);
        ids.Add(fromId);
        ids.Reverse();

        GraphNode NodeOf(string nid) => nodeById.GetValueOrDefault(nid) ?? new GraphNode { Id = nid, Name = nid, Type = "Unknown" };

        var path = new List<PathStep>(ids.Count) { new(NodeOf(ids[0]), null, true) };
        for (var i = 1; i < ids.Count; i++)
        {
            var step = prev[ids[i]];
            path.Add(new PathStep(NodeOf(ids[i]), step.Rel, step.Forward));
        }
        return path;
    }
}
