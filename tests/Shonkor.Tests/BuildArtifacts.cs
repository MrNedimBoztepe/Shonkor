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
    /// The package ids (version stripped) in the <c>libraries</c> closure of
    /// <c>src/&lt;projectName&gt;/bin/&lt;Config&gt;/&lt;Tfm&gt;/&lt;projectName&gt;.deps.json</c>.
    /// <para>
    /// Configuration and target framework are derived from this test assembly's own output directory rather
    /// than guessed, so Debug and Release both resolve correctly. Any project the tests reference is built in
    /// the same configuration, so the artifact is always there — and if it is not, this fails loudly instead
    /// of returning an empty set, which would turn every caller into a guard that passes in a vacuum.
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> PackageClosureOf(string projectName)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);       // …/tests/Shonkor.Tests/bin/<Config>/<Tfm>/
        var tfm = output.Name;
        var configuration = output.Parent!.Name;

        var depsPath = RepoPaths.File(
            "src", projectName, "bin", configuration, tfm, projectName + ".deps.json");

        Assert.True(
            File.Exists(depsPath),
            $"Expected the build artifact '{depsPath}' to exist — without it this guard would silently pass " +
            $"in a vacuum. Build {projectName} in the '{configuration}' configuration first.");

        using var doc = JsonDocument.Parse(File.ReadAllText(depsPath));
        if (!doc.RootElement.TryGetProperty("libraries", out var libraries))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Keys are "<packageId>/<version>" — the id alone is what a policy wants to reason about.
        return libraries
            .EnumerateObject()
            .Select(p => p.Name.Split('/')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
