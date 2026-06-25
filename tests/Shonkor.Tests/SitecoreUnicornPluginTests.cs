using System.Linq;
using System.Threading.Tasks;
using Shonkor.Plugin.Sitecore;
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

        // Properties (raw field values are preserved, including original casing)
        Assert.True(node.Properties.ContainsKey("Field_Datasource Location"));
        Assert.Contains("A1111111-1111-1111-1111-111111111111", node.Properties["Field_Datasource Location"]);
        Assert.True(node.Properties.ContainsKey("Field_en_1___Created"));
        Assert.True(node.Properties.ContainsKey("Field_en_1_Related Article"));

        // Edges: 1 Template, 1 Parent, 2 from Datasource Location, 1 from Related Article.
        // __Created must NOT create an edge (standard field, no presentation/inheritance semantics).
        Assert.Equal(5, edges.Count);
        Assert.DoesNotContain(edges, e => e.Relationship == "__Created");

        // GUID targets are canonicalised (lowercase, dashed, brace-less) so cross-file refs resolve.
        Assert.Contains(edges, e => e.Relationship == "REFERENCES" && e.TargetId == "a1111111-1111-1111-1111-111111111111");
    }

    [Fact]
    public async Task ParseAsync_ShouldExtractRenderingsDatasourcesAndBaseTemplates()
    {
        // Arrange — a page item with a __Renderings layout and a __Base template list.
        var plugin = new SitecoreUnicornPlugin();
        var yamlContent = @"---
ID: ""11111111-1111-1111-1111-111111111111""
Path: /sitecore/content/Home
Template: ""22222222-2222-2222-2222-222222222222""
SharedFields:
- ID: ""bt""
  Hint: __Base template
  Value: ""{55555555-5555-5555-5555-555555555555}|{66666666-6666-6666-6666-666666666666}""
Languages:
- Language: en
  Versions:
  - Version: 1
    Fields:
    - ID: ""r""
      Hint: __Renderings
      Value: |
        <r><d id=""{FE5D7FDF-89C0-4D99-9AA3-B5FBD009C9F3}""><r uid=""{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"" s=""{33333333-3333-3333-3333-333333333333}"" ph=""main"" ds=""{44444444-4444-4444-4444-444444444444}"" /></d></r>
";

        // Act
        var (nodes, edges) = await plugin.ParseAsync("Home.yml", yamlContent);

        // Assert
        Assert.Single(nodes);

        // Presentation: rendering on a placeholder, and the datasource it uses.
        Assert.Contains(edges, e => e.Relationship == "HAS_RENDERING"
            && e.TargetId == "33333333-3333-3333-3333-333333333333"
            && e.Properties["placeholder"] == "main");
        Assert.Contains(edges, e => e.Relationship == "USES_DATASOURCE"
            && e.TargetId == "44444444-4444-4444-4444-444444444444"
            && e.Properties["rendering"] == "33333333-3333-3333-3333-333333333333");

        // Template inheritance from __Base template (both base templates).
        Assert.Contains(edges, e => e.Relationship == "INHERITS_FROM" && e.TargetId == "55555555-5555-5555-5555-555555555555");
        Assert.Contains(edges, e => e.Relationship == "INHERITS_FROM" && e.TargetId == "66666666-6666-6666-6666-666666666666");
    }
}
