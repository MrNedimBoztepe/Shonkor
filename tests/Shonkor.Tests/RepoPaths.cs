// Licensed to Shonkor under the MIT License.

namespace Shonkor.Tests;

/// <summary>
/// Locates the repository root from the test assembly, so the docs-integrity guards (#156, #159) can read
/// the real <c>README.md</c> / <c>docs/</c> / <c>bench/</c> instead of a copied fixture — a guard that reads
/// a fixture cannot catch the thing it exists to catch.
/// </summary>
internal static class RepoPaths
{
    /// <summary>The repository root — the nearest ancestor directory holding the solution file.</summary>
    public static string Root { get; } = FindRoot();

    public static string File(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any()) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate the repository root (no .sln above {AppContext.BaseDirectory}).");
    }
}
