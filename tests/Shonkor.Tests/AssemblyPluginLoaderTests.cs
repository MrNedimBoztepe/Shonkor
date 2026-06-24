// Licensed to Shonkor under the MIT License.

using System.IO.Compression;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Shonkor.Core.Interfaces;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// End-to-end proof that a pre-built plugin assembly, installed from a ZIP and explicitly activated, loads
/// through the host's shared <see cref="IFileParser"/> contract and actually parses. Also confirms an
/// installed-but-not-activated plugin loads nothing.
/// </summary>
public class AssemblyPluginLoaderTests
{
    private const string PluginSource = """
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Shonkor.Core.Interfaces;
        using Shonkor.Core.Models;

        namespace DemoPlugin;

        public sealed class DemoParser : IFileParser
        {
            public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string> { ".demo" };
            public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors { get; } = new List<NodeTypeDescriptor>();

            public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(string filePath, string content)
            {
                var nodes = new List<GraphNode>
                {
                    new GraphNode { Id = filePath + "::demo", Name = "DemoNode", Type = "DemoType", FilePath = filePath }
                };
                return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, new List<GraphEdge>()));
            }
        }
        """;

    /// <summary>Compiles the demo plugin source into a real .dll on disk (test setup only, not runtime).</summary>
    private static void CompilePluginDll(string dllPath)
    {
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "DemoPlugin",
            new[] { CSharpSyntaxTree.ParseText(PluginSource) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = compilation.Emit(dllPath);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage())));
    }

    [Fact]
    public async Task ActivatedPlugin_LoadsFromAssembly_AndParses_WhileInstalledOnlyLoadsNothing()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_pluginload_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            // Build a plugin package: plugin.json + a compiled DemoPlugin.dll, zipped.
            var dll = Path.Combine(ws, "DemoPlugin.dll");
            CompilePluginDll(dll);

            var zip = Path.Combine(ws, "demo.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var m = archive.CreateEntry("plugin.json");
                using (var w = new StreamWriter(m.Open(), Encoding.UTF8))
                {
                    w.Write("""{ "id": "demo", "name": "Demo", "version": "1.0.0", "entryAssembly": "DemoPlugin.dll", "minHostApi": "1.0" }""");
                }
                archive.CreateEntryFromFile(dll, "DemoPlugin.dll");
            }

            var registry = new PluginRegistry(ws);
            Assert.True(registry.InstallFromZip(zip).Success);

            // Installed but not activated → the loader loads nothing.
            using (var inert = AssemblyPluginLoader.LoadActive(registry))
            {
                Assert.Empty(inert.Parsers);
            }

            // Activate → the assembly loads and the parser works through the host's IFileParser type.
            Assert.True(registry.Activate("demo").Success);
            using var loaded = AssemblyPluginLoader.LoadActive(registry);
            Assert.Single(loaded.Parsers);

            var parser = loaded.Parsers[0];
            Assert.Contains(".demo", parser.SupportedExtensions);

            var (nodes, _) = await parser.ParseAsync("C:/x/file.demo", "irrelevant");
            Assert.Single(nodes);
            Assert.Equal("DemoNode", nodes[0].Name);
            Assert.Equal("DemoType", nodes[0].Type);
        }
        finally
        {
            try { Directory.Delete(ws, recursive: true); } catch { }
        }
    }
}
