// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// AP1 (#428): the tier says how much to believe an edge; the reason says what kind of evidence produced
/// it. The tier is DERIVED from the reason and never maintained beside it — two fields that must agree
/// are two fields that will not, which is how the scanner's pessimistic <c>max()</c> and the store's
/// optimistic <c>MIN()</c> left 1 354 edges stranded between them until #399.
/// </summary>
public sealed class ProvenanceReasonTests
{
    /// <summary>
    /// The derivation is total: every reason yields a tier, or explicitly none. A value added to the enum
    /// without a tier must fail loudly rather than resolve to something plausible.
    /// </summary>
    [Fact]
    public void EveryReasonDerivesATier_ExceptUnspecified()
    {
        foreach (ProvenanceReason reason in Enum.GetValues<ProvenanceReason>())
        {
            var tier = ProvenanceReasons.TierOf(reason);
            if (reason == ProvenanceReason.Unspecified) Assert.Null(tier);
            else Assert.NotNull(tier);
        }
    }

    /// <summary>
    /// The reason splitting <c>TypeResolution</c> in two existed for: one producer, two tiers, from one
    /// code path. Keeping it as a single reason would have put an exception into the one rule the whole
    /// design rests on.
    /// </summary>
    [Fact]
    public void TypeResolutionIsSplitSoTheRuleHasNoException()
    {
        Assert.Equal(Provenance.Inferred, ProvenanceReasons.TierOf(ProvenanceReason.TypeResolutionUnique));
        Assert.Equal(Provenance.Ambiguous, ProvenanceReasons.TierOf(ProvenanceReason.TypeResolutionAmbiguous));
    }

    /// <summary>
    /// <c>Unspecified</c> must never imply <c>Extracted</c>. Every time this codebase collapsed "unknown"
    /// into an optimistic value it produced a real defect — 699 over-claiming edges being the largest.
    /// </summary>
    [Fact]
    public void UnspecifiedClaimsNothing()
        => Assert.Null(ProvenanceReasons.TierOf(ProvenanceReason.Unspecified));

    [Theory]
    [InlineData("CONTAINS", Provenance.Extracted, ProvenanceReason.Structural)]
    [InlineData("CALLS", Provenance.Extracted, ProvenanceReason.SemanticSymbol)]
    [InlineData("REFERENCES_TYPE", Provenance.Extracted, ProvenanceReason.SemanticSymbol)]
    [InlineData("REFERENCES_TYPE", Provenance.Inferred, ProvenanceReason.UniqueNameMatch)]
    [InlineData("REFERENCES_TYPE", Provenance.Ambiguous, ProvenanceReason.AmbiguousNameMatch)]
    [InlineData("RELATES_TO", Provenance.Inferred, ProvenanceReason.ModelAssertion)]
    [InlineData("RESOLVES_TO", Provenance.Ambiguous, ProvenanceReason.TypeResolutionAmbiguous)]
    [InlineData("REGISTERS_PROCESSOR", Provenance.Inferred, ProvenanceReason.CmsConfiguration)]
    public void RecoversTheProducerFromTheRelationAndTier(string rel, Provenance tier, ProvenanceReason expected)
        => Assert.Equal(expected, ProvenanceReasons.Recover(rel, tier));

    /// <summary>
    /// The case the migration must NOT guess at. Before #402 the syntactic parser and the semantic linker
    /// both wrote <c>IMPLEMENTS</c> at <c>Extracted</c>, so the pair identifies no producer. Assigning
    /// either reason would be a guess wearing a migration's clothes.
    /// </summary>
    [Theory]
    [InlineData("IMPLEMENTS")]
    [InlineData("EXTENDS")]
    public void PreCorrectionHeritageEdges_StayUnspecified(string rel)
    {
        Assert.Equal(ProvenanceReason.Unspecified, ProvenanceReasons.Recover(rel, Provenance.Extracted));
        Assert.Equal(ProvenanceReason.SyntacticHeritage, ProvenanceReasons.Recover(rel, Provenance.Inferred));
    }

    /// <summary>A relationship this table does not know yields no reason, rather than a plausible one.</summary>
    [Fact]
    public void UnknownRelationshipsYieldUnspecified()
        => Assert.Equal(ProvenanceReason.Unspecified, ProvenanceReasons.Recover("SOME_PLUGIN_RELATION", Provenance.Inferred));

