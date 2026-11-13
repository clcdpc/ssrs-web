using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SsrsPortal.Controllers;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;
using SsrsPortal.Soap;
namespace SsrsPortal.Tests;

public sealed class ExecutionTests
{
    [Theory]
    [InlineData("/api/executions/{token}/viewer")]
    [InlineData("/API/Executions/{token}/ViEwEr")]
    [InlineData("/api/EXECUTIONS/{token}/VIEWER/")]
    public async Task ViewerPolicyUsesMatchedRouteForAllCasingVariants(string route)
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await user.Run();
        using var response = await user.Http.GetAsync(route.Replace("{token}", run.Token, StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'none'", csp); Assert.Contains("sandbox allow-same-origin", csp); Assert.Contains("frame-ancestors 'self'", csp);
    }

    [Theory]
    [InlineData("/unrelated/viewer")]
    [InlineData("/api/reports/x/viewer")]
    [InlineData("/api/executions/x/streams/viewer")]
    [InlineData("/Viewer")]
    public async Task NonViewerRoutesKeepPortalFrameAndScriptPolicy(string route)
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        using var response = await user.Http.GetAsync(route);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp); Assert.Contains("frame-ancestors 'none'", csp); Assert.DoesNotContain("sandbox", csp);
    }

    [Fact]
    public async Task TwoUsersSameReportHaveDistinctExecutionsAndNoCrossUserPageStreamExportAccess()
    {
        await using var f = new PortalFactory(); using var one = await f.Login(); using var two = await f.Login("staff-two");
        var both = await Task.WhenAll(one.Run(), two.Run()); var first = both[0]; var second = both[1];
        Assert.NotEqual(first.Token, second.Token);
        var loads = f.Mock.Observations.Where(o => o.Operation == "LoadReport").ToArray(); Assert.Equal(2, loads.Length);
        var html1 = await one.Http.GetStringAsync("/api/executions/" + first.Token + "/viewer");
        var html2 = await two.Http.GetStringAsync("/api/executions/" + second.Token + "/viewer");
        Assert.NotEqual(html1, html2); Assert.DoesNotContain("synthetic-execution", html1);
        foreach (var route in new[] { "/info", "/viewer", "/exports/pdf", "/streams/" + StreamTokens(html1).First() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await two.Http.GetAsync("/api/executions/" + first.Token + route)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await two.Post("/api/executions/" + first.Token + "/page/2", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await two.Post("/api/executions/" + first.Token + "/cancel", new { })).StatusCode);
        await Task.WhenAll(one.Post("/api/executions/" + first.Token + "/page/2", new { }), two.Post("/api/executions/" + second.Token + "/page/2", new { }));
        Assert.Contains("Page 2", await one.Http.GetStringAsync("/api/executions/" + first.Token + "/viewer"));
        Assert.Contains("Page 2", await two.Http.GetStringAsync("/api/executions/" + second.Token + "/viewer"));
        Assert.NotEqual(await one.Http.GetStringAsync("/api/executions/" + first.Token + "/exports/csv"), await two.Http.GetStringAsync("/api/executions/" + second.Token + "/exports/csv"));
        Assert.Equal(2, f.Mock.Observations.Where(o => o.Operation == "Render").Select(o => o.ExecutionId).Distinct().Count());
    }

    [Fact]
    public async Task TwoPageViewerHasNarrowCspAndTwoAuthenticatedNonemptyOpaqueStreams()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await user.Run("Holds Link");
        Assert.Equal(2, run.Pages); var response = await user.Http.GetAsync("/api/executions/" + run.Token + "/viewer");
        var html = await response.Content.ReadAsStringAsync(); Assert.Contains("Page 1", html); Assert.Contains("Synthetic subreport details", html);
        Assert.DoesNotContain("<script", html); Assert.DoesNotContain("onclick", html); Assert.DoesNotContain("javascript:", html);
        Assert.DoesNotContain("https://synthetic", html); Assert.DoesNotContain("untrusted", html); Assert.DoesNotContain("<iframe", html);
        Assert.Contains("script-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("sandbox allow-same-origin", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var streams = StreamTokens(html); Assert.Equal(2, streams.Length);
        var mimes = new List<string>();
        foreach (var stream in streams)
        {
            using var asset = await user.Http.GetAsync("/api/executions/" + run.Token + "/streams/" + stream);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode); Assert.NotEmpty(await asset.Content.ReadAsByteArrayAsync()); mimes.Add(asset.Content.Headers.ContentType!.MediaType!);
            if (asset.Content.Headers.ContentType.MediaType == "text/css")
            {
                Assert.DoesNotContain("url(", await asset.Content.ReadAsStringAsync()); Assert.DoesNotContain("behavior", await asset.Content.ReadAsStringAsync());
            }
        }
        Assert.Contains("image/png", mimes); Assert.Contains("text/css", mimes);
        var streamHeaders = f.Mock.Observations.Where(o => o.Operation == "RenderStream").Select(o => o.ExecutionId).Distinct().ToArray(); Assert.Single(streamHeaders);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/api/executions/" + run.Token + "/streams/chart")).StatusCode);
        var page2 = await user.Post("/api/executions/" + run.Token + "/page/2", new { }); page2.EnsureSuccessStatusCode();
        Assert.Contains("Page 2", await user.Http.GetStringAsync("/api/executions/" + run.Token + "/viewer"));
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Post("/api/executions/" + run.Token + "/page/3", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await user.Post("/api/executions/" + run.Token + "/toggle/anything", new { })).StatusCode);
        var legacy = await user.Http.GetAsync("/Viewer?token=" + run.Token);
        Assert.Equal(HttpStatusCode.Redirect, legacy.StatusCode);
        var shell = await user.Http.GetStringAsync(legacy.Headers.Location);
        Assert.Contains("sandbox=\"allow-same-origin\"", shell);
        Assert.DoesNotContain("Expand/collapse", shell); Assert.DoesNotContain("unavailable in this version", shell);
    }

    [Fact]
    public async Task UnknownPageCountRemainsUnknownWhileNextPageWorks()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); f.Mock.UnknownPageCount = true;
        var run = await user.Run(); Assert.Null(run.Pages);
        var response = await user.Post("/api/executions/" + run.Token + "/page/2", new { }); response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<PageResult>())!; Assert.Equal(2, result.Page); Assert.Null(result.Pages);
        Assert.DoesNotContain(f.Mock.Observations, o => o.Operation == "Render" && o.Request.Descendants(SoapTransport.Execution + "Format").Any(x => x.Value == "PDF"));
    }

    [Theory]
    [InlineData(MockFailure.Empty, "empty")]
    [InlineData(MockFailure.WrongMime, "resource")]
    [InlineData(MockFailure.Fault, "service")]
    public async Task ResourceFailuresRemainBoundedAndSafe(MockFailure failure, string code)
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await user.Run();
        var html = await user.Http.GetStringAsync("/api/executions/" + run.Token + "/viewer");
        f.Mock.Failure = failure; f.Mock.FailOperation = "RenderStream";
        var response = await user.Http.GetAsync("/api/executions/" + run.Token + "/streams/" + StreamTokens(html).First());
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); Assert.Contains(code, text); Assert.DoesNotContain("private", text);
    }

    [Theory]
    [InlineData("pdf", "application/pdf")]
    [InlineData("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("csv", "text/csv")]
    public async Task ExportHasCorrectMimeBytesDispositionAndSecurityHeaders(string format, string mime)
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await user.Run();
        var response = await user.Http.GetAsync("/api/executions/" + run.Token + "/exports/" + format);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal(mime, response.Content.Headers.ContentType!.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync(); Assert.NotEmpty(bytes);
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength); Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.EndsWith("." + format, response.Content.Headers.ContentDisposition.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString()); Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        if (format == "pdf")
        {
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes));
        }
        if (format is "xlsx" or "docx")
        {
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes)); Assert.NotEmpty(zip.Entries);
        }
    }

    [Theory]
    [InlineData(MockFailure.Empty, "empty")]
    [InlineData(MockFailure.LargeOutput, "limit")]
    [InlineData(MockFailure.WrongMime, "format")]
    [InlineData(MockFailure.Fault, "service")]
    public async Task ExportEmptyOversizedWrongMimeAndFaultReturnSafeErrors(MockFailure failure, string code)
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await user.Run();
        f.Mock.FailOperation = "Render"; f.Mock.Failure = failure;
        foreach (var format in new[] { "pdf", "xlsx", "docx", "csv" })
        {
            var response = await user.Http.GetAsync("/api/executions/" + run.Token + "/exports/" + format);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode); var message = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"code\":\"" + code + "\"", message); Assert.DoesNotContain("private", message); Assert.DoesNotContain("faultstring", message);
            Assert.Null(response.Content.Headers.ContentDisposition);
        }
        // HTML5 has its own MIME policy and must also release a failed initial-render reservation.
        var html = await user.Post("/api/reports/" + await user.Id() + "/run", Harness.Inputs());
        Assert.Equal(HttpStatusCode.BadGateway, html.StatusCode);
        Assert.Contains("\"code\":\"" + (failure == MockFailure.WrongMime ? "resource" : code) + "\"", await html.Content.ReadAsStringAsync());
        Assert.Equal(1, f.Services.GetRequiredService<SessionStore>().Snapshot().Count);
        f.Mock.Failure = MockFailure.None;
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Http.GetAsync("/api/executions/" + run.Token + "/exports/HTML5")).StatusCode);
    }

    [Fact]
    public async Task SessionCancelInterruptsExportAndReleasesAllCapacity()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await user.Run();
        f.Mock.FailOperation = "Render"; f.Mock.Failure = MockFailure.Slow; f.Mock.DelayMilliseconds = 10000;
        var export = user.Http.GetAsync("/api/executions/" + run.Token + "/exports/pdf");
        await WaitUntil(() => f.Mock.ActiveCalls > 0);
        Assert.Equal(HttpStatusCode.NoContent, (await user.Post("/api/executions/" + run.Token + "/cancel", new { })).StatusCode);
        var response = await export; Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, f.Mock.ActiveCalls); Assert.Equal((0, 0L), f.Services.GetRequiredService<SessionStore>().Snapshot());
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/api/executions/" + run.Token + "/viewer")).StatusCode);
    }

    [Fact]
    public async Task AbortedExportDoesNotCorruptExecutionAndFailedRunReservationIsRemoved()
    {
        using var h = new Harness(); var report = await h.Report(); var run = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default);
        h.Mock.Failure = MockFailure.Slow; h.Mock.FailOperation = "Render";
        using (var abort = new CancellationTokenSource(50))
        {
            using var lease = await h.Sessions.AcquireAsync(run.Token, "owner", abort.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Execution.ExportAsync(lease, OutputFormat.All["pdf"]));
        }
        h.Mock.Failure = MockFailure.None;
        using (var lease = await h.Sessions.AcquireAsync(run.Token, "owner", default))
        {
            Assert.Equal(2, (await h.Execution.PageAsync(lease, 2)).Page);
        }
        h.Sessions.Cancel(run.Token, "owner");
        h.Mock.Failure = MockFailure.Fault; h.Mock.FailOperation = "LoadReport";
        await Assert.ThrowsAsync<PortalException>(() => h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default));
        Assert.Equal((0, 0L), h.Sessions.Snapshot());
    }

    [Fact]
    public async Task SameSessionConcurrentPagesAndExportsSerializeState()
    {
        using var h = new Harness(); var report = await h.Report(); var run = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default);
        h.Mock.ResetConcurrencyPeak();
        h.Mock.Failure = MockFailure.Slow; h.Mock.FailOperation = "Render"; h.Mock.DelayMilliseconds = 30;
        var tasks = Enumerable.Range(0, 6).Select(async i =>
        {
            using var lease = await h.Sessions.AcquireAsync(run.Token, "owner", default);
            if (i % 2 == 0)
            {
                await h.Execution.PageAsync(lease, i == 0 ? 1 : 2);
            }
            else
            {
                Assert.NotEmpty((await h.Execution.ExportAsync(lease, OutputFormat.All["csv"])).Bytes);
            }
        });
        await Task.WhenAll(tasks);
        using var final = await h.Sessions.AcquireAsync(run.Token, "owner", default);
        Assert.Equal(2, final.Session.Page); Assert.Contains("Page 2", final.Session.Html); Assert.Equal(2, final.Session.Streams.Count);
        Assert.Equal(1, h.Mock.MaxActiveCalls);
        Assert.Single(h.Mock.Observations.Where(o => o.Operation == "Render").Select(o => o.ExecutionId).Distinct());
    }

    [Fact]
    public async Task ExpiryPerUserCountsAndAggregateSizeAreBounded()
    {
        using var h = new Harness(); var report = await h.Report(); h.Options.MaxSessionsPerUser = 1;
        var s = h.Sessions.Reserve("one", report);
        Assert.Equal("capacity", Assert.Throws<PortalException>(() => h.Sessions.Reserve("one", report)).Code);
        var two = h.Sessions.Reserve("two", report); Assert.NotEqual(s.Token, two.Token);
        h.Clock.Advance(TimeSpan.FromMinutes(16)); Assert.Equal((0, 0L), h.Sessions.Snapshot());
        await Assert.ThrowsAsync<PortalException>(() => h.Sessions.AcquireAsync(s.Token, "one", default));
        var fresh = h.Sessions.Reserve("one", report); h.Options.MaxSessionBytes = 4096;
        Assert.Equal("limit", Assert.Throws<PortalException>(() => h.Sessions.SetPage(fresh, "excess", [], 1, 2)).Code);
        h.Options.MaxAggregateSessionBytes = 4096;
        Assert.Equal("capacity", (await Assert.ThrowsAsync<PortalException>(() => h.Sessions.AcquireAsync(fresh.Token, "one", default))).Code);
        h.Sessions.Cancel(fresh.Token, "one"); Assert.Equal((0, 0L), h.Sessions.Snapshot());
    }

    [Fact]
    public async Task WholeParameterRequestDeadlineIsBoundedAndMalformedInputDoesNotReflectValues()
    {
        await using var f = new PortalFactory(new() { ["Portal:RequestSeconds"] = "1" }); using var user = await f.Login(); var id = await user.Id();
        f.Mock.Failure = MockFailure.Slow; f.Mock.FailOperation = "GetItemParameters"; f.Mock.DelayMilliseconds = 400;
        // An uninitialized cascade requires another metadata wave; an explicit []
        // now correctly remains cleared and no longer triggers that extra call.
        var input = Harness.Inputs(); input.Values!.Remove("collections");
        var response = await user.Post("/api/reports/" + id + "/parameters", input);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains("timeout", await response.Content.ReadAsStringAsync());
        f.Mock.Failure = MockFailure.None;
        var malformed = await user.Post("/api/reports/" + id + "/parameters", new { values = new { note = "SENSITIVE_RAW_INPUT" } });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode); Assert.DoesNotContain("SENSITIVE_RAW_INPUT", await malformed.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("../../evil\r\nX-Hack: yes", "evilX-Hack yes")]
    [InlineData("???/", "report")]
    public void ExportFilenameRemovesPathsHeaderInjectionAndUnsupportedCharacters(string name, string expected) => Assert.Equal(expected, ReportsController.SafeFilename(name));

    private static string[] StreamTokens(string html) => Regex.Matches(html, @"/streams/([0-9a-f]{64})").Select(m => m.Groups[1].Value).Distinct().ToArray();
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The expected asynchronous mock request did not start.");
    }
}
