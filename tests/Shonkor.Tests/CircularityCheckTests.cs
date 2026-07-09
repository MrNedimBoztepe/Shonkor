// Licensed to Shonkor under the MIT License.

extern alias bench;

using bench::Shonkor.Bench;

namespace Shonkor.Tests;

/// <summary>
/// Unit tests for the TICKET-202 circularity check: a retrieval golden case is circular when its query
/// shares too many content words with the target's embedding document (so a vector hit is trivial).
/// </summary>
public class CircularityCheckTests
{
    [Fact]
    public void ContentWords_DropsStopwordsAndShortTokens_Lowercased()
    {
        var words = CircularityCheck.ContentWords("The Scanner reindexes changed files into the graph");
        Assert.Contains("scanner", words);
        Assert.Contains("reindexes", words);
        Assert.Contains("changed", words);
        Assert.Contains("files", words);
        Assert.Contains("graph", words);
        Assert.DoesNotContain("the", words);   // stopword
        Assert.DoesNotContain("into", words);  // stopword
    }

    [Fact]
    public void SharedContentWordCount_CountsDistinctOverlap_IgnoringStopwords()
    {
        // 4 shared content words: scanner, reindexes, changed, files ("the"/"into" are stopwords).
        var query = "the scanner reindexes changed files";
        var document = "Scanner reindexes changed files into the knowledge graph incrementally";
        Assert.Equal(4, CircularityCheck.SharedContentWordCount(query, document));
    }

    [Fact]
    public void IsCircular_TrueWhenOverThreshold_FalseWhenAtOrBelow()
    {
        var query = "hash api tokens store securely digest";
        var document = "Hashes API tokens for storage using a digest so secrets are never stored plaintext";
        // Exact word overlap (no stemming): api, tokens, digest = 3 shared content words.
        Assert.Equal(3, CircularityCheck.SharedContentWordCount(query, document));
        Assert.True(CircularityCheck.IsCircular(query, document, threshold: 2));  // 3 > 2
        Assert.False(CircularityCheck.IsCircular(query, document, threshold: 3)); // 3 is not > 3
    }

    [Fact]
    public void ParaphrasedQuery_WithDistinctVocabulary_IsNotCircular()
    {
        // A genuine NL paraphrase that avoids the document's vocabulary shares few/no content words.
        var query = "keep the codebase index synchronized after edits";
        var document = "GraphIndexScanner: reparses modified source and upserts nodes and edges into storage";
        Assert.False(CircularityCheck.IsCircular(query, document));
    }

    [Fact]
    public void EmptyOrNullInputs_AreNotCircular()
    {
        Assert.Equal(0, CircularityCheck.SharedContentWordCount(null, "anything"));
        Assert.Equal(0, CircularityCheck.SharedContentWordCount("anything", null));
        Assert.False(CircularityCheck.IsCircular("", ""));
    }

    [Fact]
    public void DocIntentStyleQuery_ThatEchoesTheSummary_IsFlaggedCircular()
    {
        // The doc-intent generator's query IS the summary text (minus the name), which sits verbatim in the
        // embedding document — the exact circularity TICKET-202 exists to catch.
        var summary = "Validates the citations a RAG answer emits against the provided context set";
        var embeddingDocument = "CitationValidator\n" + summary + "\npublic static CitationReport Validate(...)";
        Assert.True(CircularityCheck.IsCircular(summary, embeddingDocument));
    }
}
