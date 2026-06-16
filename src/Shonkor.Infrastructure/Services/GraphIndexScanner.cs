using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Scans a directory, parses files using registered <see cref="IFileParser"/> implementations,
/// and stores the resulting graph using an <see cref="IGraphStorageProvider"/>.
/// </summary>
public sealed class GraphIndexScanner
{
    private readonly IGraphStorageProvider _storage;
    private readonly IReadOnlyList<IFileParser> _parsers;
    private readonly ILogger? _logger;

    // Upper bound on the content stored on a File node (full content is still hashed).
    private const int MaxFileNodeContentLength = 100_000;

    /// <summary>
    /// Initializes a new instance of <see cref="GraphIndexScanner"/>.
    /// </summary>
    /// <param name="storage">The storage provider for persisting the graph.</param>
    /// <param name="parsers">The parsers to use for extracting nodes and edges from files.</param>
    /// <param name="logger">
    /// Optional logger for scan diagnostics. When omitted, diagnostics go to <c>stderr</c> — never
    /// <c>stdout</c>, which would corrupt the JSON-RPC stream when the scanner runs inside the stdio MCP
    /// server (e.g. via <c>reindex_file</c>).
    /// </param>
    private readonly bool _semanticCsharp;

    public GraphIndexScanner(IGraphStorageProvider storage, IEnumerable<IFileParser> parsers, ILogger? logger = null, bool semanticCsharp = false)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(parsers);

