// Licensed to Shonkor under the MIT License.

using System.Diagnostics;

namespace Shonkor.Tests;

/// <summary>
/// Runs <c>bench/golden/ap6/rg-only-hook.test.sh</c>, which replays the shared case table
/// (<c>rg-command-cases.tsv</c>) through the REAL hook (#513, #514). The C# half of the same table is
/// <see cref="Ap6RunReaderTests.IsRgCommand_MatchesTheSharedCaseTable"/>: the hook decides what the rg arm
/// may execute and <c>IsRgCommand</c> decides what the run is judged as, so the two must answer every case
/// identically or a command runs and is then scored as clean.
///
/// <para>The hook needs bash and node. Both are present on the Linux CI runner, so this runs for real
/// there; where one of them is missing it reports a skip rather than a green pass over nothing — the
/// lesson of #182.</para>
/// </summary>
public class Ap6RgOnlyHookTests
{
    [SkippableFact]
    public void TheHook_AgreesWithTheSharedCaseTable()
    {
        var bash = FindBash();
        Skip.If(bash is null, "no POSIX bash available (on Windows: Git for Windows)");
        Skip.IfNot(CanRun("node", "--version"), "node is not available (the hook reads its payload with it)");

        // A path relative to the repository root: this bash may be Git's, which has its own idea of what a
        // drive letter means.
        var (exitCode, output) = Run(bash!, "bench/golden/ap6/rg-only-hook.test.sh");

        Assert.True(exitCode == 0, $"rg-only-hook.test.sh failed:{Environment.NewLine}{output}");
        Assert.Contains("0 failed", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Git for Windows' bash, not <c>bash.exe</c> off PATH: on Windows that is usually WSL's, which sees no
    /// <c>C:/</c> path and would fail for a reason that has nothing to do with the rule under test.
    /// </summary>
    private static string? FindBash()
    {
        if (!OperatingSystem.IsWindows()) return "bash";
        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
        }.FirstOrDefault(File.Exists);
    }

    private static bool CanRun(string file, string arguments)
    {
        try { return Run(file, arguments).ExitCode == 0; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private static (int ExitCode, string Output) Run(string file, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoPaths.Root,
        })!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(milliseconds: 120_000);
        return (p.ExitCode, stdout + stderr);
    }
}
