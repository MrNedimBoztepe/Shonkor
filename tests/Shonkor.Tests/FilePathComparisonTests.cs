// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services.Mcp;

namespace Shonkor.Tests;

/// <summary>
/// Paths are compared the way the FILESYSTEM compares them, not the way Windows does (#235).
///
/// <para>
/// The codebase had <c>StringComparison.OrdinalIgnoreCase</c> scattered over paths, node ids and path-keyed
/// dictionaries. That is a Windows assumption — on Linux <c>Foo.cs</c> and <c>foo.cs</c> are two different
/// files — and it was invisible until CI started running on Linux too (#209). The failure mode was never a
/// crash: it was an index scanner that treats two files as one and therefore never clears a deleted one
/// (a ghost node surviving every rescan), and a content-hash dictionary where one file's hash answers for
/// another (a changed file judged Fresh and never re-parsed).
/// </para>
/// <para>
/// Separately, and worse because it is wrong on <b>every</b> platform: containment was tested with a bare
/// <c>StartsWith</c>. That is not a containment test.
/// </para>
/// </summary>
public class FilePathComparisonTests
{
    // ---- the bug that is NOT platform-specific: prefix != containment ---------------------------------

    /// <summary>
    /// <c>basePath = /x/proj</c> and a node id under the SIBLING directory <c>/x/project</c>. A bare
    /// <c>StartsWith</c> matched, and the "relative" remainder came out as <c>ect/File.cs</c> — so the handle
    /// round-trip silently reconstructed a different, non-existent file. Sibling directories sharing a prefix
    /// (<c>repo</c>/<c>repo2</c>, <c>src</c>/<c>src-gen</c>) are ordinary, so this was always reachable; Linux
    /// merely made the whole class of bug visible.
    /// </summary>
    [Fact]
    public void ASiblingDirectorySharingAPrefix_IsNotInsideTheProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");
        var sibling = Path.Combine(Path.GetTempPath(), "project", "File.cs");

        Assert.False(FilePaths.TryGetRelative(sibling, root, out _),
            "'…/project/File.cs' is not inside '…/proj' — a shared prefix is not containment");

        // The handle must therefore be left alone, not rewritten as if it lived under the root.
        Assert.Equal(sibling, McpToolHelpers.ToHandle(sibling, root));
        Assert.Equal(sibling, McpToolHelpers.Shorten(sibling, root));
    }

    [Fact]
    public void AFileGenuinelyInsideTheProject_StillShortensAndRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");
        var inside = Path.Combine(root, "src", "File.cs");

        var handle = McpToolHelpers.ToHandle(inside, root);

        Assert.Equal("@/src/File.cs", handle);                       // always '/', whatever the host separator
        Assert.Equal(inside, McpToolHelpers.FromHandle(handle, root)); // …and it comes back as the real path
    }

    /// <summary>
    /// A handle is an identifier that travels: it is emitted to agents, quoted back, and persisted. It used to
    /// carry the minting platform's separator (<c>@/Services\Foo.cs</c> on Windows), so it did not survive a
    /// trip to Linux. A handle is portable or it is not an identifier.
    /// </summary>
    [Fact]
    public void AHandleMintedAnywhere_ExpandsOnThisPlatform()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");

        var expanded = McpToolHelpers.FromHandle("@/src/deep/File.cs", root);

        Assert.Equal(Path.Combine(root, "src", "deep", "File.cs"), expanded);
        Assert.DoesNotContain('/', expanded[root.Length..].Replace(Path.DirectorySeparatorChar, '|'));
    }

    [Fact]
    public void TheProjectRootItself_HasNoRelativeForm_AndIsLeftAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");

        Assert.False(FilePaths.TryGetRelative(root, root, out _));
        Assert.Equal(root, McpToolHelpers.Shorten(root, root));
    }

    [Fact]
    public void APathEscapingUpward_IsNotInside()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");
        var outside = Path.Combine(Path.GetTempPath(), "secrets.txt");

        Assert.False(FilePaths.TryGetRelative(outside, root, out _));
    }

    // ---- the platform-dependent half: compare the way the filesystem does -----------------------------

    /// <summary>
    /// The whole point of <see cref="FilePaths.Comparison"/>: path equality is a property of the filesystem,
    /// not of the string. Asserting the same answer on both platforms would be asserting that one of them is
    /// wrong.
    /// </summary>
    [Fact]
    public void PathCaseSensitivity_FollowsThePlatform_NotAHabit()
    {
        var lower = Path.Combine(Path.GetTempPath(), "proj", "handler.cs");
        var upper = Path.Combine(Path.GetTempPath(), "proj", "Handler.cs");

        if (OperatingSystem.IsWindows())
        {
            Assert.True(FilePaths.AreEqual(lower, upper), "on Windows these name the same file");
        }
        else
        {
            Assert.False(FilePaths.AreEqual(lower, upper),
                "on Linux these are TWO files — treating them as one is what let a deleted file survive as a " +
                "ghost node, and let one file's content hash answer for another");
        }
    }

    /// <summary>
    /// The concrete consequence, at the data structure that actually caused the damage: the index scanner's
    /// candidate set and the storage layer's content-hash dictionary are both keyed by file path.
    /// </summary>
    [Fact]
    public void APathKeyedSet_KeepsTwoFilesApart_ExactlyWhenTheFilesystemDoes()
    {
        var set = new HashSet<string>(FilePaths.Comparer)
        {
            Path.Combine("src", "Handler.cs"),
            Path.Combine("src", "handler.cs")
        };

        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, set.Count);
    }
}
