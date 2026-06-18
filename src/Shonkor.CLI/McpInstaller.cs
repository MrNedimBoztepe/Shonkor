using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Shonkor.CLI;

/// <summary>
/// Registers (and removes) the Shonkor MCP server in the user's agent clients. Only writes into clients it
/// can actually detect on disk, resolves config paths per-OS, and launches the server via the installed
/// `shonkor` command — so `shonkor mcp install` works the same on Windows/macOS/Linux.
/// </summary>
public static class McpInstaller
{
    private const string ServerName = "shonkor";

    /// <summary>A target agent client: its MCP config file and a marker directory that proves it's installed.</summary>
    private sealed record Client(string Name, string ConfigPath, string DetectDir);

    public static Task<int> InstallAsync() => RunAsync(install: true);
    public static Task<int> UninstallAsync() => RunAsync(install: false);

    private static async Task<int> RunAsync(bool install)
    {
        try
        {
            var (command, args) = ResolveLaunch();
            var env = ResolveEnvironment();

            Console.WriteLine(install
                ? $"Registering MCP server '{ServerName}' — launch: {command} {string.Join(' ', args)}"
                : $"Removing MCP server '{ServerName}' from detected clients.");
            if (install && env.TryGetValue("SHONKOR_WORKSPACE", out var ws))
                Console.WriteLine($"  workspace: {ws}");

            var any = false;
            foreach (var client in GetClients())
            {
                var detected = File.Exists(client.ConfigPath) || Directory.Exists(client.DetectDir);
                if (!detected)
                {
                    Console.WriteLine($"  - {client.Name}: not detected (skipped)");
                    continue;
                }
                any = true;
                if (install)
                {
                    await UpdateConfigFileAsync(client.ConfigPath, command, args, env).ConfigureAwait(false);
                    Console.WriteLine($"  ✓ {client.Name}: registered ({client.ConfigPath})");
                }
                else
                {
                    var removed = await RemoveFromConfigAsync(client.ConfigPath).ConfigureAwait(false);
                    Console.WriteLine(removed
                        ? $"  ✓ {client.Name}: removed ({client.ConfigPath})"
                        : $"  - {client.Name}: nothing to remove");
                }
            }

            Console.ForegroundColor = any ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine(any
                ? (install ? "Done. Restart the client(s) so the server loads." : "Done.")
                : "No supported clients detected. Install one, or add the server manually (see docs/user/llm_integration.md).");
            Console.ResetColor();
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"MCP {(install ? "install" : "uninstall")} failed: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    /// <summary>Reports, per client, whether it's detected and whether Shonkor is registered.</summary>
    public static async Task<int> StatusAsync()
    {
        var (command, args) = ResolveLaunch();
        Console.WriteLine($"Shonkor MCP launch: {command} {string.Join(' ', args)}\n");
        foreach (var client in GetClients())
        {
            var detected = File.Exists(client.ConfigPath) || Directory.Exists(client.DetectDir);
            var registered = detected && File.Exists(client.ConfigPath) && await IsRegisteredAsync(client.ConfigPath).ConfigureAwait(false);
            var state = !detected ? "not detected" : registered ? "registered ✓" : "detected, NOT registered (run: shonkor mcp install)";
            Console.WriteLine($"  {client.Name,-16} {state}");
        }
        return 0;
    }

    /// <summary>The supported clients for the current OS, with per-OS config locations.</summary>
    private static IEnumerable<Client> GetClients()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Claude Desktop — OS-specific app-config location.
        string claudeDir;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            claudeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            claudeDir = Path.Combine(home, "Library", "Application Support", "Claude");
        else
            claudeDir = Path.Combine(home, ".config", "Claude");
        yield return new Client("Claude Desktop", Path.Combine(claudeDir, "claude_desktop_config.json"), claudeDir);

        // Claude Code — user-scope config file in the home directory, data dir ~/.claude.
        yield return new Client("Claude Code", Path.Combine(home, ".claude.json"), Path.Combine(home, ".claude"));

        // Antigravity / Gemini — home-relative, same on every OS.
        var gemini = Path.Combine(home, ".gemini");
        yield return new Client("Antigravity", Path.Combine(gemini, "config", "mcp_config.json"), gemini);
    }

    /// <summary>How an MCP client should launch the server: the installed `shonkor` apphost, or `dotnet shonkor.dll` in dev.</summary>
    private static (string Command, string[] Args) ResolveLaunch()
    {
        var main = Process.GetCurrentProcess().MainModule?.FileName;
        var name = main is null ? null : Path.GetFileNameWithoutExtension(main);
        if (!string.IsNullOrEmpty(main) && string.Equals(name, "shonkor", StringComparison.OrdinalIgnoreCase))
        {
            // Installed as a global tool: register the absolute apphost path (robust — no PATH dependency).
            return (main, new[] { "mcp" });
        }
        // Dev fallback (`dotnet run`): launch the entry dll via dotnet.
        var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(dll)
            ? ("shonkor", new[] { "mcp" })
            : ("dotnet", new[] { dll, "mcp" });
    }

    private static Dictionary<string, string> ResolveEnvironment()
    {
        var env = new Dictionary<string, string>();
        var workspace = Environment.GetEnvironmentVariable("SHONKOR_WORKSPACE");
        if (string.IsNullOrWhiteSpace(workspace)) workspace = ResolveWorkspacePath();
        if (!string.IsNullOrWhiteSpace(workspace)) env["SHONKOR_WORKSPACE"] = workspace;

        // SHONKOR_PROJECT is intentionally NOT pinned: clients with a per-workspace cwd resolve the project
        // from it, and global clients (Claude Desktop) switch via the set_project MCP tool. An explicit
        // override is still honoured.
        var project = Environment.GetEnvironmentVariable("SHONKOR_PROJECT");
        if (!string.IsNullOrWhiteSpace(project)) env["SHONKOR_PROJECT"] = project;
        return env;
    }

    private static string ResolveWorkspacePath()
    {
        var dir = Directory.GetCurrentDirectory();
        var current = dir;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "projects.json")) || File.Exists(Path.Combine(current, "shonkor.json")))
                return current;
            var parent = Directory.GetParent(current);
            if (parent == null || parent.FullName == current) break;
            current = parent.FullName;
        }
        return dir;
    }

    private static async Task<JsonObject> LoadOrCreateAsync(string configPath)
    {
        if (!File.Exists(configPath))
        {
            var dir = Path.GetDirectoryName(configPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return new JsonObject { ["mcpServers"] = new JsonObject() };
        }
        try { return JsonNode.Parse(await File.ReadAllTextAsync(configPath).ConfigureAwait(false))?.AsObject() ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static async Task UpdateConfigFileAsync(string configPath, string command, string[] args, IReadOnlyDictionary<string, string> env)
    {
        var root = await LoadOrCreateAsync(configPath).ConfigureAwait(false);
        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        var argsArray = new JsonArray();
        foreach (var a in args) argsArray.Add(a);
        var node = new JsonObject { ["command"] = command, ["args"] = argsArray };
        if (env.Count > 0)
        {
            var envObj = new JsonObject();
            foreach (var kv in env) envObj[kv.Key] = kv.Value;
            node["env"] = envObj;
        }
        servers[ServerName] = node;

        await File.WriteAllTextAsync(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    private static async Task<bool> RemoveFromConfigAsync(string configPath)
    {
        if (!File.Exists(configPath)) return false;
        var root = await LoadOrCreateAsync(configPath).ConfigureAwait(false);
        if (root["mcpServers"] is not JsonObject servers || !servers.ContainsKey(ServerName)) return false;
        servers.Remove(ServerName);
        await File.WriteAllTextAsync(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> IsRegisteredAsync(string configPath)
    {
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(configPath).ConfigureAwait(false))?.AsObject();
            return root?["mcpServers"] is JsonObject servers && servers.ContainsKey(ServerName);
        }
        catch { return false; }
    }
}
