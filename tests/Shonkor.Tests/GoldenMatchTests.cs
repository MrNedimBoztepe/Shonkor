// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;
using Shonkor.Core.Models;

namespace Shonkor.Tests;

/// <summary>
/// The coverage matcher that decides whether a golden case was answered (#191).
///
/// <para>
/// This is the exact logic that silently measured <b>nothing</b> for an unknown period (#157): coverage was
/// resolved through a by-id lookup, so a golden case naming a bare symbol (<c>TokenHasher</c>) never matched
/// a node whose id is a full path (<c>src/Security/TokenHasher.cs::TokenHasher</c>). The metric reported a
/// number the whole time. Nothing failed, because nothing asked.
/// </para>
/// <para>
/// A benchmark that under-reports is loud — someone chases the regression. One that reports a plausible
/// number for a comparison it never performed is not, and that is the failure being pinned here.
/// </para>
/// </summary>
public class GoldenMatchTests
{
    private static GraphNode Node(string id, string name) =>
        new() { Id = id, Name = name, Type = "Class", FilePath = "src/Security/TokenHasher.cs", Content = "" };

    private static GoldenCase Case(params string[] expected) =>
        new() { Query = "how are tokens hashed?", Expected = [.. expected] };

    [Fact]
    public void ABareExpectedName_ResolvesViaTheNodeName_EvenWhenTheIdDoesNotContainIt()
    {
        // The id deliberately does NOT contain "TokenHasher", so ONLY the name arm can answer this. The first
        // version of this test used an id of "…/TokenHasher.cs::TokenHasher" — which the id-substring arm
        // matches on its own, so deleting the name arm entirely left the test green. It asserted nothing it
        // claimed to. A node whose id and name diverge is also the realistic case: ids carry paths and
        // disambiguators, and a golden set names the symbol.
        var node = Node("src/Security/Hashing.cs::sym#42", "TokenHasher");

        Assert.True(GoldenMatch.Matches(node, Case("TokenHasher")));
    }

    [Fact]
    public void AnExpectedIdFragment_ResolvesAsASubstringOfTheId()
    {
        // Golden sets name paths when a bare symbol would be ambiguous; both spellings must work.
        var node = Node("src/Security/TokenHasher.cs::Hash", "Hash");

        Assert.True(GoldenMatch.Matches(node, Case("Security/TokenHasher.cs")));
    }

    [Fact]
    public void MatchingIsCaseInsensitive_SoAGoldenSetsSpellingIsNotLoadBearing()
    {
        var node = Node("src/Security/TokenHasher.cs::TokenHasher", "TokenHasher");

        Assert.True(GoldenMatch.Matches(node, Case("tokenhasher")));
    }

    [Fact]
    public void AnUnrelatedNode_DoesNotMatch_SoTheMatcherCannotFlatterTheNumbers()
    {
        // The counterpart that gives the tests above their meaning: a matcher that says yes to everything
        // would satisfy every assertion so far, and would report perfect coverage forever.
        var node = Node("src/Graph/GraphEdge.cs::GraphEdge", "GraphEdge");

        Assert.False(GoldenMatch.Matches(node, Case("TokenHasher")));
    }

    [Fact]
    public void ACaseIsAnsweredByAnyOfItsExpectations_NotAllOfThem()
    {
        var node = Node("src/Security/TokenHasher.cs::Hash", "Hash");

        Assert.True(GoldenMatch.Matches(node, Case("Verify", "Hash")));
    }

    [Fact]
    public void Resolve_ReturnsEveryMatchingNode_SoRecallHasAGroundTruthSetToDivideBy()
    {
        // Recall@k is |retrieved ∩ expected| / |expected|. If Resolve returned only the first hit, the
        // denominator would be 1 and recall would read far better than it is.
        var nodes = new[]
        {
            Node("src/Security/TokenHasher.cs::Hash", "Hash"),
            Node("src/Security/TokenHasher.cs::Verify", "Verify"),
            Node("src/Graph/GraphEdge.cs::GraphEdge", "GraphEdge")
        };

        var resolved = GoldenMatch.Resolve(nodes, Case("Security/TokenHasher.cs"));

        Assert.Equal(2, resolved.Count);
    }
}
