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

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            // Antigravity
            var antigravityPath = Path.Combine(userProfile, ".gemini", "config", "mcp_config.json");
            await UpdateConfigFileAsync(antigravityPath, "shonkor", exePath, args);

            // Claude Desktop
            var claudePath = Path.Combine(appData, "Claude", "claude_desktop_config.json");
            await UpdateConfigFileAsync(claudePath, "shonkor", exePath, args);

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

    private static async Task UpdateConfigFileAsync(string configPath, string serverName, string command, string[] args)
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

            mcpServers[serverName] = new JsonObject
            {
                ["command"] = command,
                ["args"] = argsArray
            };

            await File.WriteAllTextAsync(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Updated {configPath}");
        }
    }
}
