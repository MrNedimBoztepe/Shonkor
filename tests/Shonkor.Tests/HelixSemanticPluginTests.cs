using System.Threading.Tasks;
using Shonkor.Plugin.Sitecore;
using Xunit;

namespace Shonkor.Tests;

public class HelixSemanticPluginTests
{
    [Theory]
    [InlineData("src/Feature/Checkout/code/Foo.cs")]
    [InlineData(@"src\Feature\Checkout\code\Foo.cs")]
    [InlineData("repo/src/feature/Checkout/serialization/Item.yml")] // case-insensitive layer; module casing preserved
    public async Task ParseAsync_ShouldExtractHelixConcept(string path)
    {
        var plugin = new HelixSemanticPlugin();

        var (nodes, edges) = await plugin.ParseAsync(path, "// content");

        var node = Assert.Single(nodes);
        Assert.Equal("Concept", node.Type);
        Assert.Equal("Concept:Feature:Checkout", node.Id); // layer casing canonicalised
        Assert.Equal("Feature", node.Properties["HelixLayer"]);
        Assert.Equal("Checkout", node.Properties["HelixModule"]);

        var edge = Assert.Single(edges);
        Assert.Equal("BELONGS_TO_CONCEPT", edge.Relationship);
        Assert.Equal(path, edge.SourceId);
        Assert.Equal("Concept:Feature:Checkout", edge.TargetId);
    }

    [Fact]
    public async Task ParseAsync_ShouldReturnNothing_ForNonHelixPath()
    {
        var plugin = new HelixSemanticPlugin();

        var (nodes, edges) = await plugin.ParseAsync("src/SomeApp/Services/Foo.cs", "x");

        Assert.Empty(nodes);
        Assert.Empty(edges);
    }
}
