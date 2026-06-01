using System.Linq;
using System.Threading.Tasks;
using Shonkor.Plugins;
using Xunit;

namespace Shonkor.Tests;

public class SitecoreUnicornPluginTests
{
    [Fact]
    public async Task ParseAsync_ShouldExtractComplexFieldsAndRelationships()
    {
        // Arrange
        var plugin = new SitecoreUnicornPlugin();
        var yamlContent = @"---
ID: ""fc69d9bd-c738-4e69-b450-227f17f1dd1f""
Parent: ""da61ad50-8fdb-4252-a68f-b4470b1c9fe8""
Template: ""7ee0975b-0698-493e-b3a2-0b2ef33d0522""
Path: /sitecore/layout/Renderings/Feature/Blog
DB: master
SharedFields:
- ID: ""some-id""
  Hint: Datasource Location
  Value: ""{A1111111-1111-1111-1111-111111111111}|{B2222222-2222-2222-2222-222222222222}""
Languages:
- Language: en
  Versions:
  - Version: 1
    Fields:
    - ID: ""created-id""
      Hint: __Created
      Value: 20220215T091701Z
    - ID: ""some-link-id""
      Hint: Related Article
      Value: ""C3333333-3333-3333-3333-333333333333""
";

        // Act
        var (nodes, edges) = await plugin.ParseAsync("C:\\path\\Blog.yml", yamlContent);

        // Assert
        Assert.Single(nodes);
        var node = nodes[0];
        
        // Node Name Extraction
        Assert.Equal("Blog", node.Name);
        
        // Properties
        Assert.True(node.Properties.ContainsKey("Field_Datasource Location"));
        Assert.Contains("A1111111-1111-1111-1111-111111111111", node.Properties["Field_Datasource Location"]);
        Assert.True(node.Properties.ContainsKey("Field_en_1___Created"));
        Assert.True(node.Properties.ContainsKey("Field_en_1_Related Article"));

        // Edges Extraction
        // 1 for Template, 1 for Parent, 2 from Datasource Location, 1 from Related Article
        // __Created should NOT create an edge because it starts with __
        Assert.Equal(5, edges.Count);
        
        
        foreach (var e in edges) { System.Console.WriteLine("EDGE: Target=" + e.TargetId + " Rel=" + e.Relationship + ""); }
        
        foreach (var e in edges) { System.Console.WriteLine("EDGE: Target=" + e.TargetId + " Rel=" + e.Relationship + ""); }
        
        foreach (var e in edges) { System.Console.WriteLine("EDGE: Target=" + e.TargetId + " Rel=" + e.Relationship + ""); }
        
        foreach (var e in edges) { System.Console.WriteLine("EDGE: Target=" + e.TargetId + " Rel=" + e.Relationship + ""); }
        
        foreach (var e in edges) { System.Console.WriteLine("EDGE: Target=" + e.TargetId + " Rel=" + e.Relationship + ""); }
        
        // Ensure no created/metadata relationships
        Assert.DoesNotContain(edges, e => e.Relationship == "__Created");
    }
}
