// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Services;

/// <summary>
/// Small vector helpers for embedding search. L2-normalizing every stored and query vector once lets the
/// hot path score with a plain dot product — which for unit vectors equals cosine similarity but skips the
/// per-row magnitude computation cosine would otherwise repeat for every node in the database. Kept
/// dependency-free (no System.Numerics.Tensors) so it can live in Shonkor.Core; these run once per vector
/// at index/query time, not in the per-row scoring loop, so the plain loop is not a hot path.
/// </summary>
public static class VectorMath
{
    private static float MagnitudeOf(ReadOnlySpan<float> vector)
    {
        double sumSquares = 0;
        foreach (var v in vector) sumSquares += (double)v * v;
        return (float)Math.Sqrt(sumSquares);
    }

    /// <summary>
    /// L2-normalizes <paramref name="vector"/> IN PLACE (each component divided by the vector's length), so
    /// its magnitude becomes 1. A zero-magnitude vector (all components 0, or a NaN/degenerate blob) is left
    /// unchanged — there is no meaningful direction to normalize to, and dividing would produce NaN.
    /// </summary>
    public static void NormalizeL2(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length == 0) return;

        var norm = MagnitudeOf(vector);
        if (norm is 0f or float.NaN || float.IsInfinity(norm)) return;

        for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
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
    /// the one-time migration to skip vectors that are already normalized (idempotence, cheap re-runs).
    /// </summary>
    public static bool IsUnitLength(ReadOnlySpan<float> vector, float tolerance = 1e-3f)
    {
        if (vector.Length == 0) return false;
        return MathF.Abs(MagnitudeOf(vector) - 1f) <= tolerance;
    }
}
