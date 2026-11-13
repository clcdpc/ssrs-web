using System.Net;
using System.Net.Http.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SsrsPortal.Core;
using SsrsPortal.Pages;
using SsrsPortal.Mock;
using PageResult = Microsoft.AspNetCore.Mvc.RazorPages.PageResult;

namespace SsrsPortal.Tests;

public sealed class CatalogNavigationTests
{
    private static readonly CatalogEntry[] Tree =
    [
        new("root", null, "Training", "Folder"), new("reports", "root", "Reports", "Folder"),
        new("shortcuts", "root", "Shortcuts", "Folder"), new("nested", "reports", "Nested", "Folder"),
        new("a", "reports", "Alpha", "Report"), new("z", "reports", "Zulu", "Report"),
        new("link", "shortcuts", "Outside match", "LinkedReport"), new("deep", "nested", "Deep match", "Report")
    ];

    private static IndexModel Model(Harness h, string? folder = null, string? query = null, bool root = false, bool emptyFolder = false)
    {
        var context = new DefaultHttpContext();
        if (emptyFolder) { context.Request.QueryString = new QueryString("?folder="); }
        return new(h.Catalog) { Folder = folder, Q = query, Root = root, PageContext = new PageContext { HttpContext = context } };
    }

    [Fact]
    public void SingleFolderOpensExactlyOneLevelWithoutRedirectOrSoapCalls()
    {
        using var h = new Harness(); var page = Model(h);
        Assert.IsType<PageResult>(page.Populate(Tree));
        Assert.Equal("root", page.Folder);
        Assert.Equal(new[] { "Reports", "Shortcuts" }, page.Entries.Select(e => e.Name));
        Assert.Equal("Training", Assert.Single(page.Breadcrumbs.Items).Name);
        Assert.True(page.Breadcrumbs.Items[0].IsCurrent);
        Assert.Empty(h.Mock.Calls);
    }

    [Theory]
    [InlineData("Folder")]
    [InlineData("Report")]
    [InlineData("LinkedReport")]
    public void AdditionalRootEntryKeepsRootAccessible(string type)
    {
        using var h = new Harness(); var page = Model(h);
        page.Populate([.. Tree, new("extra", null, "Other", type)]);
        Assert.Null(page.Folder); Assert.Equal(2, page.Entries.Length);
        Assert.Equal(new BreadcrumbItem("All folders", null, true), Assert.Single(page.Breadcrumbs.Items));
        Assert.Contains(page.Entries, e => e.Id == "extra");
    }

