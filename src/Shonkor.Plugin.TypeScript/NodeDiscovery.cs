// Licensed to Shonkor under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Shonkor.Plugin.TypeScript;

/// <summary>Where a <c>node</c> onboarding probe landed (#303).</summary>
internal enum NodeAvailability
{
    /// <summary>A working node whose major version meets the minimum was found.</summary>
    Available,

    /// <summary>A working node was found but its major version is below the required minimum (the version gate).</summary>
    TooOld,

    /// <summary>No working node executable was found on the configured path, PATH, or common locations.</summary>
    NotFound,
}

/// <summary>
/// The single, cached outcome of Node onboarding for a scan (#303): whether a usable node was found, and if
/// not, an actionable, human-readable <see cref="Message"/> that names the requirement and how to install.
/// </summary>
internal sealed record NodeState(NodeAvailability Availability, string? Path, string? Version, int? Major, string Message)
{
    /// <summary>True only when a node meeting the version gate was found — the sidecar may be started.</summary>
    public bool IsUsable => Availability == NodeAvailability.Available;
}

/// <summary>
/// Discovers a usable <c>node</c> executable and gates its version (#303). Precedence: the configured
/// <see cref="SidecarSettings.NodePath"/> (authoritative), then <c>PATH</c>, then common install locations.
/// Each candidate is validated with a single, bounded <c>node --version</c> probe whose output is parsed for
/// the major version; a node below <see cref="RequiredMajorVersion"/> is rejected by the gate with a clear
/// found-vs-required message rather than being started and failing cryptically. Discovery runs at most once
/// per plugin instance (per scan); the caller caches the returned <see cref="NodeState"/>.
/// </summary>
internal static class NodeDiscovery
{
    /// <summary>
    /// Minimum Node <b>major</b> version the JS/TS sidecar requires. The ticket (#303) frames the bar as
    /// "18/20"; 18 is chosen as the floor — it is the oldest still-supported LTS line with the stable APIs the
    /// TypeScript Compiler API sidecar relies on, so users on Node 18/20/22/24 are all admitted while genuinely
    /// ancient runtimes (14/16) are gated out. Bump this single constant to raise the bar.
    /// </summary>
    public const int RequiredMajorVersion = 18;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resolves and version-gates a node executable. Returns a <see cref="NodeState"/> describing the outcome;
    /// when it is not <see cref="NodeAvailability.Available"/> the adapter degrades to the Esprima fallback and
    /// surfaces <see cref="NodeState.Message"/> as the diagnostic.
    /// </summary>
    public static NodeState Discover(string? configuredPath, int requiredMajor = RequiredMajorVersion)
        => Discover(configuredPath, requiredMajor, RealProbe);

    /// <summary>
    /// Test seam: the same discovery + version-gate logic over an injected <paramref name="probe"/> so the gate,
    /// the found-vs-required message, and the configured-path override can be exercised deterministically without
    /// spawning a real process. <paramref name="probe"/> returns whether the candidate started and, if so, its
    /// raw <c>--version</c> output (e.g. <c>"v18.19.0"</c>).
    /// </summary>
    internal static NodeState Discover(string? configuredPath, int requiredMajor,
        Func<string, (bool Started, string? VersionOutput)> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        NodeState? tooOld = null;
        foreach (var candidate in Candidates(configuredPath))
        {
            var (started, versionOutput) = probe(candidate);
            if (!started) continue;

            var major = ParseMajor(versionOutput);
            if (major is null) continue; // ran but the version was unintelligible → treat as an unusable candidate.

            var version = versionOutput!.Trim();
            if (major.Value >= requiredMajor)
            {
                return new NodeState(NodeAvailability.Available, candidate, version, major,
                    $"Node {version} at '{candidate}' meets the minimum (requires >= v{requiredMajor}).");
            }

            // A working-but-too-old node: remember the first one so the gate can report found-vs-required rather
            // than a bare "not found" (a far less actionable message). A configured path yields only itself, so
            // an explicit-but-too-old NodePath is reported as too old and never silently bypassed for a PATH node.
            tooOld ??= new NodeState(NodeAvailability.TooOld, candidate, version, major,
                $"Node {version} at '{candidate}' is too old for the JS/TS sidecar, which requires Node >= v{requiredMajor}. " +
                "Install a current LTS from https://nodejs.org.");
        }

        if (tooOld is not null) return tooOld;

        var message = configuredPath is { Length: > 0 }
            ? $"configured NodePath '{configuredPath}' is not a usable Node >= v{requiredMajor}. " +
              "Install Node from https://nodejs.org, or correct NodePath in the plugin's sidecar.settings.json."
            : $"no Node executable found on PATH or in common install locations. The JS/TS sidecar requires Node >= v{requiredMajor}; " +
              "install it from https://nodejs.org (JS/TS files use the Esprima fallback until then).";
        return new NodeState(NodeAvailability.NotFound, null, null, null, message);
    }

    /// <summary>
    /// Parses the major version out of a <c>node --version</c> line. Robust to a leading <c>v</c>, surrounding
    /// whitespace, and pre-release suffixes (e.g. <c>"v22.0.0-nightly"</c> → 22). Returns null when the input is
    /// empty or does not begin with a version number.
    /// </summary>
    internal static int? ParseMajor(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput)) return null;

        var s = versionOutput.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

        var dot = s.IndexOf('.');
        var majorPart = dot >= 0 ? s[..dot] : s;

        var digits = 0;
        while (digits < majorPart.Length && char.IsAsciiDigit(majorPart[digits])) digits++;
        if (digits == 0) return null;

        return int.TryParse(majorPart[..digits], out var major) ? major : null;
    }

    private static IEnumerable<string> Candidates(string? configuredPath)
    {
        // A configured NodePath is an explicit, deliberate choice: it is AUTHORITATIVE. If it does not
        // resolve to a working node, degrade to the Esprima fallback rather than silently using a different
        // node from PATH — that would mask a misconfiguration (and is exactly the AC#3 degradation case).
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath;
            yield break;
        }

        // PATH: rely on the OS resolver by using the bare command name.
        yield return IsWindows ? "node.exe" : "node";

        foreach (var common in CommonLocations())
        {
            yield return common;
        }
    }

    private static IEnumerable<string> CommonLocations()
    {
        if (IsWindows)
        {
            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(programFiles)) yield return Path.Combine(programFiles, "nodejs", "node.exe");
            if (!string.IsNullOrEmpty(programFilesX86)) yield return Path.Combine(programFilesX86, "nodejs", "node.exe");
            if (!string.IsNullOrEmpty(appData)) yield return Path.Combine(appData, "npm", "node.exe");
        }
        else
        {
            yield return "/usr/local/bin/node";
            yield return "/usr/bin/node";
            yield return "/opt/homebrew/bin/node";
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                // nvm / fnm default shims.
                yield return Path.Combine(home, ".volta", "bin", "node");
                yield return Path.Combine(home, ".local", "bin", "node");
            }
        }
    }

    private static (bool Started, string? VersionOutput) RealProbe(string candidate)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            if (!process.Start()) return (false, null);
            if (!process.WaitForExit(ProbeTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, null);
            }

            if (process.ExitCode != 0) return (false, null);
            return (true, process.StandardOutput.ReadToEnd());
        }
        catch
        {
            // Not found / not executable / access denied — the caller tries the next candidate.
            return (false, null);
        }
    }

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