        _storage = storage;
        _parsers = parsers.ToList();
        _logger = logger;
        _semanticCsharp = semanticCsharp;
    }

    /// <summary>Routes a scan diagnostic to the logger, or to stderr — never stdout (see ctor remarks).</summary>
    private void Warn(string message)
    {
        if (_logger != null) _logger.LogWarning("{ScanMessage}", message);
        else Console.Error.WriteLine(message);
    }

    /// <summary>
    /// The result of an indexing operation.
    /// </summary>
    public record IndexResult(int FilesScanned, int NodesCreated, int EdgesCreated, TimeSpan Duration);

    /// <summary>
    /// Scans the specified directory recursively, parses supported files, and updates the graph storage.
    /// Unchanged files are skipped based on their SHA256 content hash.
    /// </summary>
    /// <param name="directoryPath">The root directory to scan.</param>
    /// <param name="excludePatterns">Glob patterns for files or directories to exclude.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An <see cref="IndexResult"/> summarizing the scan.</returns>
    public async Task<IndexResult> ScanDirectoryAsync(
        string directoryPath,
        IReadOnlyList<string> excludePatterns,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(excludePatterns);

        var stopwatch = Stopwatch.StartNew();
        var filesScanned = 0;
        var nodesCreated = 0;
        var edgesCreated = 0;

        // Pre-compute parser mappings for fast lookup
        var parserMap = BuildParserMap();

        // 1. Gather all files supported by our parsers (respecting the exclude globs).
        var candidateFiles = EnumerateCandidateFiles(directoryPath, excludePatterns, parserMap);

        // 2. Pre-fetch existing file content hashes directly (no full-content / subgraph load).
        var existingHashes = await _storage.GetContentHashesAsync(candidateFiles, cancellationToken).ConfigureAwait(false);

        // 2a. If the graph was built under an older node-id scheme, the incremental hash check would skip
        //     unchanged files and leave their ids in the outdated format (a scheme change doesn't alter file
        //     content). Force a full reparse of every candidate so the whole graph is rebuilt under the
        //     current scheme; the version is re-stamped once the scan completes (step 6).
        var schemeStale =
            await _storage.GetNodeIdSchemeVersionAsync(cancellationToken).ConfigureAwait(false)
                < Shonkor.Core.Services.CsharpNodeId.SchemeVersion;
        if (schemeStale)
        {
            Warn($"Node-id scheme is outdated; forcing a full reparse to migrate to scheme v{Shonkor.Core.Services.CsharpNodeId.SchemeVersion}.");
        }

        // 3. Process candidate files incrementally
        var allNodesToUpsert = new ConcurrentBag<GraphNode>();
        var allEdgesToUpsert = new ConcurrentBag<GraphEdge>();
        var filesToClear = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(candidateFiles, cancellationToken, async (filePath, ct) =>
        {
            var extension = Path.GetExtension(filePath);
            if (!parserMap.TryGetValue(extension, out var fileParsers) || fileParsers.Count == 0)
            {
                return;
            }

            Interlocked.Increment(ref filesScanned);

            try
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > 5 * 1024 * 1024) // 5 MB limit
                {
                    Warn($"Skipping large file {filePath} ({fileInfo.Length} bytes)");
                    return;
                }

                // Skip binary files (a NUL byte in the header is a strong binary signal):
                // reading them as text produces garbage nodes and pollutes the FTS index.
                if (await IsLikelyBinaryAsync(filePath, ct).ConfigureAwait(false))
                {
                    return;
                }

                var content = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                var contentHash = ComputeSha256Hash(content);

                // Incremental Hash Check: skip if hash matches DB (unless the id scheme is stale, in which
                // case every file must be reparsed to migrate its ids to the current scheme).
                if (!schemeStale && existingHashes.TryGetValue(filePath, out var existingHash) && existingHash == contentHash)
                {
                    return; // Unchanged!
                }

                // File has changed. We need to clear its old graph structure and re-parse.
                filesToClear.Add(filePath);

                foreach (var parser in fileParsers)
                {
                    var (nodes, edges) = await parser.ParseAsync(filePath, content).ConfigureAwait(false);
                    foreach (var node in nodes) allNodesToUpsert.Add(node);
                    foreach (var edge in edges) allEdgesToUpsert.Add(edge);
                }

                // Create a File node to represent the scanned file itself.
                // Cap stored content to keep the DB / FTS index from bloating on very large files;
                // the full hash is still computed over the complete content above.
                var storedContent = content.Length > MaxFileNodeContentLength
                    ? content[..MaxFileNodeContentLength]
                    : content;

                var fileNode = new GraphNode
                {
                    Id = filePath,
                    Name = Path.GetFileName(filePath),
                    Type = "File",
                    Content = storedContent,
                    FilePath = filePath,
                    ContentHash = contentHash
                };
                allNodesToUpsert.Add(fileNode);

            }
            catch (Exception ex)
            {
                Warn($"Error parsing file {filePath}: {ex.Message}");
            }
        }).ConfigureAwait(false);

        // 3.5 Gather stale files (previously indexed but no longer matched / excluded / deleted)
        var indexedFiles = await _storage.GetAllIndexedFilePathsAsync(cancellationToken).ConfigureAwait(false);
        var candidateFilesSet = new HashSet<string>(candidateFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var indexedFile in indexedFiles)
        {
            if (indexedFile.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase) && !candidateFilesSet.Contains(indexedFile))
            {
                filesToClear.Add(indexedFile);
            }
        }

        // 4. Perform database updates (Deletes & Batch Upserts).
        // Clear all stale files in ONE transaction — looping per-file delete commits once per file,
        // which dominates the cost on large changesets (first index, branch switch, bulk re-scan).
        if (filesToClear.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _storage.DeleteByFilePathsAsync(filesToClear, cancellationToken).ConfigureAwait(false);
        }

        if (allNodesToUpsert.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _storage.UpsertNodesAsync(allNodesToUpsert, cancellationToken).ConfigureAwait(false);
            nodesCreated = allNodesToUpsert.Count;
        }

        if (allEdgesToUpsert.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _storage.UpsertEdgesAsync(allEdgesToUpsert, cancellationToken).ConfigureAwait(false);
            edgesCreated = allEdgesToUpsert.Count;
        }

        // 5. Establish Cross-Technology and Helix Architecture mappings (Post-Scan). When semantic C#
        //    linking is enabled, skip the ambiguous name-based REFERENCES_TYPE resolution here — the
        //    semantic linker produces those edges exactly (resolved symbols), then runs below.
        cancellationToken.ThrowIfCancellationRequested();
        await CrossTechLinker.EstablishCrossTechnologyConnectionsAsync(
            _storage, directoryPath, cancellationToken, resolveCSharpTypeReferences: !_semanticCsharp).ConfigureAwait(false);

        if (_semanticCsharp)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SemanticCsharpLinker.EstablishSemanticEdgesAsync(_storage, directoryPath, cancellationToken).ConfigureAwait(false);
        }

        // 6. Stamp the graph with the current node-id scheme — the whole tree was just (re)built under it,
        //    so any prior staleness is now resolved and get_stats stops recommending a re-index.
        await _storage.SetNodeIdSchemeVersionAsync(Shonkor.Core.Services.CsharpNodeId.SchemeVersion, cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();
        return new IndexResult(filesScanned, nodesCreated, edgesCreated, stopwatch.Elapsed);
    }

    /// <summary>
    /// Enumerates the files under <paramref name="directoryPath"/> that match the parser extensions and are
    /// not excluded by <paramref name="excludePatterns"/>. Shared by the full scan and drift detection so both
    /// see the exact same candidate set.
    /// </summary>
    private static List<string> EnumerateCandidateFiles(
        string directoryPath,
        IReadOnlyList<string> excludePatterns,
        Dictionary<string, List<IFileParser>> parserMap)
    {
        var matcher = new Matcher();
        matcher.AddInclude("**/*");
        foreach (var excludePattern in excludePatterns)
        {
            matcher.AddExclude(excludePattern);
        }

        var dirInfo = new DirectoryInfoWrapper(new DirectoryInfo(directoryPath));
        var matchingResult = matcher.Execute(dirInfo);

        var candidateFiles = new List<string>();
        foreach (var fileMatch in matchingResult.Files)
        {
            var filePath = Path.GetFullPath(Path.Combine(directoryPath, fileMatch.Path));
            if (parserMap.ContainsKey(Path.GetExtension(filePath)))
            {
                candidateFiles.Add(filePath);
            }
        }
        return candidateFiles;
    }

    /// <summary>The freshness of a single file relative to the graph.</summary>
    public enum FreshnessState
    {
        /// <summary>On disk and in the graph with a matching content hash — the graph reflects the file.</summary>
        Fresh,
        /// <summary>On disk and in the graph but the content hash differs — the file was edited since indexing.</summary>
        Stale,
        /// <summary>On disk (and parseable) but not in the graph — never indexed.</summary>
        Untracked,
        /// <summary>In the graph but no longer on disk — deleted since indexing.</summary>
        Deleted
    }

    /// <summary>
    /// A drift report for a directory: files whose on-disk content diverges from the graph. <see cref="Changed"/>
    /// = indexed but content hash now differs; <see cref="New"/> = on disk (parseable) but not indexed;
    /// <see cref="Deleted"/> = indexed but missing on disk. Empty lists mean the graph matches the working tree.
    /// </summary>
    public record DriftReport(IReadOnlyList<string> Changed, IReadOnlyList<string> New, IReadOnlyList<string> Deleted)
    {
        public bool IsClean => Changed.Count == 0 && New.Count == 0 && Deleted.Count == 0;
    }

    /// <summary>
    /// Compares the on-disk working tree under <paramref name="directoryPath"/> against the graph and reports
    /// drift, WITHOUT modifying the graph. Uses the same SHA256 content hashes as the incremental scan.
    /// </summary>
    public async Task<DriftReport> DetectDriftAsync(
        string directoryPath,
        IReadOnlyList<string> excludePatterns,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(excludePatterns);

        var parserMap = BuildParserMap();
        var candidateFiles = EnumerateCandidateFiles(directoryPath, excludePatterns, parserMap);
        var candidateSet = new HashSet<string>(candidateFiles, StringComparer.OrdinalIgnoreCase);

        var storedHashes = await _storage.GetContentHashesAsync(candidateFiles, cancellationToken).ConfigureAwait(false);

        var changed = new List<string>();
        var added = new List<string>();
        foreach (var filePath in candidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!storedHashes.TryGetValue(filePath, out var storedHash))
            {
                added.Add(filePath);
                continue;
            }

            // Don't read very large or binary files for hashing — they're skipped by the scanner too.
            try
            {
                var info = new FileInfo(filePath);
                if (info.Length > 5 * 1024 * 1024 || await IsLikelyBinaryAsync(filePath, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                var content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
                if (ComputeSha256Hash(content) != storedHash)
                {
                    changed.Add(filePath);
                }
            }
            catch (IOException)
            {
                // Unreadable right now — don't report as drift; a later scan will reconcile.
            }
        }

        // Indexed files under this directory that no longer match a candidate (deleted or now excluded).
        var deleted = new List<string>();
        var indexedFiles = await _storage.GetAllIndexedFilePathsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var indexedFile in indexedFiles)
        {
            if (indexedFile.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase)
                && !candidateSet.Contains(indexedFile))
            {
                deleted.Add(indexedFile);
            }
        }

        return new DriftReport(changed, added, deleted);
    }

    /// <summary>
    /// Reports whether a single file's graph representation is in sync with its on-disk content
    /// (see <see cref="FreshnessState"/>), without modifying the graph.
    /// </summary>
    public async Task<FreshnessState> CheckFreshnessAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);

        var storedHashes = await _storage.GetContentHashesAsync(new[] { fullPath }, cancellationToken).ConfigureAwait(false);
        var inGraph = storedHashes.TryGetValue(fullPath, out var storedHash);
        var onDisk = File.Exists(fullPath);

        if (inGraph && !onDisk) return FreshnessState.Deleted;
        if (!inGraph && onDisk) return FreshnessState.Untracked;
        if (!inGraph) return FreshnessState.Untracked; // neither on disk nor in graph → treat as untracked

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        return ComputeSha256Hash(content) == storedHash ? FreshnessState.Fresh : FreshnessState.Stale;
    }

    /// <summary>Maps each supported file extension to the parsers that handle it.</summary>
    private Dictionary<string, List<IFileParser>> BuildParserMap()
    {
        var parserMap = new Dictionary<string, List<IFileParser>>(StringComparer.OrdinalIgnoreCase);
        foreach (var parser in _parsers)
        {
            foreach (var ext in parser.SupportedExtensions)
            {
                if (!parserMap.TryGetValue(ext, out var list))
                {
                    list = new List<IFileParser>();
                    parserMap[ext] = list;
                }
                list.Add(parser);
            }
        }
        return parserMap;
    }

    /// <summary>
    /// Re-indexes a SINGLE file: clears its existing graph nodes/edges and re-parses it. Intended for
    /// the agentic edit loop (the AI changes a file, then refreshes just that file so the graph matches
    /// the working tree before re-querying). A missing or unparsable file is removed from the graph.
    /// </summary>
    /// <remarks>
    /// Cross-technology links are a whole-graph post-pass and are NOT recomputed here; run a full
    /// <see cref="ScanDirectoryAsync"/> to refresh those.
    /// </remarks>
    public async Task<IndexResult> ScanFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var stopwatch = Stopwatch.StartNew();
        var fullPath = Path.GetFullPath(filePath);
        var extension = Path.GetExtension(fullPath);

        IndexResult Cleared()
        {
            stopwatch.Stop();
            return new IndexResult(0, 0, 0, stopwatch.Elapsed);
        }

        var parserMap = BuildParserMap();

        // Capture the type names this file currently defines, BEFORE we clear it. Comparing against the
        // post-parse set tells us which definitions were renamed/removed/added, so we can relink the files
        // that reference them (drift Layer 2 — incoming-edge maintenance).
        var oldDefNames = DefinitionNames(await _storage.GetNodesByFilePathAsync(fullPath, cancellationToken).ConfigureAwait(false));

        // No parser for this extension, file gone, too large, or binary -> ensure no stale data lingers.
        if (!parserMap.TryGetValue(extension, out var fileParsers) || fileParsers.Count == 0 || !File.Exists(fullPath))
        {
            await _storage.DeleteByFilePathAsync(fullPath, cancellationToken).ConfigureAwait(false);
            await MaintainReferencersAsync(oldDefNames, fullPath, cancellationToken).ConfigureAwait(false);
            return Cleared();
        }

        var info = new FileInfo(fullPath);
        if (info.Length > 5 * 1024 * 1024 || await IsLikelyBinaryAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            await _storage.DeleteByFilePathAsync(fullPath, cancellationToken).ConfigureAwait(false);
            await MaintainReferencersAsync(oldDefNames, fullPath, cancellationToken).ConfigureAwait(false);
            return Cleared();
        }

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var contentHash = ComputeSha256Hash(content);

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        foreach (var parser in fileParsers)
        {
            var (parsedNodes, parsedEdges) = await parser.ParseAsync(fullPath, content).ConfigureAwait(false);
            nodes.AddRange(parsedNodes);
            edges.AddRange(parsedEdges);
        }

        var storedContent = content.Length > MaxFileNodeContentLength ? content[..MaxFileNodeContentLength] : content;
        nodes.Add(new GraphNode
        {
            Id = fullPath,
            Name = Path.GetFileName(fullPath),
            Type = "File",
            Content = storedContent,
            FilePath = fullPath,
            ContentHash = contentHash
        });

        // Replace the file's graph: clear the old nodes + outgoing edges (preserving incoming references
        // from other files, whose targets keep stable ids), then upsert the fresh parse.
        await _storage.ClearFileForReindexAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await _storage.UpsertNodesAsync(nodes, cancellationToken).ConfigureAwait(false);
        if (edges.Count > 0)
        {
            await _storage.UpsertEdgesAsync(edges, cancellationToken).ConfigureAwait(false);
        }

        // Scoped relink: ClearFileForReindexAsync dropped this file's outgoing cross-file edges, and the
        // per-file parse doesn't produce REFERENCES_TYPE (a whole-graph post-pass does). Recompute just this
        // file's outgoing REFERENCES_TYPE edges so impact/dependency analysis stays correct across the edit,
        // without a full rescan. Skipped in semantic mode — there the SemanticCsharpLinker owns exact
        // resolution via a compilation (a whole-graph concern; incremental semantic relink is a later layer).
        if (!_semanticCsharp)
        {
            await CrossTechLinker.RelinkFileReferenceTypesAsync(_storage, fullPath, cancellationToken).ConfigureAwait(false);
        }

        // Drift Layer 2: if this edit renamed/removed/added any type definition, relink the OTHER files that
        // reference those names — removing now-dangling incoming edges and creating newly-resolvable ones —
        // bounded to the referencers via the reverse index (not a whole-graph pass).
        var changedDefNames = new HashSet<string>(oldDefNames, StringComparer.Ordinal);
        changedDefNames.SymmetricExceptWith(DefinitionNames(nodes));
        await MaintainReferencersAsync(changedDefNames, fullPath, cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();
        return new IndexResult(1, nodes.Count, edges.Count, stopwatch.Elapsed);
    }

    /// <summary>The node types that represent a C# type definition (a rename/remove of which can dangle references).</summary>
    private static readonly HashSet<string> DefinitionTypes = new(StringComparer.Ordinal) { "Class", "Interface", "Record", "Struct", "Enum" };

    /// <summary>The distinct names of the type-definition nodes in <paramref name="nodes"/>.</summary>
    private static HashSet<string> DefinitionNames(IEnumerable<GraphNode> nodes) =>
        nodes.Where(n => DefinitionTypes.Contains(n.Type) && !string.IsNullOrEmpty(n.Name))
             .Select(n => n.Name)
             .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Relinks the outgoing <c>REFERENCES_TYPE</c> edges of every file that references any of
    /// <paramref name="changedDefNames"/> (found via the reverse index), excluding <paramref name="excludeFile"/>.
    /// This removes edges that now dangle (the referenced definition was renamed/removed) and creates edges
    /// that became resolvable (a referenced name is now defined). Skipped in semantic mode — exact resolution
    /// there is a whole-graph/compilation concern (a later drift layer).
    /// </summary>
    private async Task MaintainReferencersAsync(IEnumerable<string> changedDefNames, string excludeFile, CancellationToken cancellationToken)
    {
        if (_semanticCsharp) return;

        var names = changedDefNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0) return;

        var referencers = await _storage.GetReferencingFilePathsAsync(names, cancellationToken).ConfigureAwait(false);
        foreach (var referencer in referencers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(referencer, excludeFile, StringComparison.OrdinalIgnoreCase)) continue;
            await CrossTechLinker.RelinkFileReferenceTypesAsync(_storage, referencer, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ComputeSha256Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Heuristically determines whether a file is binary by scanning the first few KB
    /// for a NUL byte. Text source files virtually never contain NUL; most binary
    /// formats do within their header.
    /// </summary>
    private static async Task<bool> IsLikelyBinaryAsync(string filePath, CancellationToken cancellationToken)
    {
        const int sampleSize = 8000;
        var buffer = new byte[sampleSize];

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var read = await stream.ReadAsync(buffer.AsMemory(0, sampleSize), cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == 0)
            {
                return true;
            }
        }

        return false;
    }
}