    /// <summary>Every reason the recovery can produce must derive a tier, or the migration writes a contradiction.</summary>
    [Fact]
    public void EveryRecoverableReasonHasATier()
    {
        foreach (var rule in ProvenanceInvariant.Rules)
        {
            foreach (var tier in rule.Legitimate)
            {
                var reason = ProvenanceReasons.Recover(rule.Relationship, tier);
                if (reason == ProvenanceReason.Unspecified) continue;
                Assert.Equal(tier, ProvenanceReasons.TierOf(reason));
            }
        }
    }

    [Fact]
    public async Task ReasonRoundTripsThroughStorage()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "CALLS", Provenance = Provenance.Extracted, Reason = ProvenanceReason.SemanticSymbol }
        });

        var edge = Assert.Single(await storage.GetAllEdgesAsync());
        Assert.Equal(ProvenanceReason.SemanticSymbol, edge.Reason);
    }

    /// <summary>A writer that sets no reason must not erase one an earlier producer recorded.</summary>
    [Fact]
    public async Task AnUnspecifiedWriteDoesNotEraseAnExistingReason()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "IMPLEMENTS", Provenance = Provenance.Extracted, Reason = ProvenanceReason.SemanticSymbol }
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "IMPLEMENTS", Provenance = Provenance.Inferred }
        });

        Assert.Equal(ProvenanceReason.SemanticSymbol, Assert.Single(await storage.GetAllEdgesAsync()).Reason);
    }

    /// <summary>
    /// The reason follows the tier that won the merge. The store keeps the STRONGER tier, so keeping the
    /// weaker edge's reason would leave the two describing different edges.
    /// </summary>
    [Fact]
    public async Task TheReasonFollowsTheTierThatWon()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "IMPLEMENTS", Provenance = Provenance.Inferred, Reason = ProvenanceReason.SyntacticHeritage }
        });
        await storage.UpsertEdgesAsync(new[]
        {
            new GraphEdge { SourceId = "a", TargetId = "b", Relationship = "IMPLEMENTS", Provenance = Provenance.Extracted, Reason = ProvenanceReason.SemanticSymbol }
        });

        var edge = Assert.Single(await storage.GetAllEdgesAsync());
        Assert.Equal(Provenance.Extracted, edge.Provenance);
        Assert.Equal(ProvenanceReason.SemanticSymbol, edge.Reason);
        Assert.Equal(edge.Provenance, ProvenanceReasons.TierOf(edge.Reason));
    }

    /// <summary>
    /// The migration gives existing edges a reason where the pair identifies a producer, and leaves the
    /// rest alone — measured on the shape a pre-#402 graph actually has.
    /// </summary>
    [Fact]
    public async Task MigrationRecoversWhatItCanAndLeavesTheRest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shonkor-reason-{Guid.NewGuid():N}.db");
        try
        {
            // A graph as it existed before reasons: the Edges table without the Reason column, written by
            // something that is not this provider. Building it by hand rather than by clearing a gate keeps
            // the test free of a production hook that exists only for tests.
            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                await raw.OpenAsync();
                await using var cmd = raw.CreateCommand();
                cmd.CommandText =
                    """
                    CREATE TABLE Edges (
                        SourceId TEXT NOT NULL, TargetId TEXT NOT NULL, RelationType TEXT NOT NULL,
                        Provenance INTEGER NOT NULL DEFAULT 0, Properties TEXT,
                        PRIMARY KEY (SourceId, TargetId, RelationType));
                    INSERT INTO Edges (SourceId, TargetId, RelationType, Provenance) VALUES
                        ('a','b','CALLS',0), ('c','d','IMPLEMENTS',0);
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            using var reopened = new SqliteGraphStorageProvider(path);
            await reopened.InitializeAsync();   // adds the column, then recovers what it can

            var edges = await reopened.GetAllEdgesAsync();
            Assert.Equal(ProvenanceReason.SemanticSymbol, edges.Single(e => e.Relationship == "CALLS").Reason);
            Assert.Equal(ProvenanceReason.Unspecified, edges.Single(e => e.Relationship == "IMPLEMENTS").Reason);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}

/// <summary>
/// AP1 part two (#428): the producers state their reason, so a full scan leaves no edge unattributed.
/// The one that matters is <c>IMPLEMENTS</c>/<c>EXTENDS</c> — indistinguishable until now, and therefore
/// the one family the repair table had to leave alone (#402, #405).
/// </summary>
public sealed class ProducerReasonTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    private string DirWith(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "shonkor-reason-prod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        return dir;
    }

    /// <summary>
    /// The syntactic parser names its heritage edges for what they are. Before this, the same edge from
    /// the semantic linker and from the parser were one undifferentiated population at the same tier.
    /// </summary>
    [Fact]
    public async Task SyntacticHeritageIsNamedAsSuch()
    {
        var dir = DirWith("A.cs", "public interface IThing { }\npublic class Thing : IThing { }\n");

        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await new Shonkor.Infrastructure.Services.GraphIndexScanner(storage, new[] { new RoslynAstParser() })
            .ScanDirectoryAsync(dir, Array.Empty<string>());

        var heritage = (await storage.GetAllEdgesAsync())
            .Where(e => e.Relationship is "IMPLEMENTS" or "EXTENDS").ToList();

        Assert.NotEmpty(heritage);
        Assert.All(heritage, e => Assert.Equal(ProvenanceReason.SyntacticHeritage, e.Reason));
        // And the derived tier still agrees with the stored one — the invariant the whole design rests on.
        Assert.All(heritage, e => Assert.Equal(e.Provenance, ProvenanceReasons.TierOf(e.Reason)));
    }

    /// <summary>Structural containment is named structurally whoever emitted it.</summary>
    [Fact]
    public async Task ContainmentIsAlwaysStructural()
    {
        var dir = DirWith("B.cs", "public class Outer { public void M() { } }\n");

        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        await new Shonkor.Infrastructure.Services.GraphIndexScanner(storage, new[] { new RoslynAstParser() })
            .ScanDirectoryAsync(dir, Array.Empty<string>());

        var contains = (await storage.GetAllEdgesAsync()).Where(e => e.Relationship == "CONTAINS").ToList();
        Assert.NotEmpty(contains);
        Assert.All(contains, e => Assert.Equal(ProvenanceReason.Structural, e.Reason));
    }

    /// <summary>
    /// A parser that declares no reason produces unattributed edges rather than borrowed ones — the
    /// correct outcome for a plugin built against an older contract.
    /// </summary>
    [Fact]
    public void TheContractDefaultsToSilence()
    {
        Shonkor.Core.Interfaces.IFileParser parser = new SilentParser();   // via the interface: the default lives there
        Assert.Equal(ProvenanceReason.Unspecified, parser.DefaultReason);
    }

    private sealed class SilentParser : Shonkor.Core.Interfaces.IFileParser
    {
        public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".silent" };
        public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = Array.Empty<NodeTypeDescriptor>();
        public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
            => Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((Array.Empty<GraphNode>(), Array.Empty<GraphEdge>()));
    }
}

/// <summary>
/// The acceptance criterion of AP1, as a test: after a full scan, no edge is left unattributed (#428).
///
/// <para>
/// It is here because its absence cost a real defect. The semantic linker was first given
/// <c>ProvenanceReasons.Recover</c> — the MIGRATION's heuristic, which answers "cannot tell" for
/// <c>IMPLEMENTS</c>/<c>EXTENDS</c> at <c>Extracted</c> because in a stored graph both producers wrote
/// that pair. Inside the linker there is nothing to tell. Measured on the shonkor graph itself: <b>102
/// edges</b> came out of a full scan with no reason, and every test that only asked "does it have a
/// reason somewhere" stayed green.
/// </para>
/// </summary>
public sealed class FullScanAttributionTests : IDisposable
{
    private string? _dir;

    public void Dispose()
    {
        try { if (_dir is not null && Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AFullSemanticScanLeavesNoEdgeUnattributed()
    {
        _dir = Path.Combine(Path.GetTempPath(), "shonkor-attrib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "Model.cs"), """
            namespace Fixture;
            public interface IRepository { void Save(); }
            public abstract class RepositoryBase : IRepository { public abstract void Save(); }
            public class UserRepository : RepositoryBase
            {
                public override void Save() { }
                public void Use() { var other = new UserRepository(); other.Save(); }
            }
            """);

        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        // semanticCsharp: true — the linker is the producer whose attribution was wrong.
        var scanner = new Shonkor.Infrastructure.Services.GraphIndexScanner(
            storage, new[] { new RoslynAstParser() }, semanticCsharp: true);
        await scanner.ScanDirectoryAsync(_dir, Array.Empty<string>());

        var edges = await storage.GetAllEdgesAsync();
        Assert.NotEmpty(edges);

        var unattributed = edges.Where(e => e.Reason == ProvenanceReason.Unspecified)
            .Select(e => $"{e.Relationship} [{e.Provenance}] {e.SourceId} -> {e.TargetId}")
            .ToList();

        Assert.True(unattributed.Count == 0,
            $"{unattributed.Count} edge(s) came out of a full scan with no reason:\n" + string.Join("\n", unattributed.Take(10)));

        // And the reason still derives the tier the edge actually carries.
        Assert.All(edges, e => Assert.Equal(e.Provenance, ProvenanceReasons.TierOf(e.Reason)));
    }
}
