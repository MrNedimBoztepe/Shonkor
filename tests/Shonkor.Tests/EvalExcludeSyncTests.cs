// Licensed to Shonkor under the MIT License.

extern alias bench;

using System.Text.Json;
using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// Eval-corpus hygiene lives in two layers that must agree (#140).
/// <para>
/// The de-contamination of #132/#133 put the meta-doc exclusion in two places: <c>shonkor.json</c>'s
/// <c>ExcludePatterns</c> keeps those dirs out of the graph at <b>index time</b>, and
/// <c>RetrievalBenchmark.IsEvalMetaNode</c> ignores them at <b>measurement time</b> (defence in depth). They
/// can drift silently — add a new meta dir to one and forget the other, and the eval quietly re-contaminates
/// (missing from the config) or the config quietly over-excludes (missing from the guard).
/// </para>
/// <para>
/// The guard's directory list is now a single named constant (<c>RetrievalBenchmark.MetaDirectories</c>), and
/// this test bridges it to the config so the config side can't drift away from it. It does <b>not</b> hardcode
/// the directory list itself — that would just be a third copy to drift.
/// </para>
/// </summary>
public class EvalExcludeSyncTests
{
    private static IReadOnlyList<string> ConfigExcludePatterns()
    {
        var json = File.ReadAllText(RepoPaths.File("shonkor.json"));
        using var doc = JsonDocument.Parse(json);
        // The config loader is case-insensitive (CliConfig uses PropertyNameCaseInsensitive), so accept either
        // casing here rather than coupling the test to the exact spelling in the checked-in file.
        var prop = doc.RootElement.EnumerateObject()
            .First(p => string.Equals(p.Name, "ExcludePatterns", StringComparison.OrdinalIgnoreCase));
        return prop.Value.EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    /// <summary>Reduces a glob like <c>**/bench/golden/**</c> to its directory core <c>bench/golden</c>.</summary>
    private static string GlobDirCore(string pattern) =>
        pattern.Replace('\\', '/').Replace("**/", "").Replace("/**", "").Trim('/');

    [Fact]
    public void EveryEvalMetaDirectory_IsAlsoExcludedFromIndexing()
    {
        var cores = ConfigExcludePatterns().Select(GlobDirCore).ToList();

        foreach (var dir in RetrievalBenchmark.MetaDirectories)
        {
            Assert.True(
                cores.Any(c => string.Equals(c, dir, StringComparison.OrdinalIgnoreCase)),
                $"The eval guard treats '{dir}/' as a meta directory (RetrievalBenchmark.MetaDirectories), but " +
                $"shonkor.json's ExcludePatterns does not exclude it from indexing. The two have drifted: the " +
                $"dir would sit in the graph and only the measurement-time guard would keep it out of the eval. " +
                $"Add \"**/{dir}/**\" to shonkor.json — or, if it should stay indexed, remove it from " +
                $"MetaDirectories and rely on the deliberate guard-only path (like bench/*.md).");
        }
    }

    [Fact]
    public void TheGuardOnlyException_IsBenchMarkdown_AndIsNotAConfigExclude()
    {
        // bench/*.md is deliberately GUARD-ONLY: the measurement notes stay indexed (agents may read them) and
        // are excluded only from the eval. So there is intentionally NO matching shonkor.json directory
        // exclude for them — this asserts that exception is real and documented, not an accidental omission.
        var cores = ConfigExcludePatterns().Select(GlobDirCore).ToList();

        Assert.DoesNotContain("bench", cores);         // the whole bench dir is NOT excluded from indexing
        Assert.Contains("bench/golden", RetrievalBenchmark.MetaDirectories); // ...only bench/golden is meta

        // And the guard still catches a bench measurement note despite no config exclude for it.
        Assert.True(RetrievalBenchmark.IsEvalMetaNode(
            new Shonkor.Core.Models.GraphNode { Id = "x", Type = "File", FilePath = "bench/vector-scaling-measurement.md" }));
        // ...while leaving bench SOURCE code eligible (only .md notes are guard-only).
        Assert.False(RetrievalBenchmark.IsEvalMetaNode(
            new Shonkor.Core.Models.GraphNode { Id = "y", Type = "Class", FilePath = "src/Shonkor.Bench/RetrievalBenchmark.cs" }));
    }
}
