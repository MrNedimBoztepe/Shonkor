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

    /// <summary>
    /// Detects communities by MODULARITY (deterministic Louvain: local moving + aggregation) over the
    /// coupling subgraph — unlike <see cref="DetectCommunities"/> (connected components), this splits a
    /// single CONNECTED graph into cohesive sub-communities where intra-group coupling outweighs inter-group.
    /// Fully deterministic: fixed node order, current-community-preferred / smallest-id tie-break, so the same
    /// graph always yields the same partition (no randomness). Structural containment excluded unless
    /// <paramref name="includeStructural"/>. Returns each node's community id (small sequential ints).
    /// </summary>
    public static IReadOnlyDictionary<string, int> DetectModularityCommunities(
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

        // Level-0 weighted adjacency (parallel edges accumulate weight); no self-loops yet.
        var adj = new Dictionary<int, double>[count];
        for (var i = 0; i < count; i++) adj[i] = new Dictionary<int, double>();
        foreach (var e in edges)
        {
            if (!includeStructural && StructuralRelationships.Contains(e.Relationship)) continue;
            if (!index.TryGetValue(e.SourceId, out var u) || !index.TryGetValue(e.TargetId, out var v)) continue;
            if (u == v) continue;
            adj[u][v] = adj[u].GetValueOrDefault(v) + 1.0;
            adj[v][u] = adj[v].GetValueOrDefault(u) + 1.0;
        }
        var self = new double[count];

        var twoM = 0.0;
        for (var i = 0; i < count; i++) twoM += WeightedDegree(adj[i], self[i]);
        var m = twoM / 2.0;

        var origToLevel = new int[count];
        for (var i = 0; i < count; i++) origToLevel[i] = i;

        if (m > 0)
        {
            var curAdj = adj;
            var curSelf = self;
            var curCount = count;
            while (true)
            {
                var comm = LouvainLocalMoving(curAdj, curSelf, curCount, m);
                var numComm = curCount == 0 ? 0 : comm.Max() + 1;
                for (var i = 0; i < count; i++) origToLevel[i] = comm[origToLevel[i]];
                if (numComm == curCount) break; // no merging happened — converged
                (curAdj, curSelf) = LouvainAggregate(curAdj, curSelf, comm, numComm);
                curCount = numComm;
            }
        }

        var canonical = new Dictionary<int, int>();
        var result = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var c = origToLevel[i];
            if (!canonical.TryGetValue(c, out var cid)) { cid = canonical.Count; canonical[c] = cid; }
            result[ids[i]] = cid;
        }
        return result;
    }

    private static double WeightedDegree(Dictionary<int, double> neighbours, double selfLoop)
    {
        var d = selfLoop * 2.0;
        foreach (var w in neighbours.Values) d += w;
        return d;
    }

    /// <summary>One Louvain level: greedily moves each node to the neighbouring community with the largest
    /// positive modularity gain until stable; returns a canonicalized community id per node.</summary>
    private static int[] LouvainLocalMoving(Dictionary<int, double>[] adj, double[] self, int n, double m)
    {
        var community = new int[n];
        var k = new double[n];
        var sigmaTot = new double[n];
        for (var i = 0; i < n; i++) { community[i] = i; k[i] = WeightedDegree(adj[i], self[i]); sigmaTot[i] = k[i]; }
        var twoM = 2.0 * m;

        var improved = true;
        for (var pass = 0; pass < 100 && improved; pass++)
        {
            improved = false;
            for (var i = 0; i < n; i++)
            {
                var ci = community[i];
                sigmaTot[ci] -= k[i];
                community[i] = -1;

                // Weight from i to each neighbouring community (i already removed).
                var nbr = new Dictionary<int, double>();
                foreach (var kv in adj[i])
                {
                    var cj = community[kv.Key];
                    if (cj < 0) continue;
                    nbr[cj] = nbr.GetValueOrDefault(cj) + kv.Value;
                }
                nbr.TryAdd(ci, 0.0); // staying is always a candidate

                // Gain of joining community c ∝ w(i,c) - Σtot[c]·k[i]/2m. Prefer ci, else smallest id, on tie.
                var bestC = ci;
                var bestGain = nbr[ci] - sigmaTot[ci] * k[i] / twoM;
                foreach (var kv in nbr.OrderBy(x => x.Key))
                {
                    var gain = kv.Value - sigmaTot[kv.Key] * k[i] / twoM;
                    if (gain > bestGain + 1e-12) { bestGain = gain; bestC = kv.Key; }
                }

                community[i] = bestC;
                sigmaTot[bestC] += k[i];
                if (bestC != ci) improved = true;
            }
        }

        var canon = new Dictionary<int, int>();
        var res = new int[n];
        for (var i = 0; i < n; i++)
        {
            if (!canon.TryGetValue(community[i], out var cid)) { cid = canon.Count; canon[community[i]] = cid; }
            res[i] = cid;
        }
        return res;
    }

    /// <summary>Collapses each community into a super-node: internal edges become self-loops, inter-community
    /// edges become weighted super-edges. Total edge weight (and thus m) is preserved.</summary>
    private static (Dictionary<int, double>[] Adj, double[] Self) LouvainAggregate(
        Dictionary<int, double>[] adj, double[] self, int[] comm, int numComm)
    {
        var newAdj = new Dictionary<int, double>[numComm];
        for (var c = 0; c < numComm; c++) newAdj[c] = new Dictionary<int, double>();
        var newSelf = new double[numComm];

        for (var i = 0; i < adj.Length; i++) newSelf[comm[i]] += self[i];

        for (var i = 0; i < adj.Length; i++)
        {
            var ci = comm[i];
            foreach (var kv in adj[i])
            {
                var j = kv.Key;
                if (j < i) continue; // count each undirected edge once
                var cj = comm[j];
                if (ci == cj) newSelf[ci] += kv.Value;
                else { newAdj[ci][cj] = newAdj[ci].GetValueOrDefault(cj) + kv.Value; newAdj[cj][ci] = newAdj[cj].GetValueOrDefault(ci) + kv.Value; }
            }
        }
        return (newAdj, newSelf);
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
