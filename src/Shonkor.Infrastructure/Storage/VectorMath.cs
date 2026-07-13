// Licensed to Shonkor under the MIT License.

using System.Numerics.Tensors;

namespace Shonkor.Infrastructure.Storage;

/// <summary>
/// Vector helpers for embedding search. L2-normalizing every stored and query vector once lets the hot path
/// score with a plain dot product — which for unit vectors equals cosine similarity but skips the per-row
/// magnitude computation cosine would otherwise repeat for every node.
/// <para>
/// Lives in Infrastructure (next to the only callers) and uses <see cref="TensorPrimitives"/> — the SAME
/// SIMD-accelerated library the scoring path scores with — so the norm computed here and the dot product
/// computed there share summation semantics: a vector normalized by this class self-scores to 1.0 under the
/// same primitives, with no second hand-rolled implementation to drift.
/// </para>
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// L2-normalizes <paramref name="vector"/> IN PLACE, so its magnitude becomes 1. A zero-magnitude vector
    /// (all-zero, or a NaN/degenerate blob) is left unchanged — there is no direction to normalize to, and
    /// dividing would produce NaN.
    /// </summary>
    public static void NormalizeL2(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length == 0) return;

        var norm = TensorPrimitives.Norm(vector); // Euclidean (L2) norm, SIMD
        if (norm is 0f or float.NaN || float.IsInfinity(norm)) return;

        TensorPrimitives.Divide(vector, norm, vector);
    }

    /// <summary>Returns a normalized COPY of <paramref name="vector"/>, leaving the original untouched.</summary>
    public static float[] NormalizedCopy(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var copy = (float[])vector.Clone();
        NormalizeL2(copy);
        return copy;
    }

    /// <summary>
    /// Whether <paramref name="vector"/> is already unit-length within <paramref name="tolerance"/> — used by
    /// the one-time migration to skip already-normalized vectors (idempotence, cheap re-runs).
    /// </summary>
    public static bool IsUnitLength(ReadOnlySpan<float> vector, float tolerance = 1e-3f)
    {
        if (vector.Length == 0) return false;
        return MathF.Abs(TensorPrimitives.Norm(vector) - 1f) <= tolerance;
    }
}
