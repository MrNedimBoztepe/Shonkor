// Licensed to Shonkor under the MIT License.

using System.IO.Compression;
using System.Runtime.Loader;

using Shonkor.Infrastructure.Services;
using Shonkor.Plugin.Sitecore;

namespace Shonkor.Tests;

/// <summary>
/// Acceptance-criteria coverage for #348: the Sitecore plugin carries YamlDotNet as its OWN private
/// dependency instead of borrowing the host's, which is what let <c>Shonkor.Core</c>'s dead
/// <c>YamlDotNet</c> PackageReference stay load-bearing.
/// <para>
/// Before this, the plugin shipped a lone DLL and resolved YamlDotNet by ALC fall-through to the default
/// context (<see cref="AssemblyPluginLoader"/>'s resolver returns null, so the load reaches the host) — an
/// arrangement that <i>simulates</i> plugin isolation. Removing the reference the way #312 removed Esprima
/// would have broken the plugin silently, at parse time.
/// </para>
/// </summary>
public sealed class SitecorePluginPackagingTests : IDisposable
{
    private const string SitecorePluginId = "shonkor-sitecore";

    /// <summary>A minimal Unicorn item: enough for the parser to reach YamlDotNet and emit a SitecoreItem.</summary>
    private const string UnicornYaml = """
        ---
        ID: "fc69d9bd-c738-4e69-b450-227f17f1dd1f"
        Parent: "da61ad50-8fdb-4252-a68f-b4470b1c9fe8"
        Template: "7ee0975b-0698-493e-b3a2-0b2ef33d0522"
        Path: /sitecore/layout/Renderings/Feature/Blog
        DB: master
        """;

    private readonly List<string> _tempPaths = new();

    private string NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shonkor-sitecore-pack-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempPaths.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var p in _tempPaths)
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                else if (File.Exists(p)) File.Delete(p);
            }
            catch { /* best effort */ }
        }
    }

    // ---- AC#1: the packed artefact CARRIES YamlDotNet.dll ----
    //
    // Asserted on the ZIP entries, deliberately NOT on BuildArtifacts.PackageClosureOf: a .deps.json closure
    // is transitive, so YamlDotNet was already in this plugin's closure BEFORE this ticket — inherited through
    // Shonkor.Core, i.e. through exactly the coupling being removed. A closure cannot tell "carries it
    // privately" from "inherits it from the host"; the artefact can.

    [Fact]
    public void Package_ZipRoot_ContainsManifestDllAndTheYamlDotNetItNowCarriesPrivately()
    {
        using var archive = ZipFile.OpenRead(BuildArtifacts.PluginZipOf("Shonkor.Plugin.Sitecore"));
        var entries = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet();

        Assert.Contains("plugin.json", entries);
        Assert.Contains("Shonkor.Plugin.Sitecore.dll", entries);
        Assert.Contains("YamlDotNet.dll", entries); // the private YAML deserializer, carried into the ALC folder
    }

    // ---- AC#2: it RESOLVES from the plugin's own install folder, inside the plugin ALC ----
    //
    // Shipping the file is not the same as loading it: with the host still able to serve YamlDotNet, a plugin
    // that ignored its own copy would look identical from the outside. Three things make this decisive —
    // a real parse forces the load, the assembly is looked for in the PLUGIN's ALC (a fall-through would put
    // it in the default one), and its location must sit under the install path (not the test bin).

    [Fact]
    public async Task Yaml_ResolvesFromThePluginInstallFolder_InsideThePluginAlc_NotFromTheHost()
    {
        var ws = NewWorkspace();

        var registry = new PluginRegistry(ws);
        Assert.True(registry.InstallFromZip(BuildArtifacts.PluginZipOf("Shonkor.Plugin.Sitecore")).Success);
        Assert.True(registry.Activate(SitecorePluginId).Success);
        var entry = registry.List().Single(p => p.Manifest.Id == SitecorePluginId);

        using var loaded = AssemblyPluginLoader.LoadActive(registry);
        // The Sitecore plugin ships several parsers and the Helix one also claims .yml, so pick by TYPE NAME:
        // the Unicorn parser is the one that deserializes YAML. Matched by name (via nameof, so a rename still
        // breaks the build) rather than by type — the loaded instance is the plugin ALC's own type, which is
        // deliberately NOT identical to the one this test project references.
        var parser = Assert.Single(loaded.Parsers, p => p.GetType().Name == nameof(SitecoreUnicornPlugin));

        var project = NewWorkspace();
        var path = Path.Combine(project, "Blog.yml");
        await File.WriteAllTextAsync(path, UnicornYaml);

        var (nodes, edges) = await parser.ParseAsync(path, UnicornYaml); // deserializes → loads YamlDotNet
        // Behaviour, not just a green call: without a working deserializer the parser swallows the error and
        // returns nothing, so the load assertions below could otherwise pass without YamlDotNet being touched.
        Assert.Contains(nodes, n => n.Type == "SitecoreItem");
        Assert.Contains(edges, e => e.Relationship == "BASED_ON_TEMPLATE");

        var alc = AssemblyLoadContext.GetLoadContext(parser.GetType().Assembly)!;
        Assert.NotSame(AssemblyLoadContext.Default, alc);
        var yaml = Assert.Single(alc.Assemblies, a => a.GetName().Name == "YamlDotNet");
        Assert.StartsWith(entry.InstallPath, yaml.Location, StringComparison.OrdinalIgnoreCase);
    }

    // ---- AC#3: only once the two guards above hold — the host takes no YamlDotNet dependency at all ----
    //
    // The .deps.json is the manifest the host runtime parses to fill the default ALC's probing paths, so no
    // entry there means no host load path. Shonkor.Web is checked alongside Shonkor.Core because it was the
    // actual consumer: it force-loaded YamlDotNet.Serialization.Deserializer into the AppDomain precisely so
    // dynamic plugins could inherit it. The CLI is deliberately NOT checked — the test project does not
    // reference it, so a local run would not build it and the assertion would fail for the wrong reason.

    [Fact]
    public void Host_HasNoYamlDotNetDependency_SoNothingCanResolveItInTheDefaultAlc()
    {
        Assert.DoesNotContain("YamlDotNet", BuildArtifacts.PackageClosureOf("Shonkor.Core"));
        Assert.DoesNotContain("YamlDotNet", BuildArtifacts.PackageClosureOf("Shonkor.Web"));
    }
}
