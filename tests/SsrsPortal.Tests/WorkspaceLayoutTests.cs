using System.Net.Http.Json;
using AngleSharp.Html.Parser;
using SsrsPortal.Core;
using SsrsPortal.Mock;

namespace SsrsPortal.Tests;

public sealed class WorkspaceLayoutTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    public async Task AllAuthorizedFoldersRenderBeforeWidthEnhancement(int depth)
    {
        await using var factory = new PortalFactory();
        var folders = Enumerable.Range(0, depth).Select(i => "/Training" + string.Concat(Enumerable.Range(1, i).Select(n => "/Level " + n))).ToArray();
        factory.Mock.CatalogItems = folders.Select(path => new SyntheticCatalogItem(path, "Folder"))
            .Append(new(folders[^1] + "/Report")).ToArray();
        using var user = await factory.Login();
        var catalog = (await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!;
        var html = await user.Http.GetStringAsync(catalog.Single(e => e.Type == "Report").ReportUrl);
        var doc = new HtmlParser().ParseDocument(html);
        Assert.Equal(depth, doc.QuerySelectorAll(".breadcrumb-list>.breadcrumb-folder:not([hidden])").Length);
        Assert.Empty(doc.QuerySelectorAll(".breadcrumb-more a"));
        Assert.NotNull(doc.QuerySelector(".breadcrumb-more[hidden] summary[aria-label]"));
        Assert.Equal(catalog.Where(e => e.Type == "Folder").Select(e => "/?folder=" + e.Id).Order(),
            doc.QuerySelectorAll(".breadcrumbs a").Select(e => e.GetAttribute("href")).Order());
    }

    [Fact]
    public async Task PolarisHierarchyRendersWithoutCountBasedCollapse()
    {
        await using var factory = new PortalFactory(new() { ["Portal:AllowedRoots:0"] = "/Polaris" });
        factory.Mock.CatalogItems = [new("/Polaris", "Folder"), new("/Polaris/Custom", "Folder"),
            new("/Polaris/Custom/~Tech Services", "Folder"), new("/Polaris/Custom/~Tech Services/Holds Ratio Report")];
        using var user = await factory.Login();
        var catalog = (await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!;
        var doc = new HtmlParser().ParseDocument(await user.Http.GetStringAsync(catalog.Single(e => e.Type == "Report").ReportUrl));
        Assert.Equal(new[] { "Polaris", "Custom", "~Tech Services" }, doc.QuerySelectorAll(".breadcrumb-list>.breadcrumb-folder>a").Select(e => e.TextContent));
        Assert.Equal("Holds Ratio Report", doc.QuerySelector("[aria-current=page]")!.TextContent);
        Assert.Empty(doc.QuerySelectorAll(".breadcrumb-more a"));
        Assert.All(doc.QuerySelectorAll(".breadcrumbs a"), e => Assert.DoesNotContain("Polaris", e.GetAttribute("href")));
    }

    [Fact]
    public async Task RootReportHasCurrentIdentityWithoutInventedNavigation()
    {
        await using var factory = new PortalFactory();
        factory.Mock.CatalogItems = [new("/Training/Root report")];
        using var user = await factory.Login();
        var id = await user.Id("Root report");
        var doc = new HtmlParser().ParseDocument(await user.Http.GetStringAsync(await user.Url("Root report")));
        Assert.Equal("Root report", doc.QuerySelector("[aria-current=page]")!.TextContent);
        Assert.Single(doc.QuerySelectorAll(".breadcrumb-folder"));
        Assert.Null(doc.QuerySelector(".breadcrumb-more"));
        Assert.Equal("/", doc.QuerySelector(".brand")!.GetAttribute("href"));
    }
}
