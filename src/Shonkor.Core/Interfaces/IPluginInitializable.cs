// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Interfaces;

/// <summary>
/// An optional, additive hook a plugin component (an <see cref="IFileParser"/> or
/// <see cref="IGraphPostProcessor"/>) may implement to receive an <see cref="IPluginHost"/> right after it
/// is constructed. The host calls <see cref="Initialize"/> exactly once, and only when a host is available;
/// a plugin that does not implement this interface — the status quo — is loaded exactly as before. Use it to
/// grab a logger or start a long-lived resource that <see cref="System.IDisposable"/>/
/// <see cref="System.IAsyncDisposable"/> will later tear down when the plugin is unloaded.
/// </summary>
public interface IPluginInitializable
{
    /// <summary>Called once by the host after construction, with the host context. Never called if no host is supplied.</summary>
    void Initialize(IPluginHost host);
}