    [Fact]
    public void EmptyCatalogHasNoAutomaticFolderAndIdentifiesTheRoot()
    {
        using var h = new Harness(); var page = Model(h);
        Assert.IsType<PageResult>(page.Populate([]));
        Assert.Empty(page.Entries); Assert.Null(page.Folder);
        Assert.Equal(new BreadcrumbItem("All folders", null, true), Assert.Single(page.Breadcrumbs.Items));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitRootNeverReentersTheAutomaticFolder(bool emptyFolder)
    {
        using var h = new Harness(); var page = Model(h, root: !emptyFolder, emptyFolder: emptyFolder);
        page.Populate(Tree);
        Assert.Null(page.Folder); Assert.Equal("root", Assert.Single(page.Entries).Id);
        Assert.Equal(new BreadcrumbItem("All folders", null, true), Assert.Single(page.Breadcrumbs.Items));
    }

    [Theory]
    [InlineData("reports", "root,reports")]
    [InlineData("nested", "root,reports,nested")]
    public void ExplicitFolderHasAnOrderedNonRepeatingParentChain(string folder, string chain)
    {
        using var h = new Harness(); var page = Model(h, folder);
        page.Populate(Tree);
        Assert.Equal(folder, page.Folder);
        Assert.Equal(chain.Split(','), page.Breadcrumbs.Items.Select(e => e.FolderId));
        Assert.Equal(page.Breadcrumbs.Items.Length, page.Breadcrumbs.Items.Select(e => e.FolderId).Distinct().Count());
        Assert.True(page.Breadcrumbs.Items.Last().IsCurrent);
        Assert.All(page.Entries, e => Assert.Equal(folder, e.ParentId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("reports")]
    public void SearchAlwaysIncludesReportsAcrossFolders(string? folder)
    {
        using var h = new Harness(); var page = Model(h, folder, "match");
        page.Populate(Tree);
        Assert.Equal(new[] { "Deep match", "Outside match" }, page.Entries.Select(e => e.Name));
        var cleared = Model(h, query: ""); cleared.Populate(Tree);
        Assert.Equal("root", cleared.Folder);
    }

    [Fact]
    public void FoldersSortBeforeAlphabetizedReportsAndUnknownIdsAreRejected()
    {
        using var h = new Harness(); var page = Model(h, "reports");
        page.Populate(Tree.Reverse().ToArray());
        Assert.Equal(new[] { "Nested", "Alpha", "Zulu" }, page.Entries.Select(e => e.Name));
        Assert.IsType<NotFoundResult>(Model(h, "unknown").Populate(Tree));
        Assert.IsType<NotFoundResult>(Model(h, "a").Populate(Tree));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("nested", false)]
    [InlineData("root", false)]
    [InlineData(null, true)]
    public void TildeFolderPriorityIsConsistentInAutomaticNestedAndExplicitRootViews(string? folder, bool root)
    {
        using var h = new Harness(); var page = Model(h, folder, root: root);
        var parent = root ? null : folder ?? "root";
        CatalogEntry[] choices =
        [
            new("t2", parent, "~Tech Services", "Folder"), new("ordinary2", parent, "circulation", "Folder"),
            new("r2", parent, "Zulu report", "Report"), new("ordinary1", parent, "Administration", "Folder"),
            new("t1", parent, "~Archives", "Folder"), new("r1", parent, "Alpha report", "Report"),
            new("linked", parent, "Beta linked", "LinkedReport"), new("tildeReport", parent, "~Report", "Report"),
            new("space", parent, " ~Not tilde priority", "Folder")
        ];
        page.Populate([.. Tree, .. choices]);
        Assert.Equal(new[] { "~Archives", "~Tech Services", " ~Not tilde priority", "Administration", "circulation" },
            page.Entries.Where(e => choices.Any(c => c.Id == e.Id) && e.Type == "Folder").Select(e => e.Name));
        Assert.Equal(new[] { "~Report", "Alpha report", "Beta linked", "Zulu report" }.Order(StringComparer.OrdinalIgnoreCase),
            page.Entries.Where(e => choices.Any(c => c.Id == e.Id) && e.Type != "Folder").Select(e => e.Name));
        Assert.All(page.Entries.SkipWhile(e => e.Type == "Folder"), e => Assert.NotEqual("Folder", e.Type));
        Assert.Empty(h.Mock.Calls);
    }

    [Fact]
    public void GlobalSearchRemainsAlphabeticalAcrossEveryFolderIncludingTildeReports()
    {
        using var h = new Harness(); var page = Model(h, "reports", "match");
        page.Populate([.. Tree, new("tilde", "shortcuts", "~Match", "Report"), new("aMatch", "nested", "a match", "LinkedReport"),
            new("notReport", "root", "~Matching folder", "Folder")]);
        Assert.Equal(new[] { "Deep match", "Outside match", "~Match", "a match" }.Order(StringComparer.OrdinalIgnoreCase), page.Entries.Select(e => e.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenderedSortingAndFolderReturnUseTheSameAuthorizedCachedSnapshot(bool multipleRoots)
    {
        var settings = multipleRoots ? new Dictionary<string, string?> { ["Portal:AllowedRoots:1"] = "/Other" } : null;
        await using var factory = new PortalFactory(settings);
        if (multipleRoots)
        {
            factory.Mock.CatalogItems = [new("/Training/Reports", "Folder"), new("/Training/Reports/Nested", "Folder"),
                new("/Training/Reports/Nested/Deep report"), new("/Other", "Folder"), new("/Other/Other report")];
        }
        using var user = await factory.Login();
        var catalog = (await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!;
        var name = multipleRoots ? "Deep report" : "Check Ins by Day of Week by Hour";
        var entry = catalog.Single(e => e.Name == name);
        var before = factory.Mock.Calls.Values.Sum();
        var parser = new HtmlParser();
        foreach (var origin in new[] { "/?q=" + Uri.EscapeDataString(name), "/?folder=" + entry.ParentId })
        {
            await user.Http.GetStringAsync(origin);
            // Also works without a Referer or any history in the request.
            var report = parser.ParseDocument(await user.Http.GetStringAsync(entry.ReportUrl + "?path=/inaccessible/forged"));
            Assert.Null(report.QuerySelector(".back"));
            Assert.Equal("/?folder=" + entry.ParentId, report.QuerySelectorAll(".breadcrumbs .breadcrumb-folder a").Last().GetAttribute("href"));
            Assert.Equal("/", report.QuerySelector(".brand")!.GetAttribute("href"));
            Assert.Contains("report-main", report.QuerySelector("main")!.ClassList);
            Assert.NotNull(report.QuerySelector(".workspace-feedback #status"));
            Assert.Null(report.QuerySelector("#status.notice"));
        }
        if (!multipleRoots)
        {
            var sorting = parser.ParseDocument(await user.Http.GetStringAsync("/?folder=" + catalog.Single(e => e.Name == "Sorting fixture").Id));
            Assert.Equal(new[] { "~Archives", "~Tech Services", "Administration", "Circulation", "Alpha sample", "Linked sample", "~Report sample" }.Take(4),
                sorting.QuerySelectorAll(".catalog-name").Take(4).Select(e => e.TextContent));
            Assert.DoesNotContain(catalog, e => e.Name.Contains("Hidden", StringComparison.Ordinal) || e.Name is "Internal" or "Custom_Restored");
        }
        Assert.Equal(before, factory.Mock.Calls.Values.Sum());
    }

    [Fact]
    public void MissingContainingFolderUsesExplicitRootWithoutInventingAnId()
    {
        var root = ReportModel.ContainingFolderRoute(null);
        Assert.Equal("true", root["root"]);
        Assert.False(root.ContainsKey("folder"));
        var folder = ReportModel.ContainingFolderRoute("opaque-parent");
        Assert.Equal("opaque-parent", folder["folder"]);
        Assert.False(folder.ContainsKey("root"));
    }

    [Fact]
    public async Task HiddenExcludedAndUnknownAncestorsNeverProduceReportReturnLinks()
    {
        using var h = new Harness();
        h.Mock.CatalogItems = [new("/Training/Reports", "Folder"), new("/Training/Reports/Visible"),
            new("/Training/Reports/~Hidden", "Folder", Hidden: true), new("/Training/Reports/~Hidden/Secret"),
            new("/Training/Internal", "Folder"), new("/Training/Internal/Blocked"), new("/Training/Missing/Unknown")];
        var entries = await h.Catalog.BrowseAsync(default);
        Assert.DoesNotContain(entries, e => e.Name is "Secret" or "Blocked" or "Unknown" or "~Hidden" or "Internal");
        var report = await h.Report("Visible");
        Assert.Equal(entries.Single(e => e.Name == "Reports").Id, report.ParentId);
    }

    [Fact]
    public async Task RenderedNavigationUsesFilteredCatalogAndExplicitRootLinks()
    {
        await using var factory = new PortalFactory(); using var user = await factory.Login();
        var catalog = (await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!;
        var before = factory.Mock.Calls.Values.Sum();
        var parser = new HtmlParser();
        var page = parser.ParseDocument(await user.Http.GetStringAsync("/"));
        Assert.Equal("Training", page.QuerySelector("[aria-current=page]")!.TextContent);
        Assert.Equal(2, page.QuerySelectorAll(".catalog-row").Length);
        Assert.Null(page.QuerySelector(".intro"));
        Assert.Null(page.QuerySelector(".breadcrumbs a"));
        Assert.NotNull(page.QuerySelector("header.workspace-header .breadcrumbs"));
        Assert.Null(page.QuerySelector("main nav"));
        var root = parser.ParseDocument(await user.Http.GetStringAsync("/?root=true"));
        Assert.Equal("All folders", root.QuerySelector("[aria-current=page]")!.TextContent);
        Assert.Equal("Training", Assert.Single(root.QuerySelectorAll(".catalog-name")).TextContent);
        var id = catalog.Single(e => e.Name == "Reports").Id;
        var folder = parser.ParseDocument(await user.Http.GetStringAsync("/?folder=" + id));
        Assert.Equal("Reports", folder.QuerySelector("[aria-current=page]")!.TextContent);
        foreach (var query in new[] { "Hidden", "System", "Old" })
        {
            var search = parser.ParseDocument(await user.Http.GetStringAsync("/?q=" + query));
            Assert.DoesNotContain(search.QuerySelectorAll(".catalog-name"), e => e.TextContent == query);
        }
        var global = parser.ParseDocument(await user.Http.GetStringAsync("/?folder=" + id + "&q=Holds%20Link"));
        Assert.Contains("Holds Link", global.QuerySelector(".catalog-list")!.TextContent);
        Assert.Equal(before, factory.Mock.Calls.Values.Sum());
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/?folder=unknown")).StatusCode);
    }
}
