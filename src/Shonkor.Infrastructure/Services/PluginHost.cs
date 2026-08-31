// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Logging;

using Shonkor.Core.Interfaces;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// The host's implementation of <see cref="IPluginHost"/> (#306): the minimal context — a logger —
/// handed to a plugin that opts into <see cref="IPluginInitializable"/>. Passing this to
/// <see cref="AssemblyPluginLoader.LoadActive(PluginRegistry, IPluginHost?)"/> is what lets a plugin such
/// as the TypeScript sidecar (#292) surface its diagnostics (timeouts, degradation, parse errors) through
/// the host's logging pipeline instead of into the void.
/// </summary>
public sealed class PluginHost : IPluginHost
{
    public PluginHost(ILogger logger) => Logger = logger;

    public ILogger Logger { get; }
}
