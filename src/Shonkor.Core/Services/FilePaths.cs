// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Services;

/// <summary>
/// How to compare file paths — <b>the one place that decides</b> (#235).
///
/// <para>
/// Path equality is a property of the <i>filesystem</i>, not of the string. On Windows <c>Foo.cs</c> and
/// <c>foo.cs</c> are the same file; on Linux they are two different files. The codebase had scattered
/// <c>StringComparison.OrdinalIgnoreCase</c> over paths, node ids and path-keyed dictionaries — a Windows
/// assumption, applied everywhere, and invisible until CI started running on Linux too (#209).
/// </para>
/// <para>
/// The failure mode was never a crash. It was <b>silent wrong answers</b>: an index scanner that treats
/// <c>Handler.cs</c> and <c>handler.cs</c> as one file computes staleness against a set that is missing real
/// files, so a deleted file is never cleared and a ghost node survives every rescan; a content-hash dictionary
/// keyed case-insensitively lets one file's hash answer for another, so a changed file is judged Fresh and
/// never re-parsed. Nothing throws. Nothing is logged.
/// </para>
/// <para>
/// So: compare paths with <see cref="Comparison"/> / <see cref="Comparer"/>, never with a hand-picked
/// <c>StringComparison</c>. The values below follow the platform's real semantics rather than one platform's
/// habit.
/// </para>
/// </summary>
public static class FilePaths
{
    /// <summary>
    /// Case-insensitive on Windows, case-sensitive everywhere else — matching what the filesystem actually does.
    /// </summary>
    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>The <see cref="Comparison"/> as a comparer, for path-keyed sets and dictionaries.</summary>
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>Whether two paths name the same file, by the platform's rules.</summary>
    public static bool AreEqual(string? a, string? b) => string.Equals(a, b, Comparison);

    /// <summary>
    /// Whether <paramref name="path"/> lies strictly inside <paramref name="baseDir"/>, and if so what its
    /// relative form is.
    ///
    /// <para>
    /// <b>This exists because a raw <c>StartsWith</c> is not a containment test</b>, and the codebase was using
    /// one. With <c>baseDir = /x/proj</c>, the path <c>/x/project/File.cs</c> passes <c>StartsWith</c> — so the
    /// "relative" remainder came out as <c>ect/File.cs</c>, and the handle round-trip silently reconstructed a
    /// different, non-existent file. Sibling directories sharing a prefix (<c>repo</c>/<c>repo2</c>,
    /// <c>src</c>/<c>src-gen</c>) are ordinary, so this was reachable on every platform — Linux merely made it
    /// obvious.
    /// </para>
    /// <para>
    /// <see cref="Path.GetRelativePath"/> is the correct primitive: it understands separators, and it is
    /// case-sensitive exactly where the filesystem is. A result that escapes upward (<c>..</c>) or comes back
    /// rooted means "outside", which is the same test the security-critical containment gate already uses.
    /// </para>
    /// </summary>
    public static bool TryGetRelative(string? path, string? baseDir, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(baseDir)) return false;

        string rel;
        try
        {
            rel = Path.GetRelativePath(baseDir, path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        // "." means the path IS the base dir — inside, but with nothing relative to say.
        if (rel is "." or "") return false;

        if (rel == ".."
            || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || rel.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(rel))
        {
            return false; // escapes the base directory
        }

        relative = rel;
        return true;
    }
}
