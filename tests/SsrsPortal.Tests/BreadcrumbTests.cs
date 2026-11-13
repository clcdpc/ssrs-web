using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SsrsPortal.Core;
using SsrsPortal.Mock;

namespace SsrsPortal.Tests;

public sealed class BreadcrumbTests
{
    private static CatalogEntry[] Tree(string arrangement) =>
    [
        .. arrangement == "reports-only" || arrangement == "empty" ? [] : new CatalogEntry[]
        {
            new("polaris", null, "Polaris", "Folder"), new("custom", "polaris", "Custom", "Folder"),
            new("tech", "custom", "~Tech Services", "Folder"), new("holds", "tech", "Holds Ratio Report", "Report")
        },
        .. arrangement is "two-folders" or "mixed" ? new CatalogEntry[] { new("other", null, "Other", "Folder") } : [],
        .. arrangement is "with-report" or "mixed" or "reports-only" ? new CatalogEntry[] { new("root-report", null, "Root report", "Report") } : [],
        .. arrangement is "with-link" or "mixed" or "reports-only" ? new CatalogEntry[] { new("root-link", null, "Root link", "LinkedReport") } : []
    ];

    [Theory]
    [InlineData("single", true)]
    [InlineData("two-folders", false)]
    [InlineData("with-report", false)]
    [InlineData("with-link", false)]
    [InlineData("mixed", false)]
    [InlineData("reports-only", false)]
    [InlineData("empty", false)]
    public void RootDecisionCountsEveryNavigableRootEntry(string arrangement, bool singleRoot)
    {
        var entries = Tree(arrangement);
        Assert.Equal(singleRoot, BreadcrumbNavigation.SingleRoot(entries) != null);
        var root = BreadcrumbNavigation.ForCatalog(entries, null, false);
        Assert.Equal(new BreadcrumbItem("All folders", null, true), Assert.Single(root.Items));
        Assert.Empty(BreadcrumbNavigation.Ancestors(entries, "unavailable"));
    }

    [Theory]
    [InlineData("single", "Polaris")]
    [InlineData("two-folders", "All folders,Polaris")]
    [InlineData("with-report", "All folders,Polaris")]
    [InlineData("with-link", "All folders,Polaris")]
    [InlineData("mixed", "All folders,Polaris")]
    public void CatalogAndReportShareRootAncestryRoutesAndCurrentSemantics(string arrangement, string prefix)
    {
        var entries = Tree(arrangement);
        var folder = BreadcrumbNavigation.ForCatalog(entries, "tech", false);
        var report = BreadcrumbNavigation.ForReport(entries, new("holds", "Holds Ratio Report", "/private/path", "policy", "tech"));
        Assert.Equal((prefix + ",Custom,~Tech Services").Split(','), folder.Items.Select(e => e.Name));
        Assert.Equal(folder.Items.Select(e => e.Name).Append("Holds Ratio Report"), report.Items.Select(e => e.Name));
        Assert.True(folder.Items[^1].IsCurrent); Assert.True(report.Items[^1].IsCurrent);
        Assert.All(folder.Items[..^1], e => Assert.False(e.IsCurrent));
        Assert.All(report.Items[..^1], e => Assert.False(e.IsCurrent));
        foreach (var (catalogItem, reportItem) in folder.Items.Zip(report.Items))
        {
            Assert.Equal(catalogItem.Route, reportItem.Route);
            Assert.DoesNotContain("path", string.Join(',', reportItem.Route.Values));
            Assert.Single(reportItem.Route);
            if (reportItem.FolderId == null) { Assert.Equal("true", reportItem.Route["root"]); }
            else { Assert.Equal(reportItem.FolderId, reportItem.Route["folder"]); }
        }
        var top = BreadcrumbNavigation.ForCatalog(entries, "polaris", false);
        Assert.Equal(prefix.Split(','), top.Items.Select(e => e.Name));
        Assert.True(top.Items[^1].IsCurrent);
    }

