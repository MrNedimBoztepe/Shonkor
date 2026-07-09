// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>
/// Tests the TICKET-205 token-budget planning: the context is bounded to the model window (shrinking the
/// per-node content cap, then dropping the lowest-relevance nodes), the instruction block sits at the END
/// so a front-truncation can't drop the grounding rules, and truncation is reported in the plan.
/// </summary>
public class RagContextBudgetTests
{
    private static GraphNode Node(string id, string name, int contentLen) =>
        new()
        {
            Id = id, Name = name, Type = "Class", FilePath = $"{name}.cs", StartLine = 1, EndLine = 100,
            Content = new string('x', contentLen)
        };

    [Fact]
    public void PlanContext_SmallContext_KeepsEveryNode_AtFullCap()
    {
        var nodes = new[] { Node("a", "Alpha", 300), Node("b", "Beta", 300) };
        var plan = RagPromptBuilder.PlanContext(nodes);

        Assert.Equal(2, plan.Nodes.Count);
        Assert.Empty(plan.TruncatedNodeIds);
        Assert.Equal(0, plan.DroppedNodeCount);
        Assert.Equal(2000, plan.PerNodeContentChars); // largest cap, nothing to shrink
    }

    [Fact]
    public void PlanContext_ManyLargeNodes_ShrinksPerNodeCap_ThenReportsTruncation()
    {
        // 6 nodes × 8k chars each would blow a small window; the planner shrinks the per-node cap.
        var nodes = Enumerable.Range(0, 6).Select(i => Node($"n{i}", $"N{i}", 8000)).ToList();
        var plan = RagPromptBuilder.PlanContext(nodes, new RagPromptOptions { NumCtx = 4096, AnswerReserveTokens = 1024 });

        Assert.True(plan.PerNodeContentChars < 2000, "per-node cap should shrink under budget pressure");
        // Whatever survives, every kept node with >cap content is reported truncated.
        Assert.All(plan.Nodes, n => Assert.Contains(n.Id, plan.TruncatedNodeIds));
        // The total rendered size must fit the (approx) char budget.
        var budgetChars = (int)((4096 - 1024 - 400) * 3.5);
        var rendered = plan.Nodes.Count * (plan.PerNodeContentChars + 200);
        Assert.True(rendered <= budgetChars + 400, "planned context should fit the token budget");
    }

    [Fact]
    public void PlanContext_TinyWindow_DropsLowestRelevanceNodesFirst()
    {
        // Many large nodes against a tiny window force the planner past cap-shrinking into dropping nodes.
        // Scores descend with index, so the last (weakest) must be dropped while the first (strongest) stays.
        var nodes = Enumerable.Range(0, 20).Select(i => Node($"n{i}", $"N{i}", 6000)).ToList();
        var scores = nodes.ToDictionary(n => n.Id, n => 1.0 - int.Parse(n.Id[1..]) * 0.04);
        var options = new RagPromptOptions { NumCtx = 2048, AnswerReserveTokens = 512, MatchStrength = scores };

        var plan = RagPromptBuilder.PlanContext(nodes, options);

        Assert.True(plan.DroppedNodeCount >= 1, "a tiny window over 20 large nodes must drop some");
        Assert.Contains(plan.Nodes, n => n.Id == "n0");        // strongest survives
        Assert.DoesNotContain(plan.Nodes, n => n.Id == "n19"); // weakest is dropped first
    }

    [Fact]
    public void Build_PlacesInstructionBlockAfterContext_SoFrontTruncationKeepsTheRules()
    {
        var nodes = new[] { Node("a", "Alpha", 200) };
        var prompt = RagPromptBuilder.Build("What is Alpha?", nodes);

        var contextPos = prompt.IndexOf("CONTEXT SOURCES", StringComparison.Ordinal);
        var rulesPos = prompt.IndexOf("RULES (binding)", StringComparison.Ordinal);
        var questionPos = prompt.IndexOf("FINAL USER QUESTION", StringComparison.Ordinal);

        Assert.True(contextPos >= 0 && rulesPos >= 0 && questionPos >= 0);
        Assert.True(contextPos < rulesPos, "context must come before the rules");
        Assert.True(rulesPos < questionPos, "rules must come before the final question");
        // The abstention obligation and citation rule live in the trailing rules block (English default).
        Assert.Contains(RagPromptBuilder.AbstentionMarkerEn, prompt[rulesPos..]);
    }

    [Fact]
    public void EstimateTokens_ApproximatesCharsOverThreePointFive()
    {
        Assert.Equal(2, RagPromptBuilder.EstimateTokens(7));   // 7 / 3.5 = 2
        Assert.Equal(3, RagPromptBuilder.EstimateTokens(8));   // ceil(8 / 3.5) = 3
    }

    [Fact]
    public void Build_FromPlan_OnlyRendersPlannedNodes()
    {
        var nodes = new[] { Node("a", "Alpha", 200), Node("b", "Beta", 200), Node("c", "Gamma", 200) };
        var plan = new ContextPlan(new[] { nodes[0] }, new HashSet<string>(), 2, 2000);

        var prompt = RagPromptBuilder.Build("Frage", plan);

        Assert.Contains("Alpha @", prompt);
        Assert.DoesNotContain("Beta @", prompt);
        Assert.DoesNotContain("Gamma @", prompt);
    }
}
