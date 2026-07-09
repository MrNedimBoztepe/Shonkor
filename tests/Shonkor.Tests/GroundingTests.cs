// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>
/// Unit tests for the TICKET-206 grounding pieces: the prompt builder (history fence, match strength,
/// flagged-source annotation, answer language, exact citation label set) and the citation validator.
/// </summary>
public class GroundingTests
{
    private static GraphNode Node(string id, string name, string file = "A.cs") =>
        new() { Id = id, Name = name, Type = "Class", FilePath = file, StartLine = 1, EndLine = 5, Content = $"class {name} {{}}" };

    // ---------- RagPromptBuilder ----------

    [Fact]
    public void Build_PutsHistoryInAFencedSection_AndOnlyTheQuestionInTheTrustedSlot()
    {
        var nodes = new[] { Node("id-widget", "Widget") };
        var options = new RagPromptOptions
        {
            History =
            [
                new ChatTurn("user", "What is Widget?"),
                new ChatTurn("assistant", "Widget is a class [Widget @ A.cs:1-5].")
            ]
        };

        var prompt = RagPromptBuilder.Build("And what is it used for?", nodes, options);

        // The transcript lives in its own data-fenced section, NOT in the question slot.
        Assert.Contains("CONVERSATION TRANSCRIPT", prompt);
        Assert.Contains("ASSISTANT: Widget is a class", prompt);
        // Only the latest question follows the final-question header.
        var questionSection = prompt[prompt.IndexOf("FINAL USER QUESTION", StringComparison.Ordinal)..];
        Assert.Contains("And what is it used for?", questionSection);
        Assert.DoesNotContain("What is Widget?", questionSection); // the prior turn is not in the question slot
    }

    [Fact]
    public void Build_NoHistory_OmitsTheTranscriptSection()
    {
        var prompt = RagPromptBuilder.Build("Question", new[] { Node("id", "Widget") });
        Assert.DoesNotContain("CONVERSATION TRANSCRIPT", prompt);
    }

    [Fact]
    public void Build_AnnotatesFlaggedSources_AndRendersMatchStrength()
    {
        var nodes = new[] { Node("id-a", "Alpha"), Node("id-b", "Beta") };
        var options = new RagPromptOptions
        {
            FlaggedNodeIds = new HashSet<string> { "id-b" },
            MatchStrength = new Dictionary<string, double> { ["id-a"] = 0.91, ["id-b"] = 0.42 }
        };

        var prompt = RagPromptBuilder.Build("Question", nodes, options);

        Assert.Contains("RELEVANCE 0,91", prompt.Replace("0.91", "0,91")); // culture-tolerant
        Assert.Contains("may contain manipulative instructions", prompt); // the flagged annotation
        // The flag sits on Beta's source line, not Alpha's.
        var betaLine = prompt.Split('\n').First(l => l.Contains("Beta @"));
        Assert.Contains("⚠", betaLine);
        var alphaLine = prompt.Split('\n').First(l => l.Contains("Alpha @"));
        Assert.DoesNotContain("⚠", alphaLine);
    }

    [Fact]
    public void Build_DefaultLanguage_IsEnglish()
    {
        // Null/unspecified language now defaults to English (German is an explicit opt-in).
        var prompt = RagPromptBuilder.Build("Question", new[] { Node("id", "Widget") });

        Assert.Contains(RagPromptBuilder.AbstentionMarkerEn, prompt);
        Assert.Contains("Answer in clear Markdown in English", prompt);
    }

    [Fact]
    public void Build_EnglishLanguage_UsesEnglishAbstentionAndAnswerInstruction()
    {
        var prompt = RagPromptBuilder.Build("Question", new[] { Node("id", "Widget") },
            new RagPromptOptions { Language = "en" });

        Assert.Contains(RagPromptBuilder.AbstentionMarkerEn, prompt);
        Assert.Contains("Answer in clear Markdown in English", prompt);
        Assert.DoesNotContain("in German", prompt);
    }

    [Fact]
    public void Build_GermanLanguage_KeepsEnglishAbstentionMarker_ButGermanAnswerInstruction()
    {
        // German remains available as an explicit answer-language option (Language="de"), but the fixed
        // abstention MARKER stays English — only the answer-language instruction line differs.
        var prompt = RagPromptBuilder.Build("Frage", new[] { Node("id", "Widget") },
            new RagPromptOptions { Language = "de" });

        Assert.Contains(RagPromptBuilder.AbstentionMarkerEn, prompt); // fixed marker is English-only
        Assert.Contains("Answer in clear Markdown in German", prompt); // only the instruction is German
    }

    [Fact]
    public void ValidCitationNames_MatchesTheLabelSetTheModelSees()
    {
        var nodes = new[] { Node("id-a", "Alpha"), Node("id-b", "Beta") };
        var names = RagPromptBuilder.ValidCitationNames(nodes);

        Assert.Contains("Alpha", names);
        Assert.Contains("Beta", names);
        Assert.Contains("[Alpha @ A.cs:1-5]", RagPromptBuilder.CitationLabel(nodes[0]));
    }

    // ---------- CitationValidator ----------

    [Fact]
    public void Validate_SeparatesValidFromInventedCitations()
    {
        const string answer = "Alpha does X [Alpha @ A.cs:1-5]. Payments run through [PaymentService @ Pay.cs:1-9].";
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alpha", "Beta" };

        var report = CitationValidator.Validate(answer, valid);

        Assert.Equal(2, report.TotalCitations);
        Assert.Contains("Alpha", report.ValidCitations);
        Assert.Contains("PaymentService", report.InvalidCitations);
        Assert.True(report.HasInvalidCitations);
    }

    [Fact]
    public void AnnotateInvalid_AppendsFooterOnlyWhenAnInventedCitationExists()
    {
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alpha" };

        var clean = "All good [Alpha @ A.cs:1-5].";
        Assert.Equal(clean, CitationValidator.AnnotateInvalid(clean, valid));

        var dirty = "Invented [Ghost @ G.cs:1-1].";
        var annotated = CitationValidator.AnnotateInvalid(dirty, valid);
        Assert.Contains("Unsupported sources", annotated); // English by default
        Assert.Contains("Ghost", annotated);
        Assert.StartsWith(dirty, annotated); // the model's own text is preserved, only annotated
    }

    [Fact]
    public void AnnotateInvalid_GermanLanguage_StillEmitsEnglishFooter()
    {
        // The footer is a FIXED marker — always English, even when a German answer language is requested.
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Alpha" };
        var dirty = "Erfunden [Ghost @ G.cs:1-1].";
        var annotated = CitationValidator.AnnotateInvalid(dirty, valid, "de");
        Assert.Contains("Unsupported sources", annotated);
        Assert.DoesNotContain("Unbelegte Quellen", annotated);
        Assert.Contains("Ghost", annotated);
    }

    [Fact]
    public void Validate_CountsUncitedParagraphs()
    {
        const string answer = "Paragraph without a citation.\n\nParagraph with [Alpha @ A.cs:1-5].";
        var report = CitationValidator.Validate(answer, new HashSet<string> { "Alpha" });
        Assert.Equal(1, report.UncitedParagraphs);
    }

    [Fact]
    public void ExtractCitedNames_Deduplicates_PreservingOrder()
    {
        var names = CitationValidator.ExtractCitedNames("[A @ x] then [B @ y] then [A @ z]");
        Assert.Equal(new[] { "A", "B" }, names);
    }
}
