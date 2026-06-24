using System.Linq;
using System.Threading.Tasks;
using Shonkor.Plugins;
using Xunit;

namespace Shonkor.Tests;

public class SitecoreXmCloudPluginTests
{
    [Fact]
    public async Task ParseAsync_JssComponent_ExtractsFieldsPlaceholdersAndDefinesEdge()
    {
        var plugin = new SitecoreXmCloudPlugin();
        var tsx = @"
import { Text, RichText, Placeholder } from '@sitecore-jss/sitecore-jss-nextjs';
const Hero = (props) => (
  <div>
    <Text field={props.fields.Title} />
    <RichText field={props.fields.Body} />
    <Placeholder name=""hero-cta"" rendering={props.rendering} />
  </div>
);
export default Hero;
";

        var (nodes, edges) = await plugin.ParseAsync("components/Hero.tsx", tsx);

        var node = Assert.Single(nodes);
        Assert.Equal("XmCloudComponent", node.Type);
        Assert.Equal("xmcloud:component:hero", node.Id);
        Assert.Equal("Hero", node.Name);
        Assert.Contains("Title", node.Properties["fieldsUsed"]);
        Assert.Contains("Body", node.Properties["fieldsUsed"]);
        Assert.Equal("hero-cta", node.Properties["placeholdersExposed"]);

        var edge = Assert.Single(edges);
        Assert.Equal("DEFINES_COMPONENT", edge.Relationship);
        Assert.Equal("components/Hero.tsx", edge.SourceId);
        Assert.Equal("xmcloud:component:hero", edge.TargetId);
    }

    [Fact]
    public async Task ParseAsync_RouteJson_WalksPlaceholderTreeIntoComponentAndDatasourceEdges()
    {
        var plugin = new SitecoreXmCloudPlugin();
        var json = @"{
  ""sitecore"": {
    ""route"": {
      ""name"": ""home"",
      ""placeholders"": {
        ""main"": [
          {
            ""componentName"": ""Hero"",
            ""dataSource"": ""{33333333-3333-3333-3333-333333333333}"",
            ""placeholders"": { ""hero-cta"": [ { ""componentName"": ""Button"" } ] }
          }
        ]
      }
    }
  }
}";

        var (nodes, edges) = await plugin.ParseAsync("data/routes/home/en.json", json);

        var node = Assert.Single(nodes);
        Assert.Equal("XmCloudRouteData", node.Type);
        Assert.Equal("home", node.Properties["routeName"]);

        // Route renders the Hero component on 'main' — id matches the .tsx component id (cross-file link).
        Assert.Contains(edges, e => e.Relationship == "RENDERS_COMPONENT"
            && e.TargetId == "xmcloud:component:hero" && e.Properties["placeholder"] == "main");

        // Hero's datasource GUID is normalised and linkable to the Unicorn SitecoreItem node.
        Assert.Contains(edges, e => e.Relationship == "USES_DATASOURCE"
            && e.TargetId == "33333333-3333-3333-3333-333333333333"
            && e.Properties["component"] == "Hero");

        // Nested placeholder is walked too.
        Assert.Contains(edges, e => e.Relationship == "RENDERS_COMPONENT"
            && e.TargetId == "xmcloud:component:button" && e.Properties["placeholder"] == "hero-cta");
    }

    [Fact]
    public async Task ParseAsync_NonRouteJson_ProducesNothing()
    {
        var plugin = new SitecoreXmCloudPlugin();
        var (nodes, edges) = await plugin.ParseAsync("package.json", @"{ ""name"": ""my-app"", ""version"": ""1.0.0"" }");
        Assert.Empty(nodes);
        Assert.Empty(edges);
    }

    [Fact]
    public async Task ParseAsync_NonJssComponent_ProducesNothing()
    {
        var plugin = new SitecoreXmCloudPlugin();
        var (nodes, edges) = await plugin.ParseAsync("components/Plain.tsx", "const Plain = () => <div>hi</div>; export default Plain;");
        Assert.Empty(nodes);
        Assert.Empty(edges);
    }
}
