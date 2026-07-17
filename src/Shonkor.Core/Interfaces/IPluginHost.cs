// Licensed to Shonkor under the MIT License.

using Microsoft.Extensions.Logging;

namespace Shonkor.Core.Interfaces;

/// <summary>
/// The minimal, additive context the host offers a plugin at load time. Deliberately tiny — just a
/// diagnostics channel (<see cref="Logger"/>) — so a plugin with a long-lived resource (e.g. a sidecar
/// process, see #292) can report to the host without the host exposing a DI container or a wider service
/// surface. Kept intentionally small to keep the plugin contract's blast radius minimal; it can grow
/// additively later.
/// </summary>
public interface IPluginHost
{
    /// <summary>A logger the plugin may use to surface diagnostics through the host's logging pipeline.</summary>
    ILogger Logger { get; }
}
