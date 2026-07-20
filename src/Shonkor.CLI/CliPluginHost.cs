// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Logging;

using Shonkor.Core.Interfaces;

namespace Shonkor.CLI;

/// <summary>
/// The CLI's <see cref="IPluginHost"/> (#306): hands plugins a logger that writes to <b>stderr</b>. In the
/// CLI — which is also the MCP stdio server — stdout carries the JSON-RPC protocol, so stderr is the only
/// safe log channel. This is what lets the TypeScript sidecar plugin (#292) surface its diagnostics
/// (timeouts, Node-unavailable degradation, parse errors) without corrupting the protocol stream.
/// </summary>
internal sealed class CliPluginHost : IPluginHost
{
    public static CliPluginHost Instance { get; } = new();

    public ILogger Logger { get; } = new StdErrLogger();

    private sealed class StdErrLogger : ILogger
    {
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception != null) message += $" ({exception.Message})";
            Console.Error.WriteLine($"[plugin:{logLevel}] {message}");
        }
    }
}
