// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-211: a File node's stored content is capped at 100k characters. Without an explicit marker a
/// consumer cannot tell a truncated file from a complete one and silently reasons over a partial body.
/// </summary>
public class FileNodeTruncationTests
{
    private const int Cap = 100_000;

    private static async Task<(string Root, SqliteGraphStorageProvider Storage)> IndexAsync(string fileName, string content)
    {
        var root = Path.Combine(Path.GetTempPath(), $"shonkor_trunc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, fileName), content);

        var storage = new SqliteGraphStorageProvider(Path.Combine(root, "g.db"));
        await storage.InitializeAsync();
        var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser(), new MarkdownHierarchyParser() });
        await scanner.ScanDirectoryAsync(root, Array.Empty<string>());
        return (root, storage);
    }

    [Fact]
    public async Task OversizedFile_StoresTruncationMarker()
    {
        // A markdown file comfortably over the 100k cap but under the "too large to parse" bound.
        var body = string.Join("\n", Enumerable.Repeat("filler line of prose that adds up quickly", 4000));
        var (root, storage) = await IndexAsync("big.md", "# Big\n\n" + body);

        using (storage)
        {
            var fileNode = await storage.GetNodeByIdAsync(Path.Combine(root, "big.md"));
            Assert.NotNull(fileNode);
            Assert.True(fileNode!.Content.Length > Cap, "the marker is appended after the cap");
            Assert.Contains("[truncated:", fileNode.Content);
            Assert.Contains("100,000", fileNode.Content);
            // Everything before the marker is the untouched head of the file.
            Assert.StartsWith("# Big", fileNode.Content);
        }
    }

    [Fact]
    public async Task SmallFile_HasNoTruncationMarker()
    {
        var (root, storage) = await IndexAsync("small.md", "# Small\n\nshort body\n");

        using (storage)
        {
            var fileNode = await storage.GetNodeByIdAsync(Path.Combine(root, "small.md"));
            Assert.NotNull(fileNode);
            Assert.DoesNotContain("[truncated:", fileNode!.Content);
        }
    }
}
