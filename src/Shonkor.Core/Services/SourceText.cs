// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Services;

/// <summary>
/// Reading source text — <b>the one place line endings are settled</b> (#239).
///
/// <para>
/// The repo has no <c>.gitattributes</c>, <c>.editorconfig</c> says <c>end_of_line = crlf</c>, and
/// <c>core.autocrlf</c> is on. So the same source file is <b>CRLF on Windows and LF on Linux</b>, on disk,
/// right now. Everything downstream — hashing, parsing, line ranges, stored content, embeddings — was reading
/// the raw text and quietly inheriting that difference.
/// </para>
///
/// <para><b>Two real bugs came from it, both measured, not theorised:</b></para>
/// <list type="number">
///   <item>
///   <b>The exact-citation invariant was FALSE on Windows.</b> The product's claim is that a node's
///   <c>StartLine..EndLine</c> slices back to exactly its <c>Content</c> — that is what makes a citation
///   verifiable. The markdown parser splits on <c>'\n'</c>, so under CRLF every line keeps a trailing
///   <c>'\r'</c>, and a <c>TrimEnd()</c> then strips it from the <b>last line only</b>. Measured on a real
///   file: the slice came back as <c>"# Doc\r\n\r\nIntro.\r"</c> against a stored <c>Content</c> of
///   <c>"# Doc\r\n\r\nIntro."</c> — off by exactly one CR, on every section.
///   <para>
///   The test that pins this invariant could never catch it: its fixture is an <b>LF string literal</b>, never
///   a file read from disk. The test encoded the very assumption it existed to test — the same "fixture easier
///   than reality" that hid #215 and #237.
///   </para>
///   </item>
///   <item>
///   <b>The same logical file hashed differently per OS.</b> <c>ContentHash</c> is SHA-256 over the raw text,
///   so an index built on Windows and read on Linux (a Docker volume mount, an image-baked index) reports
///   <b>every single file as stale</b>: <c>freshness</c> claims the whole project drifted, the incremental
///   hash-skip never hits, and every analysis result carries an "edited since indexing" warning. CI could not
///   see it, because each leg indexes its own checkout.
///   </item>
/// </list>
///
/// <para>
/// So text is normalised to <c>\n</c> <b>once, at the read</b>, before anything hashes it or parses it. Line
/// endings then stop being a property of the machine that happened to do the indexing.
/// </para>
/// </summary>
public static class SourceText
{
    /// <summary>
    /// Collapses CRLF and lone CR to <c>\n</c>. Idempotent, and a no-op for text that is already LF — which is
    /// every file on Linux, so this changes nothing there and makes Windows agree with it.
    /// </summary>
    public static string NormalizeLineEndings(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.ReplaceLineEndings("\n");

    /// <summary>
    /// Reads a source file and normalises its line endings. Use this <b>instead of</b>
    /// <see cref="File.ReadAllTextAsync(string, CancellationToken)"/> anywhere the text is about to be hashed,
    /// parsed, stored as node content, or sliced by line — i.e. everywhere it matters.
    /// </summary>
    public static async Task<string> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        NormalizeLineEndings(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
}
