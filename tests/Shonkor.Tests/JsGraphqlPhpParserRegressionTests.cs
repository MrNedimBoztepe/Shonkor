// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Services;

namespace Shonkor.Tests;

/// <summary>
/// Regression tests for BUG-012 (JS/GraphQL node ids were lowercased — every IMPORTS/DEFINED_IN edge
/// dangled on Windows paths, and the JSComponent id collided with the File node on all-lowercase paths)
/// and BUG-013 (metadata.php phantom EXTENDS edges from every 'k' => 'v' pair; abstract/final and
/// namespaced base classes missed).
/// </summary>
public class JsGraphqlPhpParserRegressionTests
{
    // ---------- JavaScript (BUG-012) ----------

    [Fact]
    public async Task JsComponent_Id_PreservesCase_AndIsDistinctFromTheFileNodeId()
    {
        var filePath = @"C:\Projects\App\src\Button.tsx";
        var (nodes, edges) = await new JavaScriptParser().ParseAsync(filePath, "export const Button = () => null;");

        var component = Assert.Single(nodes);
        Assert.Equal($"{filePath}::Button", component.Id); // original case, not the file id itself
        // The component hangs off the scanner's File node (id = the original-case full path).
        Assert.Contains(edges, e => e.Relationship == "CONTAINS" && e.SourceId == filePath && e.TargetId == component.Id);
    }

    [Fact]
    public async Task JsImports_ResolveExtensionlessRelativeImports_ToTheActualFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_jsimp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "components"));
        try
        {
            var buttonFile = Path.Combine(dir, "Button.tsx");
            await File.WriteAllTextAsync(buttonFile, "export const Button = () => null;");
            var indexFile = Path.Combine(dir, "components", "index.ts");
            await File.WriteAllTextAsync(indexFile, "export {};");

            var appFile = Path.Combine(dir, "App.tsx");
            const string code = "import { Button } from './Button';\nimport * as c from './components';\n";
            var (_, edges) = await new JavaScriptParser().ParseAsync(appFile, code);

            var imports = edges.Where(e => e.Relationship == "IMPORTS").ToList();
            Assert.Equal(2, imports.Count);
            // './Button' resolves to Button.tsx, './components' to components/index.ts — the IMPORTS
            // edges target real File node ids (original case) instead of extensionless lowercase paths.
            Assert.Contains(imports, e => e.TargetId == Path.GetFullPath(buttonFile));
            Assert.Contains(imports, e => e.TargetId == Path.GetFullPath(indexFile));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // ---------- GraphQL (BUG-012) ----------

    [Fact]
    public async Task GraphQl_DefinedInEdges_TargetTheOriginalCaseFileId()
    {
        var filePath = @"C:\Projects\App\Queries\GetBlog.graphql";
        var (nodes, edges) = await new GraphQLParser().ParseAsync(filePath, "query GetBlogPost { id }");

        var query = Assert.Single(nodes);
        Assert.Equal($"{filePath}::query::GetBlogPost", query.Id); // original case throughout
        Assert.Contains(edges, e => e.Relationship == "DEFINED_IN" && e.SourceId == query.Id && e.TargetId == filePath);
    }

    [Fact]
    public async Task GraphQl_InlineFragmentWithoutSpace_IsDetected()
    {
        var (nodes, _) = await new GraphQLParser().ParseAsync(
            @"C:\q.graphql", "query Q { item { ...on Promo { title } } }");

        var query = Assert.Single(nodes);
        Assert.Equal("Promo", query.Properties.GetValueOrDefault("referencedTemplates"));
    }

    // ---------- PHP / OXID (BUG-013) ----------

    [Fact]
    public async Task MetadataPhp_OnlyTheExtendArray_ProducesExtendsEdges()
    {
        const string metadata = """
            <?php
            $aModule = [
                'id'          => 'mymodule',
                'title'       => 'My Module',
                'author'      => 'Jane Doe',
                'extend'      => [
                    'oxArticle' => 'MyVendor\MyModule\Model\Article',
                    'oxOrder'   => 'MyVendor\MyModule\Model\Order',
                ],
                'templates'   => [ 'mytpl.tpl' => 'mymodule/views/mytpl.tpl' ],
                'settings'    => [ [ 'name' => 'blDebug', 'type' => 'bool' ] ],
            ];
            """;
        var (_, edges) = await new PhpModuleParser().ParseAsync(@"C:\shop\modules\mymodule\metadata.php", metadata);

        // Pre-fix: id/title/author/templates/settings pairs each became a phantom EXTENDS edge.
        Assert.Equal(2, edges.Count);
        Assert.All(edges, e => Assert.Equal("EXTENDS", e.Relationship));
        Assert.Contains(edges, e => e.TargetId == "oxArticle" && e.SourceId.EndsWith("::MyVendor\\MyModule\\Model\\Article"));
        Assert.Contains(edges, e => e.TargetId == "oxOrder");
    }

    [Fact]
    public async Task MetadataPhp_WithoutExtendArray_ProducesNoEdges()
    {
        const string metadata = """
            <?php
            $aModule = [ 'id' => 'mymodule', 'title' => 'My Module' ];
            """;
        var (_, edges) = await new PhpModuleParser().ParseAsync(@"C:\shop\modules\mymodule\metadata.php", metadata);
        Assert.Empty(edges);
    }

    [Fact]
    public async Task PhpClasses_AbstractFinal_AndNamespacedBases_AreDetected()
    {
        const string php = """
            <?php
            abstract class MyBase extends \OxidEsales\Eshop\Application\Model\Article { }
            final class MyFinal extends oxOrder { }
            class Plain extends MyBase { }
            """;
        var (nodes, edges) = await new PhpModuleParser().ParseAsync(@"C:\shop\modules\m\classes.php", php);

        Assert.Equal(3, nodes.Count); // pre-fix, abstract/final classes produced no node at all
        Assert.Contains(edges, e => e.SourceId.EndsWith("::MyBase") && e.TargetId == @"\OxidEsales\Eshop\Application\Model\Article");
        Assert.Contains(edges, e => e.SourceId.EndsWith("::MyFinal") && e.TargetId == "oxOrder");
        Assert.Contains(edges, e => e.SourceId.EndsWith("::Plain") && e.TargetId == "MyBase");
    }

    [Fact]
    public async Task SmartyBlocks_SingleQuotes_AndExtraAttributes_AreDetected()
    {
        const string tpl = "[{block name='details_header'}]…[{/block}] [{block name=\"footer\" append}]…[{/block}]";
        var (_, edges) = await new PhpModuleParser().ParseAsync(@"C:\shop\modules\m\views\page.tpl", tpl);

        var blocks = edges.Where(e => e.Relationship == "OVERRIDES_BLOCK").Select(e => e.TargetId).ToList();
        Assert.Contains("details_header", blocks); // single quotes — previously missed
        Assert.Contains("footer", blocks);         // extra attribute — previously missed
    }
}