    [Theory]
    [InlineData("single", "Polaris", "polaris")]
    [InlineData("two-folders", "All folders", null)]
    [InlineData("with-report", "All folders", null)]
    [InlineData("with-link", "All folders", null)]
    [InlineData("mixed", "All folders", null)]
    [InlineData("reports-only", "All folders", null)]
    [InlineData("empty", "All folders", null)]
    public void GlobalSearchUsesTheMeaningfulRootWithoutImplyingAFolderScope(string arrangement, string rootName, string? rootId)
    {
        var entries = Tree(arrangement);
        foreach (var folder in new[] { null, "polaris", "tech" })
        {
            var navigation = BreadcrumbNavigation.ForCatalog(entries, folder, true);
            Assert.Equal(new[] { new BreadcrumbItem(rootName, rootId), new BreadcrumbItem("Search results", null, true) }, navigation.Items);
            Assert.Equal(BreadcrumbNavigation.FolderRoute(rootId), navigation.Items[0].Route);
        }
    }

    [Theory]
    [InlineData("with-report", "root-report", "Root report")]
    [InlineData("with-link", "root-link", "Root link")]
    [InlineData("reports-only", "root-report", "Root report")]
    [InlineData("reports-only", "root-link", "Root link")]
    public void ReportsAtTheCatalogRootHaveNoInventedFolder(string arrangement, string id, string name)
    {
        var navigation = BreadcrumbNavigation.ForReport(Tree(arrangement), new(id, name, "/private", "policy"));
        Assert.Equal(new[] { new BreadcrumbItem("All folders", null), new BreadcrumbItem(name, null, true) }, navigation.Items);
        Assert.Equal("true", navigation.Items[0].Route["root"]);
    }

    [Fact]
    public void AncestorsAreOrderedAndBoundedEvenForUnusualGraphs()
    {
        CatalogEntry[] entries = [new("a", "b", "A", "Folder"), new("b", "a", "B", "Folder"), new("report", "a", "R", "Report")];
        Assert.Equal(new[] { "B", "A" }, BreadcrumbNavigation.Ancestors(entries, "a").Select(e => e.Name));
        Assert.Empty(BreadcrumbNavigation.Ancestors(entries, "report"));
        var deep = Enumerable.Range(0, 40).Select(i => new CatalogEntry("f" + i, i == 0 ? null : "f" + (i - 1), "Level " + i, "Folder")).ToArray();
        var trail = BreadcrumbNavigation.ForReport(deep, new("r", "Report", "/private", "policy", "f39"));
        Assert.Equal(deep.Select(e => e.Name).Append("Report"), trail.Items.Select(e => e.Name));
        Assert.Single(trail.Items, e => e.IsCurrent);
    }

    [Theory]
    [InlineData("with-report", "root-report", "Root report")]
    [InlineData("with-link", "root-link", "Root link")]
    [InlineData("reports-only", "root-report", "Root report")]
    public async Task RootReportRendersTheSamePartialWithACopyableSeparator(string arrangement, string id, string name)
    {
        await using var factory = new PortalFactory(); using var user = await factory.Login();
        var navigation = BreadcrumbNavigation.ForReport(Tree(arrangement), new(id, name, "/private", "policy"));
        var doc = new HtmlParser().ParseDocument(await BreadcrumbRendering.RenderAsync(factory.Services, navigation));
        Assert.Equal("/?root=true", Assert.Single(doc.QuerySelectorAll("a")).GetAttribute("href"));
        Assert.Equal("All folders", doc.QuerySelector("a")!.TextContent);
        Assert.Equal(" > ", Assert.Single(doc.QuerySelectorAll(".breadcrumb-separator")).TextContent);
        Assert.Equal(name, Assert.Single(doc.QuerySelectorAll("[aria-current=page]")).TextContent);
        Assert.Null(doc.QuerySelector("[aria-current] a,.breadcrumb-more"));
    }

