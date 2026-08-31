// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// #436: the scanner fed the parser LF-normalized text (<see cref="SourceText.ReadAsync"/>) while the
/// semantic compilation read the same file raw. On a CRLF checkout every Roslyn span was one character
/// per preceding line ahead of the parser's, so the <c>@spanStart</c> half of an overloaded method's id
/// disagreed and the edge landed on a node that does not exist.
///
/// <para>
/// Measured on a Sitecore solution before the fix: 349 of 353 dangling <c>CALLS</c> targets had an id
/// whose prefix before <c>@</c> DID exist, differing only in the offset — and the difference was exactly
/// the number of carriage returns before the declaration. 3 318 edges at <c>Extracted</c> pointed at
/// nothing, invisibly, because a traversal cannot expand through a target that has no node.
/// </para>
///
/// <para>
/// The fixture writes <c>\r\n</c> explicitly rather than relying on the checkout. That is the whole point:
/// this defect is invisible on the Linux CI leg, and a test that inherits the platform's line endings
/// would be invisible there too — the same shape as #182/#209.
/// </para>
/// </summary>
public sealed class CrlfNodeIdAgreementTests : IDisposable
{
    private readonly List<string> _dirs = new();

    /// <summary>
    /// Two same-arity overloads (so the id actually carries an <c>@spanStart</c>) plus a caller, with
    /// enough lines above them that a per-line offset drift is unmissable.
    /// </summary>
    private const string Fixture = """
        using System;

        namespace Fixture;

        /// <summary>Nothing here matters except that it occupies lines.</summary>
        public class Handler
        {
            private readonly string _name = "handler";

            public string Name => _name;

            public void Handle(string message)
            {
                Console.WriteLine(message);
            }

            public void Handle(int code)
            {
                Console.WriteLine(code);
            }

            public void Run()
            {
                Handle("x");
                Handle(1);
            }
        }
        """;

    private string WriteFixture(string lineEnding)
    {
        var dir = Path.Combine(Path.GetTempPath(), "shonkor-436-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        // Normalize to LF first, then expand — so the file's endings are exactly what this test asked for
        // regardless of how the source above was checked out.
        var text = Fixture.ReplaceLineEndings("\n").Replace("\n", lineEnding);
        File.WriteAllText(Path.Combine(dir, "Handler.cs"), text);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ScanAsync(string dir)
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        // semanticCsharp is what produces CALLS from resolved symbols — the exact path whose ids diverged.
        var scanner = new GraphIndexScanner(storage, new IFileParser[] { new RoslynAstParser() }, semanticCsharp: true);
        await scanner.ScanDirectoryAsync(dir, Array.Empty<string>());
        return (await storage.GetAllNodesAsync(), await storage.GetAllEdgesAsync());
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public async Task OverloadIds_AgreeBetweenParserAndSemanticLinker_WhateverTheLineEndings(string lineEnding)
    {
        var (nodes, edges) = await ScanAsync(WriteFixture(lineEnding));

        var ids = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        // Guard first: without an @spanStart in play this test proves nothing, and a silently vacuous
        // check is exactly the failure mode #436 is about.
        Assert.Contains(nodes, n => n.Type == "Method" && n.Name == "Handle" && n.Id.Contains("@", StringComparison.Ordinal));

        var calls = edges.Where(e => e.Relationship == "CALLS").ToList();
        Assert.NotEmpty(calls);

        var dangling = calls.Where(e => !ids.Contains(e.TargetId)).Select(e => e.TargetId).ToList();
        Assert.Empty(dangling);
    }

    /// <summary>
    /// The same file under both line endings must produce the same graph. Ids embed source offsets, so
    /// without normalization a Windows checkout and a Linux one describe the same code with different
    /// names — and nothing in either graph reveals which one it is.
    /// </summary>
    [Fact]
    public async Task TheSameCode_ProducesTheSameIds_UnderCrlfAndLf()
    {
        var (crlfNodes, crlfEdges) = await ScanAsync(WriteFixture("\r\n"));
        var (lfNodes, lfEdges) = await ScanAsync(WriteFixture("\n"));

        // Ids carry the temp directory, which differs per run — compare the part after the file name.
        static IEnumerable<string> Tails(IEnumerable<string> ids) =>
            ids.Select(i => i[(i.IndexOf("Handler.cs", StringComparison.Ordinal) is var ix && ix >= 0 ? ix : 0)..])
               .OrderBy(s => s, StringComparer.Ordinal);

        Assert.Equal(Tails(lfNodes.Select(n => n.Id)), Tails(crlfNodes.Select(n => n.Id)));
        Assert.Equal(
            Tails(lfEdges.Where(e => e.Relationship == "CALLS").Select(e => e.TargetId)),
            Tails(crlfEdges.Where(e => e.Relationship == "CALLS").Select(e => e.TargetId)));
    }
}
