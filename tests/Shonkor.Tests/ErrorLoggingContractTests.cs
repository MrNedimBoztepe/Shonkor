// Licensed to Shonkor under the MIT License.

namespace Shonkor.Tests;

/// <summary>
/// Production error paths go through <see cref="Microsoft.Extensions.Logging.ILogger"/>, not straight to the
/// console (#256).
///
/// <para>
/// Writing the exception to <c>Console.Error</c> works, which is exactly why it kept spreading: it needs no
/// wiring, so it is always the shortest edit. The cost only shows up elsewhere — the write ignores log levels
/// and configuration, cannot be routed or filtered by the host, and arrives as flat text rather than
/// structured.
/// </para>
/// <para>
/// The one place a bare console write is still CORRECT is the stdio MCP host, where stdout carries the
/// JSON-RPC protocol and stderr is the log channel. That is why the shared components keep an
/// <c>ILogger?</c>-with-stderr-fallback (the <c>GraphIndexScanner.Warn</c> idiom) rather than requiring a
/// logger: the fallback is the stdio host's correct behaviour, not a leftover.
/// </para>
/// </summary>
public class ErrorLoggingContractTests
{
    private const string Contract =
        "Route it through ILogger instead (#256). The endpoint groups resolve one via EndpointHelpers.ApiLogger; " +
        "shared components take an optional ILogger? and fall back to stderr for the stdio MCP host.";

    /// <summary>
    /// Every .cs file under the Web project. This is the host that HAS a logger for the asking — the CLI and
    /// Bench are console programs where writing to the console is the entire point, so they are not in scope.
    /// </summary>
    private static IEnumerable<string> WebSources() =>
        Directory.EnumerateFiles(RepoPaths.File("src", "Shonkor.Web"), "*.cs", SearchOption.AllDirectories);

    [Fact]
    public void TheWebHost_WritesNoErrorsStraightToTheConsole_SoLogConfigurationApplies()
    {
        var offenders = WebSources()
            .Where(f => File.ReadAllText(f).Contains("Console.Error.WriteLine", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Console.Error.WriteLine in the Web host: {string.Join(", ", offenders)}. {Contract}");
    }

    [Fact]
    public void TheStdioHostsFallback_IsStderr_NeverStdout_SoTheJsonRpcChannelStaysClean()
    {
        // The fallback that makes the ILogger optional must write to stderr. If it ever became
        // Console.WriteLine, the stdio MCP session would interleave a log line into the JSON-RPC stream and
        // the client would see a protocol error instead of a diagnostic — the failure this guard exists for
        // is a corrupted session, not an ugly log.
        var handler = File.ReadAllText(
            RepoPaths.File("src", "Shonkor.Infrastructure", "Services", "McpRequestHandler.cs"));

        var logError = handler[handler.IndexOf("private void LogError(", StringComparison.Ordinal)..];
        logError = logError[..logError.IndexOf("\n    }", StringComparison.Ordinal)];

        Assert.Contains("Console.Error.WriteLine", logError, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.WriteLine", logError, StringComparison.Ordinal);
    }
}
