using System.Net;
using System.Net.Http.Json;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.DependencyInjection;
using SsrsPortal.Core;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class ReportUrlTests
{
    [Theory]
    [InlineData("/Training/Reports/Nested report")]
    [InlineData("/Polaris/Custom_Restored/~Tech Services/Holds Ratio Report")]
    [InlineData("/Root report")]
    [InlineData("/Training/日本語/Å report")]
    [InlineData("/Training/100% + %2F/What? # & 'report'")]
    public async Task CatalogNamesRoundTripThroughHttpAndRemainStableAcrossProcessKeys(string path)
    {
        string? url = null, oldId = null;
        for (var process = 0; process < 2; process++)
        {
            await using var factory = new PortalFactory(new() { ["Portal:AllowedRoots:0"] = "/" });
            var parts = path.Split('/');
            factory.Mock.CatalogItems = [.. Enumerable.Range(1, parts.Length - 2)
                .Select(n => new SsrsPortal.Mock.SyntheticCatalogItem(string.Join('/', parts.Take(n + 1)), "Folder")), new(path)];
            using var user = await factory.Login();
            if (process == 1)
            {
                // No browse request has initialized this host's catalog. A copied
                // location must be independently usable after a process/key change.
                Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync(url)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/Report?id=" + oldId)).StatusCode);
            }
            var report = Assert.Single((await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!, e => e.Type == "Report");
            if (process == 0) { url = report.ReportUrl; oldId = report.Id; }
            else { Assert.NotEqual(oldId, report.Id); Assert.Equal(url, report.ReportUrl); }
            Assert.Equal(path, ReportUrl.ParsePath(url!));
            using var response = await user.Http.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var doc = new HtmlParser().ParseDocument(await response.Content.ReadAsStringAsync());
            Assert.Equal(url, doc.QuerySelector("#report")!.GetAttribute("data-canonical-url"));
            var search = new HtmlParser().ParseDocument(await user.Http.GetStringAsync("/?q=" + Uri.EscapeDataString(parts[^1])));
            Assert.Equal(url, Assert.Single(search.QuerySelectorAll("a.catalog-row")).GetAttribute("href"));
            Assert.DoesNotContain(factory.Mock.Observations, o => o.Operation is "GetItemParameters" or "LoadReport");
        }
    }

    [Fact]
    public async Task LinkedReportsUseTheirOwnCatalogPathAndLongPathsAreBounded()
    {
        using var h = new Harness();
        var link = await h.Catalog.ResolvePathAsync("/Training/Shortcuts/Holds Link", default);
        Assert.Equal("/report/Training/Shortcuts/Holds%20Link", ReportUrl.For(link));
        var warmCalls = h.Mock.Calls.Values.Sum();
        for (var i = 0; i < 10; i++) { await h.Catalog.ResolvePathAsync(link.Path, default); }
        Assert.Equal(warmCalls, h.Mock.Calls.Values.Sum());
        var path = "/Training/" + new string('a', 490);
        CatalogCacheTests.Graph(h, path);
        h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1));
        Assert.Equal(path, (await h.Catalog.ResolvePathAsync(ReportUrl.ParsePath("/report/Training/" + new string('a', 490)), default)).Path);
        Assert.Throws<PortalException>(() => ReportUrl.ParsePath("/report/Training/" + new string('a', 513)));
    }

    [Theory]
    [InlineData("/report/Training//Report")]
    [InlineData("/report/Training/Report/")]
    [InlineData("/report/Training/../Report")]
    [InlineData("/report/Training/%2e%2e/Report")]
    [InlineData("/report/Training/%2FReport")]
    [InlineData("/report/Training/%5cReport")]
    [InlineData("/report/Training/%")]
    [InlineData("/report/Training/%2")]
    [InlineData("/report/Training/%GG")]
    [InlineData("/report/Training/%c0%af")]
    [InlineData("/report/Training/%FF")]
    [InlineData("/report/Training/%00")]
    [InlineData("/report/")]
    public void RejectsRawAmbiguousPathsBeforeResolution(string path) =>
        Assert.Equal("notfound", Assert.Throws<PortalException>(() => ReportUrl.ParsePath(path)).Code);

    [Theory]
    [InlineData("/Training/Reports/Unknown")]
    [InlineData("/Training/Internal/System")]
    [InlineData("/Training/Custom_Restored/Hidden")]
    [InlineData("/Training/Reports")]
    [InlineData("/Training/Hidden report")]
    [InlineData("/Training/Hidden folder/Child")]
    public async Task ExcludedAndNonReportPathsNeverReachParameterOrExecutionSoap(string path)
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        f.Mock.CatalogItems = [new("/Training/Reports", "Folder"), new("/Training/Reports/Visible"),
            new("/Training/Hidden report", Hidden: true), new("/Training/Hidden folder", "Folder", Hidden: true), new("/Training/Hidden folder/Child")];
        var url = "/report/" + string.Join('/', path[1..].Split('/').Select(Uri.EscapeDataString));
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync(url)).StatusCode);
        Assert.DoesNotContain(f.Mock.Observations, o => o.Operation is "GetItemParameters" or "LoadReport");
    }

    [Fact]
    public async Task RemovalPolicyChangeAndLegacyLinksRevalidateTheAuthorizedSnapshot()
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var report = (await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!.Single(e => e.Name == "Holds Ratio");
        var legacy = await user.Http.GetAsync("/Report?id=" + report.Id);
        Assert.Equal(HttpStatusCode.Redirect, legacy.StatusCode);
        Assert.Equal(report.ReportUrl, legacy.Headers.Location!.OriginalString);
        var legacyQuery = await user.Http.GetAsync("/Report?id=" + report.Id + "&library=1&Library=2&portal%3AnoExecute=true");
        Assert.Equal(report.ReportUrl + "?library=1&Library=2&portal%3AnoExecute=true", legacyQuery.Headers.Location!.OriginalString);
        var differentCase = await user.Http.GetAsync(report.ReportUrl!.ToLowerInvariant());
        Assert.Equal(report.ReportUrl, differentCase.Headers.Location!.OriginalString);
        f.Mock.CatalogItems = [];
        var options = f.Services.GetRequiredService<PortalOptions>();
        f.Clock.Advance(TimeSpan.FromSeconds(options.CatalogSeconds + 1));
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync(report.ReportUrl)).StatusCode);
        f.Mock.CatalogItems = null;
        options.ExcludedPrefixes = ["/Training/Reports"];
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync(report.ReportUrl)).StatusCode);
        Assert.DoesNotContain(f.Mock.Observations, o => o.Operation == "LoadReport");
    }
}
