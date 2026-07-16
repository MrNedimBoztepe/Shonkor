// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// A caller-controlled value can never write its own log line (#276, CodeQL <c>cs/log-forging</c>).
///
/// <para>
/// The diagnostics interpolate values that are not ours: a project name arriving from <c>X-Project-Name</c>,
/// and file paths from whatever repository is being indexed (POSIX filenames may contain newlines). A raw
/// newline in one of those splits the output into two lines, so the caller can forge an entry — a fake
/// <c>[ERROR]</c>, or enough blank lines to push their own past a reader. The control being defeated is log
/// INTEGRITY: the reader can no longer tell which lines the program actually wrote.
/// </para>
/// <para>
/// The assertion is deliberately "how many lines came out", not "does the text match". Matching text would
/// pass just as happily on a forged second line as on a flattened one — which is the entire failure.
/// </para>
/// </summary>
public class LogForgingTests
{
    /// <summary>Captures each entry as the single string a plain-text sink would render.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<string> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter) => Entries.Add(formatter(state, ex));
    }

    /// <summary>The name IS the payload: a newline plus a plausible-looking forged entry.</summary>
    private const string Forging = "evil\r\n[ERROR] Deleted all projects";

    [Fact]
    public void AProjectNameCarryingANewline_CannotWriteASecondLogLine()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_forge_{Guid.NewGuid():N}");
        var projectDir = Path.Combine(ws, "p");
        Directory.CreateDirectory(projectDir);

        // The project must EXIST, or GetProjectConfig throws before it ever reaches the diagnostic — the
        // first cut of this test did exactly that and would have passed while asserting nothing.
        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = Forging, Path = projectDir, DatabasePath = Path.Combine(ws, "g.db") } },
            ActiveProjectName = Forging
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

        // ...and its config must be unreadable, which is what makes the diagnostic fire.
        File.WriteAllText(Path.Combine(projectDir, "shonkor.json"), "{ this is not json");

        var captured = new CapturingLogger();
        var pm = new ProjectManager(ws, captured);

        pm.GetProjectConfig(Forging);

        var entry = Assert.Single(captured.Entries);
        Assert.Contains("evil", entry, StringComparison.Ordinal); // the name still reaches the reader...
        // ...but it could not become a line of its own. Counted via IndexOf rather than Split().Length, which
        // the xUnit analyser reads as a collection-size assertion (xUnit2013).
        Assert.True(entry.IndexOf('\n') < 0, $"the diagnostic became more than one line: {entry}");
        Assert.DoesNotContain("\r", entry, StringComparison.Ordinal);
    }

    /// <summary>
    /// The flattening itself, isolated from whichever call path happens to reach a logger today.
    /// <c>ReplaceLineEndings</c> is what the diagnostics call, and it must swallow the exotic terminators too:
    /// U+0085 and U+2028 end a line for some readers, so a CR/LF-only replace would leave a hole exactly where
    /// a hand-rolled sanitiser usually has one. (Written as escapes on purpose — these characters are
    /// invisible in an editor, and one of them terminates a line in JavaScript too.)
    /// </summary>
    [Theory]
    [InlineData("evil\nforged")]
    [InlineData("evil\r\nforged")]
    [InlineData("evil\rforged")]
    [InlineData("evil\u0085forged")]
    [InlineData("evil\u2028forged")]
    [InlineData("evil\u2029forged")]
    public void EveryLineTerminator_IsFlattened_NotJustCrLf(string payload)
    {
        Assert.Equal("evil forged", payload.ReplaceLineEndings(" "));
    }
}
