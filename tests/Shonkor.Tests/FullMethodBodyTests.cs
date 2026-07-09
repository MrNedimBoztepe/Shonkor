// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Services;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// TICKET-204: method/constructor nodes store the FULL body (no 500-char cap), every Roslyn symbol carries
/// a `signature` property, and type nodes carry a member-signature skeleton as content — so FTS can match
/// the second half of a long method and `get_source` returns the whole thing.
/// </summary>
public class FullMethodBodyTests
{
    // A method whose body is well over 500 characters, with a distinctive token near the very end.
    private const string LongMethodCode = """
        namespace T;
        public class Calculator
        {
            public int Compute(int seed)
            {
                var a1 = seed + 1;   // filler line one padding padding padding padding
                var a2 = a1 + 2;     // filler line two padding padding padding padding
                var a3 = a2 + 3;     // filler line three padding padding padding padding
                var a4 = a3 + 4;     // filler line four padding padding padding padding
                var a5 = a4 + 5;     // filler line five padding padding padding padding
                var a6 = a5 + 6;     // filler line six padding padding padding padding
                var a7 = a6 + 7;     // filler line seven padding padding padding padding
                var a8 = a7 + 8;     // filler line eight padding padding padding padding
                return a8 + zzzMarkerSentinel;
            }
        }
        """;

    [Fact]
    public async Task Method_OverFiveHundredChars_StoresFullBody_WithSignature()
    {
        var (nodes, _) = await new RoslynAstParser().ParseAsync("Calculator.cs", LongMethodCode);
        var method = Assert.Single(nodes, n => n.Type == "Method" && n.Name == "Compute");

        Assert.True(method.Content.Length > 500, "body should not be capped at 500 chars");
        Assert.Contains("zzzMarkerSentinel", method.Content);      // the tail survives
        Assert.DoesNotContain("...", method.Content[^5..]);         // no legacy truncation marker
        Assert.Equal("public int Compute(int seed)", method.Properties.GetValueOrDefault("signature"));
        Assert.NotNull(method.EndLine);
    }

    [Fact]
    public async Task LongMethod_SecondHalfToken_IsFtsSearchable()
    {
        using var storage = new SqliteGraphStorageProvider(":memory:");
        await storage.InitializeAsync();
        var (nodes, edges) = await new RoslynAstParser().ParseAsync("Calculator.cs", LongMethodCode);
        await storage.UpsertNodesAsync(nodes);
        if (edges.Count > 0) await storage.UpsertEdgesAsync(edges);

        // The sentinel lives in the LAST line of the method — beyond the old 500-char cap.
        var hits = await storage.SearchAsync("zzzMarkerSentinel", 10);
        Assert.Contains(hits, h => h.Node.Name == "Compute");
    }

    [Fact]
    public async Task TypeNode_CarriesSignature_AndMemberSkeletonContent()
    {
        const string code = """
            namespace T;
            public sealed class Widget : IWidget
            {
                public string Name { get; set; }
                public Widget(int id) { }
                public int Render(int x) => x;
            }
            public interface IWidget { }
            """;
        var (nodes, _) = await new RoslynAstParser().ParseAsync("Widget.cs", code);
        var type = Assert.Single(nodes, n => n.Type == "Class" && n.Name == "Widget");

        Assert.Equal("public sealed class Widget : IWidget", type.Properties.GetValueOrDefault("signature"));
        // The skeleton lists each member's signature (no bodies) so a class node is FTS/embeddable.
        Assert.Contains("public string Name", type.Content);
        Assert.Contains("public Widget(int id)", type.Content);
        Assert.Contains("public int Render(int x)", type.Content);
        Assert.DoesNotContain("=> x", type.Content); // member BODIES are not in the skeleton
    }

    [Fact]
    public async Task Property_And_Constructor_CarrySignatures()
    {
        const string code = """
            namespace T;
            public class Box
            {
                public int Size { get; init; }
                public Box(int size) { Size = size; }
            }
            """;
        var (nodes, _) = await new RoslynAstParser().ParseAsync("Box.cs", code);

        var prop = Assert.Single(nodes, n => n.Type == "Property" && n.Name == "Size");
        Assert.Equal("public int Size", prop.Properties.GetValueOrDefault("signature"));

        var ctor = Assert.Single(nodes, n => n.Type == "Constructor");
        Assert.Equal("public Box(int size)", ctor.Properties.GetValueOrDefault("signature"));
    }
}
