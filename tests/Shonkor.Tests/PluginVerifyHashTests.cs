// Licensed to Shonkor under the MIT License.

using System.IO.Compression;

using Shonkor.Core.Models;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// #416: a scan's output depends on which BUILD of each plugin is loaded, and nothing checked that. This
/// workspace held four stale first-party binaries as recently as 2026-08-17 — one from 2 July running
/// against a contract from 9 July — and a cold scan of a real solution produced 3 767 wrongly-tiered edges
/// as a result. Neither the scan nor the loader said anything.
///
/// <para>
/// The comparison itself is <see cref="StandardPluginSeeder.TryHashEntryAssemblyInZip"/>, already proven by
/// #414's refresh path. What these tests pin is the property that makes it usable as a gate: it compares the
/// artifact's <b>content</b>, so it does not depend on anyone having remembered to bump a version.
/// </para>
/// </summary>
public sealed class PluginVerifyHashTests : IDisposable
{
    private readonly List<string> _paths = new();

    private string Temp(string suffix)
    {
        var p = Path.Combine(Path.GetTempPath(), $"shonkor-verify-test-{Guid.NewGuid():N}{suffix}");
        _paths.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in _paths)
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                else if (File.Exists(p)) File.Delete(p);
            }
            catch { /* best effort */ }
        }
    }

    private string MaterializeEmbedded()
    {
        using var stream = StandardPluginSeeder.OpenEmbeddedZip();
        Assert.NotNull(stream);
        var path = Temp(".zip");
        using (var file = File.Create(path)) stream!.CopyTo(file);
        return path;
    }

    /// <summary>Rewrites the package's entry assembly with one byte appended, leaving the manifest alone.</summary>
    private static void MutateEntryAssembly(string zipPath)
    {
        string entryName;
        using (var probe = ZipFile.OpenRead(zipPath))
        {
            using var manifest = probe.GetEntry("plugin.json")!.Open();
            entryName = System.Text.Json.JsonDocument.Parse(manifest)
                .RootElement.GetProperty("entryAssembly").GetString()!;
        }

        byte[] original;
        using (var read = ZipFile.OpenRead(zipPath))
        using (var s = read.GetEntry(entryName)!.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            original = ms.ToArray();
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        archive.GetEntry(entryName)!.Delete();
        using var w = archive.CreateEntry(entryName).Open();
        w.Write(original);
        w.WriteByte(0x00);
    }

    /// <summary>
    /// The gate's core: an installed plugin whose binary differs from the built artifact is detectable by
    /// comparing hashes, with no reliance on the manifest.
    /// </summary>
    [Fact]
    public void ChangedBinary_ProducesADifferentHash()
    {
        var pristine = MaterializeEmbedded();
        var mutated = MaterializeEmbedded();
        MutateEntryAssembly(mutated);

        var a = StandardPluginSeeder.TryHashEntryAssemblyInZip(pristine);
        var b = StandardPluginSeeder.TryHashEntryAssemblyInZip(mutated);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// Why the hash and not the version — the measured reason, pinned as behaviour. The mutated package still
    /// declares the same version, so a version comparison sees two identical plugins. When the four stale
    /// first-party plugins here were rebuilt, all four binaries changed and only one had moved its version:
    /// a version check would have caught one in four.
    /// </summary>
    [Fact]
    public void ChangedBinary_KeepsTheSameDeclaredVersion()
    {
        var pristine = MaterializeEmbedded();
        var mutated = MaterializeEmbedded();
        MutateEntryAssembly(mutated);

        static string VersionOf(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            using var manifest = zip.GetEntry("plugin.json")!.Open();
            return System.Text.Json.JsonDocument.Parse(manifest).RootElement.GetProperty("version").GetString()!;
        }

        Assert.Equal(VersionOf(pristine), VersionOf(mutated));           // the claim is unchanged...
        Assert.NotEqual(                                                 // ...the artifact is not
            StandardPluginSeeder.TryHashEntryAssemblyInZip(pristine),
            StandardPluginSeeder.TryHashEntryAssemblyInZip(mutated));
    }

    /// <summary>
    /// An installed plugin's recorded <c>EntryAssemblySha256</c> is directly comparable with the hash taken
    /// out of the package — the extraction is byte-identical, so the gate needs no new registry field.
    /// </summary>
    [Fact]
    public void RecordedInstallHash_MatchesTheHashTakenFromThePackage()
    {
        var workspace = Temp("");
        Directory.CreateDirectory(workspace);
        var package = MaterializeEmbedded();

        var registry = new PluginRegistry(workspace);
        Assert.True(registry.InstallFromZip(package).Success);

        var installed = registry.List().Single(p => p.Manifest.Id == StandardPluginSeeder.TypeScriptPluginId);

        Assert.Equal(
            StandardPluginSeeder.TryHashEntryAssemblyInZip(package),
            installed.EntryAssemblySha256,
            ignoreCase: true);
    }

    /// <summary>
    /// An unreadable package must return null rather than a hash of nothing: "cannot verify" and "verified
    /// different" have to stay distinguishable, or the gate turns a missing artifact into a false alarm —
    /// and the caller reports it as NOT verified instead of as passing.
    /// </summary>
    [Fact]
    public void UnreadablePackage_IsNullRatherThanAWrongAnswer()
    {
        var notAZip = Temp(".zip");
        File.WriteAllText(notAZip, "this is not a zip archive");

        Assert.Null(StandardPluginSeeder.TryHashEntryAssemblyInZip(notAZip));
        Assert.Null(StandardPluginSeeder.TryHashEntryAssemblyInZip(Temp(".zip"))); // absent file
    }
}
