// Licensed to Shonkor under the MIT License.

namespace Shonkor.Bench;

/// <summary>One retrieval evaluation case: a query and the node ids/names that count as a correct hit.</summary>
internal sealed class GoldenCase
{
    public string Id { get; set; } = "";
    public string Query { get; set; } = "";
    /// <summary>"graph" (FTS, default) or "semantic" (vector).</summary>
    public string? Mode { get; set; }
    /// <summary>Node-id substrings or exact names that count as a correct hit.</summary>
    public List<string> Expected { get; set; } = new();
}

/// <summary>Aggregate retrieval metrics for one retriever over a golden set.</summary>
internal sealed record MetricSet(int Cases, double PrecisionAt1, double PrecisionAtK, double RecallAtK, double Mrr)
{
    public string Row(string label) =>
        $"| {label} | {Cases} | {PrecisionAt1:F3} {Ci(PrecisionAt1)} | {PrecisionAtK:F3} | {RecallAtK:F3} {Ci(RecallAtK)} | {Mrr:F3} |";

    /// <summary>95% confidence half-width (normal approximation) so a small golden set reads as a range,
    /// not a false-precision point estimate.</summary>
    private string Ci(double p)
    {
        if (Cases <= 0) return string.Empty;
        var halfWidth = 1.96 * Math.Sqrt(Math.Max(p * (1 - p), 0) / Cases);
        return $"±{halfWidth:F3}";
    }
}

/// <summary>Aggregate token-budget result: naive full-dump vs the budgeted capsule of the same subgraph.</summary>
internal sealed record TokenResult(int Queries, long NaiveTokens, long CapsuleTokens)
{
    public double ReductionPct => NaiveTokens > 0 ? (1.0 - (double)CapsuleTokens / NaiveTokens) * 100 : 0;
}
