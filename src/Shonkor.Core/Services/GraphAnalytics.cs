// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// Self-contained, embedding-free topology analysis over the knowledge graph — no external graph library
/// and no model calls, so it fits the "deterministic, self-contained" moat. Operates on plain node/edge
/// lists (storage-agnostic) so it is unit-testable in isolation.
/// </summary>
public static class GraphAnalytics
{
    /// <summary>
    /// Containment/grouping edges that are structure, not semantic coupling. Excluded from centrality by
    /// default so a "hotspot" reflects real dependency brokerage, not that a file contains many symbols.
    /// </summary>
    private static readonly HashSet<string> StructuralRelationships = new(StringComparer.Ordinal)
    {
        "CONTAINS", "BELONGS_TO_MODULE"
    };

    /// <summary>Centrality scores for a node: its betweenness (brokerage) and undirected degree.</summary>
    public sealed record CentralityScore(string NodeId, double Betweenness, int Degree);

    /// <summary>
    /// Computes exact betweenness centrality (Brandes' algorithm, unweighted, treated as undirected) and
    /// degree for every node, over the coupling subgraph (structural containment excluded unless
    /// <paramref name="includeStructural"/>). High betweenness = a change-risk hotspot / "god node": many
    /// shortest dependency paths pass through it. O(V·E); intended for on-demand analysis, not per-index.
    /// Edges whose endpoints are not both in <paramref name="nodes"/> are ignored (danglers).
    /// </summary>
    public static IReadOnlyList<CentralityScore> Centrality(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        bool includeStructural = false)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        // Index distinct node ids.
        var ids = new List<string>(nodes.Count);
        var index = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (index.ContainsKey(n.Id)) continue;
            index[n.Id] = ids.Count;
            ids.Add(n.Id);
        }
        var count = ids.Count;
        if (count == 0) return Array.Empty<CentralityScore>();

        // Undirected adjacency over non-structural edges between known nodes.
        var adj = new HashSet<int>[count];
        for (var i = 0; i < count; i++) adj[i] = new HashSet<int>();
        foreach (var e in edges)
        {
            if (!includeStructural && StructuralRelationships.Contains(e.Relationship)) continue;
            if (!index.TryGetValue(e.SourceId, out var u) || !index.TryGetValue(e.TargetId, out var v)) continue;
            if (u == v) continue;
            adj[u].Add(v);
            adj[v].Add(u);
        }

        // Brandes' betweenness centrality.
        var betweenness = new double[count];
        for (var s = 0; s < count; s++)
        {
            var stack = new Stack<int>();
            var pred = new List<int>[count];
            var sigma = new double[count];
            var dist = new int[count];
            for (var i = 0; i < count; i++) { pred[i] = new List<int>(); dist[i] = -1; }
            sigma[s] = 1;
            dist[s] = 0;

            var queue = new Queue<int>();
            queue.Enqueue(s);
            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                stack.Push(v);
                foreach (var w in adj[v])
                {
                    if (dist[w] < 0) { dist[w] = dist[v] + 1; queue.Enqueue(w); }
                    if (dist[w] == dist[v] + 1) { sigma[w] += sigma[v]; pred[w].Add(v); }
                }
            }

            var delta = new double[count];
            while (stack.Count > 0)
            {
                var w = stack.Pop();
                foreach (var v in pred[w])
                {
                    delta[v] += (sigma[v] / sigma[w]) * (1 + delta[w]);
                }
                if (w != s) betweenness[w] += delta[w];
            }
        }

        var result = new List<CentralityScore>(count);
        for (var i = 0; i < count; i++)
        {
            // Undirected: each unordered pair is counted from both endpoints, so halve.
            result.Add(new CentralityScore(ids[i], betweenness[i] / 2.0, adj[i].Count));
        }
        return result;
    }

    /// <summary>
    /// Groups nodes into clusters by CONNECTED COMPONENT over the coupling subgraph (structural containment
    /// excluded unless <paramref name="includeStructural"/>): each maximal set of nodes reachable from one
    /// another is one cluster, and an unconnected node is its own singleton. Fully deterministic and O(V+E)
    /// — no randomness, no model — so it is reproducible, fitting the "no cloud" stance. This is a robust
    /// baseline that surfaces isolated modules / dead subsystems; modularity-based splitting of a single
    /// connected graph (Leiden/Louvain) is a heavier future enhancement, deliberately not attempted here
    /// because a naive Label-Propagation is either non-deterministic or degenerate (oscillates/collapses).
    /// Returns each node's cluster id (small sequential ints, assigned in node order).
    /// </summary>
    public static IReadOnlyDictionary<string, int> DetectCommunities(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        bool includeStructural = false)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var ids = new List<string>(nodes.Count);
        var index = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (index.ContainsKey(n.Id)) continue;
            index[n.Id] = ids.Count;
            ids.Add(n.Id);
        }
        var count = ids.Count;
        if (count == 0) return new Dictionary<string, int>();

        var adj = new List<int>[count];
        for (var i = 0; i < count; i++) adj[i] = new List<int>();
        foreach (var e in edges)
        {
            if (!includeStructural && StructuralRelationships.Contains(e.Relationship)) continue;
            if (!index.TryGetValue(e.SourceId, out var u) || !index.TryGetValue(e.TargetId, out var v)) continue;
            if (u == v) continue;
            adj[u].Add(v);
            adj[v].Add(u);
        }

        // BFS flood-fill: nodes visited in index order, so cluster ids are stable across runs.
        var component = new int[count];
        Array.Fill(component, -1);
        var nextCluster = 0;
        var queue = new Queue<int>();
        for (var start = 0; start < count; start++)
        {
            if (component[start] != -1) continue;
            var cluster = nextCluster++;
            component[start] = cluster;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                foreach (var w in adj[v])
                {
                    if (component[w] != -1) continue;
                    component[w] = cluster;
                    queue.Enqueue(w);
                }
            }
        }

        var communities = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++) communities[ids[i]] = component[i];
        return communities;
    }

    /// <summary>A pair of nodes that are semantically similar but not directly linked in the graph.</summary>
    public sealed record SurprisingConnection(string SourceId, string TargetId, double Similarity);

    /// <summary>
    /// Finds "surprising connections": node pairs whose embeddings are highly similar (cosine ≥
    /// <paramref name="minSimilarity"/>) yet which have NO direct edge between them — code that looks
    /// semantically related but carries no structural dependency. Deterministic given the embeddings; the
    /// similarity is embedding-derived, so any consumer must treat these as INFERRED, never EXTRACTED
    /// (they are not proven relationships). Only nodes with an embedding of matching dimension are compared.
    /// O(N²·dim) — the caller should bound N (e.g. cap the nodes it passes). Returns the top pairs by
    /// similarity, each unordered pair once (source id ordinal-less-than target id).
    /// </summary>
    public static IReadOnlyList<SurprisingConnection> SurprisingConnections(
        IReadOnlyList<GraphNode> nodes,
        IReadOnlyList<GraphEdge> edges,
        double minSimilarity = 0.85,
        int maxResults = 20,
        bool includeStructural = false)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        // Keep only nodes that actually carry an embedding.
        var embedded = nodes.Where(n => n.Embedding is { Length: > 0 }).ToList();
        if (embedded.Count < 2) return Array.Empty<SurprisingConnection>();

        // Directly-linked pairs (undirected, non-structural) are excluded — an existing edge is not surprising.
        var linked = new HashSet<(string, string)>();
        foreach (var e in edges)
        {
            if (!includeStructural && StructuralRelationships.Contains(e.Relationship)) continue;
            linked.Add(OrderedPair(e.SourceId, e.TargetId));
        }

        var results = new List<SurprisingConnection>();
        for (var i = 0; i < embedded.Count; i++)
        {
            for (var j = i + 1; j < embedded.Count; j++)
            {
                var a = embedded[i];
                var b = embedded[j];
                if (a.Embedding!.Length != b.Embedding!.Length) continue; // dimension mismatch — not comparable
                if (linked.Contains(OrderedPair(a.Id, b.Id))) continue;   // already structurally linked

                var sim = CosineSimilarity(a.Embedding, b.Embedding);
                if (sim < minSimilarity) continue;

                var (s, t) = OrderedPair(a.Id, b.Id);
                results.Add(new SurprisingConnection(s, t, sim));
            }
        }

        return results
            .OrderByDescending(r => r.Similarity)
            .ThenBy(r => r.SourceId, StringComparer.Ordinal)
            .ThenBy(r => r.TargetId, StringComparer.Ordinal)
            .Take(maxResults)
            .ToList();
    }

    private static (string, string) OrderedPair(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
