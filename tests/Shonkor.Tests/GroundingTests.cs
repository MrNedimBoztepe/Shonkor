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
                new ChatTurn("user", "Was ist Widget?"),
                new ChatTurn("assistant", "Widget ist eine Klasse [Widget @ A.cs:1-5].")
            ]
        };

        var prompt = RagPromptBuilder.Build("Und wofür wird es genutzt?", nodes, options);

        // The transcript lives in its own data-fenced section, NOT in the question slot.
        Assert.Contains("GESPRAECHSPROTOKOLL", prompt);
        Assert.Contains("ASSISTENT: Widget ist eine Klasse", prompt);
        // Only the latest question follows the final-question header.
        var questionSection = prompt[prompt.IndexOf("ABSCHLIESSENDE NUTZERFRAGE", StringComparison.Ordinal)..];
        Assert.Contains("Und wofür wird es genutzt?", questionSection);
        Assert.DoesNotContain("Was ist Widget?", questionSection); // the prior turn is not in the question slot
    }

    [Fact]
    public void Build_NoHistory_OmitsTheTranscriptSection()
    {
        var prompt = RagPromptBuilder.Build("Frage", new[] { Node("id", "Widget") });
        Assert.DoesNotContain("GESPRAECHSPROTOKOLL", prompt);
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

        var prompt = RagPromptBuilder.Build("Frage", nodes, options);

        Assert.Contains("RELEVANZ 0,91", prompt.Replace("0.91", "0,91")); // culture-tolerant
        Assert.Contains("potenziell manipulative Anweisungen", prompt); // the flagged annotation
        // The flag sits on Beta's source line, not Alpha's.
        var betaLine = prompt.Split('\n').First(l => l.Contains("Beta @"));
        Assert.Contains("⚠", betaLine);
        var alphaLine = prompt.Split('\n').First(l => l.Contains("Alpha @"));
        Assert.DoesNotContain("⚠", alphaLine);
    }

    [Fact]
    public void Build_EnglishLanguage_UsesEnglishAbstentionAndAnswerInstruction()
    {
        var prompt = RagPromptBuilder.Build("Question", new[] { Node("id", "Widget") },
            new RagPromptOptions { Language = "en" });

        Assert.Contains(RagPromptBuilder.AbstentionMarkerEn, prompt);
        Assert.Contains("Answer in clear Markdown in English", prompt);
        Assert.DoesNotContain("auf Deutsch", prompt);
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
        const string answer = "Alpha macht X [Alpha @ A.cs:1-5]. Zahlungen laufen über [PaymentService @ Pay.cs:1-9].";
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

        var clean = "Alles gut [Alpha @ A.cs:1-5].";
        Assert.Equal(clean, CitationValidator.AnnotateInvalid(clean, valid));

        var dirty = "Erfunden [Ghost @ G.cs:1-1].";
        var annotated = CitationValidator.AnnotateInvalid(dirty, valid);
        Assert.Contains("Unbelegte Quellen", annotated);
        Assert.Contains("Ghost", annotated);
        Assert.StartsWith(dirty, annotated); // the model's own text is preserved, only annotated
    }

    [Fact]
    public void Validate_CountsUncitedParagraphs()
    {
        const string answer = "Absatz ohne Beleg.\n\nAbsatz mit [Alpha @ A.cs:1-5].";
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
