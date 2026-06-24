using System.Linq;
using System.Threading.Tasks;
using Shonkor.Plugins;
using Xunit;

namespace Shonkor.Tests;

public class SitecoreConfigPluginTests
{
    [Fact]
    public async Task ParseAsync_ExtractsPipelinesServicesEventsAndSettings()
    {
        var plugin = new SitecoreConfigPlugin();
        var config = @"<configuration xmlns:patch=""http://www.sitecore.net/xmlconfig/"">
  <sitecore>
    <pipelines>
      <httpRequestBegin>
        <processor type=""My.Pipelines.MyProcessor, My.Assembly"" />
      </httpRequestBegin>
    </pipelines>
    <services>
      <register serviceType=""My.Abstractions.IFoo, My.Assembly"" implementationType=""My.Services.Foo, My.Assembly"" lifetime=""Singleton"" />
      <configurator type=""My.Di.MyConfigurator, My.Assembly"" />
    </services>
    <events>
      <event name=""item:saved"">
        <handler type=""My.Events.SavedHandler, My.Assembly"" method=""OnItemSaved"" />
      </event>
    </events>
    <settings>
      <setting name=""MySetting"" value=""123"" />
    </settings>
  </sitecore>
</configuration>";

        var (nodes, edges) = await plugin.ParseAsync("App_Config/Include/My.config", config);

        // Primary config node + its settings metadata.
        var configNode = nodes.Single(n => n.Type == "SitecoreConfig");
        Assert.Equal("My.config", configNode.Name);
        Assert.Contains("MySetting", configNode.Properties["settings"]);

        // Pipeline processor.
        Assert.Contains(edges, e => e.Relationship == "REGISTERS_PROCESSOR"
            && e.TargetId == "clrtype:My.Pipelines.MyProcessor" && e.Properties["pipeline"] == "httpRequestBegin");

        // DI registration (implementation is the target; service + lifetime are metadata).
        Assert.Contains(edges, e => e.Relationship == "REGISTERS_SERVICE"
            && e.TargetId == "clrtype:My.Services.Foo"
            && e.Properties["serviceType"] == "My.Abstractions.IFoo"
            && e.Properties["lifetime"] == "Singleton");

        Assert.Contains(edges, e => e.Relationship == "REGISTERS_CONFIGURATOR" && e.TargetId == "clrtype:My.Di.MyConfigurator");
        Assert.Contains(edges, e => e.Relationship == "HANDLES_EVENT"
            && e.TargetId == "clrtype:My.Events.SavedHandler" && e.Properties["event"] == "item:saved");

        // CLR-type anchor node carries the simple name for display.
        Assert.Contains(nodes, n => n.Type == "ClrType" && n.Id == "clrtype:My.Pipelines.MyProcessor" && n.Name == "MyProcessor");
    }

    [Fact]
    public async Task ParseAsync_NonSitecoreConfig_ProducesNothing()
    {
        var plugin = new SitecoreConfigPlugin();
        var (nodes, edges) = await plugin.ParseAsync("web.config",
            @"<configuration><appSettings><add key=""x"" value=""y"" /></appSettings></configuration>");
        Assert.Empty(nodes);
        Assert.Empty(edges);
    }
}
