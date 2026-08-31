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
