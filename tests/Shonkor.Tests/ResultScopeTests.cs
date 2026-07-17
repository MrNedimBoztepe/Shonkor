// Licensed to Shonkor under the MIT License.

using System.Text.Json;
using Shonkor.Core.Models;
using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// An answer says which project produced it — above all an EMPTY one (#286).
///
/// <para>
/// "No matches for 'X'." reads as <i>this symbol does not exist</i>. The true statement is <i>this symbol
/// does not exist in project X</i>, and the reader supplies the missing words themselves — wrongly, when the
/// index is pointed at a different repository. That is the #157 class in our own tool surface: a plausible
/// answer to a question nobody asked.
/// </para>
/// <para>
/// This is not hypothetical. An agent querying a ~20-file JavaScript repo against a graph built from a
/// 2.726-node .NET solution had to run <c>get_stats</c> AND <c>orient</c>, then reason from node types
/// (<c>Record</c>, <c>IMPLEMENTS_MEMBER</c>), to work out that its empty results meant "wrong index" rather
/// than "no usages". The node count is what finally gave it away — so it belongs in the answer.
/// </para>
/// </summary>
public class ResultScopeTests
{
    private const string ProjectName = "ScopeProbe";

    private static async Task<(McpRequestHandler Handler, string Workspace)> HandlerAsync()
    {
        var ws = Path.Combine(Path.GetTempPath(), $"shonkor_scope_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        var dbPath = Path.Combine(ws, "g.db");

        using (var storage = new SqliteGraphStorageProvider(dbPath))
        {
            await storage.InitializeAsync();
            await storage.UpsertNodesAsync(new[]
            {
                new GraphNode { Id = "src/A.cs::Alpha", Name = "Alpha", Type = "Class", FilePath = "src/A.cs", Content = "class Alpha {}" },
                new GraphNode { Id = "src/B.cs::Beta", Name = "Beta", Type = "Class", FilePath = "src/B.cs", Content = "class Beta {}" }
            });
        }

        var registry = new
        {
            Organizations = Array.Empty<object>(),
            Users = Array.Empty<object>(),
            Projects = new[] { new { Name = ProjectName, Path = ws, DatabasePath = dbPath } },
            ActiveProjectName = ProjectName
        };
        await File.WriteAllTextAsync(Path.Combine(ws, "projects.json"), JsonSerializer.Serialize(registry));

        return (new McpRequestHandler(new ProjectManager(ws), new ContextCapsuleSynthesizer(), ProjectName,
            lockToContextProject: true), ws);
    }

    private static async Task<string> CallAsync(McpRequestHandler handler, string tool, object args)
    {
        var req = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = tool, arguments = args }
        });
        return await handler.ProcessJsonRpcMessageAsync(req) ?? "";
    }

    [Theory]
    [InlineData("search_graph")]
    [InlineData("search_hybrid")]
    public async Task AnEmptySearchResult_NamesTheProjectAndItsSize_SoItCannotBeReadAsAbsence(string tool)
    {
        var (handler, _) = await HandlerAsync();

        var response = await CallAsync(handler, tool, new { query = "zzz_symbol_that_does_not_exist" });

        Assert.Contains(ProjectName, response, StringComparison.Ordinal);
        // The node count is the tell: a query against a graph of the wrong SIZE is the cheapest signal that
        // the index is not the repo you think it is.
        Assert.Contains("2 nodes", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyFindUsagesResult_NamesTheProject_TheExactCaseFromTheFieldReport()
    {
        // "No usages of 'X' found in the graph." — in WHICH graph? That ambiguity cost an agent several turns.
        var (handler, _) = await HandlerAsync();

        var response = await CallAsync(handler, "find_usages", new { symbol = "Alpha" });

        Assert.Contains(ProjectName, response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonEmptyResult_IsNotBloatedWithTheScopeNote_BecauseItsHitsAlreadyShowTheProject()
    {
        // The suffix is for the answer that shows nothing. A hit lists real paths from the real project, so
        // repeating the scope on every successful search would be tokens for nothing.
        var (handler, _) = await HandlerAsync();

        var response = await CallAsync(handler, "search_graph", new { query = "Alpha" });

        Assert.Contains("Alpha", response, StringComparison.Ordinal);
        Assert.DoesNotContain("2 nodes", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetProject_DescribesItsScopeAsSessionLocal_SoAnAgentDoesNotDeclineOutOfMisplacedCaution()
    {
        // The behaviour was always session-local; only the DESCRIPTION was silent about it, and an agent read
        // "ACTIVE project" as global and refused to switch — declining a correct action on a guess. The
        // description is what it reads BEFORE deciding, so that is where the scope has to be stated.
        var (handler, _) = await HandlerAsync();

        var list = await handler.ProcessJsonRpcMessageAsync(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/list" })) ?? "";

        var doc = JsonDocument.Parse(list);
        var setProject = doc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "set_project");
        var description = setProject.GetProperty("description").GetString()!;

        Assert.Contains("session-local", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("other chat", description, StringComparison.OrdinalIgnoreCase);
    }
}
