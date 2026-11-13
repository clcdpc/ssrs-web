using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using SsrsPortal.Core;
using SsrsPortal.Mock;

namespace SsrsPortal.Tests;

public sealed class GlobalBlocklistTests
{
    [Fact]
    public async Task UnlistedUnblockedReportIsDiscoveredParameterizedAndExecuted()
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var id = await user.Id("Additional Report");
        Assert.All(f.Mock.Calls.Keys, key => Assert.True(key.StartsWith("GetProperties|", StringComparison.Ordinal) || key.StartsWith("ListChildren|", StringComparison.Ordinal)));
        var parameters = await user.Post("/api/reports/" + id + "/parameters", new ParameterInput(new()));
        Assert.Equal(HttpStatusCode.OK, parameters.StatusCode);
        var run = await user.Run("Additional Report", new ParameterInput(new()));
        Assert.Equal(HttpStatusCode.OK, (await user.Http.GetAsync("/api/executions/" + run.Token + "/exports/pdf")).StatusCode);
        Assert.Equal(1, f.Mock.Calls["LoadReport|/Training/Reports/Additional Report"]);
    }

    [Theory]
    [InlineData("/training/reports/archived")]
    [InlineData("/Training/Reports/Archived/Old")]
    public async Task FolderAndReportBlocksUseCaseInsensitiveSegmentBoundaries(string blocked)
    {
        const string old = "/Training/Reports/Archived/Old";
        const string neighbor = "/Training/Reports/ArchivedReports/Current";
        using var h = new Harness(); CatalogCacheTests.Graph(h, old, neighbor);
        var before = await h.Catalog.BrowseAsync(default);
        var id = before.Single(e => e.Name == "Old").Id;
        h.Options.ExcludedPrefixes = [blocked];
        var after = await h.Catalog.BrowseAsync(default);
        Assert.DoesNotContain(after, e => e.Name is "Old" or "Archived");
        Assert.Contains(after, e => e.Name == "Current");
        var parent = after.Single(e => e.Name == "ArchivedReports");
        Assert.Equal(parent.Id, after.Single(e => e.Name == "Current").ParentId);
        Assert.Equal("notfound", (await Assert.ThrowsAsync<PortalException>(() => h.Execution.RunAsync("owner", id, Harness.Inputs(), default))).Code);
        var current = await h.Execution.RunAsync("owner", after.Single(e => e.Name == "Current").Id, new ParameterInput(new()), default);
        Assert.NotEmpty(current.Token);
        Assert.Equal(0, h.Mock.Calls.GetValueOrDefault("LoadReport|" + old));
    }

    [Theory]
    [InlineData("restored")]
    [InlineData("archived")]
    [InlineData("internal")]
    [InlineData("system")]
    [InlineData("Custom_Restored")]
    [InlineData("ArchivedReports")]
    public async Task FormerlyImplicitNamesAreEligibleUnlessExplicitlyBlocked(string name)
    {
        var path = "/Training/Reports/" + name + "/Supported Report";
        using var h = new Harness(); CatalogCacheTests.Graph(h, path);
        var report = (await h.Catalog.BrowseAsync(default)).Single(e => e.Name == "Supported Report");
        Assert.True(PathPolicy.Permitted(path, h.Options));
        Assert.NotEmpty((await h.Execution.RunAsync("owner", report.Id, new ParameterInput(new()), default)).Token);
        h.Options.ExcludedPrefixes = ["/Training/Reports/" + name];
        Assert.Empty(await h.Catalog.BrowseAsync(default));
    }

    [Theory]
    [InlineData("/Training/Reports/Holds Ratio")]
    [InlineData("/Training/Reports")]
    public async Task OldOpaqueIdsCannotBypassBlocksInParametersOptionsRunSearchOrPages(string blocked)
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var reportId = await user.Id();
        var folderId = (await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!.Single(e => e.Name == "Reports").Id;
        f.Services.GetRequiredService<PortalOptions>().ExcludedPrefixes = [blocked];
        foreach (var route in new[] { "parameters", "options/library", "run" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await user.Post("/api/reports/" + reportId + "/" + route, new { values = new Dictionary<string, string?[]>() })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/Report?id=" + reportId)).StatusCode);
        var search = await user.Http.GetStringAsync("/?q=Holds%20Ratio");
        Assert.DoesNotContain(reportId, search);
        var catalog = (await user.Http.GetFromJsonAsync<CatalogEntry[]>("/api/catalog"))!;
        Assert.DoesNotContain(catalog, e => e.Name == "Holds Ratio");
        if (blocked == "/Training/Reports")
        {
            Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/?folder=" + folderId)).StatusCode);
        }
        Assert.Equal(0, f.Mock.Calls.GetValueOrDefault("LoadReport|" + SyntheticSsrsHandler.Holds));
    }

    [Theory]
    [InlineData("/Training/Reports/Holds Ratio")]
    [InlineData("/Training/Reports/Details")]
    [InlineData("/Training/Reports")]
    public async Task AnyChangedGlobalPolicyRevokesAlreadyRunningSessions(string blocked)
    {
        await using var f = new PortalFactory(); using var user = await f.Login();
        var run = await user.Run();
        var before = f.Mock.Calls.Values.Sum();
        f.Services.GetRequiredService<PortalOptions>().ExcludedPrefixes = [blocked];
        foreach (var route in new[] { "info", "viewer", "streams/x", "exports/pdf" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await user.Http.GetAsync("/api/executions/" + run.Token + "/" + route)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await user.Post("/api/executions/" + run.Token + "/page/2", new { })).StatusCode);
        Assert.Equal(before, f.Mock.Calls.Values.Sum());
    }

    [Fact]
    public async Task EmptyBlocklistIncludesReportsThatSsrsMayRejectAtExecution()
    {
        using var h = new Harness(); h.Options.ExcludedPrefixes = [];
        Assert.NotNull(h.Options.Validate(true));
        var entries = await h.Catalog.BrowseAsync(default);
        Assert.Contains(entries, e => e.Name == "Old");
        Assert.Contains(entries, e => e.Name == "System");
        Assert.Contains(entries, e => e.Name == "Unsafe Link"); // Its target is technically safe, now unblocked.
        Assert.Contains(entries, e => e.Name == "User Identity");
        Assert.Contains(entries, e => e.Name == "Prompted");
        Assert.Contains(entries, e => e.Name == "Broken");
        Assert.DoesNotContain(entries, e => e.Name == "Hidden");
        var run = await h.Execution.RunAsync("owner", entries.Single(e => e.Name == "Old").Id, new ParameterInput(new()), default);
        Assert.NotEmpty(run.Token);
    }

    [Fact]
    public async Task BlockedDependenciesDoNotHideParentsOrAliasesAndAliasMustBeBlockedDirectly()
    {
        using var h = new Harness();
        var before = await h.Catalog.BrowseAsync(default);
        var link = before.Single(e => e.Name == "Holds Link").Id;
        h.Options.ExcludedPrefixes = [SyntheticSsrsHandler.Details, SyntheticSsrsHandler.Holds];
        var current = await h.Catalog.BrowseAsync(default);
        Assert.Contains(current, e => e.Name == "Holds Link");
        Assert.DoesNotContain(current, e => e.Name is "Holds Ratio" or "Details");
        Assert.NotEmpty((await h.Execution.RunAsync("owner", link, Harness.Inputs(), default)).Token);
        h.Options.ExcludedPrefixes = [SyntheticSsrsHandler.Details];
        current = await h.Catalog.BrowseAsync(default);
        Assert.NotEmpty((await h.Execution.RunAsync("owner", current.Single(e => e.Name == "Holds Ratio").Id, Harness.Inputs(), default)).Token);
        h.Options.ExcludedPrefixes = [SyntheticSsrsHandler.Link];
        await Assert.ThrowsAsync<PortalException>(() => h.Catalog.ResolveAsync(link, default));
        CatalogCacheTests.NoPreflight(h);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenReportsAndHiddenAncestorsAreExcludedWithoutInspectingTheirParents(bool ancestor)
    {
        const string parent = "/Training/Reports/Parent";
        const string child = "/Training/Children/Child";
        using var h = new Harness(); CatalogCacheTests.Graph(h, parent, child);
        h.Mock.CatalogItems = h.Mock.CatalogItems!.Select(i => i.Path == "/Training/Children" ? i with { Hidden = ancestor } : i).ToArray();
        if (!ancestor)
        {
            h.Mock.CatalogItems = h.Mock.CatalogItems.Select(i => i.Path == child ? i with { Hidden = true } : i).ToArray();
        }
        var visible = await h.Catalog.BrowseAsync(default);
        Assert.Contains(visible, e => e.Name == "Parent");
        Assert.DoesNotContain(visible, e => e.Name == "Child");
    }

    [Theory]
    [InlineData("/Training/Reports")]
    [InlineData("/Training")]
    public async Task HiddenCatalogRootOrAncestorAboveItCannotAuthorizeOldIds(string hidden)
    {
        const string path = "/Training/Reports/Report";
        using var h = new Harness(); CatalogCacheTests.Graph(h, path);
        h.Options.AllowedRoots = ["/Training/Reports"];
        var id = (await h.Catalog.BrowseAsync(default)).Single(e => e.Name == "Report").Id;
        h.Mock.CatalogItems = h.Mock.CatalogItems!.Where(i => i.Path != hidden).Append(new(hidden, "Folder", true)).ToArray();
        h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1));
        await Assert.ThrowsAsync<PortalException>(() => h.Catalog.ResolveAsync(id, default));
        Assert.Empty(await h.Catalog.BrowseAsync(default));
        Assert.Equal(0, h.Mock.Calls.GetValueOrDefault("LoadReport|" + path));
    }

    [Fact]
    public async Task UnavailableRootMetadataFailsClosedAndCanRetry()
    {
        using var h = new Harness();
        h.Mock.FaultTargets["GetProperties|/Training"] = true;
        Assert.Equal("service", (await Assert.ThrowsAsync<PortalException>(() => h.Catalog.BrowseAsync(default))).Code);
        Assert.Equal(0, h.Mock.Calls.GetValueOrDefault("GetItemDefinition|" + SyntheticSsrsHandler.Holds));
        h.Mock.FaultTargets.Clear();
        Assert.Contains(await h.Catalog.BrowseAsync(default), e => e.Name == "Holds Ratio");
    }

    [Theory]
    [InlineData("/Training/Reports/Holds Ratio")]
    [InlineData("/Training/Reports/Details")]
    public async Task PolicyChangeDuringParameterResolutionCannotStartAnExecution(string blocked)
    {
        using var h = new Harness();
        var id = (await h.Catalog.BrowseAsync(default)).Single(e => e.Name == "Holds Ratio").Id;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mock.TargetResponseGates["GetItemParameters|" + SyntheticSsrsHandler.Holds] = gate.Task;
        try
        {
            var run = h.Execution.RunAsync("owner", id, Harness.Inputs(), default);
            await CatalogCacheTests.WaitUntil(() => h.Mock.Calls.GetValueOrDefault("GetItemParameters|" + SyntheticSsrsHandler.Holds) == 1);
            h.Options.ExcludedPrefixes = [blocked];
            gate.TrySetResult();
            Assert.Equal("notfound", (await Assert.ThrowsAsync<PortalException>(() => run)).Code);
            Assert.Equal(0, h.Mock.Calls.GetValueOrDefault("LoadReport|" + SyntheticSsrsHandler.Holds));
            Assert.Equal(0, h.Sessions.Snapshot().Count);
        }
        finally
        {
            h.Mock.TargetResponseGates.Clear(); gate.TrySetResult();
        }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("")]
    public async Task UnknownCatalogHiddenStateCannotBeInterpretedAsVisible(string value)
    {
        using var h = new Harness(wrapHandler: inner => new UnknownHiddenHandler(inner, value));
        Assert.Equal("service", (await Assert.ThrowsAsync<PortalException>(() => h.Catalog.BrowseAsync(default))).Code);
        Assert.Equal(0, h.Mock.Calls.GetValueOrDefault("GetItemDefinition|" + SyntheticSsrsHandler.Holds));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyChangeWhileParameterOrOptionRequestAwaitsCannotReturnMetadata(bool search)
    {
        using var h = new Harness(); var report = await h.Report();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mock.TargetResponseGates["GetItemParameters|" + report.Path] = gate.Task;
        Task request = search ? h.Parameters.ChoicesAsync(report, Harness.Inputs(), "library", default) :
            h.Parameters.ResolveAsync(report, Harness.Inputs(), false, default);
        await CatalogCacheTests.WaitUntil(() => CatalogCacheTests.Count(h, "GetItemParameters") == 1);
        h.Options.ExcludedPrefixes = [report.Path]; gate.TrySetResult();
        Assert.Equal("notfound", (await Assert.ThrowsAsync<PortalException>(() => request)).Code);
        Assert.Equal(1, CatalogCacheTests.Count(h, "GetItemParameters"));
    }

    [Theory]
    [InlineData("/Training/Reports/Holds Ratio")]
    [InlineData("/Training/Unrelated")]
    public async Task ReservationRejectsAnOldEffectivePolicyEvenWhenTheReportRemainsPermitted(string blocked)
    {
        using var h = new Harness(); var report = await h.Report();
        h.Options.ExcludedPrefixes = [blocked];
        Assert.Equal("notfound", Assert.Throws<PortalException>(() => h.Sessions.Reserve("owner", report)).Code);
        Assert.Equal((0, 0L), h.Sessions.Snapshot());
    }

    private sealed class UnknownHiddenHandler(HttpMessageHandler inner, string value) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.Headers.GetValues("SOAPAction").Single().EndsWith("/ListChildren\"", StringComparison.Ordinal))
            {
                var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                xml.Descendants(SyntheticSsrsHandler.Management + "Hidden").First().Value = value;
                response.Content.Dispose(); response.Content = new StringContent(xml.ToString(), Encoding.UTF8, "text/xml");
            }
            return response;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingRootsShareConsistentFolderMetadataAndOneCatalog(bool innerFirst)
    {
        using var h = new Harness();
        h.Options.AllowedRoots = innerFirst ? ["/Training/Reports", "/Training"] : ["/Training", "/Training/Reports"];
        var entries = await h.Catalog.BrowseAsync(default);
        Assert.Single(entries, e => e.Name == "Holds Ratio");
        Assert.Equal(SyntheticSsrsHandler.Holds, (await h.Catalog.ResolveAsync(entries.Single(e => e.Name == "Holds Ratio").Id, default)).Path);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("resource")]
    public async Task UnknownAncestorMetadataCannotMakeAReportEligible(string kind)
    {
        const string parent = "/Training/Reports/Parent";
        const string child = "/Training/Unknown/Child";
        using var h = new Harness(); CatalogCacheTests.Graph(h, parent, child);
        h.Mock.CatalogItems = h.Mock.CatalogItems!.Where(i => i.Path != "/Training/Unknown").ToArray();
        if (kind == "resource")
        {
            h.Mock.CatalogItems = [.. h.Mock.CatalogItems!, new("/Training/Unknown", "Resource")];
        }
        var entries = await h.Catalog.BrowseAsync(default);
        Assert.Contains(entries, e => e.Name == "Parent");
        Assert.DoesNotContain(entries, e => e.Name == "Child");
        CatalogCacheTests.MetadataOnly(h);
    }
}
