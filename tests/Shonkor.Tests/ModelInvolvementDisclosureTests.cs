// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services.Mcp;

namespace Shonkor.Tests;

/// <summary>
/// AP7 stage 1 (#445): a caller must be able to tell an impact analysis from a topic cloud.
///
/// <para>
/// Measured on live <c>sitecoreMuM</c>: 28 145 of 45 873 <c>Inferred</c> edges — 61 % — are model
/// assertions, and <c>RELATES_TO</c> runs code node → <c>Concept</c>, where concepts act as hubs (one
/// carries 4 871 incoming edges). A depth-2 reverse traversal from a real class reaches <b>7 046</b>
/// nodes with those edges and <b>92</b> without, and nothing in today's answer says which it was.
/// </para>
///
/// <para>
/// Stage 1 discloses without changing what is traversed; the default flip is staged separately. So these
/// tests pin the disclosure's two hard properties: it is always present, and it is never wrong.
/// </para>
/// </summary>
public sealed class ModelInvolvementDisclosureTests
{
    [Fact]
    public void CountsOnlyTheRelationshipsAModelAuthors()
    {
        var (model, total) = McpToolHelpers.ModelInvolvement(new[]
        {
            "CALLS", "RELATES_TO", "REFERENCES_TYPE", "INFLUENCES", "AFFECTS", "IMPLEMENTS"
        });

        Assert.Equal(3, model);
        Assert.Equal(6, total);
    }

    /// <summary>
    /// The predicate is the one the storage layer already uses to decide what survives a re-index (#434).
    /// Two definitions of "model-authored" would drift, and the graph would disagree with its own answers.
    /// </summary>
    [Fact]
    public void UsesTheSamePredicateAsTheStorageLayer()
    {
        foreach (var rel in AgentAuthoredRelations.All)
        {
            Assert.Equal((1, 1), McpToolHelpers.ModelInvolvement(new[] { rel }));
        }
    }

    /// <summary>
    /// A result resting on no model edges still says so. "None were involved" and "this tool does not
    /// report involvement" are otherwise the same output — which is the ambiguity the whole work package
    /// exists to remove, and the one this codebase has already been caught by more than once.
    /// </summary>
    [Fact]
    public void DisclosesZeroExplicitly_RatherThanSayingNothing()
    {
        var note = McpToolHelpers.ModelInvolvementNote(new[] { "CALLS", "IMPLEMENTS" });

        Assert.Contains("model-authored edges: 0 of 2", note);
        Assert.Contains("extracted from code", note);
    }

    /// <summary>An empty result is still a result, and still discloses.</summary>
    [Fact]
    public void DisclosesOnAnEmptyResult()
    {
        Assert.Contains("model-authored edges: 0 of 0", McpToolHelpers.ModelInvolvementNote(Array.Empty<string>()));
    }

    /// <summary>
    /// The share is what makes the number actionable: "5 of 7" reads very differently from "5 of 5 000".
    /// The lesson is #406's — a bare count let 699 violations sit unnoticed because nobody knew the base.
    /// </summary>
    [Fact]
    public void NamesTheShareAndTheRelationships()
    {
        var note = McpToolHelpers.ModelInvolvementNote(new[] { "RELATES_TO", "RELATES_TO", "CALLS", "IMPLEMENTS" });

        Assert.Contains("2 of 4 (50 %)", note);
        Assert.Contains("RELATES_TO", note);
        Assert.Contains("asserted by a model", note);
    }

    /// <summary>
    /// An unknown relationship — a third-party plugin's — counts toward the total and not toward the model
    /// share. Guessing either way would be worse: this table knows which relations a model writes, and
    /// nothing else about the rest.
    /// </summary>
    [Fact]
    public void UnknownRelationshipsCountTowardTheTotalOnly()
    {
        Assert.Equal((0, 2), McpToolHelpers.ModelInvolvement(new[] { "SOME_PLUGIN_RELATION", "ANOTHER" }));
    }
}

/// <summary>
/// AP7 stage 2 (#445): model-authored edges are excluded from traversals unless asked for. The default
/// flip is the visible behaviour change — 7 046 reachable nodes became 92 on a real solution — so these
/// pin the switch itself and, more importantly, that an answer which shrank says so.
/// </summary>
public sealed class ModelEdgeExclusionTests
{
    private static System.Text.Json.Nodes.JsonObject Args(string json) =>
        (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!;

    [Fact]
    public void DefaultsToExcluding_WhenTheArgumentIsAbsent()
    {
        Assert.False(McpToolHelpers.ReadIncludeModelEdges(Args("{}")));
        Assert.False(McpToolHelpers.ReadIncludeModelEdges(null));
    }

    [Theory]
    [InlineData("{\"includeModelEdges\":true}", true)]
    [InlineData("{\"includeModelEdges\":false}", false)]
    [InlineData("{\"includeModelEdges\":\"true\"}", true)]   // clients that stringify booleans
    [InlineData("{\"includeModelEdges\":\"TRUE\"}", true)]
    [InlineData("{\"includeModelEdges\":\"yes\"}", false)]   // not a boolean — the safe reading is "no"
    [InlineData("{\"includeModelEdges\":1}", false)]
    public void ReadsTheSwitch(string json, bool expected)
        => Assert.Equal(expected, McpToolHelpers.ReadIncludeModelEdges(Args(json)));

    [Fact]
    public void ExcludesModelRelationsAndKeepsExtractedOnes()
    {
        Assert.False(McpToolHelpers.PassesModelEdgeFilter("RELATES_TO", includeModelEdges: false));
        Assert.False(McpToolHelpers.PassesModelEdgeFilter("INFLUENCES", includeModelEdges: false));
        Assert.True(McpToolHelpers.PassesModelEdgeFilter("CALLS", includeModelEdges: false));
        Assert.True(McpToolHelpers.PassesModelEdgeFilter("RELATES_TO", includeModelEdges: true));
    }

    /// <summary>
    /// The load-bearing one. A result that shrank because a filter removed everything must not read as
    /// "there is nothing there" — that is the most dangerous shape an answer can take, and the exact
    /// mistake this project has repeatedly been caught by.
    /// </summary>
    [Fact]
    public void AnEmptyResultSaysWhatWasHeldBack()
    {
        var note = McpToolHelpers.ModelInvolvementNote(Array.Empty<string>(), excluded: 12);

        Assert.Contains("0 of 0", note);
        Assert.Contains("12 were excluded by default", note);
        Assert.Contains("includeModelEdges=true", note);
    }

    /// <summary>And when nothing was held back, it does not imply something was.</summary>
    [Fact]
    public void SaysNothingAboutExclusionWhenNoneHappened()
        => Assert.DoesNotContain("excluded", McpToolHelpers.ModelInvolvementNote(new[] { "CALLS" }));
}
