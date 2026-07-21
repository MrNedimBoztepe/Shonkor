// Licensed to Shonkor under the MIT License.

using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Shonkor.Core.Interfaces;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// The parsers loaded from the currently-active plugin assemblies, together with the collectible load
/// contexts they live in. Dispose to unload them and reclaim memory.
/// </summary>
public sealed class AssemblyPluginLoadResult : IDisposable
{
    private List<AssemblyLoadContext>? _contexts;

    public IReadOnlyList<IFileParser> Parsers { get; }

    /// <summary>The graph-aware "phase 2" post-processors loaded from the active plugin assemblies.</summary>
    public IReadOnlyList<IGraphPostProcessor> PostProcessors { get; }

    internal AssemblyPluginLoadResult(IReadOnlyList<IFileParser> parsers, IReadOnlyList<IGraphPostProcessor> postProcessors, List<AssemblyLoadContext> contexts)
    {
        Parsers = parsers;
        PostProcessors = postProcessors;
        _contexts = contexts;
    }

    public static AssemblyPluginLoadResult Empty { get; } = new(Array.Empty<IFileParser>(), Array.Empty<IGraphPostProcessor>(), new List<AssemblyLoadContext>());

    public void Dispose()
    {
        if (_contexts == null) return;

        // Tear the loaded components down BEFORE their load contexts are unloaded — once an ALC is unloaded
        // its types are gone, so a component's Dispose can only run while the context is still alive. This is
        // what lets a plugin owning a long-lived resource (e.g. a sidecar process) shut it down. Parser and
        // post-processor share this path since both are constructed identically via Activator.CreateInstance.
        foreach (var parser in Parsers) DisposeComponent(parser);
        foreach (var postProcessor in PostProcessors) DisposeComponent(postProcessor);

        foreach (var ctx in _contexts)
        {
            if (ctx.IsCollectible) ctx.Unload();
        }
        _contexts = null;
    }

