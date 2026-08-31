// Licensed to Shonkor under the MIT License.

using System.Text.Json;

namespace Shonkor.Tests;

/// <summary>
/// Reads the <c>.deps.json</c> a project emits next to its own build output — the dependency manifest the
/// host runtime parses to fill the probing paths of the default <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
/// <para>
/// This is the honest oracle for "what does project X drag along?" (#349): compile-time metadata such as
/// <c>GetReferencedAssemblies()</c> only sees packages the code actually <i>uses</i>, so an unused
/// <c>PackageReference</c> slips straight through it while still landing in the runtime manifest.
/// </para>
/// <para>
/// Deliberately policy-free: it names no package and knows nothing about any single project's rules, so the
/// per-project guards own their own assertions and the helper can be reused as-is (#348, and the plugin
/// families after it).
/// </para>
/// </summary>
internal static class BuildArtifacts
{
    /// <summary>
    /// The package ids (version stripped) in the <c>libraries</c> closure of the <c>.deps.json</c> under
    /// <c>src/&lt;projectName&gt;/bin/&lt;Config&gt;/&lt;Tfm&gt;/</c>.
    /// <para>
    /// Configuration and target framework are derived from this test assembly's own output directory rather
    /// than guessed, so Debug and Release both resolve correctly. Any project the tests reference is built in
    /// the same configuration, so the artifact is always there — and every way of not getting a real closure
    /// (no output, no manifest, no <c>libraries</c> section) fails loudly instead of returning an empty set,
    /// which would turn every caller into a guard that passes in a vacuum.
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> PackageClosureOf(string projectName)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);       // …/tests/Shonkor.Tests/bin/<Config>/<Tfm>/
        var tfm = output.Name;
        var configuration = output.Parent!.Name;

        // The manifest is named after the ASSEMBLY, which is not always the project folder — Shonkor.CLI emits
        // shonkor.deps.json, Shonkor.Bench emits shonkor-bench.deps.json. Globbing keeps the call form
        // project-based without baking that equivalence in, so later callers need no signature change.
        var outputDir = RepoPaths.File("src", projectName, "bin", configuration, tfm);
        var manifests = Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, "*.deps.json") : [];

        Assert.True(
            manifests.Length == 1,
            $"Expected exactly one '*.deps.json' under '{outputDir}' but found {manifests.Length}. Without exactly " +
            $"one there is no closure to read and the caller would pass in a vacuum. Build {projectName} in the " +
            $"'{configuration}' configuration first.");

        var depsPath = manifests[0];
        using var doc = JsonDocument.Parse(File.ReadAllText(depsPath));

        Assert.True(
            doc.RootElement.TryGetProperty("libraries", out var libraries),
            $"'{depsPath}' has no 'libraries' section — an empty closure would make every caller's assertion " +
            $"pass in a vacuum, which is the exact failure mode this helper exists to rule out.");

        // Keys are "<packageId>/<version>" — the id alone is what a policy wants to reason about.
        return libraries
            .EnumerateObject()
            .Select(p => p.Name.Split('/')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The path of the installable plugin ZIP a plugin project packs into its own build output
    /// (<c>src/&lt;projectName&gt;/bin/&lt;Config&gt;/&lt;Tfm&gt;/&lt;projectName&gt;.zip</c>).
    /// <para>
    /// A closure cannot answer "does the plugin CARRY this dependency?" — a <c>.deps.json</c> is transitive, so
    /// a package inherited from the host shows up exactly like a privately-bundled one (#348). The artefact
    /// itself is the honest oracle for that question, and it is only reachable for plugins that are not
    /// embedded/seeded (those go through <c>StandardPluginSeeder.OpenEmbeddedZip</c> instead).
    /// </para>
    /// <para>
    /// Kept as policy-free as <see cref="PackageClosureOf"/>: it names no package and no plugin, so the caller
    /// owns the assertion and the plugin families after Sitecore can reuse it unchanged.
    /// </para>
    /// </summary>
    public static string PluginZipOf(string projectName)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);       // …/tests/Shonkor.Tests/bin/<Config>/<Tfm>/
        var tfm = output.Name;
        var configuration = output.Parent!.Name;

        // Globbed rather than derived from the project name, for the same reason PackageClosureOf globs its
        // manifest: the ZIP is named after the ASSEMBLY, which need not match the project folder.
        var outputDir = RepoPaths.File("src", projectName, "bin", configuration, tfm);
        var zips = Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, "*.zip") : [];

        Assert.True(
            zips.Length == 1,
            $"Expected exactly one '*.zip' under '{outputDir}' but found {zips.Length}. Without exactly one " +
            $"there is no packed artefact to inspect and the caller would pass in a vacuum. Build " +
            $"{projectName} in the '{configuration}' configuration first.");

        return zips[0];
    }
}
