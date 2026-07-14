// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Services.Mcp;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Path containment (#104 symlinks, #105 central enforcement).
/// <para>
/// TICKET-209 built this guard to stop a tool reading outside the project root. It had two holes: it was
/// applied <b>per tool</b> (six copies, and nothing stopping the seventh from forgetting), and it compared
/// paths <b>lexically</b> — so a symlink inside the tree pointing outside it was "contained", and read.
/// </para>
/// </summary>
public class McpPathContainmentTests
{
    private static string NewWorkspace()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_path_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        return ws;
    }

    private static async Task<(McpRequestHandler Handler, string Workspace)> HandlerAsync()
    {
        var ws = NewWorkspace();
        var dbPath = Path.Combine(ws, "g.db");
        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = Path.Combine(ws, "Real.cs"), Name = "Real.cs", Type = "File", FilePath = Path.Combine(ws, "Real.cs"), Content = "x" }
            });
        }
        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = "P", Path = ws, DatabasePath = dbPath, OrganizationId = "", RepositoryUrl = "", ApiKey = "" } },
            ActiveProjectName = "P"
        };
        File.WriteAllText(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));
        return (new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), "P", lockToContextProject: true), ws);
    }

    private static string ToolCall(string name, object args) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments = args } });

    private static JsonElement Parse(string? json) => JsonDocument.Parse(json!).RootElement.Clone();

    /// <summary>
    /// Creates a directory link that escapes/points wherever we say — a <b>junction</b> on Windows (needs no
    /// elevation, and <c>ResolveLinkTarget</c> resolves it), a symlink elsewhere. Returns false only when even
    /// that fails, which on a normal dev box or CI runner it does not.
    /// <para>
    /// This deliberately does NOT use <c>Directory.CreateSymbolicLink</c>: that needs Developer Mode or admin
    /// on Windows, so it would leave the #104 directory-escape test — the important one — silently skipped on
    /// the very platform where the escape matters. A junction reproduces the same attack (a directory link out
    /// of the tree) with no privilege, so the guard is actually exercised here.
    /// </para>
    /// </summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit(10_000);
            return proc.ExitCode == 0 && Directory.Exists(link);
        }
        try { Directory.CreateSymbolicLink(link, target); return Directory.Exists(link); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return false; }
    }

    // ---- #104: the escape the lexical check could not see ---------------------------------------------

    [Fact]
    public void SymlinkedDirectory_EscapingTheRoot_IsRejected()
    {
        var ws = NewWorkspace();
        var outside = NewWorkspace();
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secrets");

        var link = Path.Combine(ws, "docs");
        Assert.True(TryCreateDirectoryLink(link, outside), "could not create a directory link to build the attack");

        // The attack: `docs` is a link out of the tree, but `docs/secret.txt` is an ORDINARY FILE. It is not
        // itself a link, so resolving only the leaf finds nothing to follow. The escape hides in the
        // DIRECTORY. Lexically the path sits under the root and the old guard waved it through.
        var attack = Path.Combine("docs", "secret.txt");

        Assert.False(
            McpToolHelpers.TryResolveContainedPath(attack, ws, out _, out var error),
            "a path traversing a symlinked directory out of the project root must be rejected");
        Assert.Contains("outside the project root", error);
    }

    [Fact]
    public void SymlinkedFile_PointingOutsideTheRoot_IsRejected()
    {
        var ws = NewWorkspace();
        var outside = NewWorkspace();
        var secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "secrets");

        // A FILE symlink genuinely needs elevation on Windows (junctions are directory-only), so this
        // secondary case may not be buildable here. That is acceptable: the directory-escape test above —
        // the sneakier variant #104 explicitly calls out — runs for real everywhere via a junction, so the
        // fix is never left wholly unverified. If a file symlink CAN be made, assert the guard catches it.
        var link = Path.Combine(ws, "innocent.cs");
        try { File.CreateSymbolicLink(link, secret); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }

        Assert.False(McpToolHelpers.TryResolveContainedPath("innocent.cs", ws, out _, out _));
    }

    [Fact]
    public void SymlinkStayingInsideTheRoot_IsStillAllowed()
    {
        // The guard must reject escapes, not symlinks. A link that resolves back inside the tree is fine,
        // and failing it would break ordinary repos (vendored dirs, monorepo links).
        var ws = NewWorkspace();
        var real = Path.Combine(ws, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.cs"), "x");

        var link = Path.Combine(ws, "alias");
        Assert.True(TryCreateDirectoryLink(link, real), "could not create a directory link for the inside-the-root case");

        Assert.True(
            McpToolHelpers.TryResolveContainedPath(Path.Combine("alias", "a.cs"), ws, out var full, out var error),
            $"a symlink that stays inside the root must be allowed — got: {error}");

        // ...and it must hand back the REAL path, so whatever opens it opens the file the gate approved.
        Assert.Contains("real", full);
    }

    [Fact]
    public void PlainTraversal_IsStillRejected()
    {
        var ws = NewWorkspace();
        Assert.False(McpToolHelpers.TryResolveContainedPath("../../etc/passwd", ws, out _, out _));
    }

    [Fact]
    public void APathThatDoesNotExistYet_IsNotRejectedJustForThat()
    {
        // check_edit may legitimately name a file about to be created. There is no link to follow, and the
        // lexical containment check still applies.
        var ws = NewWorkspace();
        Assert.True(McpToolHelpers.TryResolveContainedPath("New.cs", ws, out _, out var error), error);
    }

    [Fact]
    public void ResolveSymlinks_OnAPlainPath_IsANoOp()
    {
        // Runs on EVERY machine, symlinks or not: proves the new resolution path executes and leaves an
        // ordinary path untouched. The symlink-escape tests above carry the security proof but skip where the
        // OS forbids link creation; this one guarantees the code at least runs and does no harm everywhere.
        var ws = NewWorkspace();
        var file = Path.Combine(ws, "sub", "a.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        Assert.Equal(Path.GetFullPath(file), McpToolHelpers.ResolveSymlinks(file));
        // A path with a non-existent tail is returned lexically, not dropped.
        var future = Path.Combine(ws, "sub", "does", "not", "exist.cs");
        Assert.Equal(Path.GetFullPath(future), McpToolHelpers.ResolveSymlinks(future));
    }

    // ---- #105: enforcement is central, and cannot be forgotten ---------------------------------------

    [Fact]
    public async Task Dispatcher_RejectsAnEscape_BeforeTheToolRuns()
    {
        var (handler, _) = await HandlerAsync();

        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("outline", new { path = "../../../../etc/passwd" })));

        Assert.Equal(-32602, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(McpErrorCode.PathOutsideRoot, root.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Dispatcher_ContainsEveryElementOfAnArrayArgument()
    {
        // `review` takes an array. One poisoned entry must reject the whole call — not be quietly skipped
        // (which would under-report impact) and certainly not be honoured.
        var (handler, ws) = await HandlerAsync();

        var root = Parse(await handler.ProcessJsonRpcMessageAsync(
            ToolCall("review", new { paths = new[] { "Real.cs", "../../../../etc/passwd" } })));

        Assert.Equal(-32602, root.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(McpErrorCode.PathOutsideRoot, root.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    /// <summary>
    /// The load-bearing test of #105. Containment is only structural if a tool <b>cannot forget</b> to opt
    /// in — and a tool opts in by declaring <see cref="IMcpTool.PathArguments"/>. So: any tool whose schema
    /// advertises a path-shaped argument must declare it. A new tool that takes a <c>path</c> and forgets
    /// this list fails the build instead of quietly bypassing the guard.
    /// </summary>
    [Fact]
    public void EveryToolThatAdvertisesAPathArgument_DeclaresIt()
    {
        string[] pathish = ["path", "paths", "file", "files", "directory", "dir"];
        var offenders = new List<string>();

        foreach (var tool in McpToolRegistryFactory.CreateTools())
        {
            var schema = JsonSerializer.SerializeToElement(tool.GetSchema());
            if (!schema.TryGetProperty("inputSchema", out var input) ||
                !input.TryGetProperty("properties", out var props)) continue;

            foreach (var prop in props.EnumerateObject())
            {
                if (!pathish.Contains(prop.Name, StringComparer.OrdinalIgnoreCase)) continue;
                if (tool.PathArguments.Contains(prop.Name, StringComparer.Ordinal)) continue;
                offenders.Add($"{tool.Name}.{prop.Name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These tools advertise a filesystem-path argument but do not declare it in IMcpTool.PathArguments, " +
            "so the dispatcher will not contain it and the tool can read outside the project root:\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheGuardCanActuallyFail()
    {
        // A guard that cannot fail is not a guard. OutlineTool accepts `file` as an alias of `path`; if that
        // alias were ever dropped from PathArguments, EveryToolThatAdvertisesAPathArgument_DeclaresIt must
        // catch it. Pin the alias so the coverage is not silently narrowed.
        var outline = McpToolRegistryFactory.CreateTools().Single(t => t.Name == "outline");
        Assert.Contains("path", outline.PathArguments);
        Assert.Contains("file", outline.PathArguments);
    }
}
