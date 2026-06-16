using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Shonkor.CLI;

public static class McpInstaller
{
    public static async Task<int> InstallAsync()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback for dotnet run during dev
                exePath = "dotnet";
            }

            var args = new[] { "shonkor.dll", "mcp" };
            if (exePath != "dotnet")
            {
                args = new[] { "mcp" };
            }
            else
            {
                // If we are in dev mode using dotnet run, we might want to register the absolute path to dotnet run instead,
                // but let's assume we are running a published exe for real installation.
                // However, since the user asked for the absolute path to the exe:
                exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "dotnet";
                args = new[] { "mcp" };
            }

            Console.WriteLine($"Registering MCP Server with path: {exePath}");

            // MCP clients launch this server with their own working directory (not the project dir),
            // so we pin the workspace at install time via an env var. Otherwise the server can't
            // locate projects.json / the active project and effectively does nothing.
            var env = ResolveEnvironment();
            if (env.TryGetValue("SHONKOR_WORKSPACE", out var ws))
            {
                Console.WriteLine($"Pinning SHONKOR_WORKSPACE={ws}");
            }
            if (env.TryGetValue("SHONKOR_PROJECT", out var proj))
            {
                Console.WriteLine($"Pinning SHONKOR_PROJECT={proj}");
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            // Antigravity
            var antigravityPath = Path.Combine(userProfile, ".gemini", "config", "mcp_config.json");
            await UpdateConfigFileAsync(antigravityPath, "shonkor", exePath, args, env);

            // Claude Desktop
            var claudePath = Path.Combine(appData, "Claude", "claude_desktop_config.json");
            await UpdateConfigFileAsync(claudePath, "shonkor", exePath, args, env);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("MCP Server successfully registered in available clients!");
            Console.ResetColor();

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to install MCP server: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static System.Collections.Generic.Dictionary<string, string> ResolveEnvironment()
    {
        var env = new System.Collections.Generic.Dictionary<string, string>();

        // Honor an explicit override, otherwise walk up from the install-time working directory
        // looking for the workspace marker (projects.json / shonkor.json).
        var workspace = Environment.GetEnvironmentVariable("SHONKOR_WORKSPACE");
        if (string.IsNullOrWhiteSpace(workspace))
        {
            workspace = ResolveWorkspacePath();
        }
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            env["SHONKOR_WORKSPACE"] = workspace;
        }

        // We deliberately do NOT pin SHONKOR_PROJECT. MCP clients like Claude Desktop have no
        // per-chat working directory, so a hardcoded project would lock every session to one graph.
        // With SHONKOR_PROJECT unset, the server follows the registry's ActiveProjectName, which the
        // user can switch without re-installing. An explicit override is still honored if present.
        var project = Environment.GetEnvironmentVariable("SHONKOR_PROJECT");
        if (!string.IsNullOrWhiteSpace(project))
        {
            env["SHONKOR_PROJECT"] = project;
        }

        return env;
    }

    private static string ResolveWorkspacePath()
    {
        var dir = Directory.GetCurrentDirectory();
        var current = dir;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "projects.json")) ||
                File.Exists(Path.Combine(current, "shonkor.json")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null || parent.FullName == current)
            {
                break;
            }
            current = parent.FullName;
        }

        return dir;
    }

    private static async Task UpdateConfigFileAsync(string configPath, string serverName, string command, string[] args, System.Collections.Generic.IReadOnlyDictionary<string, string>? env = null)
    {
        if (!File.Exists(configPath))
        {
            var dir = Path.GetDirectoryName(configPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            var emptyConfig = new JsonObject
            {
                ["mcpServers"] = new JsonObject()
            };
            await File.WriteAllTextAsync(configPath, emptyConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        var json = await File.ReadAllTextAsync(configPath);
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json)?.AsObject();
        }
        catch
        {
            root = new JsonObject();
        }

        root ??= new JsonObject();

        if (!root.ContainsKey("mcpServers"))
        {
            root["mcpServers"] = new JsonObject();
        }

        var mcpServers = root["mcpServers"]?.AsObject();
        if (mcpServers != null)
        {
            var argsArray = new JsonArray();
            foreach (var arg in args)
            {
                argsArray.Add(arg);
            }

            var serverNode = new JsonObject
            {
                ["command"] = command,
                ["args"] = argsArray
            };

            if (env != null && env.Count > 0)
            {
                var envObject = new JsonObject();
                foreach (var kvp in env)
                {
                    envObject[kvp.Key] = kvp.Value;
                }
                serverNode["env"] = envObject;
            }

            mcpServers[serverName] = serverNode;

            await File.WriteAllTextAsync(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Updated {configPath}");
        }
    }
}