    /// <summary>
    /// Disposes one loaded plugin component, preferring <see cref="IAsyncDisposable"/> over
    /// <see cref="IDisposable"/>. A failure is isolated (swallowed) so one plugin's faulty teardown never
    /// blocks the remaining components or the ALC unload — mirroring the load-time failure isolation.
    /// </summary>
    private static void DisposeComponent(object component)
    {
        try
        {
            if (component is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else if (component is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch
        {
            // Intentionally swallowed: a plugin's teardown must not block the others or the context unload.
        }
    }
}

/// <summary>
/// Loads the <see cref="Shonkor.Core.Models.PluginState.Active"/> plugins of a workspace from their
/// pre-built assemblies (no compilation). Each plugin is loaded into its own collectible
/// <see cref="AssemblyLoadContext"/> with an <see cref="AssemblyDependencyResolver"/>, so a plugin's
/// private dependencies resolve from its own folder while the shared contract assembly
/// (<c>Shonkor.Core</c>, which defines <see cref="IFileParser"/>) is always served by the host — that
/// shared type identity is what lets a plugin's parser plug into the host.
/// </summary>
/// <remarks>
/// SECURITY: a pre-built assembly still executes code when loaded. That is why loading only happens for
/// plugins the user has explicitly ACTIVATED — installation alone (see <see cref="PluginRegistry"/>) runs
/// nothing. There is no runtime compilation of source, removing the arbitrary-source RCE surface.
/// </remarks>
public static class AssemblyPluginLoader
{
    /// <summary>Assemblies that must be shared from the host (never loaded privately) to keep type identity.</summary>
    private static readonly HashSet<string> SharedContractAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shonkor.Core",
        // The host-supplied IPluginHost.Logger is a Microsoft.Extensions.Logging.ILogger that crosses the ALC
        // boundary. If a plugin ships its own copy of Logging.Abstractions, the resolver would load it
        // privately, yielding a SECOND ILogger type — casting the host logger to it throws InvalidCastException.
        // Serving this assembly from the host (like Shonkor.Core) keeps one ILogger identity across the boundary.
        "Microsoft.Extensions.Logging.Abstractions"
    };

    /// <summary>
    /// Loads every active plugin in the workspace. Failures are isolated: a plugin that won't load is
    /// marked <see cref="Shonkor.Core.Models.PluginState.Failed"/> in the registry and skipped, so one bad
    /// plugin never blocks the others. The returned result must be disposed once the parsers are done.
    /// </summary>
    public static AssemblyPluginLoadResult LoadActive(string workspacePath, IPluginHost? host = null)
    {
        var registry = new PluginRegistry(workspacePath);

        // Fresh-deploy seeding (#313): make the first-party standard plugins (JS/TS) install + active out of the
        // box so their file types are parsed without a manual step — otherwise JS/TS parsing silently vanishes
        // after #292 removed the in-host parser. This workspace-path overload is the single seam every host
        // uses (both Shonkor.Web and Shonkor.CLI, and every load call site), so seeding here covers both paths
        // with no per-host wiring (the #179 lesson). It is idempotent and cheap once the plugin is registered.
        StandardPluginSeeder.EnsureSeeded(registry);

        return LoadActive(registry, host);
    }

    public static AssemblyPluginLoadResult LoadActive(PluginRegistry registry, IPluginHost? host = null)
    {
        var active = registry.ActivePlugins();
        if (active.Count == 0) return AssemblyPluginLoadResult.Empty;

        var parsers = new List<IFileParser>();
        var postProcessors = new List<IGraphPostProcessor>();
        var contexts = new List<AssemblyLoadContext>();

        foreach (var plugin in active)
        {
            var entryPath = Path.Combine(plugin.InstallPath, plugin.Manifest.EntryAssembly);
            try
            {
                if (!File.Exists(entryPath))
                {
                    registry.MarkFailed(plugin.Manifest.Id, $"Entry assembly missing on disk: {entryPath}");
                    continue;
                }

                // Tamper check: the on-disk assembly must match the SHA-256 recorded at install time, so a
                // file swapped under an activated plugin's folder is caught BEFORE its code is loaded.
                if (!string.IsNullOrEmpty(plugin.EntryAssemblySha256))
                {
                    var actualHash = Sha256OfFile(entryPath);
                    if (!string.Equals(actualHash, plugin.EntryAssemblySha256, StringComparison.OrdinalIgnoreCase))
                    {
                        registry.MarkFailed(plugin.Manifest.Id,
                            "Entry assembly hash does not match the value recorded at install — the file was modified after installation. Reinstall the plugin from a trusted package.");
                        continue;
                    }
                }

                var context = new PluginLoadContext(plugin.Manifest.Id, entryPath);
                var assembly = context.LoadFromAssemblyPath(entryPath);

                var found = 0;
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsInterface || type.IsAbstract) continue;

                    if (typeof(IFileParser).IsAssignableFrom(type) && Activator.CreateInstance(type) is IFileParser parser)
                    {
                        Initialize(parser, host);
                        parsers.Add(parser);
                        found++;
                    }
                    else if (typeof(IGraphPostProcessor).IsAssignableFrom(type) && Activator.CreateInstance(type) is IGraphPostProcessor postProcessor)
                    {
                        Initialize(postProcessor, host);
                        postProcessors.Add(postProcessor);
                        found++;
                    }
                }

                if (found == 0)
                {
                    context.Unload();
                    registry.MarkFailed(plugin.Manifest.Id, "Assembly loaded but contains no IFileParser or IGraphPostProcessor implementation.");
                    continue;
                }

                contexts.Add(context);
            }
            catch (Exception ex)
            {
                registry.MarkFailed(plugin.Manifest.Id, $"Load failed: {ex.Message}");
            }
        }

        return parsers.Count == 0 && postProcessors.Count == 0
            ? AssemblyPluginLoadResult.Empty
            : new AssemblyPluginLoadResult(parsers, postProcessors, contexts);
    }

    /// <summary>
    /// Optional, additive init hook: a component that opts into <see cref="IPluginInitializable"/> receives
    /// the host exactly once, right after construction. With no host (the default), nothing is called — the
    /// status quo for every existing plugin, none of which implement the interface. A throwing
    /// <c>Initialize</c> propagates and marks the plugin failed, consistent with load-time failure isolation.
    /// </summary>
    private static void Initialize(object component, IPluginHost? host)
    {
        if (host != null && component is IPluginInitializable initializable)
        {
            initializable.Initialize(host);
        }
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// A collectible load context for one plugin. Private dependencies resolve from the plugin folder via
    /// <see cref="AssemblyDependencyResolver"/>; the shared contract assembly defers to the host (returns
    /// null) so <see cref="IFileParser"/> and the model types are the SAME types the host uses.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string name, string entryAssemblyPath)
            : base($"ShonkorPlugin_{name}_{Guid.NewGuid():N}", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Always serve the shared contract from the host so types unify across the boundary.
            if (assemblyName.Name != null && SharedContractAssemblies.Contains(assemblyName.Name))
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}
