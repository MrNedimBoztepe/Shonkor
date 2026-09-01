using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;

using Shonkor.Core.Services;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Scans a directory, parses files using registered <see cref="IFileParser"/> implementations,
/// and stores the resulting graph using an <see cref="IGraphStorageProvider"/>.
/// </summary>
public sealed class GraphIndexScanner
{
    private readonly IGraphStorageProvider _storage;
    private readonly IReadOnlyList<IFileParser> _parsers;
    private readonly IReadOnlyList<IGraphPostProcessor> _postProcessors;
    private readonly GraphPostProcessorContext _postProcessorContext;
    private readonly ILogger? _logger;

    // Upper bound on the content stored on a File node (full content is still hashed).
    private const int MaxFileNodeContentLength = 100_000;

    /// <summary>
    /// Appended when a File node's content is cut at <see cref="MaxFileNodeContentLength"/>. Without it a
    /// consumer cannot tell a truncated file from a complete one, and silently reasons over a partial body.
    /// </summary>
    private const string FileContentTruncationMarker =
        "\n\n… [truncated: the file exceeds 100,000 characters; only the first 100,000 are stored on the File node. Query its sections/symbols for the rest.]";

    /// <summary>Stores at most <see cref="MaxFileNodeContentLength"/> characters, marking the cut explicitly.</summary>
    private static string TruncateFileContent(string content) =>
        content.Length > MaxFileNodeContentLength
            ? content[..MaxFileNodeContentLength] + FileContentTruncationMarker
            : content;

    // Files above this size are never parsed/indexed; the drift detector applies the same bound so it
    // never reports a file the scanner would refuse (which would loop forever as "New"/"Changed").
    private const long MaxParseableFileBytes = 5 * 1024 * 1024;

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
    private readonly SemanticCompilationCache? _compilationCache;

    public GraphIndexScanner(IGraphStorageProvider storage, IEnumerable<IFileParser> parsers, ILogger? logger = null, bool semanticCsharp = false, SemanticCompilationCache? compilationCache = null, IEnumerable<IGraphPostProcessor>? postProcessors = null, GraphPostProcessorContext? postProcessorContext = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(parsers);

        _storage = storage;
        _parsers = parsers.ToList();
        _postProcessors = ComposePostProcessors(postProcessors);
        _postProcessorContext = postProcessorContext ?? GraphPostProcessorContext.Empty;
        _logger = logger;
        _semanticCsharp = semanticCsharp;
        _compilationCache = compilationCache;
    }

    /// <summary>
    /// Appends the always-on first-party post-processors (<see cref="FirstPartyPostProcessors"/>) to whatever the
    /// caller supplied, so the security phase runs on EVERY full scan by construction (#332).
    ///
    /// <para>
    /// It lives here rather than at the call sites because the call sites are exactly what failed: the web index
    /// endpoint and the CLI never appended them, so a full scan triggered from either produced no
    /// <c>security.suspicious-instruction-in-content</c> diagnostics — and the RAG prompt's injection flagging
    /// (which reads that code) was silently inert on those graphs. The constructor is the one point every ingest
    /// path must pass through, and there is deliberately no opt-out flag: a flag would only be the same gap in a
    /// new shape.
    /// </para>
    ///
    /// <para>
    /// Caller-supplied entries whose <see cref="IGraphPostProcessor.Name"/> collides with a first-party one are
    /// dropped. Diagnostics are stored keyed by that name and a re-scan REPLACES the set for a name, so a plugin
    /// claiming <c>security.suspicious-content</c> and running after the first-party processor would wipe its
    /// findings. Filtering by name makes the guarantee independent of ordering.
    /// </para>
    /// </summary>
    private static List<IGraphPostProcessor> ComposePostProcessors(IEnumerable<IGraphPostProcessor>? callerSupplied)
    {
        var firstParty = FirstPartyPostProcessors.Create().ToList();
        var reservedNames = firstParty.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var composed = callerSupplied?
            .Where(p => p is not null && !reservedNames.Contains(p.Name))
            .ToList() ?? new List<IGraphPostProcessor>();
        composed.AddRange(firstParty);
        return composed;
    }

