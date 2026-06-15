using System.Reflection;
using System.Runtime.Loader;
using Shonkor.Core.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Holds the parsers compiled from a plugins directory together with the collectible
/// <see cref="AssemblyLoadContext"/> they were loaded into. Dispose to unload the context
/// and reclaim memory once the parsers are no longer needed.
/// </summary>
public sealed class PluginLoadResult : IDisposable
{
    private AssemblyLoadContext? _context;

    public IReadOnlyList<IFileParser> Parsers { get; }

    internal PluginLoadResult(IReadOnlyList<IFileParser> parsers, AssemblyLoadContext? context)
    {
        Parsers = parsers;
        _context = context;
    }

    /// <summary>An empty result that loads nothing (used when plugins are disabled).</summary>
    public static PluginLoadResult Empty { get; } = new(Array.Empty<IFileParser>(), null);

    public void Dispose()
    {
        if (_context is { IsCollectible: true } context)
        {
            context.Unload();
        }
        _context = null;
    }
}

/// <summary>
/// Dynamically compiles and loads C# source files at runtime from a plugins directory
/// and registers any classes implementing <see cref="IFileParser"/>.
/// </summary>
/// <remarks>
/// SECURITY: compiling and executing arbitrary <c>.cs</c> files is effectively remote code
/// execution. Callers MUST gate <see cref="LoadPlugins"/> behind an explicit opt-in
/// (e.g. <c>Security:EnablePlugins</c>) and should only expose plugin-management endpoints to
/// trusted local users. Assemblies are loaded into a dedicated <em>collectible</em>
/// <see cref="AssemblyLoadContext"/> so they can be unloaded via <see cref="PluginLoadResult.Dispose"/>.
/// </remarks>
public static class PluginLoader
{
    /// <summary>
    /// Scans the specified directory, compiles C# files into a single dynamic, collectible
    /// assembly-load context, and instantiates all IFileParser implementations found.
    /// The returned <see cref="PluginLoadResult"/> must be disposed once the parsers are done.
    /// </summary>
    public static PluginLoadResult LoadPlugins(string pluginsDirectory)
    {
        if (!Directory.Exists(pluginsDirectory))
        {
            return PluginLoadResult.Empty;
        }

        var files = Directory.GetFiles(pluginsDirectory, "*.cs");
        if (files.Length == 0)
        {
            return PluginLoadResult.Empty;
        }

        var context = new AssemblyLoadContext($"ShonkorPlugins_{Guid.NewGuid():N}", isCollectible: true);
        var parsers = new List<IFileParser>();

        try
        {
            var syntaxTrees = new List<SyntaxTree>();
            foreach (var file in files)
            {
                try
                {
                    var code = File.ReadAllText(file);
                    syntaxTrees.Add(CSharpSyntaxTree.ParseText(code));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[PluginLoader] Error reading plugin '{file}': {ex.Message}");
                }
            }

            if (syntaxTrees.Count == 0)
            {
                context.Unload();
                return PluginLoadResult.Empty;
            }

            // Gather metadata references from current AppDomain ONCE (O(1) compilation)
            var references = new List<MetadataReference>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(assembly.Location));
                    }
                    catch
                    {
                        // Ignore assemblies that cannot be referenced
                    }
                }
            }

            // Explicitly add YamlDotNet just in case it wasn't picked up
            var yamlAssemblyPath = typeof(YamlDotNet.Serialization.Deserializer).Assembly.Location;
            if (!string.IsNullOrEmpty(yamlAssemblyPath))
            {
                references.Add(MetadataReference.CreateFromFile(yamlAssemblyPath));
            }

            var compilation = CSharpCompilation.Create(
                $"Shonkor.Plugins_{Guid.NewGuid():N}",
                syntaxTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);

            if (!result.Success)
            {
                var failures = result.Diagnostics.Where(diagnostic =>
                    diagnostic.IsWarningAsError ||
                    diagnostic.Severity == DiagnosticSeverity.Error);

                var errorMsg = string.Join("\n", failures.Select(f => $"{f.Id}: {f.GetMessage()}"));
                Console.Error.WriteLine($"[PluginLoader] Compilation failed:\n{errorMsg}");
                context.Unload();
                return PluginLoadResult.Empty;
            }

            ms.Seek(0, SeekOrigin.Begin);

            // Load into the dedicated collectible context so it can be unloaded later.
            var loadedAssembly = context.LoadFromStream(ms);

            foreach (var type in loadedAssembly.GetTypes())
            {
                if (typeof(IFileParser).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                {
                    if (Activator.CreateInstance(type) is IFileParser parser)
                    {
                        parsers.Add(parser);
                        Console.Error.WriteLine($"[PluginLoader] Loaded dynamic parser plugin: {type.Name}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PluginLoader] Fatal error loading plugins: {ex.Message}");
            context.Unload();
            throw;
        }

        if (parsers.Count == 0)
        {
            // Nothing usable was produced; unload immediately so we don't leak the context.
            context.Unload();
            return PluginLoadResult.Empty;
        }

        return new PluginLoadResult(parsers, context);
    }
}
