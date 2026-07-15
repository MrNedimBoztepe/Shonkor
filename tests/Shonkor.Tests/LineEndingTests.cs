// Licensed to Shonkor under the MIT License.

using System.Security.Cryptography;
using System.Text;
using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>
/// Line endings are settled at the read, so they stop being a property of the machine that did the
/// indexing (#239).
///
/// <para>
/// The repo has no <c>.gitattributes</c>, <c>.editorconfig</c> says <c>end_of_line = crlf</c>, and
/// <c>core.autocrlf</c> is on — so the same file is <b>CRLF on Windows and LF on Linux</b>, on disk. Two real
/// bugs followed, and both are pinned below.
/// </para>
/// <para>
/// <b>Every test here writes a REAL file to disk and reads it back.</b> That is not incidental. The existing
/// test for the citation invariant could never catch the bug because its fixture was an <b>LF string
/// literal</b> — it encoded the very assumption it existed to test. Same shape as #215 and #237: a fixture
/// easier than reality is not a fixture.
/// </para>
/// </summary>
public class LineEndingTests
{
    /// <summary>Writes a real file with the given newline, returns its path.</summary>
    private static async Task<string> WriteFileAsync(string newline, params string[] lines)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_crlf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "doc.md");
        await File.WriteAllTextAsync(path, string.Join(newline, lines));
        return path;
    }

    private static readonly string[] Doc =
        ["# Doc", "", "Intro.", "", "## Chapter", "", "Framing.", ""];

    private static string Sha256(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    // ---- bug 1: the exact-citation invariant --------------------------------------------------------

    /// <summary>
    /// The invariant the product's citation claim rests on: a node's <c>StartLine..EndLine</c> range slices
    /// back to <b>exactly</b> its <c>Content</c>. If it does not, a cited line range points at text that is not
    /// the text we stored, and the citation is not verifiable.
    ///
    /// <para>
    /// It was <b>false on Windows</b>. The parser splits on <c>'\n'</c>, so under CRLF every line kept a
    /// trailing <c>'\r'</c>, and a <c>TrimEnd()</c> stripped it from the <b>last line only</b>. Measured, on a
    /// real file: slice <c>"# Doc\r\n\r\nIntro.\r"</c> vs content <c>"# Doc\r\n\r\nIntro."</c> — off by one CR,
    /// on every section.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task TheCitationInvariant_HoldsForAFileReadFromDisk_WhateverItsLineEndings(string newline)
    {
        var path = await WriteFileAsync(newline, Doc);
        var content = await SourceText.ReadAsync(path);

        var (nodes, _) = await new MarkdownHierarchyParser().ParseAsync(path, content);
        var lines = content.Split('\n');

        Assert.NotEmpty(nodes);
        foreach (var n in nodes.Where(n => n.StartLine.HasValue && n.EndLine.HasValue))
        {
            var slice = string.Join("\n", lines[(n.StartLine!.Value - 1)..n.EndLine!.Value]);
            Assert.Equal(n.Content, slice);
        }
    }

    // ---- bug 2: the content hash -------------------------------------------------------------------

    /// <summary>
    /// <c>ContentHash</c> is SHA-256 over the file text. Over the RAW text, the same logical file hashed
    /// differently on Windows and Linux — so an index built on one and read on the other reported <b>every
    /// file as stale</b>: <c>freshness</c> claimed the whole project had drifted, the incremental hash-skip
    /// never fired, and every analysis result carried an "edited since indexing" warning.
    ///
    /// <para>
    /// CI could never catch this, because each leg indexes its own checkout. It bites when an index crosses an
    /// OS boundary — a Windows dev host into a Linux container, or an index baked into the Docker image.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheSameLogicalFile_HashesIdentically_WhicheverPlatformWroteIt()
    {
        var lf = await SourceText.ReadAsync(await WriteFileAsync("\n", Doc));
        var crlf = await SourceText.ReadAsync(await WriteFileAsync("\r\n", Doc));

        Assert.Equal(Sha256(lf), Sha256(crlf));
    }

    [Fact]
    public async Task TheSameLogicalFile_AlsoParsesToIdenticalContent_SoFtsAndEmbeddingsAgree()
    {
        // Not just the hash: the stored node Content used to differ byte-for-byte across platforms, so the FTS
        // text and the embedding vectors differed for the identical logical document.
        var lfPath = await WriteFileAsync("\n", Doc);
        var crlfPath = await WriteFileAsync("\r\n", Doc);

        var (lfNodes, _) = await new MarkdownHierarchyParser()
            .ParseAsync(lfPath, await SourceText.ReadAsync(lfPath));
        var (crlfNodes, _) = await new MarkdownHierarchyParser()
            .ParseAsync(crlfPath, await SourceText.ReadAsync(crlfPath));

        Assert.Equal(
            lfNodes.Select(n => n.Content).ToList(),
            crlfNodes.Select(n => n.Content).ToList());
    }

    // ---- the normaliser itself ----------------------------------------------------------------------

    [Fact]
    public void Normalising_IsIdempotent_AndANoOpForTextThatIsAlreadyLf()
    {
        const string lf = "a\nb\nc";

        Assert.Equal(lf, SourceText.NormalizeLineEndings(lf));                       // Linux: unchanged
        Assert.Equal(lf, SourceText.NormalizeLineEndings("a\r\nb\r\nc"));            // Windows: agrees with it
        Assert.Equal(lf, SourceText.NormalizeLineEndings("a\rb\rc"));                // lone CR too
        Assert.Equal(lf, SourceText.NormalizeLineEndings(SourceText.NormalizeLineEndings("a\r\nb\r\nc")));
    }
}
