// Licensed to Shonkor under the MIT License.

using System.Numerics.Tensors;
using System.Runtime.InteropServices;

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

    /// <summary>
    /// Reinterprets a stored embedding BLOB as <see cref="float"/>s in place — zero-copy, no per-row
    /// allocation — and is the single choke point for that reinterpret so the endianness assumption lives in
    /// exactly one place (#129).
    /// <para>
    /// The blob is packed native-endian (<see cref="SqliteRowMapper.EmbeddingToBytes"/> uses
    /// <see cref="Buffer.BlockCopy(Array,int,Array,int,int)"/>) and read back native-endian here, so a
    /// write→read round-trip on a single host is always self-consistent regardless of endianness. What the
    /// format is NOT is a fixed little-endian wire format: a <c>.db</c> written on a little-endian host and
    /// then read on a big-endian one (or vice versa) would reinterpret every float with swapped bytes and
    /// return silently-wrong similarities — no crash, just garbage scores.
    /// </para>
    /// <para>
    /// Shonkor targets little-endian platforms only (x64, ARM64). Rather than leave the big-endian case as a
    /// silent-wrong, this guard turns it into a loud <see cref="PlatformNotSupportedException"/>. The check is
    /// free on the hot path: <see cref="BitConverter.IsLittleEndian"/> is a JIT intrinsic that folds to a
    /// compile-time constant, so on a little-endian target the branch is eliminated entirely.
    /// </para>
    /// </summary>
    internal static ReadOnlySpan<float> AsFloats(ReadOnlySpan<byte> blob) =>
        AsFloats(blob, BitConverter.IsLittleEndian);

    /// <summary>
    /// The endianness decision as an explicit parameter — a testing seam so the big-endian guard branch can be
    /// exercised on a little-endian test host (<see cref="BitConverter.IsLittleEndian"/> cannot be flipped at
    /// runtime). The single-argument overload passes the intrinsic, so the JIT still folds the branch away on
    /// the hot path.
    /// </summary>
    internal static ReadOnlySpan<float> AsFloats(ReadOnlySpan<byte> blob, bool littleEndian)
    {
        if (!littleEndian)
        {
            throw new PlatformNotSupportedException(
                "Shonkor persists embeddings as a native-endian float blob and only supports little-endian " +
                "platforms (x64/ARM64). On this big-endian host the zero-copy vector read would reinterpret " +
                "every stored float with swapped bytes and return silently-wrong similarity scores. Re-index " +
                "on this host to rewrite the embeddings, or run Shonkor on a little-endian platform. (#129)");
        }

        return MemoryMarshal.Cast<byte, float>(blob);
    }
}
