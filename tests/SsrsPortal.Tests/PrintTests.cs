using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Security;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class PrintTests
{
    [Fact]
    public async Task PrintReusesCompletePdfExportWithoutChangingPageRevisionOrDownloadDisposition()
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var run = await user.Run();
        var page = (await (await user.Post($"/api/executions/{run.Token}/page/2", new { })).Content.ReadFromJsonAsync<PageResult>())!;
        var before = await user.Http.GetStringAsync($"/api/executions/{run.Token}/info");
        using var print = await user.Http.GetAsync($"/api/executions/{run.Token}/print?revision={page.Revision}");
        Assert.Equal(HttpStatusCode.OK, print.StatusCode);
        Assert.Equal("application/pdf", print.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", print.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains(".pdf", print.Content.Headers.ContentDisposition.ToString());
        Assert.Equal("no-store", print.Headers.CacheControl!.ToString());
        var bytes = await print.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
        Assert.Contains("/Count 2", Encoding.ASCII.GetString(bytes));
        Assert.Contains("(Page 1 - Synthetic run", Encoding.ASCII.GetString(bytes));
        Assert.Contains("(Page 2 - Synthetic run", Encoding.ASCII.GetString(bytes));
        var rendered = f.Mock.Observations.Last(o => o.Operation == "Render").Request;
        Assert.Equal("PDF", rendered.Descendants(SyntheticSsrsHandler.Execution + "Format").Single().Value);
        // PDF's empty DeviceInfo requests the full report, not the HTML page/section.
        Assert.Equal("<DeviceInfo/>", rendered.Descendants(SyntheticSsrsHandler.Execution + "DeviceInfo").Single().Value);
        foreach (var format in OutputFormat.All)
        {
            using var download = await user.Http.GetAsync($"/api/executions/{run.Token}/exports/{format.Key}");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(format.Value.Mime, download.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
            if (format.Key == "pdf") { Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync()); }
        }
        Assert.Equal(before, await user.Http.GetStringAsync($"/api/executions/{run.Token}/info"));
    }

    [Fact]
    public async Task AnonymousCrossUserExpiredStaleAndUnsynchronizedSessionsCannotPrint()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); using var other = await f.Login("staff-two");
        var run = await user.Run(); var url = $"/api/executions/{run.Token}/print";
        using var anonymous = f.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await user.Http.GetAsync(url + "?revision=0")).StatusCode);
        var store = f.Services.GetRequiredService<SessionStore>();
        using (var lease = await store.AcquireAsync(run.Token, AuthenticationTests.Tenant + ":00000000-0000-0000-0000-000000000011", default))
        {
            store.InvalidateView(lease.Session);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await user.Http.GetAsync(url)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task CatalogRevocationAndPdfFailuresRemainEnforced()
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var run = await user.Run(); var url = $"/api/executions/{run.Token}/print";
        f.Mock.FailOperation = "Render"; f.Mock.Failure = MockFailure.WrongMime;
        Assert.Equal(HttpStatusCode.BadGateway, (await user.Http.GetAsync(url)).StatusCode);
        f.Mock.Failure = MockFailure.HtmlPdf;
        Assert.Equal(HttpStatusCode.BadGateway, (await user.Http.GetAsync(url)).StatusCode);
        f.Mock.Failure = MockFailure.Fault;
        Assert.Equal(HttpStatusCode.BadGateway, (await user.Http.GetAsync(url)).StatusCode);
        f.Mock.Failure = MockFailure.None;
        f.Mock.CatalogItems = [];
        f.Clock.Advance(TimeSpan.FromSeconds(f.Services.GetRequiredService<PortalOptions>().CatalogSeconds + 1));
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync(url)).StatusCode);
        Assert.DoesNotContain((await user.Http.GetAsync(url)).Headers, h => h.Key == "Content-Disposition");
    }
}
