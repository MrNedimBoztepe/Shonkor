// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Services;

/// <summary>
/// The revision a directory was at, read from <c>.git</c> as files — never by running <c>git</c>.
///
/// <para>
/// AP8 stage 1 (#449): the graph records which revision it was built from, so an answer can say whether
/// it still matches the working tree. Without it the only question the index can answer is "did this one
/// file change", which is not the question an agent has before trusting a result.
/// </para>
///
/// <para>
/// Reading files rather than spawning a process is not a micro-optimisation. The published runtime image
/// ships no <c>git</c> binary, so a process-based implementation would return "unknown" for every
/// containerised deployment while working perfectly on the developer machine that wrote it — the exact
/// asymmetry that let a CRLF defect live in this codebase until #436, invisible to one CI leg.
/// </para>
///
/// <para>
/// Returns <c>null</c> when there is no readable revision. That is a third state, not a failure: a
/// directory can legitimately not be a repository, and "unknown" must never be reported as "matches".
/// </para>
/// </summary>
public static class WorkingTreeRevision
{
    /// <summary>
    /// The commit id <paramref name="directory"/> currently points at, or <c>null</c> if it cannot be
    /// determined. Walks up from the directory so a scan of a subfolder still finds the repository root.
    /// </summary>
    public static string? TryRead(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(directory));
            while (dir is not null)
            {
                var gitPath = Path.Combine(dir.FullName, ".git");

                // A worktree or submodule has `.git` as a FILE containing "gitdir: <path>".
                var gitDir = Directory.Exists(gitPath) ? gitPath
                    : File.Exists(gitPath) ? ResolveGitDirFile(gitPath, dir.FullName)
                    : null;

                if (gitDir is not null) return ReadHead(gitDir);
                dir = dir.Parent;
            }
        }
        catch
        {
            // Unreadable for any reason — indistinguishable from "not a repository" to the caller, and
            // both mean the same thing here: we cannot claim the graph matches anything.
        }

        return null;
    }

    private static string? ResolveGitDirFile(string gitFile, string baseDir)
    {
        var line = File.ReadAllText(gitFile).Trim();
        const string prefix = "gitdir:";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var target = line[prefix.Length..].Trim();
        var resolved = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(baseDir, target));
        return Directory.Exists(resolved) ? resolved : null;
    }

    private static string? ReadHead(string gitDir)
    {
        var headFile = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headFile)) return null;

        var head = File.ReadAllText(headFile).Trim();

        // Detached HEAD: the file holds the commit id itself.
        if (!head.StartsWith("ref:", StringComparison.Ordinal)) return Normalize(head);

        var refName = head[4..].Trim();                       // e.g. refs/heads/develop
        var loose = Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose)) return Normalize(File.ReadAllText(loose).Trim());

        // Packed refs: one "<sha> <refname>" per line, comments and peeled tags mixed in.
        var packed = Path.Combine(gitDir, "packed-refs");
        if (!File.Exists(packed)) return null;
        foreach (var line in File.ReadLines(packed))
        {
            if (line.Length == 0 || line[0] is '#' or '^') continue;
            var space = line.IndexOf(' ');
            if (space > 0 && line[(space + 1)..].Trim() == refName) return Normalize(line[..space]);
        }

        return null;
    }

    /// <summary>A commit id is 40 hex characters; anything else is not one, and guessing would be worse.</summary>
    private static string? Normalize(string value)
    {
        var v = value.Trim();
        return v.Length == 40 && v.All(Uri.IsHexDigit) ? v.ToLowerInvariant() : null;
    }
}
