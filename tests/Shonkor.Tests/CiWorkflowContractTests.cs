// Licensed to Shonkor under the MIT License.

namespace Shonkor.Tests;

/// <summary>
/// The CI workflow carries a load-bearing assumption, and this pins it (#182).
///
/// <para>
/// <c>McpPathContainmentTests.SymlinkedFile_PointingOutsideTheRoot_IsRejected</c> — the FILE-symlink escape
/// from #104 — cannot run on an unprivileged Windows box, because a file symlink needs Developer Mode or admin
/// there. It skips. That is only acceptable because <b>some CI job runs it for real</b>, and on Linux
/// <c>File.CreateSymbolicLink</c> needs no privilege, so it does.
/// </para>
/// <para>
/// "It runs on Linux CI" was an <b>assumption</b>, though — nothing enforced it. Flip the workflow's runner to
/// a Windows image and the file-symlink escape becomes untested <i>everywhere</i>, silently, with the suite
/// still green. That is precisely the failure #180 was careful to avoid for the directory case, reintroduced
/// for the file case by the back door. So the assumption is now a test: if the default runner stops being a
/// Linux image, this fails and says why.
/// </para>
/// </summary>
public class CiWorkflowContractTests
{
    private static string Ci() => File.ReadAllText(RepoPaths.File(".github", "workflows", "ci.yml"));

    /// <summary>
    /// The runner is deliberately overridable (a dispatch input, then the <c>CI_RUNNER</c> variable, then a
    /// literal fallback) so CI can be moved onto the self-hosted box. The <b>fallback</b> — what push and PR
    /// builds actually use unless someone opts out — must stay a Linux image.
    /// </summary>
    [Fact]
    public void TheDefaultCiRunner_IsLinux_SoThePrivilegedSymlinkTestsRunForReal()
    {
        var ci = Ci();

        Assert.True(
            ci.Contains("|| 'ubuntu-latest'", StringComparison.Ordinal),
            "ci.yml's Build & Test job must fall back to a Linux runner. On Windows a FILE symlink needs " +
            "Developer Mode/admin, so McpPathContainmentTests.SymlinkedFile_PointingOutsideTheRoot_IsRejected " +
            "skips there — it is only covered at all because the default runner is Linux. Move the default to a " +
            "Windows image and that path-containment escape (#104) becomes untested everywhere, silently.");
    }

    /// <summary>
    /// ...and that runner must actually execute the test project. A Linux runner that only builds would satisfy
    /// the check above while still leaving the symlink case unexercised.
    /// </summary>
    [Fact]
    public void TheCiJob_ActuallyRunsTheTestProject()
    {
        var ci = Ci();

        Assert.Contains("dotnet test tests/Shonkor.Tests/Shonkor.Tests.csproj", ci, StringComparison.Ordinal);
    }

    /// <summary>
    /// The suite must not be run with a filter that could exclude the containment tests. A `--filter` here
    /// would let the Linux job pass while skipping the very class this contract exists to protect.
    /// </summary>
    [Fact]
    public void TheCiTestStep_RunsTheWholeSuite_Unfiltered()
    {
        var testStep = Ci()
            .Split('\n')
            .First(l => l.Contains("dotnet test tests/Shonkor.Tests", StringComparison.Ordinal));

        Assert.DoesNotContain("--filter", testStep, StringComparison.Ordinal);
    }
}
