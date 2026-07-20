// Licensed to Shonkor under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Shonkor.Plugin.TypeScript;

/// <summary>
/// Discovers a usable <c>node</c> executable, in precedence order: the configured <see cref="SidecarSettings.NodePath"/>,
/// then <c>PATH</c>, then a handful of common install locations. The chosen candidate is validated with a
/// single, bounded <c>node --version</c> probe and the result is cached — discovery runs at most once per
/// plugin instance (per scan).
/// </summary>
internal static class NodeDiscovery
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Returns the path to a validated node executable, or null when none is usable (the adapter then
    /// degrades to the Esprima fallback). <paramref name="reason"/> carries a human-readable explanation
    /// for the diagnostic surfaced on degradation.
    /// </summary>
    public static string? Discover(string? configuredPath, out string reason)
    {
        foreach (var candidate in Candidates(configuredPath))
        {
            if (Probe(candidate))
            {
                reason = $"node found at '{candidate}'";
                return candidate;
            }
        }

        reason = configuredPath is { Length: > 0 }
            ? $"configured NodePath '{configuredPath}' is not a working node executable, and no node was found on PATH or in common locations"
            : "no node executable found on PATH or in common install locations";
        return null;
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

    private static bool Probe(string candidate)
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

            if (!process.Start()) return false;
            if (!process.WaitForExit(ProbeTimeout))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            if (process.ExitCode != 0) return false;
            var version = process.StandardOutput.ReadToEnd().Trim();
            // A working node prints e.g. "v24.15.0".
            return version.StartsWith('v');
        }
        catch
        {
            // Not found / not executable / access denied — try the next candidate.
            return false;
        }
    }

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