    [Theory]
    [InlineData("visible", false)]
    [InlineData("hidden", true)]
    [InlineData("blocked", true)]
    [InlineData("empty", true)]
    public async Task FilteredSnapshotDrivesBothPagesWithoutAdditionalSoap(string otherState, bool singleRoot)
    {
        var settings = new Dictionary<string, string?> { ["Portal:AllowedRoots:1"] = "/Other" };
        if (otherState == "blocked") { settings["Portal:ExcludedPrefixes:2"] = "/Other"; }
        await using var factory = new PortalFactory(settings);
        factory.Mock.CatalogItems = [new("/Training/Custom", "Folder"), new("/Training/Custom/~Tech Services", "Folder"),
            new("/Training/Custom/~Tech Services/Holds Ratio Report"), new("/Training/Direct report"),
            new("/Other", "Folder", Hidden: otherState == "hidden"),
            .. otherState == "empty" ? [] : new SyntheticCatalogItem[] { new("/Other/Other report", "LinkedReport") },
            new("/Training/Hidden", "Folder", Hidden: true), new("/Training/Hidden/Secret report"),
            new("/Training/Internal", "Folder"), new("/Training/Internal/Blocked report")];
        using var user = await factory.Login();
        var entries = (await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!;
        Assert.Equal(singleRoot, BreadcrumbNavigation.SingleRoot(entries) != null);
        var calls = factory.Mock.Calls.Values.Sum();
        var parser = new HtmlParser();
        var tech = entries.Single(e => e.Name == "~Tech Services");
        var prefix = singleRoot ? "Training,Custom,~Tech Services" : "All folders,Training,Custom,~Tech Services";
        foreach (var (url, expected) in new[]
        {
            ("/?folder=" + tech.Id, prefix),
            (entries.Single(e => e.Name == "Holds Ratio Report").ReportUrl!, prefix + ",Holds Ratio Report"),
            (entries.Single(e => e.Name == "Direct report").ReportUrl!, (singleRoot ? "Training" : "All folders,Training") + ",Direct report"),
            ("/?folder=" + tech.Id + "&q=report", (singleRoot ? "Training" : "All folders") + ",Search results"),
            ("/?root=true", "All folders")
        })
        {
            var doc = parser.ParseDocument(await user.Http.GetStringAsync(url));
            var nav = Assert.Single(doc.QuerySelectorAll("nav[aria-label='Report location']"));
            Assert.Equal("HEADER", nav.ParentElement!.TagName);
            Assert.Null(doc.QuerySelector("main nav,.catalog-breadcrumbs"));
            Assert.Equal("OL", nav.FirstElementChild!.TagName);
            var items = nav.QuerySelectorAll(".breadcrumb-list>li:not([hidden])");
            Assert.Equal(expected.Split(','), items.Select(e => e.QuerySelector("a,[aria-current]")!.TextContent));
            Assert.Single(nav.QuerySelectorAll("[aria-current=page]"));
            Assert.Null(items.Last().QuerySelector("a"));
            Assert.Null(items.First().QuerySelector(".breadcrumb-separator"));
            Assert.Equal(items.Length - 1, items.SelectMany(e => e.QuerySelectorAll(":scope>.breadcrumb-separator")).Count());
            foreach (var item in items.Skip(1))
            {
                var separator = item.QuerySelector(":scope>.breadcrumb-separator")!;
                Assert.Equal(" > ", separator.TextContent);
                Assert.Equal("true", separator.GetAttribute("aria-hidden"));
                Assert.Equal(NodeType.Text, Assert.Single(separator.ChildNodes).NodeType);
            }
            Assert.DoesNotContain("Secret report", nav.TextContent);
            Assert.DoesNotContain("Blocked report", nav.TextContent);
            Assert.All(nav.QuerySelectorAll("a"), a => Assert.Matches(@"^/\?(folder=[a-f0-9]{64}|root=true)$", a.GetAttribute("href")!));
            if (url.Contains("q=", StringComparison.Ordinal))
            {
                Assert.Equal("/", doc.QuerySelector(".clear-search")!.GetAttribute("href"));
                Assert.NotNull(doc.QuerySelector("main .catalog-search"));
                Assert.DoesNotContain(doc.QuerySelectorAll(".catalog-name"), e => e.TextContent is "Secret report" or "Blocked report");
            }
        }
        Assert.Equal(calls, factory.Mock.Calls.Values.Sum());
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/Report?id=unknown")).StatusCode);
        Assert.Equal(calls, factory.Mock.Calls.Values.Sum());
    }
}
