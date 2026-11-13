using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.DependencyInjection;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task InitialWorkspaceUsesAuthorizedHeaderAndDoesNotExecute()
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var id = await user.Id("Check Ins by Day of Week by Hour");
        var doc = new HtmlParser().ParseDocument(await user.Http.GetStringAsync((await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!.Single(e => e.Id == id).ReportUrl));
        Assert.Equal("Check Ins by Day of Week by Hour · Staff reports", doc.Title);
        Assert.Equal("Check Ins by Day of Week by Hour", doc.QuerySelector("h1.visually-hidden")!.TextContent);
        Assert.Equal(new[] { "Training", "Reports", "Check Ins by Day of Week by Hour" }, doc.QuerySelectorAll(".breadcrumbs>ol>li>a,.breadcrumbs>ol>li>[aria-current]").Select(n => n.TextContent));
        Assert.Equal("Check Ins by Day of Week by Hour", doc.QuerySelector(".breadcrumbs [aria-current=page]")!.TextContent);
        Assert.Null(doc.QuerySelector(".back,#report-name,#status.notice,.interaction-note"));
        Assert.NotNull(doc.QuerySelector("#parameters #fields"));
        Assert.NotNull(doc.QuerySelector("form#parameters.parameter-grid"));
        Assert.NotNull(doc.QuerySelector("section.parameter-section[aria-label='Report parameters']"));
        Assert.Null(doc.QuerySelector(".parameter-command-row,.parameter-heading,#parameter-toggle-label,.parameter-section h2"));
        Assert.NotNull(doc.QuerySelector(".parameter-section+.parameter-divider+#viewer"));
        Assert.NotNull(doc.QuerySelector(".parameter-divider #toggle-parameters[type=button][aria-label='Hide parameters']"));
        Assert.Null(doc.QuerySelector("#parameters #toggle-parameters,#viewer #toggle-parameters"));
        Assert.Equal("parameters", doc.QuerySelector("#toggle-parameters")!.GetAttribute("aria-controls"));
        Assert.NotNull(doc.QuerySelector("#parameters>#parameter-controls+.report-actions #run[type=submit]"));
        Assert.NotNull(doc.QuerySelector(".report-actions #cancel-run[aria-hidden=true][disabled][type=button]"));
        Assert.NotNull(doc.QuerySelector("#run>.run-label"));
        Assert.NotNull(doc.QuerySelector("#run>.run-pending[aria-hidden=true]>.run-spinner[aria-hidden=true]"));
        Assert.NotNull(doc.QuerySelector("#status.visually-hidden[role=status][aria-live=polite][aria-atomic=true]"));
        Assert.Null(doc.QuerySelector("#parameter-controls button,.workspace-feedback button:not(#resynchronize),#parameter-controls .report-actions"));
        Assert.NotNull(doc.QuerySelector(".workspace-feedback #resynchronize[type=button][hidden]"));
        Assert.NotNull(doc.QuerySelector("#parameters>#dirty[hidden]"));
        Assert.Single(doc.QuerySelectorAll("form#parameters"));
        Assert.Empty(doc.QuerySelectorAll("#report [style],#report style"));
        Assert.NotNull(doc.QuerySelector("#viewer[hidden] iframe[sandbox='allow-same-origin']"));
        Assert.Null(doc.QuerySelector("iframe[src]"));
        Assert.Equal("true", doc.QuerySelector("#toggle-parameters")!.GetAttribute("aria-expanded"));
        Assert.DoesNotContain(f.Mock.Observations, o => o.Operation is "LoadReport" or "Render");
    }

    [Fact]
    public async Task DeepBreadcrumbContainsEveryAuthorizedFolderAndEncodesNames()
    {
        await using var f = new PortalFactory();
        f.Mock.CatalogItems = [new("/Training/Custom", "Folder"), new("/Training/Custom/Regional", "Folder"),
            new("/Training/Custom/Regional/Circulation", "Folder"), new("/Training/Custom/Regional/Circulation/Hourly", "Folder"),
            new("/Training/Custom/Regional/Circulation/Hourly/Long <script> & report")];
        using var user = await f.Login();
        var all = (await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!;
        var id = all.Single(e => e.Type == "Report").Id;
        var doc = new HtmlParser().ParseDocument(await user.Http.GetStringAsync((await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!.Single(e => e.Id == id).ReportUrl));
        Assert.NotNull(doc.QuerySelector(".breadcrumbs details summary[aria-label='More parent folders']"));
        Assert.NotNull(doc.QuerySelector(".breadcrumb-more[hidden]"));
        Assert.Empty(doc.QuerySelectorAll(".breadcrumb-more a"));
        Assert.Equal(all.Count(e => e.Type == "Folder"), doc.QuerySelectorAll(".breadcrumb-list>.breadcrumb-folder:not([hidden])").Length);
        Assert.Equal(all.Where(e => e.Type == "Folder").Select(e => "/?folder=" + e.Id).Order(),
            doc.QuerySelectorAll(".breadcrumbs a").Select(e => e.GetAttribute("href")).Order());
        Assert.Equal("Long <script> & report", doc.QuerySelector("[aria-current=page]")!.GetAttribute("title"));
        Assert.Null(doc.QuerySelector(".breadcrumbs script"));
        Assert.All(doc.QuerySelectorAll(".breadcrumbs a"), e => Assert.DoesNotContain("/Training", e.GetAttribute("href")));
    }

    [Theory]
    [InlineData("/Training", "Training,Reports")]
    [InlineData("/Training/Reports", "Reports")]
    public async Task OnlyAccessibleAncestryIsLinked(string root, string expected)
    {
        using var h = new Harness(o => o.AllowedRoots = [root]);
        var report = await h.Report();
        var nav = await h.Catalog.WorkspaceAsync(report.Id, default);
        Assert.Equal(expected.Split(','), nav.Breadcrumbs.Items.Where(f => !f.IsCurrent).Select(f => f.Name));
        Assert.Equal(nav.Breadcrumbs.Items[^2].FolderId, nav.Report.ParentId);
    }

    [Fact]
    public void RootLevelOrMissingAncestorsHaveNoInventedFolders()
    {
        CatalogEntry[] entries = [new("visible", "unavailable", "Visible", "Folder")];
        Assert.Empty(BreadcrumbNavigation.Ancestors(entries, null));
        Assert.Equal("Visible", Assert.Single(BreadcrumbNavigation.Ancestors(entries, "visible")).Name);
        Assert.Empty(BreadcrumbNavigation.Ancestors(entries, "unavailable"));
    }

    [Fact]
    public async Task RefreshAndLegacyEntryRestoreOnlyTheOwnersExecutedValuesWithoutRerunning()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); using var other = await f.Login("staff-two");
        var input = Harness.Inputs("2");
        input.Values!["start"] = ["2026-10-30T09:15:23.1234567"];
        var run = await user.Run(input: input);
        var before = f.Mock.Calls.Values.Sum();
        var legacy = await user.Http.GetAsync("/Viewer?token=" + run.Token);
        Assert.Equal(HttpStatusCode.Redirect, legacy.StatusCode);
        Assert.StartsWith("/Report?id=", legacy.Headers.Location!.OriginalString);
        Assert.Contains("&token=" + run.Token, legacy.Headers.Location.OriginalString);
        var html = await user.Http.GetStringAsync(legacy.Headers.Location);
        Assert.Contains("data-token=\"" + run.Token + "\"", html);
        var info = await user.Http.GetFromJsonAsync<JsonElement>("/api/executions/" + run.Token + "/info");
        var restored = info.GetProperty("input").Deserialize<ParameterInput>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("2", restored.Values!["library"].Single());
        Assert.Equal("2026-10-30T09:15:23.1234567", restored.Values["start"].Single());
        Assert.False(restored.Values.ContainsKey("internalMode"));
        Assert.Null(restored.ReplaceToken);
        Assert.Equal(before, f.Mock.Calls.Values.Sum());
        var denied = await other.Http.GetAsync("/Viewer?token=" + run.Token);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Null(denied.Headers.Location);
        Assert.DoesNotContain("Holds Ratio", await denied.Content.ReadAsStringAsync());
        var wrongReport = await user.Id("Details");
        var wrong = await user.Http.GetStringAsync("/Report?id=" + wrongReport + "&token=" + run.Token);
        Assert.DoesNotContain("data-token=\"" + run.Token, wrong);
        Assert.Contains("expired or is unavailable", wrong);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("cancelled")]
    public async Task InvalidCompatibilityLinksFailPredictablyAndReportUrlsRecoverDefaults(string kind)
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var id = await user.Id(); var run = await user.Run();
        if (kind == "expired") { f.Clock.Advance(TimeSpan.FromMinutes(16)); }
        if (kind == "cancelled") { await user.Post("/api/executions/" + run.Token + "/cancel", new { }); }
        var token = kind == "missing" ? "unknown" : run.Token;
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/Viewer?token=" + token)).StatusCode);
        var before = f.Mock.Observations.Count(o => o.Operation == "LoadReport");
        var html = await user.Http.GetStringAsync("/Report?id=" + id + "&token=" + token);
        Assert.Contains("data-token=\"\"", html);
        Assert.Contains("expired or is unavailable", html);
        Assert.Equal(before, f.Mock.Observations.Count(o => o.Operation == "LoadReport"));
    }

    [Fact]
    public async Task ReplacementsWorkAtBothSessionCountLimitsAndCommitIsIdempotent()
    {
        using var h = new Harness(o => { o.MaxSessionsPerUser = 1; o.MaxSessions = 1; });
        var report = await h.Report();
        var active = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default);
        for (var i = 0; i < 8; i++)
        {
            var next = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs("2") with { ReplaceToken = active.Token }, default);
            Assert.Equal(2, h.Sessions.Snapshot().Count);
            using (var previous = await h.Sessions.AcquireAsync(active.Token, "owner", default))
            {
                Assert.Contains("Page 1", previous.Session.Html);
                Assert.NotEmpty((await h.Execution.ExportAsync(previous, OutputFormat.All["pdf"])).Bytes);
            }
            h.Sessions.Commit(next.Token, "owner"); h.Sessions.Commit(next.Token, "owner");
            Assert.Equal(1, h.Sessions.Snapshot().Count);
            await Assert.ThrowsAsync<PortalException>(() => h.Sessions.AcquireAsync(active.Token, "owner", default));
            active = next;
        }
        h.Sessions.Cancel(active.Token, "owner"); Assert.Equal((0, 0L), h.Sessions.Snapshot());
    }

    [Theory]
    [InlineData("LoadReport")]
    [InlineData("SetExecutionParameters")]
    [InlineData("Render")]
    [InlineData("GetExecutionInfo")]
    public async Task FailedReplacementReleasesReservationAndPreservesPreviousReportAndExports(string operation)
    {
        using var h = new Harness(o => o.MaxSessionsPerUser = 1); var report = await h.Report();
        var active = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default);
        var before = h.Sessions.Snapshot();
        h.Mock.FailOperation = operation; h.Mock.Failure = MockFailure.Fault;
        await Assert.ThrowsAsync<PortalException>(() => h.Execution.RunAsync("owner", report.Id, Harness.Inputs("2") with { ReplaceToken = active.Token }, default));
        Assert.Equal(before, h.Sessions.Snapshot());
        h.Mock.Failure = MockFailure.None;
        using (var lease = await h.Sessions.AcquireAsync(active.Token, "owner", default))
        {
            Assert.Equal("1", lease.Session.Input.Values!["library"].Single());
            Assert.Equal(2, (await h.Execution.PageAsync(lease, 2)).Page);
            foreach (var format in OutputFormat.All.Values) { Assert.NotEmpty((await h.Execution.ExportAsync(lease, format)).Bytes); }
        }
        var next = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs("2") with { ReplaceToken = active.Token }, default);
        h.Sessions.Commit(next.Token, "owner"); Assert.Equal(1, h.Sessions.Snapshot().Count);
    }

    [Fact]
    public async Task ReplacementReservationIsExclusiveCannotChainAndKeepsByteBudget()
    {
        using var h = new Harness(); var report = await h.Report();
        var active = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default);
        var next = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs() with { ReplaceToken = active.Token }, default);
        Assert.Equal("capacity", Assert.Throws<PortalException>(() => h.Sessions.Reserve("owner", report, active.Token)).Code);
        Assert.Equal("session", Assert.Throws<PortalException>(() => h.Sessions.Reserve("owner", report, next.Token)).Code);
        Assert.Equal("session", Assert.Throws<PortalException>(() => h.Sessions.Reserve("other", report, active.Token)).Code);
        h.Sessions.CancelReplacement(active.Token, "other"); Assert.Equal(2, h.Sessions.Snapshot().Count);
        h.Sessions.CancelReplacement(active.Token, "owner"); Assert.Equal(1, h.Sessions.Snapshot().Count);
        h.Options.MaxAggregateSessionBytes = h.Sessions.Snapshot().Bytes;
        Assert.Equal("capacity", Assert.Throws<PortalException>(() => h.Sessions.Reserve("owner", report, active.Token)).Code);
        h.Sessions.Cancel(active.Token, "owner"); Assert.Equal((0, 0L), h.Sessions.Snapshot());
    }

    [Fact]
    public async Task AbortedReplacementCleansUpAndDoesNotInterruptThePreviousSession()
    {
        using var h = new Harness(o => o.MaxSessionsPerUser = 1); var report = await h.Report();
        var active = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default);
        var before = h.Sessions.Snapshot();
        h.Mock.FailOperation = "Render"; h.Mock.Failure = MockFailure.Slow; h.Mock.DelayMilliseconds = 10000;
        using var stop = new CancellationTokenSource();
        var pending = h.Execution.RunAsync("owner", report.Id, Harness.Inputs() with { ReplaceToken = active.Token }, stop.Token);
        for (var i = 0; h.Sessions.Snapshot().Count < 2 && i < 200; i++) { await Task.Delay(10); }
        Assert.Equal(2, h.Sessions.Snapshot().Count);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(before, h.Sessions.Snapshot());
        h.Mock.Failure = MockFailure.None;
        using var lease = await h.Sessions.AcquireAsync(active.Token, "owner", default);
        Assert.NotEmpty((await h.Execution.ExportAsync(lease, OutputFormat.All["csv"])).Bytes);
    }

    [Fact]
    public async Task PromotionSafelyReleasesAnOldLeaseAndWrongReportsCannotReplaceIt()
    {
        using var h = new Harness(); var report = await h.Report();
        var active = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default);
        var other = await h.Report("Details");
        Assert.Equal("session", Assert.Throws<PortalException>(() => h.Sessions.Reserve("owner", other, active.Token)).Code);
        var next = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs() with { ReplaceToken = active.Token }, default);
        using (var lease = await h.Sessions.AcquireAsync(active.Token, "owner", default))
        {
            h.Sessions.Commit(next.Token, "owner");
            Assert.True(lease.Cancellation.IsCancellationRequested);
            Assert.Equal(1, h.Sessions.Snapshot().Count);
        }
        h.Sessions.Cancel(next.Token, "owner"); Assert.Equal((0, 0L), h.Sessions.Snapshot());
    }

    [Fact]
    public async Task PolicyRevocationRemovesBothMembersOfAReplacementPair()
    {
        using var h = new Harness(); var report = await h.Report();
        var active = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default);
        var next = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs() with { ReplaceToken = active.Token }, default);
        h.Options.ExcludedPrefixes = [.. h.Options.ExcludedPrefixes, report.Path];
        Assert.Equal((0, 0L), h.Sessions.Snapshot());
        Assert.Equal("session", Assert.Throws<PortalException>(() => h.Sessions.Commit(next.Token, "owner")).Code);
    }

    [Fact]
    public async Task FailedOldCancellationCallbackCannotInvalidatePromotionOrLeakCapacity()
    {
        using var h = new Harness(); var report = await h.Report();
        var active = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default);
        var next = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs() with { ReplaceToken = active.Token }, default);
        using (var old = await h.Sessions.AcquireAsync(active.Token, "owner", default))
        {
            using var callback = old.Cancellation.Register(() => throw new InvalidOperationException("Synthetic callback detail must stay private"));
            h.Sessions.Commit(next.Token, "owner");
            Assert.True(old.Cancellation.IsCancellationRequested);
        }
        using (var lease = await h.Sessions.AcquireAsync(next.Token, "owner", default))
        {
            Assert.NotEmpty((await h.Execution.ExportAsync(lease, OutputFormat.All["csv"])).Bytes);
        }
        h.Sessions.Cancel(next.Token, "owner"); Assert.Equal((0, 0L), h.Sessions.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingOrExpiringSourceAlsoReleasesPendingSession(bool expire)
    {
        using var h = new Harness(); var report = await h.Report();
        var active = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs(), default);
        var next = await h.Execution.RunAsync("owner", report.Id, Harness.Inputs() with { ReplaceToken = active.Token }, default);
        if (expire) { h.Clock.Advance(TimeSpan.FromMinutes(16)); h.Sessions.Sweep(); }
        else { h.Sessions.Cancel(active.Token, "owner"); }
        Assert.Equal((0, 0L), h.Sessions.Snapshot());
        Assert.Equal("session", Assert.Throws<PortalException>(() => h.Sessions.Commit(next.Token, "owner")).Code);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("replacement/cancel")]
    public async Task NewMutationsRequireStaffCsrfAndOwnership(string operation)
    {
        await using var f = new PortalFactory(); using var one = await f.Login(); using var two = await f.Login("staff-two");
        var active = await one.Run();
        var next = await one.Run(input: Harness.Inputs() with { ReplaceToken = active.Token });
        var token = operation == "commit" ? next.Token : active.Token;
        var url = "/api/executions/" + token + "/" + operation;
        using var anonymous = f.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(url, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await one.Http.PostAsJsonAsync(url, new { })).StatusCode);
        var denied = await two.Post(url, new { });
        Assert.Equal(operation == "commit" ? HttpStatusCode.NotFound : HttpStatusCode.NoContent, denied.StatusCode);
        Assert.Equal(2, f.Services.GetRequiredService<SessionStore>().Snapshot().Count);
        Assert.Equal(HttpStatusCode.NoContent, (await one.Post(url, new { })).StatusCode);
        Assert.Equal(1, f.Services.GetRequiredService<SessionStore>().Snapshot().Count);
    }

    [Fact]
    public async Task PendingExecutionCanRecoverThroughTheWorkspaceWithoutAnotherSoapExecution()
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var active = await user.Run();
        var pending = await user.Run(input: Harness.Inputs("2") with { ReplaceToken = active.Token });
        var before = f.Mock.Calls.Values.Sum();
        var legacy = await user.Http.GetAsync("/Viewer?token=" + pending.Token);
        Assert.Equal(HttpStatusCode.Redirect, legacy.StatusCode);
        var html = await user.Http.GetStringAsync(legacy.Headers.Location);
        Assert.Contains("data-token=\"" + pending.Token + "\"", html);
        var info = await user.Http.GetFromJsonAsync<JsonElement>("/api/executions/" + pending.Token + "/info");
        Assert.True(info.GetProperty("pending").GetBoolean());
        Assert.Equal(active.Token, info.GetProperty("sourceToken").GetString());
        Assert.Equal(before, f.Mock.Calls.Values.Sum());
        Assert.Equal(HttpStatusCode.NoContent, (await user.Post("/api/executions/" + pending.Token + "/commit", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/api/executions/" + active.Token + "/viewer")).StatusCode);
    }
}
