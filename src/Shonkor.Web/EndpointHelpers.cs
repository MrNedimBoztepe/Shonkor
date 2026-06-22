// Licensed to Shonkor under the MIT License.

using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Shonkor.Core.Interfaces;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Web;

/// <summary>
/// Cross-cutting helpers shared by the minimal-API endpoint groups: uniform error responses,
/// per-request tenant/storage resolution, loopback detection, and the (opt-in) dynamic-plugin loading.
/// </summary>
public static class EndpointHelpers
{
    /// <summary>
    /// Logs the full exception server-side but returns only a generic message to the client,
    /// so internal paths/stack details are never leaked over the API.
    /// </summary>
    public static IResult Fail(string clientMessage, Exception ex)
    {
        Console.Error.WriteLine($"[API] {clientMessage} :: {ex}");
        return Results.Problem(clientMessage);
    }

    /// <summary>
    /// Resolves the storage provider for the current request's tenant, taken from the
    /// <c>X-Project-Name</c> header (set authoritatively by <see cref="Middleware.ApiKeyMiddleware"/>),
    /// falling back to the active project when absent.
    /// </summary>
    public static Task<IGraphStorageProvider> GetStorageForRequestAsync(this ProjectManager pm, HttpContext context, CancellationToken ct)
    {
        var projectName = context.Request.Headers["X-Project-Name"].ToString();
        return string.IsNullOrEmpty(projectName)
            ? pm.GetActiveStorageProviderAsync(ct)
            : pm.GetStorageProviderAsync(projectName, ct);
    }

    /// <summary>True when the request originates from the loopback interface (local dashboard).</summary>
    public static bool IsLoopback(this HttpContext context) =>
        context.Connection.RemoteIpAddress != null &&
        IPAddress.IsLoopback(context.Connection.RemoteIpAddress);

    /// <summary>
    /// Global kill-switch for the assembly-plugin system. The real trust gate is now per-plugin activation
    /// (installing a plugin runs nothing), so this defaults to ON; set <c>Security:EnablePlugins=false</c>
    /// to hard-disable loading every plugin regardless of its activation state.
    /// </summary>
    public static bool PluginsEnabled(IConfiguration config) => config.GetValue("Security:EnablePlugins", true);

    /// <summary>
    /// Whether to use exact semantic C# resolution when indexing <paramref name="project"/>: the
    /// per-project <see cref="Project.SemanticCSharp"/> setting wins; otherwise the global
    /// <c>Indexing:SemanticCSharp</c> default applies.
    /// </summary>
    public static bool UseSemanticCSharp(Project project, IConfiguration config) =>
        project.SemanticCSharp ?? config.GetValue<bool>("Indexing:SemanticCSharp");

    /// <summary>Compiles workspace plugins into a collectible context; returns <see cref="PluginLoadResult.Empty"/> on any failure.</summary>
    public static PluginLoadResult LoadWorkspacePlugins(string workspacePath)
    {
        try
        {
            var pluginsDir = Path.Combine(workspacePath, "plugins");
            return PluginLoader.LoadPlugins(pluginsDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Plugins] Failed to compile dynamic plugins. :: {ex}");
            return PluginLoadResult.Empty;
        }
    }

    /// <summary>
    /// Generates a fully-functional C# source file implementing IFileParser
    /// for runtime Roslyn compilation via PluginLoader.
    /// </summary>
    public static string GeneratePluginBoilerplate(string className, string extension, string languageName)
    {
        return $@"using System.Collections.Generic;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugins;

/// <summary>
/// Dynamic {languageName} parser plugin for Shonkor.
/// This file is compiled at runtime via the Roslyn Plugin Engine.
/// Extend the ParseAsync method with your custom extraction logic.
/// </summary>
public class {className} : IFileParser
{{
    public IReadOnlySet<string> SupportedExtensions {{ get; }} = new HashSet<string> {{ ""{extension}"" }};

    public IReadOnlyList<NodeTypeDescriptor> NodeTypeDescriptors {{ get; }} = new[]
    {{
        new NodeTypeDescriptor(""Method"", ""Code"", true),
        // Add more descriptors here if you extract classes, structs, etc.
    }};

    public Task<(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)> ParseAsync(
        string filePath, string content)
    {{
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        // --- File-level node ---
        var fileId = $""file::{{filePath}}"";
        nodes.Add(new GraphNode
        {{
            Id = fileId,
            Type = ""File"",
            Name = System.IO.Path.GetFileName(filePath),
            FilePath = filePath,
            Content = content.Length > 10000 ? content[..10000] : content
        }});

        // --- Custom extraction logic ---
        // TODO: Add your own parsing rules here.
        // Example: Split content by lines, detect function definitions, classes, etc.
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {{
            var line = lines[i].TrimStart();
            // Placeholder: detect lines that look like function/method definitions
            // Customize this regex or logic for {languageName} syntax
            if (line.StartsWith(""def "") || line.StartsWith(""function "") || line.StartsWith(""fn ""))
            {{
                var funcName = line.Split(new[] {{ ' ', '(' }}, System.StringSplitOptions.RemoveEmptyEntries);
                if (funcName.Length >= 2)
                {{
                    var name = funcName[1].TrimEnd(':', '(', ')');
                    var funcId = $""func::{{filePath}}::{{name}}"";
                    nodes.Add(new GraphNode
                    {{
                        Id = funcId,
                        Type = ""Method"",
                        Name = name,
                        FilePath = filePath,
                        StartLine = i + 1
                    }});
                    edges.Add(new GraphEdge {{ SourceId = fileId, TargetId = funcId, Relationship = ""CONTAINS"" }});
                }}
            }}
        }}

        return Task.FromResult<(IReadOnlyList<GraphNode>, IReadOnlyList<GraphEdge>)>((nodes, edges));
    }}
}}";
    }
}
