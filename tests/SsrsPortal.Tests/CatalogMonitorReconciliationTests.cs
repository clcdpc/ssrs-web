using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class CatalogMonitorReconciliationTests
{
    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("type")]
    [InlineData("modified")]
    [InlineData("hidden")]
    [InlineData("link")]
    public void EveryCatalogTransitionRequestsMetadataRefresh(string transition)
    {
        using var h = new MonitorHarness();
        var initial = h.Snapshot(); var item = initial.Catalog[h.ItemId(MonitorHarness.A)];
        var changed = transition switch
        {
            "move" => item with { Path = "/Training/Moved/A" },
            "type" => item with { Type = 5 },
            "modified" => item with { ModifiedDate = item.ModifiedDate!.Value.AddMinutes(1) },
            "hidden" => item with { Hidden = null },
            "link" => item with { LinkSourceId = h.ItemId(MonitorHarness.B) },
            _ => item with { ItemId = Guid.NewGuid() }
        };
        var items = initial.Catalog.Values.Where(i => transition == "add" || i.ItemId != item.ItemId).ToList();
        if (transition != "delete") { items.Add(changed); }
        var delta = new CatalogSnapshot(items, initial.DataSources.Values, 6000).ChangesFrom(initial);
        Assert.True(delta.Changed); Assert.Equal(1, delta.CatalogItemsChanged); Assert.Equal(0, delta.DataSourcesChanged);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("owner")]
    [InlineData("shared link")]
    [InlineData("null owner")]
    [InlineData("unknown owner")]
    [InlineData("fingerprint")]
    public async Task SourceTransitionsRefreshMetadataOnceWithoutChangingReportEligibility(string transition)
    {
        using var h = new MonitorHarness();
        var entries = await h.Portal.Catalog.BrowseAsync(default);
        await h.Monitor.PollAsync(default);
        var initial = h.Provider.Snapshot;
        var source = h.Sources.Values.First();
        if (transition == "delete") { h.Sources.Remove(source.DataSourceId); }
        else
        {
            var changed = transition switch
            {
                "add" => source with { DataSourceId = Guid.NewGuid() },
                "owner" => source with { ItemId = h.ItemId(MonitorHarness.B) },
                "shared link" => source with { Link = h.ItemId("/Training/Missing/Shared") },
                "null owner" => source with { ItemId = null },
                "unknown owner" => source with { ItemId = Guid.NewGuid() },
                _ => source with { Fingerprint = MonitorHarness.Fingerprint("Changed credentials or provider") }
            };
            h.Sources[changed.DataSourceId] = changed;
        }
        h.UpdateSnapshot();
        Assert.Equal(1, h.Provider.Snapshot.ChangesFrom(initial).DataSourcesChanged);
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        Assert.Equal(entries, await h.Portal.Catalog.BrowseAsync(default));
        Assert.Equal(2, h.Calls("ListChildren")); Assert.Equal(2, h.Calls("GetProperties"));
        var calls = h.TotalSoap;
        Assert.Equal(CatalogPollOutcome.Unchanged, await h.Monitor.PollAsync(default)); Assert.Equal(calls, h.TotalSoap);
        CatalogCacheTests.MetadataOnly(h.Portal);
    }

    [Fact]
    public async Task SharedSourceMoveAndLinkChangeAreDetectedWithoutResolvingDependencies()
    {
        using var h = new MonitorHarness(paths: [MonitorHarness.A, MonitorHarness.B, SyntheticSsrsHandler.Link]);
        h.AddShared(MonitorHarness.Shared, MonitorHarness.A);
        h.ModifyCatalog(SyntheticSsrsHandler.Link, item => item with { Type = "LinkedReport" });
        h.Portal.Mock.LinkedTargets[SyntheticSsrsHandler.Link] = MonitorHarness.A;
        h.UpdateSnapshot();
        await h.Portal.Catalog.BrowseAsync(default); await h.Monitor.PollAsync(default);
        var sourceRows = h.Sources.Values.ToArray();
        h.ModifyCatalog(MonitorHarness.Shared, item => item with { Path = "/Training/DataSources/Moved" });
        h.UpdateSnapshot();
        var aliasId = h.ItemId(SyntheticSsrsHandler.Link);
        h.Provider.Snapshot = new CatalogSnapshot(h.Provider.Snapshot.Catalog.Values.Select(item => item.ItemId == aliasId
            ? item with { LinkSourceId = h.ItemId(MonitorHarness.B) } : item), sourceRows, 6000);
        h.Portal.Mock.LinkedTargets[SyntheticSsrsHandler.Link] = MonitorHarness.B;
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        var alias = (await h.Portal.Catalog.BrowseAsync(default)).Single(e => e.Name == "Holds Link");
        Assert.NotEmpty((await h.Portal.Execution.RunAsync("owner", alias.Id, Harness.Inputs(), default)).Token);
        Assert.Equal(2, h.Calls("ListChildren")); CatalogCacheTests.NoPreflight(h.Portal);
    }

    [Fact]
    public async Task SqlDeletionWhileParametersAwaitCannotReserveOrLoadAReport()
    {
        ResponsePauseHandler? pause = null;
        using var h = new MonitorHarness(wrapper: inner => pause = new ResponsePauseHandler(inner));
        var id = (await h.Portal.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        await h.Monitor.PollAsync(default);
        pause!.Pause("GetItemParameters", MonitorHarness.A);
        var run = h.Portal.Execution.RunAsync("owner", id, new(new()), default);
        await pause.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Delete(MonitorHarness.A); h.UpdateSnapshot();
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        pause.Release();
        Assert.Equal("notfound", (await Assert.ThrowsAsync<PortalException>(() => run)).Code);
        Assert.Equal(0, h.Calls("LoadReport")); Assert.Equal(0, h.Portal.Sessions.Snapshot().Count);
    }

    [Fact]
    public async Task FailedSqlRefreshCannotAuthorizeFromPreviousCachedMembership()
    {
        using var h = new MonitorHarness();
        var id = (await h.Portal.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        await h.Monitor.PollAsync(default);
        h.Delete(MonitorHarness.A); h.UpdateSnapshot();
        h.Portal.Mock.FaultTargets["ListChildren|/Training"] = true;
        Assert.Equal(CatalogPollOutcome.Failed, await h.Monitor.PollAsync(default));
        await Assert.ThrowsAsync<PortalException>(() => h.Portal.Execution.RunAsync("owner", id, new(new()), default));
        Assert.Equal(0, h.Calls("LoadReport"));
        h.Portal.Mock.FaultTargets.Clear();
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        await Assert.ThrowsAsync<PortalException>(() => h.Portal.Catalog.ResolveAsync(id, default));
        CatalogCacheTests.MetadataOnly(h.Portal);
    }
}
