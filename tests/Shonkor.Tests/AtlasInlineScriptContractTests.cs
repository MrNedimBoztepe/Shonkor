// Licensed to Shonkor under the MIT License.

using System.Text.RegularExpressions;

namespace Shonkor.Tests;

/// <summary>
/// ATLAS ships no script-in-markup, so the CSP can refuse it (#271).
///
/// <para>
/// #260 shipped the CSP with <c>'unsafe-inline'</c> in <c>script-src</c> because the shell was full of inline
/// <c>onclick</c> attributes. That setting is the one that matters: a policy permitting the page's own inline
/// handlers permits <i>any</i> injected inline script, which is most of what a CSP is for. #271 removed the
/// last of them — the script moved to <c>atlas.js</c> and the controls dispatch from <c>data-act</c>.
/// </para>
/// <para>
/// This guard exists because the failure is silent in the direction that matters. Add an inline handler back
/// and nothing here complains at build time; the control just quietly stops working in a browser, and the
/// tempting fix is to put <c>'unsafe-inline'</c> back and undo the ticket. There is no partial retreat
/// available either: a nonce or hash cannot cover generated markup, and browsers ignore <c>'unsafe-inline'</c>
/// the moment a nonce or hash is present. It is all of them or none, so the count that has to stay at zero is
/// worth pinning.
/// </para>
/// </summary>
public class AtlasInlineScriptContractTests
{
    private const string Contract =
        "ATLAS must contain no script-in-markup, or the CSP's script-src has to re-admit 'unsafe-inline' and #271 is undone. " +
        "Use data-act/data-chg and the delegated dispatcher at the bottom of atlas.js instead.";

    private static string Atlas(string file) =>
        File.ReadAllText(RepoPaths.File("src", "Shonkor.Web", "wwwroot", "atlas", file));

    /// <summary>
    /// An event handler written as MARKUP: <c>onclick="..."</c> inside an HTML string. Deliberately NOT the
    /// same thing as <c>el.onclick = fn</c>, which is ordinary JS, was never a CSP concern, and is still used
    /// throughout atlas.js — a pattern that matched both would fail for a reason that does not exist.
    /// </summary>
    private static readonly Regex MarkupHandler = new(@"\bon[a-z]+\s*=\s*""", RegexOptions.Compiled);

    [Fact]
    public void TheAtlasShell_HasNoInlineScriptBlock_SoScriptSrcSelfCoversAllOfIt()
    {
        var html = Atlas("index.html");

        // Every <script> must have a src. An inline block (<script> with no src) would need 'unsafe-inline'.
        foreach (Match tag in Regex.Matches(html, @"<script\b[^>]*>", RegexOptions.IgnoreCase))
        {
            Assert.True(tag.Value.Contains("src=", StringComparison.OrdinalIgnoreCase),
                $"Inline <script> block found in atlas/index.html: {tag.Value}. {Contract}");
        }
    }

    [Fact]
    public void TheAtlasMarkup_CarriesNoInlineEventHandlers_SoNoneNeedsUnsafeInline()
    {
        foreach (var file in new[] { "index.html", "atlas.js" })
        {
            var match = MarkupHandler.Match(Atlas(file));
            Assert.False(match.Success,
                $"Inline event handler in atlas/{file} at offset {match.Index}: '{match.Value}...'. {Contract}");
        }
    }

    /// <summary>
    /// Drops <c>//</c> lines so the assertions are about the POLICY, not the prose about it. Learned the hard
    /// way: the comment above the policy explains why script-src carries no 'unsafe-inline', and the first cut
    /// of this test matched that sentence and failed. Same trap <c>OllamaCompletionOptionContractTests</c> hit.
    /// </summary>
    private static string CodeOnly(string source) => string.Join('\n',
        source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void TheCsp_RefusesInlineScript_ButStillAllowsInlineStyle()
    {
        var program = CodeOnly(File.ReadAllText(RepoPaths.File("src", "Shonkor.Web", "Program.cs")));
        var csp = Regex.Match(program, @"script-src[^""]*").Value;

        Assert.NotEqual(string.Empty, csp);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);

        // style-src keeps 'unsafe-inline' ON PURPOSE (#271): inline style attributes execute no code, so
        // removing ~46 of them would be a large diff for very little. Pinned so the asymmetry reads as a
        // decision rather than as an oversight someone should "fix" by loosening script-src to match.
        Assert.Contains("style-src 'self' 'unsafe-inline'", program, StringComparison.Ordinal);
    }
}
