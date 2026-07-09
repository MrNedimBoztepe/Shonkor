// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>Tests the shared embedding-document builder, incl. the head+tail bounding for large symbols.</summary>
public class EmbeddingTextBuilderTests
{
    [Fact]
    public void SummaryMode_ReturnsSummaryOnly()
    {
        var node = new GraphNode { Type = "Class", Name = "Foo", Content = "class Foo {}" };
        Assert.Equal("A summary.", EmbeddingTextBuilder.Build(node, "A summary.", "summary"));
    }

    [Fact]
    public void CodeMode_IncludesTypeNameSignatureSummaryAndBody()
    {
        var node = new GraphNode
        {
            Type = "Method",
            Name = "Run",
            Content = "void Run() { Do(); }",
            Properties = new() { ["signature"] = "public void Run()" }
        };
        var text = EmbeddingTextBuilder.Build(node, "Runs the thing.", "code");

        Assert.Contains("Method Run", text);
        Assert.Contains("public void Run()", text);
        Assert.Contains("Runs the thing.", text);
        Assert.Contains("Do();", text);
    }

    [Fact]
    public void SmallBody_IsUnchanged()
    {
        var body = "line1\nline2\nline3";
        Assert.Equal(body, EmbeddingTextBuilder.HeadAndTail(body, 1500));
    }

    [Fact]
    public void LargeBody_KeepsHeadAndTail_WithinBudget()
    {
        // A body far over budget whose head and tail carry distinct markers.
        var head = "HEAD_MARKER_START\n" + new string('a', 2000);
        var tail = new string('b', 2000) + "\nTAIL_MARKER_END";
        var body = head + "\n" + tail;

        var result = EmbeddingTextBuilder.HeadAndTail(body, EmbeddingTextBuilder.MaxBodyChars);

        Assert.True(result.Length <= EmbeddingTextBuilder.MaxBodyChars);
        Assert.Contains("HEAD_MARKER_START", result);   // opening survives
        Assert.Contains("TAIL_MARKER_END", result);      // closing survives (not just the head)
        Assert.Contains("middle truncated", result);     // the middle-gap marker is present
    }
}
