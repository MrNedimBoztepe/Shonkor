// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

public class SurprisingConnectionExplainerTests
{
    [Fact]
    public void BuildQuery_MentionsBothSymbols_AndFramesAsInference()
    {
        var a = new GraphNode { Id = "A", Name = "Alpha", Type = "Class" };
        var b = new GraphNode { Id = "B", Name = "Beta", Type = "Interface" };

        var q = SurprisingConnectionExplainer.BuildQuery(a, b);

        Assert.Contains("Alpha", q);
        Assert.Contains("Beta", q);
        Assert.Contains("NO direct dependency", q);
        Assert.Contains("inference", q); // the model is told this is a guess, not a fact
    }

    [Fact]
    public async Task ExplainAsync_LabelsOutputAsInferred_AndGroundsInBothNodes()
    {
        var a = new GraphNode { Id = "A", Name = "Alpha", Type = "Class" };
        var b = new GraphNode { Id = "B", Name = "Beta", Type = "Class" };
        var analyzer = new MockSemanticAnalyzer(); // canned RAG response echoing the query + node count

        var result = await SurprisingConnectionExplainer.ExplainAsync(analyzer, a, b);

        Assert.StartsWith(SurprisingConnectionExplainer.InferredLabel, result);
        Assert.Contains("INFERRED", result); // never presented as a proven relationship
        Assert.Contains("2 nodes", result);  // both nodes were passed as grounding context
        Assert.Contains("Alpha", result);    // the query (echoed by the mock) names both symbols
        Assert.Contains("Beta", result);
    }
}
