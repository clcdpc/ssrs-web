using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class ToggleExecutionTests
{
    [Fact]
    public async Task BrowserCompatibleActionScriptControlsToggleOriginalRuntimeIdAndRerenderSameExecution()
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var run = await Run(user);
        var initialRender = f.Mock.Observations.Last(o => o.Operation == "Render");
        var source = System.Xml.Linq.XElement.Parse(initialRender.Request.Descendants().Single(e => e.Name.LocalName == "DeviceInfo").Value);
        Assert.Matches("^SsrsPortalAction_[0-9a-f]{64}$", source.Element("ActionScript")!.Value);
        Assert.StartsWith("Mozilla/5.0 ", initialRender.UserAgent!);
        var before = f.Mock.Observations.Length;
        await Toggle(user, run.Token, "Library");
        Assert.Contains("Branch East", await Html(user, run.Token));
        await Toggle(user, run.Token, "Library");
        Assert.DoesNotContain("Branch East", await Html(user, run.Token));
        var operations = f.Mock.Observations.Skip(before).ToArray();
        var toggles = operations.Where(o => o.Operation == "ToggleItem").ToArray();
        Assert.Equal(2, toggles.Length);
        Assert.All(toggles, o =>
        {
            Assert.Equal("1iT0", o.Request.Descendants().Single(e => e.Name.LocalName == "ToggleID").Value);
            Assert.Equal("", o.UserAgent);
        });
        var renders = operations.Where(o => o.Operation == "Render").ToArray();
        Assert.Equal(2, renders.Length);
        Assert.All(renders, o => Assert.Equal(initialRender.UserAgent, o.UserAgent));
        Assert.All(operations, o => Assert.Equal(initialRender.ExecutionId, o.ExecutionId));
        Assert.DoesNotContain(operations, o => o.Operation is "LoadReport" or "SetExecutionParameters" or "GetItemParameters");
    }

    [Fact]
    public async Task ReportViewerCaptureRegistersExactRuntimeActionAndRerendersSameExecutionWithoutParameterRefresh()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); using var other = await f.Login("staff-two");
        var run = await Run(user, behavior: "reportviewer");
        var initial = new HtmlParser().ParseDocument(await Html(user, run.Token));
        var button = initial.QuerySelector("table.system-items button")!;
        var action = button.GetAttribute("data-portal-toggle")!;
        Assert.Equal("false", button.GetAttribute("aria-expanded")); Assert.Equal("15", button.NextElementSibling!.TextContent);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Post("/api/executions/" + run.Token + "/toggle/" + action, new { })).StatusCode);
        var second = await Run(user, behavior: "reportviewer");
        Assert.Equal(HttpStatusCode.Conflict, (await user.Post("/api/executions/" + second.Token + "/toggle/" + action, new { })).StatusCode);
        var before = f.Mock.Observations.Count();
        using var response = await user.Post("/api/executions/" + run.Token + "/toggle/" + action, new { }); response.EnsureSuccessStatusCode();
        var next = (await response.Content.ReadFromJsonAsync<PageResult>())!;
        var operations = f.Mock.Observations.Skip(before).ToArray();
        var toggle = Assert.Single(operations, o => o.Operation == "ToggleItem");
        Assert.Equal("230iT0R0x11", Assert.Single(toggle.Request.Descendants(), e => e.Name.LocalName == "ToggleID").Value);
        Assert.Contains(operations, o => o.Operation == "Render");
        Assert.All(operations, o => Assert.Equal(toggle.ExecutionId, o.ExecutionId));
        Assert.DoesNotContain(operations, o => o.Operation is "LoadReport" or "SetExecutionParameters" or "GetItemParameters");
        Assert.True(next.Revision > run.Revision);
        var expanded = new HtmlParser().ParseDocument(await Html(user, run.Token));
        Assert.Equal("true", expanded.QuerySelector("table.system-items button")!.GetAttribute("aria-expanded"));
        Assert.Equal("15", expanded.QuerySelector("table.system-items button")!.NextElementSibling!.TextContent);
        Assert.Contains("ReportViewer nested group", expanded.Body!.TextContent);
        Assert.Equal(HttpStatusCode.Conflict, (await user.Post("/api/executions/" + run.Token + "/toggle/" + action, new { })).StatusCode);
        var collapse = expanded.QuerySelector("table.system-items button")!.GetAttribute("data-portal-toggle")!;
        (await user.Post("/api/executions/" + run.Token + "/toggle/" + collapse, new { })).EnsureSuccessStatusCode();
        var collapsed = new HtmlParser().ParseDocument(await Html(user, run.Token));
        Assert.Equal("false", collapsed.QuerySelector("table.system-items button")!.GetAttribute("aria-expanded"));
        Assert.DoesNotContain("ReportViewer nested group", collapsed.Body!.TextContent);
    }

    private static ParameterInput Input(bool expanded = false, string behavior = "normal") => new(new()
        { ["initiallyExpanded"] = [expanded ? "true" : "false"], ["behavior"] = [behavior] });
    private static Task<RunResult> Run(LoginClient user, bool expanded = false, string behavior = "normal") => user.Run("Expandable Groups", Input(expanded, behavior));
    private static Task<string> Html(LoginClient user, string token) => user.Http.GetStringAsync("/api/executions/" + token + "/viewer");
    private static string Action(string html, string label) => new HtmlParser().ParseDocument(html).QuerySelectorAll("tr")
        .Single(r => r.QuerySelector("button") != null && r.QuerySelector("td")!.TextContent.Trim().EndsWith(" " + label, StringComparison.Ordinal))
        .QuerySelector("button")!.GetAttribute("data-portal-toggle")!;
    private static string[] Streams(string html) => new HtmlParser().ParseDocument(html).QuerySelectorAll("img,link[rel=stylesheet]")
        .Select(e => e.GetAttribute(e.LocalName == "img" ? "src" : "href")!.Split('/').Last()).ToArray();
    private static async Task<PageResult> Toggle(LoginClient user, string token, string label)
    {
        var action = Action(await Html(user, token), label);
        using var result = await user.Post("/api/executions/" + token + "/toggle/" + action, new { });
        Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
        return (await result.Content.ReadFromJsonAsync<PageResult>())!;
    }

    [Fact]
    public async Task SingleNestedAndSiblingActionsUseOneExecutionAndPreserveBackendDescendantState()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await Run(user);
        var initial = await Html(user, run.Token); var oldAction = Action(initial, "Library"); var oldStreams = Streams(initial);
        Assert.DoesNotContain("Single detail", initial); Assert.DoesNotContain("Branch East", initial);
        var before = f.Mock.Observations.Count(o => o.Operation is "LoadReport" or "SetExecutionParameters" or "GetItemParameters");
        await Toggle(user, run.Token, "Single level"); Assert.Contains("Single detail", await Html(user, run.Token));
        await Toggle(user, run.Token, "Single level"); Assert.DoesNotContain("Single detail", await Html(user, run.Token));
        Assert.Equal(3, (await Toggle(user, run.Token, "Library")).Pages);
        var parent = await Html(user, run.Token); Assert.Contains("Branch East", parent); Assert.DoesNotContain("Detail East", parent);
        Assert.Equal(4, (await Toggle(user, run.Token, "Branch East")).Pages);
        Assert.Contains("Detail East", await Html(user, run.Token));
        await Toggle(user, run.Token, "Branch East"); Assert.DoesNotContain("Detail East", await Html(user, run.Token));
        await Toggle(user, run.Token, "Branch East");
        await Toggle(user, run.Token, "Branch West");
        await Toggle(user, run.Token, "Library South");
        await Toggle(user, run.Token, "Branch East");
        var siblings = await Html(user, run.Token); Assert.DoesNotContain("Detail East", siblings); Assert.Contains("Detail West", siblings); Assert.Contains("Detail South", siblings);
        await Toggle(user, run.Token, "Branch East");
        await Toggle(user, run.Token, "Library"); Assert.DoesNotContain("Branch East", await Html(user, run.Token));
        await Toggle(user, run.Token, "Library");
        var restored = await Html(user, run.Token); Assert.Contains("Detail East", restored); Assert.Contains("Detail West", restored);
        var count = f.Mock.Observations.Count(o => o.Operation == "ToggleItem");
        Assert.Equal(HttpStatusCode.Conflict, (await user.Post("/api/executions/" + run.Token + "/toggle/" + oldAction, new { })).StatusCode);
        Assert.Equal(count, f.Mock.Observations.Count(o => o.Operation == "ToggleItem"));
        Assert.Equal(before, f.Mock.Observations.Count(o => o.Operation is "LoadReport" or "SetExecutionParameters" or "GetItemParameters"));
        Assert.Single(f.Mock.Observations.Where(o => o.Operation is "Render" or "ToggleItem").Select(o => o.ExecutionId).Distinct());
        foreach (var stream in oldStreams) { Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/api/executions/" + run.Token + "/streams/" + stream)).StatusCode); }
        foreach (var stream in Streams(restored)) { Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync("/api/executions/" + run.Token + "/streams/" + stream)).StatusCode); }
        var csv = await user.Http.GetStringAsync("/api/executions/" + run.Token + "/exports/csv"); Assert.Contains("Visibility: 1iT0;2iT0;3iT0;5iT0", csv);
    }

    [Fact]
    public async Task PaginationClampsAfterCollapseAndRejectsStalePagesAndActions()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await Run(user);
        await Toggle(user, run.Token, "Library"); await Toggle(user, run.Token, "Branch East");
        using var page = await user.Post("/api/executions/" + run.Token + "/page/4", new { }); page.EnsureSuccessStatusCode();
        var previous = (await page.Content.ReadFromJsonAsync<PageResult>())!;
        var oldAction = Action(await Html(user, run.Token), "Branch East");
        var result = await Toggle(user, run.Token, "Library"); Assert.Equal(2, result.Page); Assert.Equal(2, result.Pages); Assert.True(result.Revision > previous.Revision);
        Assert.Contains("Page 2", await user.Http.GetStringAsync(result.ViewerUrl));
        Assert.Equal(HttpStatusCode.Conflict, (await user.Http.GetAsync(previous.ViewerUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await user.Post("/api/executions/" + run.Token + "/toggle/" + oldAction, new { })).StatusCode);
        Assert.Equal(4, (await Toggle(user, run.Token, "Library")).Pages);
        using var first = await user.Post("/api/executions/" + run.Token + "/page/1", new { }); first.EnsureSuccessStatusCode();
        Assert.Contains("Detail East", await Html(user, run.Token));
        using var second = await user.Post("/api/executions/" + run.Token + "/page/2", new { }); second.EnsureSuccessStatusCode();
        Assert.Contains("Detail East", await Html(user, run.Token));
    }

    [Fact]
    public async Task UnknownPaginationUsesSafeRecoveryPageAndRetainsVisibility()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await Run(user, behavior: "unknown"); Assert.Null(run.Pages);
        using var next = await user.Post("/api/executions/" + run.Token + "/page/2", new { }); next.EnsureSuccessStatusCode();
        var result = await Toggle(user, run.Token, "Library"); Assert.Null(result.Pages); Assert.Equal(1, result.Page);
        Assert.Contains("Branch East", await Html(user, run.Token));
    }

    [Fact]
    public async Task ExecutionAndUserIsolationAndNewRunInitialVisibilityAreIndependent()
    {
        await using var f = new PortalFactory(); using var one = await f.Login(); using var two = await f.Login("staff-two");
        var a = await Run(one); var b = await Run(one); var c = await Run(two, expanded: true);
        await Toggle(one, a.Token, "Library");
        Assert.Contains("Branch East", await Html(one, a.Token)); Assert.DoesNotContain("Branch East", await Html(one, b.Token));
        Assert.Contains("Detail East", await Html(two, c.Token)); Assert.Contains("Single detail", await Html(two, c.Token));
        var action = Action(await Html(one, a.Token), "Library");
        Assert.Equal(HttpStatusCode.NotFound, (await two.Post("/api/executions/" + a.Token + "/toggle/" + action, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await one.Post("/api/executions/" + b.Token + "/toggle/" + action, new { })).StatusCode);
        var fresh = await one.Run("Expandable Groups", Input() with { ReplaceToken = a.Token });
        Assert.DoesNotContain("Branch East", await Html(one, fresh.Token));
        Assert.Equal(HttpStatusCode.NoContent, (await one.Post("/api/executions/" + fresh.Token + "/commit", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await one.Post("/api/executions/" + a.Token + "/toggle/" + action, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await one.Post("/api/executions/" + fresh.Token + "/toggle/" + action, new { })).StatusCode);
    }

    [Theory]
    [InlineData("toggle")]
    [InlineData("synchronize")]
    public async Task AuthenticationStaffPolicyAndAntiforgeryAreRequired(string route)
    {
        await using var f = new PortalFactory(); using var anonymous = f.Browser(); using var user = await f.Login(); var run = await Run(user);
        var suffix = route == "toggle" ? "/toggle/" + Action(await Html(user, run.Token), "Library") : "/synchronize";
        var path = "/api/executions/" + run.Token + suffix;
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Http.PostAsJsonAsync(path, new { })).StatusCode);
        var options = f.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var unauthorized = new ClaimsPrincipal(new ClaimsIdentity(AuthenticationTests.Principal().Claims.Where(c => c.Type != "roles"), CookieAuthenticationDefaults.AuthenticationScheme));
        using var denied = f.Browser();
        denied.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(new AuthenticationTicket(unauthorized, CookieAuthenticationDefaults.AuthenticationScheme)));
        // The cookie validator rejects a ticket without Staff claims before authorization.
        Assert.Equal(HttpStatusCode.Unauthorized, (await denied.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.DoesNotContain(f.Mock.Observations, o => o.Operation == "ToggleItem");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("1iT0")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task UnknownOrForgedClientIdsNeverBecomeSoapArguments(string action)
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await Run(user);
        var response = await user.Post("/api/executions/" + run.Token + "/toggle/" + action, new { ToggleID = "1iT0" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(f.Mock.Observations, o => o.Operation == "ToggleItem");
        using var info = JsonDocument.Parse(await user.Http.GetStringAsync("/api/executions/" + run.Token + "/info"));
        Assert.False(info.RootElement.GetProperty("needsSynchronization").GetBoolean());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("cancelled")]
    [InlineData("policy")]
    public async Task ExpiredCancelledAndRevokedSessionActionsCannotMutate(string reason)
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await Run(user);
        var action = Action(await Html(user, run.Token), "Library");
        if (reason == "expired") { f.Clock.Advance(TimeSpan.FromMinutes(16)); }
        if (reason == "cancelled") { Assert.Equal(HttpStatusCode.NoContent, (await user.Post("/api/executions/" + run.Token + "/cancel", new { })).StatusCode); }
        if (reason == "policy") { f.Services.GetRequiredService<PortalOptions>().ExcludedPrefixes = ["/Training/Reports"]; }
        Assert.Equal(HttpStatusCode.NotFound, (await user.Post("/api/executions/" + run.Token + "/toggle/" + action, new { })).StatusCode);
        Assert.DoesNotContain(f.Mock.Observations, o => o.Operation == "ToggleItem");
    }

    [Theory]
    [InlineData("Render", MockFailure.Fault, "synchronize", true)]
    [InlineData("ToggleItem", MockFailure.Fault, "synchronize", false)]
    [InlineData("ToggleItem", MockFailure.Unavailable, "synchronize", false)]
    [InlineData("ToggleItem", MockFailure.ToggleNotFound, "toggle-missing", false)]
    [InlineData("ToggleItem", MockFailure.ToggleLostResponse, "synchronize", true)]
    public async Task FailedOrAmbiguousMutationKeepsOldOutputButRequiresPureRerender(string operation, MockFailure failure, string code, bool changed)
    {
        using var h = new Harness(); var report = await h.Report("Expandable Groups"); var run = await h.Execution.RunAsync("owner", report.Id, Input(), default);
        string oldHtml, action; Dictionary<string, string> oldStreams; long revision;
        using (var lease = await h.Sessions.AcquireAsync(run.Token, "owner", default))
        {
            oldHtml = lease.Session.Html; oldStreams = lease.Session.Streams; action = Action(oldHtml, "Library"); revision = lease.Session.Revision;
            h.Mock.FailOperation = operation; h.Mock.Failure = failure;
            Assert.Equal(code, (await Assert.ThrowsAsync<PortalException>(() => h.Execution.ToggleAsync(lease, action))).Code);
            Assert.Equal(oldHtml, lease.Session.Html); Assert.Same(oldStreams, lease.Session.Streams); Assert.Equal(revision, lease.Session.Revision);
            Assert.True(lease.Session.NeedsSynchronization); Assert.Empty(lease.Session.ToggleActions);
            Assert.Equal("synchronize", (await Assert.ThrowsAsync<PortalException>(() => h.Execution.ExportAsync(lease, OutputFormat.All["pdf"]))).Code);
            Assert.Equal("synchronize", (await Assert.ThrowsAsync<PortalException>(() => h.Execution.ToggleAsync(lease, action))).Code);
        }
        h.Mock.Failure = MockFailure.None;
        using (var lease = await h.Sessions.AcquireAsync(run.Token, "owner", default))
        {
            var recovered = await h.Execution.SynchronizeAsync(lease); Assert.True(recovered.Revision > revision);
            Assert.False(lease.Session.NeedsSynchronization); Assert.Equal(changed, lease.Session.Html.Contains("Branch East", StringComparison.Ordinal));
        }
        Assert.Single(h.Mock.Observations, o => o.Operation == "ToggleItem");
        Assert.Single(h.Mock.Observations, o => o.Operation == "LoadReport");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeAndAfterMutationNeverAllowsReplay(bool after)
    {
        using var h = new Harness(); var report = await h.Report("Expandable Groups"); var run = await h.Execution.RunAsync("owner", report.Id, Input(), default);
        using var stop = new CancellationTokenSource();
        using (var lease = await h.Sessions.AcquireAsync(run.Token, "owner", stop.Token))
        {
            var action = Action(lease.Session.Html, "Library"); var html = lease.Session.Html;
            if (after) { h.Mock.FailOperation = "Render"; h.Mock.Failure = MockFailure.Slow; h.Mock.DelayMilliseconds = 10000; }
            else { stop.Cancel(); }
            var task = h.Execution.ToggleAsync(lease, action);
            if (after) { await Until(() => h.Mock.Observations.Count(o => o.Operation == "Render") >= 2); stop.Cancel(); }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(html, lease.Session.Html); Assert.Equal(after, lease.Session.NeedsSynchronization);
            Assert.Equal(after ? 1 : 0, h.Mock.Observations.Count(o => o.Operation == "ToggleItem"));
        }
        h.Mock.Failure = MockFailure.None;
        using var recovery = await h.Sessions.AcquireAsync(run.Token, "owner", default);
        await h.Execution.SynchronizeAsync(recovery); Assert.Equal(after, recovery.Session.Html.Contains("Branch East", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentDuplicateRequestsConsumeActionOnceAndSerializeWithPageNavigation()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await Run(user);
        var action = Action(await Html(user, run.Token), "Library");
        f.Mock.FailOperation = "ToggleItem"; f.Mock.Failure = MockFailure.Slow; f.Mock.DelayMilliseconds = 80; f.Mock.ResetConcurrencyPeak();
        var url = "/api/executions/" + run.Token + "/toggle/" + action;
        var first = user.Post(url, new { });
        await Until(() => f.Mock.Observations.Any(o => o.Operation == "ToggleItem"));
        var tasks = new[] { first, user.Post(url, new { }), user.Post("/api/executions/" + run.Token + "/page/2", new { }) };
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Take(2).Count(r => r.StatusCode == HttpStatusCode.OK)); Assert.Equal(1, results.Take(2).Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.OK, results[2].StatusCode);
        Assert.Single(f.Mock.Observations, o => o.Operation == "ToggleItem"); Assert.Equal(1, f.Mock.MaxActiveCalls);
        Assert.Contains("Branch East", await Html(user, run.Token));
    }

    [Fact]
    public async Task CancelDuringMutationCannotResurrectSessionOrMappings()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await Run(user);
        var action = Action(await Html(user, run.Token), "Library");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); f.Mock.ToggleResponseGate = gate.Task;
        var toggle = user.Post("/api/executions/" + run.Token + "/toggle/" + action, new { });
        await Until(() => f.Mock.Observations.Any(o => o.Operation == "ToggleItem"));
        Assert.Equal(HttpStatusCode.NoContent, (await user.Post("/api/executions/" + run.Token + "/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await toggle).StatusCode);
        Assert.Equal((0, 0L), f.Services.GetRequiredService<SessionStore>().Snapshot());
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/api/executions/" + run.Token + "/viewer")).StatusCode);
        gate.SetResult();
    }

    [Theory]
    [InlineData("session")]
    [InlineData("aggregate")]
    [InlineData("actions")]
    public async Task ActionMemoryAndPublicationFailureStayWithinSessionAndAggregateBudget(string limit)
    {
        using var h = new Harness(); var report = await h.Report("Expandable Groups"); var run = await h.Execution.RunAsync("owner", report.Id, Input(), default);
        using (var lease = await h.Sessions.AcquireAsync(run.Token, "owner", default))
        {
            var s = lease.Session; var html = s.Html; var bytes = s.Bytes;
            Assert.Equal(3, s.ToggleActions.Count);
            var expected = 4096L + System.Text.Encoding.UTF8.GetByteCount(s.Html) + s.Streams.Sum(p => 128L + System.Text.Encoding.UTF8.GetByteCount(p.Value)) +
                s.ToggleActions.Sum(p => 256L + 2L * p.Value.Length) + JsonSerializer.SerializeToUtf8Bytes(s.Input).LongLength;
            Assert.Equal(expected, bytes);
            if (limit == "session") { h.Options.MaxSessionBytes = bytes; }
            if (limit == "aggregate") { h.Options.MaxAggregateSessionBytes = h.Sessions.Snapshot().Bytes; }
            if (limit == "actions") { h.Options.MaxToggleActions = 3; }
            Assert.Equal("synchronize", (await Assert.ThrowsAsync<PortalException>(() => h.Execution.ToggleAsync(lease, Action(html, "Library")))).Code);
            Assert.Equal(html, s.Html); Assert.Equal(bytes, s.Bytes); Assert.True(s.NeedsSynchronization);
            h.Options.MaxSessionBytes = 32 * 1024 * 1024;
            h.Options.MaxAggregateSessionBytes = 128 * 1024 * 1024;
            h.Options.MaxToggleActions = 1000;
            await h.Execution.SynchronizeAsync(lease); Assert.Contains("Branch East", s.Html);
        }
        h.Sessions.Cancel(run.Token, "owner"); Assert.Equal((0, 0L), h.Sessions.Snapshot());
    }

    [Fact]
    public async Task PageCountDiscoveredDuringRenderGetsOneBoundedCorrectionWithoutRetoggling()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); f.Mock.PageCountChangesAfterRender = true;
        var run = await Run(user); await Toggle(user, run.Token, "Library"); await Toggle(user, run.Token, "Branch East");
        using var page = await user.Post("/api/executions/" + run.Token + "/page/4", new { }); page.EnsureSuccessStatusCode();
        var before = f.Mock.Observations.Count(o => o.Operation == "Render");
        var toggles = f.Mock.Observations.Count(o => o.Operation == "ToggleItem");
        var collapsed = await Toggle(user, run.Token, "Library"); Assert.Equal(2, collapsed.Page); Assert.Equal(2, collapsed.Pages);
        var renders = f.Mock.Observations.Where(o => o.Operation == "Render").Skip(before).ToArray(); Assert.Equal(2, renders.Length);
        Assert.Equal(new[] { "4", "2" }, renders.Select(o => System.Xml.Linq.XElement.Parse(o.Request.Descendants().Single(e => e.Name.LocalName == "DeviceInfo").Value).Element("Section")!.Value));
        Assert.Equal(toggles + 1, f.Mock.Observations.Count(o => o.Operation == "ToggleItem"));
        Assert.Contains("Page 2", await user.Http.GetStringAsync(collapsed.ViewerUrl));
    }

    [Fact]
    public async Task InitialActionLimitFailureRemovesReservation()
    {
        using var h = new Harness(o => o.MaxToggleActions = 2); var report = await h.Report("Expandable Groups");
        Assert.Equal("limit", (await Assert.ThrowsAsync<PortalException>(() => h.Execution.RunAsync("owner", report.Id, Input(), default))).Code);
        Assert.Equal((0, 0L), h.Sessions.Snapshot());
    }

    [Fact]
    public async Task CanceledToggleReplyAfterMutationRecoversWithoutReplay()
    {
        using var h = new Harness(); var report = await h.Report("Expandable Groups"); var run = await h.Execution.RunAsync("owner", report.Id, Input(), default);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); h.Mock.ToggleResponseGate = gate.Task;
        using var stop = new CancellationTokenSource();
        using (var lease = await h.Sessions.AcquireAsync(run.Token, "owner", stop.Token))
        {
            var task = h.Execution.ToggleAsync(lease, Action(lease.Session.Html, "Library"));
            await Until(() => h.Mock.Observations.Any(o => o.Operation == "ToggleItem")); stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(lease.Session.NeedsSynchronization); Assert.DoesNotContain("Branch East", lease.Session.Html);
        }
        gate.SetResult(); h.Mock.ToggleResponseGate = null;
        using var recovery = await h.Sessions.AcquireAsync(run.Token, "owner", default);
        await h.Execution.SynchronizeAsync(recovery); Assert.Contains("Branch East", recovery.Session.Html);
        Assert.Single(h.Mock.Observations, o => o.Operation == "ToggleItem");
    }

    [Fact]
    public async Task ToggleQueuedBehindPageNavigationCannotUsePreviousPagesAction()
    {
        await using var f = new PortalFactory(); using var user = await f.Login(); var run = await Run(user);
        var action = Action(await Html(user, run.Token), "Library");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Mock.TargetResponseGates["Render|(execution)"] = gate.Task;
        var before = f.Mock.Observations.Count(o => o.Operation == "Render");
        var navigation = user.Post("/api/executions/" + run.Token + "/page/2", new { });
        await Until(() => f.Mock.Observations.Count(o => o.Operation == "Render") > before);
        var toggle = user.Post("/api/executions/" + run.Token + "/toggle/" + action, new { }); gate.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await navigation).StatusCode); Assert.Equal(HttpStatusCode.Conflict, (await toggle).StatusCode);
        Assert.DoesNotContain(f.Mock.Observations, o => o.Operation == "ToggleItem");
    }

    private static async Task Until(Func<bool> predicate)
    {
        for (var i = 0; i < 300; i++) { if (predicate()) { return; } await Task.Delay(10); }
        Assert.Fail("Expected synthetic operation did not start.");
    }
}