    /// <summary>
    /// Routes a scan diagnostic to the logger, or to stderr — never stdout (see ctor remarks).
    ///
    /// <para>
    /// Line endings are flattened first (#276, <c>cs/log-forging</c>). Callers interpolate <b>file paths</b>,
    /// and a path is not ours: POSIX filenames may contain newlines, so indexing an untrusted repository would
    /// otherwise let a checked-in filename write its own log lines. CodeQL only flagged the ProjectManager
    /// site, but this one takes the more obviously attacker-supplied value of the two.
    /// </para>
    /// </summary>
    private void Warn(string message)
    {
        var line = message.ReplaceLineEndings(" ");
        if (_logger != null) _logger.LogWarning("{ScanMessage}", line);
        else Console.Error.WriteLine(line);
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
    /// <param name="forceReparse">
    /// Ignore every staleness signal and reparse every candidate file (#430). The content hash answers "did
    /// this file change", never "did the code that interprets it change", so a parser fix, a rebuilt plugin
    /// or a corrected post-processor leaves an existing graph untouched — measured: a full rescan of a real
    /// solution with a corrected parser moved 0 of 1 679 wrongly-tiered edges. This is the manual escape
    /// hatch for that class, and the oracle for the automatic detection in #408: a normal scan and a forced
    /// one must agree, and any difference names a change dimension the key does not cover.
    /// </param>
    public async Task<IndexResult> ScanDirectoryAsync(
        string directoryPath,
        IReadOnlyList<string> excludePatterns,
        CancellationToken cancellationToken = default,
        bool forceReparse = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(excludePatterns);

        var stopwatch = Stopwatch.StartNew();
        // Read BEFORE any work (#449): a commit landing mid-scan would otherwise be stamped as indexed
        // while half the graph predates it. An index that overstates its currency is worse than one that
        // understates it — the whole point of the marker is that it can be trusted when it says "matches".
        var revisionAtStart = WorkingTreeRevision.TryRead(directoryPath);
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

        // 2b. The content hash answers "did this FILE change" and nothing else, so a corrected parser or a
        //     rebuilt plugin leaves an existing graph untouched — measured: a full rescan of a real solution
        //     with the #402-corrected parser moved 0 of 1 679 wrongly-tiered edges, because no source file
        //     had changed. The toolchain fingerprint closes that: when the set of assemblies that interpret
        //     the files differs from the one that built the graph, every file is stale by definition (#408).
        //
        //     A null stored value means "built before this existed", i.e. by an unknown toolchain — treated
        //     as changed, which costs one forced scan per legacy graph and is a no-op on an empty one.
        var toolchainFingerprint = ComputeToolchainFingerprint();
        var storedFingerprint = await _storage.GetToolchainFingerprintAsync(cancellationToken).ConfigureAwait(false);
        var toolchainChanged = !string.Equals(storedFingerprint, toolchainFingerprint, StringComparison.Ordinal);
        if (toolchainChanged && storedFingerprint is not null)
        {
            Warn("The parser/plugin set differs from the one this graph was built with; reparsing every file.");
        }

        // 2c. All three reasons meet in ONE condition rather than three skip paths — two ways of expressing
        //     "reparse anyway" is how they drift apart later. (#430 is the manual one.)
        var reparseEverything = schemeStale || forceReparse || toolchainChanged;
        if (forceReparse)
        {
            Warn("Forced reparse requested; the content-hash check is bypassed for every candidate file.");
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
                if (fileInfo.Length > MaxParseableFileBytes)
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

                var content = await SourceText.ReadAsync(filePath, ct).ConfigureAwait(false);
                var contentHash = ComputeSha256Hash(content);

                // Incremental Hash Check: skip if the hash matches the DB — unless something has declared
                // that the file must be reparsed regardless of its content (a stale node-id scheme, or an
                // explicit --force). The hash answers "did this file change" and nothing else; when the code
                // that INTERPRETS the file changed, only reparseEverything gets the correction into the data.
                if (!reparseEverything && existingHashes.TryGetValue(filePath, out var existingHash) && existingHash == contentHash)
                {
                    return; // Unchanged!
                }

                // File has changed. We need to clear its old graph structure and re-parse.
                filesToClear.Add(filePath);

                foreach (var parser in fileParsers)
                {
                    var (nodes, edges) = await parser.ParseAsync(filePath, content).ConfigureAwait(false);
                    foreach (var node in nodes) allNodesToUpsert.Add(node);
                    foreach (var edge in edges) allEdgesToUpsert.Add(StampReason(StampProvenance(edge, parser.DefaultProvenance), parser.DefaultReason));
                }

                // Create a File node to represent the scanned file itself.
                // Cap stored content to keep the DB / FTS index from bloating on very large files;
                // the full hash is still computed over the complete content above.
                var storedContent = TruncateFileContent(content);

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
        // Path-keyed set: case-insensitive here collapsed Handler.cs and handler.cs into one entry on Linux,
        // so staleness was computed against a set missing real files and a deleted file was never cleared
        // (a ghost node surviving every rescan). This set drives DeleteByFilePathsAsync (#235).
        var candidateFilesSet = new HashSet<string>(candidateFiles, FilePaths.Comparer);
        var dirPrefix = NormalizedDirPrefix(directoryPath);
        foreach (var indexedFile in indexedFiles)
        {
            if (indexedFile.StartsWith(dirPrefix, FilePaths.Comparison) && !candidateFilesSet.Contains(indexedFile))
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
            // A full scan re-read the whole tree; drop any cached compilation so the next incremental
            // reconcile rebuilds from the current sources rather than swapping onto a stale base.
            _compilationCache?.Invalidate(directoryPath);
        }

        // 5.5 Phase 2: graph-aware post-processors observe the assembled graph and add enrichment
        //     (nodes/edges) + diagnostics. Additive and isolated — a failing post-processor is logged, skipped
        //     and recorded as a `postprocessor.incomplete` diagnostic so the gap is visible (#353). Whole-graph
        //     concern, so it runs on full scans only (not single-file reindex).
        if (_postProcessors.Count > 0)
        {
            var view = new StorageBackedGraphView(_storage);
            foreach (var postProcessor in _postProcessors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var enrichment = await postProcessor.ProcessAsync(view, _postProcessorContext).ConfigureAwait(false);
                    if (enrichment.Nodes.Count > 0)
                        await _storage.UpsertNodesAsync(enrichment.Nodes, cancellationToken).ConfigureAwait(false);
                    // Stamped exactly like parser output (#400): post-processor edges used to be upserted
                    // raw, so an edge whose producer forgot to tag it defaulted to Extracted and a heuristic
                    // link claimed compiler-grade trust. StampProvenance only ever RAISES uncertainty, so a
                    // post-processor that already sets Inferred/Ambiguous per edge is unaffected.
                    if (enrichment.Edges.Count > 0)
                        await _storage.UpsertEdgesAsync(
                            enrichment.Edges.Select(e => StampReason(StampProvenance(e, postProcessor.DefaultProvenance), postProcessor.DefaultReason)),
                            cancellationToken).ConfigureAwait(false);
                    // Replace exactly this post-processor's diagnostics (tagged by its Name) so a re-scan
                    // refreshes them without touching others.
                    await _storage.ReplaceDiagnosticsAsync(postProcessor.Name, enrichment.Diagnostics, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // A cancelled scan is not a failed check: the storage calls above all take the token, so
                    // the generic catch below would otherwise swallow the cancellation and let the scan carry
                    // on to phase 6 as if nothing happened. Cancellation propagates immediately instead.
                    // (It does not, on today's store, decide whether a marker is written: the marker write
                    // uses the same cancelled token and fails inside its own best-effort catch. That is an
                    // implementation detail of the storage provider, not something this catch guarantees.)
                    throw;
                }
                catch (Exception ex)
                {
                    Warn($"Post-processor '{postProcessor.Name}' failed: {ex.Message}");

                    // Leave the failure behind as data, not just as a log line (#353) — a stderr warning is
                    // invisible to get_diagnostics and to the dashboard. The marker is written under the
                    // SAME source as the processor's findings because ReplaceDiagnosticsAsync is keyed by
                    // source: that both clears this run's now-stale findings and lets the next successful
                    // scan clear the marker again. Best-effort in its own try/catch — a store that cannot
                    // take the marker must not fail the scan at a new place.
                    try
                    {
                        await _storage.ReplaceDiagnosticsAsync(
                            postProcessor.Name,
                            new[] { PostProcessorDiagnostics.Incomplete(postProcessor.Name, $"{ex.GetType().Name}: {ex.Message}") },
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception markerEx)
                    {
                        Warn($"Could not record the incompleteness marker for '{postProcessor.Name}': {markerEx.Message}");
                    }
                }
            }
        }

        // 6. Stamp the graph with the current node-id scheme — the whole tree was just (re)built under it,
        //    so any prior staleness is now resolved and get_stats stops recommending a re-index.
        await _storage.SetNodeIdSchemeVersionAsync(Shonkor.Core.Services.CsharpNodeId.SchemeVersion, cancellationToken).ConfigureAwait(false);
        await _storage.SetToolchainFingerprintAsync(toolchainFingerprint, cancellationToken).ConfigureAwait(false);
        // Only when there is one to record (#449). A tree that is not a repository leaves the marker
        // ABSENT, and absent is reported as "unknown" — never as "matches", which is the one reading that
        // would make the disclosure worse than none.
        if (revisionAtStart is not null)
        {
            await _storage.SetIndexedRevisionAsync(revisionAtStart, cancellationToken).ConfigureAwait(false);
        }

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
        var candidateSet = new HashSet<string>(candidateFiles, FilePaths.Comparer);

        var storedHashes = await _storage.GetContentHashesAsync(candidateFiles, cancellationToken).ConfigureAwait(false);

        var changed = new List<string>();
        var added = new List<string>();
        foreach (var filePath in candidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Don't read very large or binary files for hashing — they're skipped by the scanner too.
            // This guard must cover NEW files as well: a never-indexable file reported as New would be
            // fed to reconcile, rejected there, and reported New again every cycle — drift never clean.
            try
            {
                var info = new FileInfo(filePath);
                if (info.Length > MaxParseableFileBytes || await IsLikelyBinaryAsync(filePath, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (!storedHashes.TryGetValue(filePath, out var storedHash))
                {
                    added.Add(filePath);
                    continue;
                }

                var content = await SourceText.ReadAsync(filePath, cancellationToken).ConfigureAwait(false);
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
        var dirPrefix = NormalizedDirPrefix(directoryPath);
        foreach (var indexedFile in indexedFiles)
        {
            if (indexedFile.StartsWith(dirPrefix, FilePaths.Comparison)
                && !candidateSet.Contains(indexedFile))
            {
                deleted.Add(indexedFile);
            }
        }

        return new DriftReport(changed, added, deleted);
    }

    /// <summary>
    /// Drift Layer 3 (reconcile-from-drift): detects drift against the working tree and re-indexes ONLY the
    /// changed/new/deleted files surgically (each via <see cref="ScanFileAsync"/>, which does the Layer 1+2
    /// scoped relink), instead of a whole-tree rescan. Catches out-of-band edits (git pull, branch switch,
    /// external editor) at bounded cost; intended to be driven periodically by a background reconciler.
    /// </summary>
    public async Task<IndexResult> ReconcileDriftAsync(
        string directoryPath,
        IReadOnlyList<string> excludePatterns,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(excludePatterns);

        var drift = await DetectDriftAsync(directoryPath, excludePatterns, cancellationToken).ConfigureAwait(false);
        if (drift.IsClean) return new IndexResult(0, 0, 0, TimeSpan.Zero);

        // Deleted = indexed but no longer a candidate: gone from disk OR now matched by an exclude
        // pattern. ScanFileAsync doesn't know the exclude patterns, so an excluded-but-present file
        // would be RE-indexed (resurrected) instead of removed — and reported Deleted again next cycle,
        // forever. Force-remove the Deleted set so reconcile converges.
        var paths = drift.Changed.Concat(drift.New).Concat(drift.Deleted);
        return await ReconcilePathsAsync(directoryPath, paths, cancellationToken, forceRemovePaths: drift.Deleted).ConfigureAwait(false);
    }

    /// <summary>
    /// Drift Layer 4 (git-aware / explicit-set reconcile): re-indexes a KNOWN set of changed paths (e.g. from
    /// <c>git diff --name-only</c> or a webhook push payload) surgically via <see cref="ScanFileAsync"/> —
    /// without hashing the whole tree. Relative paths are resolved against <paramref name="rootDirectory"/>.
    /// </summary>
    public async Task<IndexResult> ReconcilePathsAsync(
        string rootDirectory,
        IEnumerable<string> paths,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? forceRemovePaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(paths);

        var stopwatch = Stopwatch.StartNew();
        string Resolve(string p) => Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(rootDirectory, p));
        var fullPaths = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Resolve)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Paths that must be REMOVED from the graph even if they still exist on disk (e.g. drift-Deleted
        // files that are now exclude-matched — re-scanning them would resurrect excluded content).
        var removeSet = forceRemovePaths is null
            ? null
            : forceRemovePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Resolve).ToHashSet(FilePaths.Comparer);

        // Capture the type names the changed files define BEFORE re-indexing, so a rename/delete in semantic
        // mode can still relink the referencers of the old name (their incoming edges would otherwise dangle).
        var defNames = new HashSet<string>(StringComparer.Ordinal);
        if (_semanticCsharp)
        {
            foreach (var full in fullPaths)
            {
                foreach (var name in DefinitionNames(await _storage.GetNodesByFilePathAsync(full, cancellationToken).ConfigureAwait(false)))
                    defNames.Add(name);
            }
        }

        var filesScanned = 0;
        var nodesCreated = 0;
        var edgesCreated = 0;
        foreach (var full in fullPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = removeSet?.Contains(full) == true
                ? await RemoveFileAsync(full, cancellationToken).ConfigureAwait(false)
                : await ScanFileAsync(full, cancellationToken).ConfigureAwait(false);
            filesScanned += r.FilesScanned;
            nodesCreated += r.NodesCreated;
            edgesCreated += r.EdgesCreated;
        }

        // Incremental SEMANTIC relink (drift): refresh CALLS / exact REFERENCES_TYPE / IMPLEMENTS / EXTENDS
        // for the changed files and their referencers, using ONE compilation per batch (ScanFileAsync skips
        // semantic resolution per file). In non-semantic mode this is a no-op (ScanFileAsync did Layer 1+2).
        if (_semanticCsharp && fullPaths.Count > 0)
        {
            await SemanticReconcileAsync(rootDirectory, fullPaths, defNames, cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return new IndexResult(filesScanned, nodesCreated, edgesCreated, stopwatch.Elapsed);
    }

    /// <summary>
    /// Builds one project compilation and re-emits the semantic edges for the changed files plus the files
    /// that reference their type names (old and new), so incoming CALLS/REFERENCES_TYPE are refreshed and
    /// rename/remove danglers are cleared — bounded to the referencers via the reverse index.
    /// </summary>
    private async Task SemanticReconcileAsync(
        string rootDirectory,
        IReadOnlyCollection<string> changedFullPaths,
        HashSet<string> oldDefNames,
        CancellationToken cancellationToken)
    {
        var relinkNames = new HashSet<string>(oldDefNames, StringComparer.Ordinal);
        foreach (var full in changedFullPaths)
        {
            foreach (var name in DefinitionNames(await _storage.GetNodesByFilePathAsync(full, cancellationToken).ConfigureAwait(false)))
                relinkNames.Add(name);
        }

        var relinkSet = new HashSet<string>(changedFullPaths, FilePaths.Comparer);
        if (relinkNames.Count > 0)
        {
            foreach (var referencer in await _storage.GetReferencingFilePathsAsync(relinkNames, cancellationToken).ConfigureAwait(false))
                relinkSet.Add(referencer);
        }

        // Reuse the cached compilation when available (swap only the changed trees) instead of rebuilding
        // it (an O(repo) parse) every reconcile; falls back to a fresh build when there's no cache.
        var compilation = _compilationCache is not null
            ? await _compilationCache.ApplyEditsAsync(rootDirectory, changedFullPaths, cancellationToken).ConfigureAwait(false)
            : await SemanticCsharpLinker.BuildCompilationForDirectoryAsync(rootDirectory, cancellationToken).ConfigureAwait(false);
        if (compilation is null) return;

        await SemanticCsharpLinker.RelinkFilesAsync(_storage, compilation, relinkSet, cancellationToken).ConfigureAwait(false);
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

        var content = await SourceText.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
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
        if (info.Length > MaxParseableFileBytes || await IsLikelyBinaryAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            await _storage.DeleteByFilePathAsync(fullPath, cancellationToken).ConfigureAwait(false);
            await MaintainReferencersAsync(oldDefNames, fullPath, cancellationToken).ConfigureAwait(false);
            return Cleared();
        }

        var content = await SourceText.ReadAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var contentHash = ComputeSha256Hash(content);

        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        foreach (var parser in fileParsers)
        {
            var (parsedNodes, parsedEdges) = await parser.ParseAsync(fullPath, content).ConfigureAwait(false);
            nodes.AddRange(parsedNodes);
            foreach (var edge in parsedEdges) edges.Add(StampReason(StampProvenance(edge, parser.DefaultProvenance), parser.DefaultReason));
        }

        var storedContent = TruncateFileContent(content);
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

    /// <summary>
    /// Removes a file's graph representation (nodes, edges, referencer maintenance) regardless of whether
    /// the file still exists on disk — the counterpart of <see cref="ScanFileAsync"/>'s clear branch, used
    /// when a still-present file must leave the graph (e.g. it is now matched by an exclude pattern).
    /// </summary>
    private async Task<IndexResult> RemoveFileAsync(string fullPath, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var oldDefNames = DefinitionNames(await _storage.GetNodesByFilePathAsync(fullPath, cancellationToken).ConfigureAwait(false));
        await _storage.DeleteByFilePathAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await MaintainReferencersAsync(oldDefNames, fullPath, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return new IndexResult(1, 0, 0, stopwatch.Elapsed);
    }

    /// <summary>
    /// A directory's full path with a guaranteed trailing separator, for indexed-file prefix checks.
    /// Without the separator, scanning <c>C:\Repo</c> would classify files under a SIBLING directory with
    /// a shared name prefix (<c>C:\Repo2\…</c>) as "under this directory" and delete their graph data; and
    /// a non-normalized (relative / trailing-slash) input would silently never match, disabling cleanup.
    /// </summary>
    private static string NormalizedDirPrefix(string directoryPath)
    {
        var full = Path.GetFullPath(directoryPath);
        return full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar)
            ? full
            : full + Path.DirectorySeparatorChar;
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
            if (string.Equals(referencer, excludeFile, FilePaths.Comparison)) continue;
            await CrossTechLinker.RelinkFileReferenceTypesAsync(_storage, referencer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Elevates an edge to the more-uncertain of its parser's baseline tier and the edge's own tier
    /// (Extracted &lt; Inferred &lt; Ambiguous). A deterministic parser (default Extracted) leaves edges
    /// untouched; a heuristic parser (default Inferred) raises every untagged edge to Inferred while a
    /// per-edge Ambiguous escalation is preserved. This is the enforcement point that keeps the
    /// provenance signal honest even if a parser forgets to tag an individual edge.
    /// </summary>
    /// <summary>Structural membership edges are deterministic facts (the node IS in this file), so a
    /// heuristic parser default must not downgrade them from Extracted (TICKET-207).</summary>
    private static readonly HashSet<string> StructuralEdges = new(StringComparer.Ordinal) { "CONTAINS", "DEFINED_IN" };

    private static GraphEdge StampProvenance(GraphEdge edge, Provenance parserDefault) =>
        !StructuralEdges.Contains(edge.Relationship) && (int)parserDefault > (int)edge.Provenance
            ? edge with { Provenance = parserDefault }
            : edge;

    /// <summary>
    /// Gives an edge the producer's default reason when it set none (AP1, #428), and gives structural
    /// edges <see cref="ProvenanceReason.Structural"/> regardless of who emitted them.
    ///
    /// <para>
    /// Deliberately NOT a second capping rule beside <see cref="StampProvenance"/>. The tier is derived
    /// from the reason, so capping both independently is how two fields that must agree stop agreeing —
    /// the failure that left 1 354 edges stranded between the scanner's <c>max()</c> and the store's
    /// <c>MIN()</c> until #399. An edge that states its own reason keeps it; one that states none inherits.
    /// </para>
    ///
    /// <para>
    /// A producer that has not been taught to declare a reason yields <see cref="ProvenanceReason.Unspecified"/>,
    /// which claims nothing. That is the correct outcome for a third-party plugin built against an older
    /// contract: silence, not a fabricated attribution.
    /// </para>
    /// </summary>
    private static GraphEdge StampReason(GraphEdge edge, ProvenanceReason producerDefault)
    {
        if (StructuralEdges.Contains(edge.Relationship))
            return edge.Reason == ProvenanceReason.Structural ? edge : edge with { Reason = ProvenanceReason.Structural };

        if (edge.Reason != ProvenanceReason.Unspecified || producerDefault == ProvenanceReason.Unspecified)
            return edge;

        // The default applies only where it AGREES with the tier this edge actually carries. A producer
        // that emits more than one tier — TypeScriptSemanticLinker emits Extracted and Ambiguous — would
        // otherwise stamp its optimistic default onto its own weaker edges, and the reason would imply a
        // tier the edge does not have. That contradiction is worse than no reason: an unattributed edge
        // says "unknown", a wrong one says something false with the full weight of the type system.
        return ProvenanceReasons.TierOf(producerDefault) == edge.Provenance
            ? edge with { Reason = producerDefault }
            : edge;
    }

    /// <summary>
    /// An opaque fingerprint of the toolchain that will interpret this scan's files: every parser and every
    /// post-processor, identified by its declaring assembly's <b>MVID</b> plus its own type name (#408).
    ///
    /// <para>
    /// MVID rather than a version string, and the reason is not that deterministic builds make it stable —
    /// it is that <b>nobody has to remember to bump it</b>. A version is a claim about an artifact; the MVID
    /// is a property of it. The difference is measured: when four stale first-party plugins were rebuilt,
    /// all four binaries changed and only one had moved its manifest version, so a version comparison would
    /// have caught one of four (#414).
    /// </para>
    ///
    /// <para>
    /// The type name is folded in as well, so adding or removing a parser changes the fingerprint even when
    /// no assembly did — that is a change to the toolchain just as much as a rebuild is.
    /// </para>
    ///
    /// <para>
    /// Known limit, stated rather than discovered later: this sees ASSEMBLIES. Behaviour that changes through
    /// configuration rather than code — the <c>SHONKOR_SEMANTIC_CSHARP</c> switch, a plugin's own settings
    /// file — is invisible to it. That dimension can be folded into this computation later without touching
    /// the storage contract, which is why the stored value is an opaque string and not an assembly id. Until
    /// then, <c>--force</c> (#430) is the escape hatch, and comparing a normal scan against a forced one is
    /// how such a gap is detected rather than assumed.
    /// </para>
    /// </summary>
    private string ComputeToolchainFingerprint()
    {
        var parts = _parsers.Select(p => p.GetType())
            .Concat(_postProcessors.Select(p => p.GetType()))
            .Select(t => $"{t.FullName}@{t.Assembly.ManifestModule.ModuleVersionId:N}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal);

        return ComputeSha256Hash(string.Join('\n', parts));
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
