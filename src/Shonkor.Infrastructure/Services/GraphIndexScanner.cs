using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Scans a directory, parses files using registered <see cref="IFileParser"/> implementations,
/// and stores the resulting graph using an <see cref="IGraphStorageProvider"/>.
/// </summary>
public sealed class GraphIndexScanner
{
    private readonly IGraphStorageProvider _storage;
    private readonly IReadOnlyList<IFileParser> _parsers;

    // Upper bound on the content stored on a File node (full content is still hashed).
    private const int MaxFileNodeContentLength = 100_000;

    /// <summary>
    /// Initializes a new instance of <see cref="GraphIndexScanner"/>.
    /// </summary>
    /// <param name="storage">The storage provider for persisting the graph.</param>
    /// <param name="parsers">The parsers to use for extracting nodes and edges from files.</param>
    public GraphIndexScanner(IGraphStorageProvider storage, IEnumerable<IFileParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(parsers);

        _storage = storage;
        _parsers = parsers.ToList();
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

        var matcher = new Matcher();
        matcher.AddInclude("**/*"); // Include everything by default
        foreach (var excludePattern in excludePatterns)
        {
            matcher.AddExclude(excludePattern);
        }

        var dirInfo = new DirectoryInfoWrapper(new DirectoryInfo(directoryPath));
        var matchingResult = matcher.Execute(dirInfo);

        // Pre-compute parser mappings for fast lookup
        var parserMap = BuildParserMap();

        // 1. Gather all files supported by our parsers
        var candidateFiles = new List<string>();
        foreach (var fileMatch in matchingResult.Files)
        {
            var filePath = Path.GetFullPath(Path.Combine(directoryPath, fileMatch.Path));
            var extension = Path.GetExtension(filePath);
            if (parserMap.ContainsKey(extension))
            {
                candidateFiles.Add(filePath);
            }
        }

        // 2. Pre-fetch existing file content hashes directly (no full-content / subgraph load).
        var existingHashes = await _storage.GetContentHashesAsync(candidateFiles, cancellationToken).ConfigureAwait(false);

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
                    Console.WriteLine($"Skipping large file {filePath} ({fileInfo.Length} bytes)");
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

                // Incremental Hash Check: skip if hash matches DB
                if (existingHashes.TryGetValue(filePath, out var existingHash) && existingHash == contentHash)
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
                Console.WriteLine($"Error parsing file {filePath}: {ex.Message}");
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

        // 5. Establish Cross-Technology and Helix Architecture mappings (Post-Scan)
        cancellationToken.ThrowIfCancellationRequested();
        await CrossTechLinker.EstablishCrossTechnologyConnectionsAsync(_storage, directoryPath, cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();
        return new IndexResult(filesScanned, nodesCreated, edgesCreated, stopwatch.Elapsed);
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

        // No parser for this extension, file gone, too large, or binary -> ensure no stale data lingers.
        if (!parserMap.TryGetValue(extension, out var fileParsers) || fileParsers.Count == 0 || !File.Exists(fullPath))
        {
            await _storage.DeleteByFilePathAsync(fullPath, cancellationToken).ConfigureAwait(false);
            return Cleared();
        }

        var info = new FileInfo(fullPath);
        if (info.Length > 5 * 1024 * 1024 || await IsLikelyBinaryAsync(fullPath, cancellationToken).ConfigureAwait(false))
        {
            await _storage.DeleteByFilePathAsync(fullPath, cancellationToken).ConfigureAwait(false);
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

        // Replace the file's graph: clear the old nodes/edges, then upsert the fresh parse.
        await _storage.DeleteByFilePathAsync(fullPath, cancellationToken).ConfigureAwait(false);
        await _storage.UpsertNodesAsync(nodes, cancellationToken).ConfigureAwait(false);
        if (edges.Count > 0)
        {
            await _storage.UpsertEdgesAsync(edges, cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        return new IndexResult(1, nodes.Count, edges.Count, stopwatch.Elapsed);
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
