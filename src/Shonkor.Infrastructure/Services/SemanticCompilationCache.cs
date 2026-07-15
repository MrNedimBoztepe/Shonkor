// Licensed to Shonkor under the MIT License.

using System.Collections.Concurrent;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Shonkor.Core.Services;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Caches a per-directory Roslyn <see cref="CSharpCompilation"/> so incremental semantic relinks don't
/// rebuild it (an O(repo) parse) on every edit. A Roslyn compilation is immutable, so an edit produces a
/// NEW compilation that reuses every unchanged tree's binding — swapping one tree is cheap. This makes the
/// interactive <c>reindex_file</c> loop and repeated drift reconciles fast in semantic mode.
/// </summary>
/// <remarks>
/// Thread-safety: one entry per directory, guarded by a per-entry semaphore. Readers get the immutable
/// compilation; writers (build / apply-edits / invalidate) hold the gate. Registered as a singleton so it
/// outlives the per-operation <see cref="GraphIndexScanner"/>.
/// </remarks>
public sealed class SemanticCompilationCache
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public CSharpCompilation? Compilation;
        public bool Built;
    }

    private readonly ConcurrentDictionary<string, Entry> _byDirectory = new(FilePaths.Comparer);

    private static string Key(string directoryPath) => Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Returns the cached compilation for the directory, building it once if absent. May be <c>null</c> when the directory has no .cs files.</summary>
    public async Task<CSharpCompilation?> GetOrBuildAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        var entry = _byDirectory.GetOrAdd(Key(directoryPath), _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!entry.Built)
            {
                entry.Compilation = await SemanticCsharpLinker.BuildCompilationForDirectoryAsync(directoryPath, cancellationToken).ConfigureAwait(false);
                entry.Built = true;
            }
            return entry.Compilation;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Applies the given changed files to the cached compilation (reading their current on-disk content) and
    /// returns the updated compilation. A changed file that no longer exists is removed; a new one is added;
    /// an existing one has its tree replaced. If nothing is cached yet, the compilation is built fresh first.
    /// </summary>
    public async Task<CSharpCompilation?> ApplyEditsAsync(string directoryPath, IEnumerable<string> changedFullPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedFullPaths);

        var entry = _byDirectory.GetOrAdd(Key(directoryPath), _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!entry.Built)
            {
                entry.Compilation = await SemanticCsharpLinker.BuildCompilationForDirectoryAsync(directoryPath, cancellationToken).ConfigureAwait(false);
                entry.Built = true;
            }

            var compilation = entry.Compilation;

            foreach (var raw in changedFullPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(FilePaths.Comparer))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!raw.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;

                var full = Path.GetFullPath(raw);
                var existing = compilation?.SyntaxTrees.FirstOrDefault(t => string.Equals(t.FilePath, full, FilePaths.Comparison));

                if (!File.Exists(full))
                {
                    if (existing is not null) compilation = (CSharpCompilation)compilation!.RemoveSyntaxTrees(existing);
                    continue;
                }

                string code;
                try { code = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false); }
                catch { continue; } // unreadable right now — leave the old tree in place

                var newTree = CSharpSyntaxTree.ParseText(code, path: full);

                if (compilation is null)
                {
                    compilation = RoslynSemantics.BuildCompilationFromTrees(new[] { newTree });
                }
                else if (existing is not null)
                {
                    compilation = (CSharpCompilation)compilation.ReplaceSyntaxTree(existing, newTree);
                }
                else
                {
                    compilation = (CSharpCompilation)compilation.AddSyntaxTrees(newTree);
                }
            }

            entry.Compilation = compilation;
            return compilation;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Drops the cached compilation for a directory (e.g. after a full scan rebuilt the whole graph).</summary>
    public void Invalidate(string directoryPath)
    {
        if (_byDirectory.TryGetValue(Key(directoryPath), out var entry))
        {
            // Best-effort: clear without taking the gate; the next Get/Apply rebuilds under the gate.
            entry.Built = false;
            entry.Compilation = null;
        }
    }
}
