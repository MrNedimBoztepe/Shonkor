using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shonkor.CLI;

/// <summary>
/// A local MCP client that relays JSON-RPC stdio traffic to the Shonkor Web API
/// (acting as an HTTP proxy) and writes the HTTP response back to stdio.
/// </summary>
public static class McpProxyClient
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Parse the API URL. For example: shonkor mcp-proxy --url https://api.shonkor.com/api/mcp/relay
        // or rely on an environment variable.
        var apiUrl = Environment.GetEnvironmentVariable("SHONKOR_API_URL") ?? "http://localhost:5000/api/mcp/relay";
        var projectName = Environment.GetEnvironmentVariable("SHONKOR_PROJECT") ?? "";
        var apiKey = Environment.GetEnvironmentVariable("SHONKOR_API_KEY") ?? "";

        for (var i = 1; i < args.Length; i++)
        {
            if ((args[i] == "-u" || args[i] == "--url") && i + 1 < args.Length)
            {
                apiUrl = args[i + 1];
                i++;
            }
            else if ((args[i] == "-p" || args[i] == "--project") && i + 1 < args.Length)
            {
                projectName = args[i + 1];
                i++;
            }
            else if ((args[i] == "-k" || args[i] == "--key") && i + 1 < args.Length)
            {
                apiKey = args[i + 1];
                i++;
            }
        }

        Console.Error.WriteLine($"[MCP Proxy] Starting proxy to {apiUrl}");
        if (!string.IsNullOrEmpty(projectName))
        {
            Console.Error.WriteLine($"[MCP Proxy] Target project: {projectName}");
        }

        var repoUrl = "";
        if (string.IsNullOrEmpty(projectName))
        {
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "config --get remote.origin.url",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                repoUrl = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                
                if (!string.IsNullOrEmpty(repoUrl))
                {
                    Console.Error.WriteLine($"[MCP Proxy] Inferred Repo URL: {repoUrl}");
                }
            }
            catch
            {
                // git might not be installed or not a git repository
            }
        }

        // Long-running tools (audit, generate_capsule on a large graph) easily exceed HttpClient's
        // 100-second default; a proxy-side timeout mid-tool otherwise kills the request.
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

        // Every request (a line WITH an id) must receive exactly one response — a host waiting on a
        // request id that never gets answered hangs the whole conversation. Notifications get none.
        static void WriteErrorResponse(string? requestLine, string message)
        {
            if (string.IsNullOrWhiteSpace(requestLine)) return;
            try
            {
                var reqNode = JsonNode.Parse(requestLine) as JsonObject;
                var idNode = reqNode?["id"];
                if (idNode == null) return; // notification — no response expected
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = idNode.GetValue<JsonElement>(),
                    error = new { code = -32603, message }
                }));
            }
            catch { /* unparseable line — nothing to answer */ }
        }

        while (true)
        {
            string? line = null;
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    break; // EOF
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // Verify it's a valid JSON before sending to avoid unnecessary HTTP calls
                try
                {
                    JsonNode.Parse(line);
                }
                catch
                {
                    Console.Error.WriteLine("[MCP Proxy] Invalid JSON received on stdin, skipping.");
                    continue;
                }

                var content = new StringContent(line, Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(projectName))
                {
                    content.Headers.Add("X-Project-Name", projectName);
                }
                else if (!string.IsNullOrEmpty(repoUrl))
                {
                    content.Headers.Add("X-Repo-Url", repoUrl);
                }

                if (!string.IsNullOrEmpty(apiKey))
                {
                    content.Headers.Add("X-Api-Key", apiKey);
                }

                var response = await httpClient.PostAsync(apiUrl, content).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(responseJson))
                    {
                        Console.WriteLine(responseJson);
                    }
                }
                else
                {
                    var errorResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Console.Error.WriteLine($"[MCP Proxy Error] HTTP {response.StatusCode}: {errorResponse}");
                    WriteErrorResponse(line, $"HTTP Proxy Error: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                // Transport failure (backend down, DNS, timeout): logging alone would leave the host
                // waiting forever on this request id — synthesize the error response too.
                Console.Error.WriteLine($"[MCP Proxy Exception] {ex.Message}");
                WriteErrorResponse(line, $"MCP proxy transport error: {ex.Message}");
            }
        }

        return 0;
    }
}
