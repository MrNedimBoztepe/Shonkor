// Licensed to Shonkor under the MIT License.

namespace Shonkor.Tests;

/// <summary>
/// The CI workflow carries load-bearing assumptions about a <b>security</b> guard, and this pins them
/// (#182, #209).
///
/// <para>
/// Path containment (#104/#105) stops an MCP tool reading outside the project root, and its two halves are
/// <b>platform-specific</b>:
/// </para>
/// <list type="bullet">
///   <item><b>Linux</b> — <c>File.CreateSymbolicLink</c> needs no privilege, so the file-symlink escape runs
///   for real. On Windows it must skip (a file symlink needs Developer Mode/admin), so Linux is the <i>only</i>
///   place that case is ever exercised.</item>
///   <item><b>Windows</b> — junctions, drive-letter paths, and the case-<i>insensitive</i> path comparison
///   <c>TryResolveContainedPath</c> depends on. None of that is exercised on Linux at all.</item>
/// </list>
/// <para>
/// So the suite must run on <b>both</b>, on <b>every PR</b>. Neither was true. #182 pinned the Linux runner but
/// left Windows uncovered; and the workflow's triggers named only <c>main</c> while every feature PR targets
/// <c>develop</c> — so CI ran when develop was promoted, and <b>never on the PR that introduced the code</b>.
/// A gate that does not run on the change is not a gate, which is why these are tests and not a comment.
/// </para>
/// </summary>
public class CiWorkflowContractTests
{
    private static string Ci() => File.ReadAllText(RepoPaths.File(".github", "workflows", "ci.yml"));

    /// <summary>
    /// The integration branch is <c>develop</c>: every feature PR targets it. If CI does not trigger on it, the
    /// suite never runs on the commit that introduces a regression — which is exactly what was happening (#209).
    /// </summary>
    [Fact]
    public void CiRunsOnDevelop_TheBranchEveryFeaturePrActuallyTargets()
    {
        var ci = Ci();

        Assert.Contains("branches: [ main, develop ]", ci, StringComparison.Ordinal);

        // Both triggers, not just one: a push-only filter would leave PRs unchecked, and vice versa.
        var occurrences = ci.Split("branches: [ main, develop ]").Length - 1;
        Assert.True(occurrences >= 2,
            "both the push and pull_request triggers must include develop — otherwise the suite does not run " +
            "on the PRs where code actually lands, and every guard CI is supposed to enforce is decorative.");
    }

    /// <summary>
    /// The Linux leg. Its runner stays deliberately overridable (dispatch input, then the <c>CI_RUNNER</c>
    /// variable) so CI can be moved onto the self-hosted box — it is the <b>fallback</b> that is load-bearing.
    /// </summary>
    [Fact]
    public void ALinuxLegExists_SoThePrivilegedSymlinkEscapeRunsForReal()
    {
        var ci = Ci();

        Assert.Contains("|| 'ubuntu-latest'", ci, StringComparison.Ordinal);
        Assert.Contains("name: linux", ci, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Windows leg (#209). Without it, the junction-based directory escape — the way the #104 attack is
    /// actually built on Windows — and the case-insensitive path comparison are verified on nobody's machine
    /// but a developer's.
    /// </summary>
    [Fact]
    public void AWindowsLegExists_SoTheWindowsHalfOfPathContainmentIsTestedToo()
    {
        var ci = Ci();

        Assert.Contains("windows-latest", ci, StringComparison.Ordinal);
        Assert.Contains("name: windows", ci, StringComparison.Ordinal);
    }

    /// <summary>
    /// A break on one platform must not cancel the other leg, or a Windows-only regression could mask a
    /// Linux-only one (and we would fix one and ship the other).
    /// </summary>
    [Fact]
    public void TheMatrixDoesNotFailFast_SoOnePlatformsBreakCannotHideAnothers()
    {
        Assert.Contains("fail-fast: false", Ci(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A Linux runner that only builds would satisfy the checks above while covering nothing.
    /// </summary>
    [Fact]
    public void TheCiJob_ActuallyRunsTheTestProject()
    {
        Assert.Contains("dotnet test tests/Shonkor.Tests/Shonkor.Tests.csproj", Ci(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ...and runs the whole suite. A <c>--filter</c> would let the job go green while skipping the very class
    /// this contract exists to protect.
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
