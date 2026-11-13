using Microsoft.Data.SqlClient;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class CatalogMonitorTests
{
    [Fact]
    public async Task InitialAndTenUnchangedSnapshotsAddZeroSoapWork()
    {
        using var h = new MonitorHarness();
        var initial = await h.Portal.Catalog.BrowseAsync(default);
        var calls = h.TotalSoap;
        Assert.Equal(CatalogPollOutcome.Baseline, await h.Monitor.PollAsync(default));
        for (var i = 0; i < 10; i++)
        {
            h.Portal.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(CatalogPollOutcome.Unchanged, await h.Monitor.PollAsync(default));
            Assert.Same(initial, await h.Portal.Catalog.BrowseAsync(default));
        }
        Assert.Equal(calls, h.TotalSoap);
        Assert.Equal(11, h.Provider.Calls);
        Assert.Equal(0, h.Monitor.Status.CacheInvalidations);
        Assert.Equal(44, h.Monitor.Status.CatalogRows);
        Assert.Equal(33, h.Monitor.Status.DataSourceRows);
    }

    [Fact]
    public async Task ColdInitialBaselineDoesNotWarmCatalogThroughSoap()
    {
        using var h = new MonitorHarness();
        Assert.Equal(CatalogPollOutcome.Baseline, await h.Monitor.PollAsync(default));
        Assert.Equal(0, h.TotalSoap);
        Assert.Contains(await h.Portal.Catalog.BrowseAsync(default), e => e.Name == "A");
        Assert.Equal(1, h.Calls("ListChildren"));
    }

    [Fact]
    public async Task AddedUnblockedReportIsDiscoveredOnlyAfterAuthoritativeSoapEnumeration()
    {
        using var h = new MonitorHarness();
        const string added = "/Training/Reports/Added";
        await h.Portal.Catalog.BrowseAsync(default);
        await h.Monitor.PollAsync(default);
        h.Portal.Mock.CatalogItems = [.. h.Portal.Mock.CatalogItems!, new(added, Identity: h.ItemId(added).ToString())];
        h.Source(added, null, "direct stored source");
        h.UpdateSnapshot();
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        Assert.Contains(await h.Portal.Catalog.BrowseAsync(default), e => e.Name == "Added");
    }

    [Fact]
    public async Task SqlOnlyAdditionCannotAuthorizeUnlistedOrBlockedReport()
    {
        using var h = new MonitorHarness();
        await h.Portal.Catalog.BrowseAsync(default);
        await h.Monitor.PollAsync(default);
        var phantom = new SqlCatalogItem(h.ItemId("/Training/Reports/Phantom"), "/Training/Reports/Phantom", 2, new DateTime(2026, 10, 10), false);
        h.Provider.Snapshot = new CatalogSnapshot([.. h.Provider.Snapshot.Catalog.Values, phantom], h.Provider.Snapshot.DataSources.Values,
            h.Portal.Options.MaxMetadataItems);
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        Assert.DoesNotContain(await h.Portal.Catalog.BrowseAsync(default), e => e.Name == "Phantom");
        const string blocked = "/Training/Reports/Blocked";
        h.Portal.Options.ExcludedPrefixes = [blocked];
        h.Portal.Mock.CatalogItems = [.. h.Portal.Mock.CatalogItems!, new(blocked, Identity: h.ItemId(blocked).ToString())];
        h.UpdateSnapshot();
        await h.Monitor.PollAsync(default);
        Assert.DoesNotContain(await h.Portal.Catalog.BrowseAsync(default), e => e.Name == "Blocked");
        Assert.Equal(0, h.Calls("GetItemDefinition", phantom.Path));
        Assert.Equal(0, h.Calls("GetItemDefinition", blocked));
    }

    [Fact]
    public async Task DeletionRemovesOldIdentifierBeforeAnotherExecutionCanStart()
    {
        using var h = new MonitorHarness();
        var id = (await h.Portal.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        await h.Monitor.PollAsync(default);
        h.Delete(MonitorHarness.A);
        h.UpdateSnapshot();
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        Assert.DoesNotContain(await h.Portal.Catalog.BrowseAsync(default), e => e.Id == id);
        var calls = h.TotalSoap;
        var denied = await Assert.ThrowsAsync<PortalException>(() => h.Portal.Execution.RunAsync("owner", id, Harness.Inputs(), default));
        Assert.Equal("notfound", denied.Code);
        Assert.Equal(calls, h.TotalSoap);
        Assert.Equal(0, h.Calls("LoadReport"));
    }

    [Theory]
    [InlineData("/Training/Reports/Renamed")]
    [InlineData("/Training/Moved/A")]
    public async Task RenameOrMoveUsesStableItemIdButWithdrawsTheOldPathAndBrowserId(string newPath)
    {
        using var h = new MonitorHarness();
        var id = (await h.Portal.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        await h.Monitor.PollAsync(default);
        h.ModifyCatalog(MonitorHarness.A, i => i with { Path = newPath });
        if (newPath.StartsWith("/Training/Moved/", StringComparison.Ordinal))
        {
            h.Portal.Mock.CatalogItems = [.. h.Portal.Mock.CatalogItems!, new("/Training/Moved", "Folder", Identity: h.ItemId("/Training/Moved").ToString())];
        }
        h.UpdateSnapshot();
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        var entries = await h.Portal.Catalog.BrowseAsync(default);
        Assert.DoesNotContain(entries, e => e.Id == id);
        Assert.Contains(entries, e => e.Name == newPath.Split('/').Last());
        Assert.Equal(MonitorHarness.A, h.Ids.Single(p => p.Value == h.Provider.Snapshot.Catalog.Values.Single(i => i.Path == newPath).ItemId).Key);
        await Assert.ThrowsAsync<PortalException>(() => h.Portal.Catalog.ResolveAsync(id, default));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("type")]
    [InlineData("folder")]
    public async Task VisibilityAndTypeChangesWithdrawReports(string change)
    {
        using var h = new MonitorHarness();
        var id = (await h.Portal.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        await h.Monitor.PollAsync(default);
        h.ModifyCatalog(change == "folder" ? "/Training/Reports" : MonitorHarness.A,
            i => change == "type" ? i with { Type = "Folder" } : i with { Hidden = true });
        h.UpdateSnapshot();
        await h.Monitor.PollAsync(default);
        Assert.DoesNotContain(await h.Portal.Catalog.BrowseAsync(default), e => e.Id == id);
        await Assert.ThrowsAsync<PortalException>(() => h.Portal.Catalog.ResolveAsync(id, default));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NullableHiddenKeepsAllThreeStatesDistinctAndDetectsEveryTransition(bool? before, bool? after)
    {
        using var h = new MonitorHarness();
        var original = h.Provider.Snapshot;
        CatalogSnapshot WithHidden(bool? hidden) => new(original.Catalog.Values.Select(item =>
            item.Path == MonitorHarness.A ? item with { Hidden = hidden } : item), original.DataSources.Values, h.Portal.Options.MaxMetadataItems);
        var previous = WithHidden(before);
        var current = WithHidden(after);
        Assert.Equal(before, previous.Catalog[h.ItemId(MonitorHarness.A)].Hidden);
        Assert.Equal(after, current.Catalog[h.ItemId(MonitorHarness.A)].Hidden);
        var change = current.ChangesFrom(previous);
        Assert.Equal(before != after, change.Changed);
        Assert.Equal(before != after ? 1 : 0, change.CatalogItemsChanged);
        Assert.Equal(0, change.DataSourcesChanged);
        Assert.False(change.ForceRefresh);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlHiddenValuesOnlyTriggerReconciliationAndCannotOverrideSoapVisibility(bool? sqlHidden)
    {
        using var h = new MonitorHarness();
        var id = (await h.Portal.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        var initialHidden = sqlHidden == false ? true : false;
        CatalogSnapshot WithHidden(bool? hidden) => new(h.Snapshot().Catalog.Values.Select(item =>
            item.Path == MonitorHarness.A ? item with { Hidden = hidden } : item), h.Sources.Values, h.Portal.Options.MaxMetadataItems);
        h.Provider.Snapshot = WithHidden(initialHidden);
        await h.Monitor.PollAsync(default);
        h.ModifyCatalog(MonitorHarness.A, item => item with { Hidden = true });
        h.Provider.Snapshot = WithHidden(sqlHidden);
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        Assert.DoesNotContain(await h.Portal.Catalog.BrowseAsync(default), entry => entry.Id == id);
        await Assert.ThrowsAsync<PortalException>(() => h.Portal.Execution.RunAsync("owner", id, Harness.Inputs(), default));
        Assert.Equal(0, h.Calls("LoadReport"));
        Assert.Equal(CatalogPollOutcome.Unchanged, await h.Monitor.PollAsync(default));
    }

    [Fact]
    public async Task SqlFailurePreservesFallbackAndUnchangedRecoveryForcesFullSoapReconciliation()
    {
        using var h = new MonitorHarness();
        var entries = await h.Portal.Catalog.BrowseAsync(default);
        var id = entries.Single(e => e.Name == "A").Id;
        await h.Monitor.PollAsync(default);
        h.Provider.BeforeRead = _ => throw new InvalidOperationException("Server=private;Password=do-not-log; raw query fault");
        var before = h.TotalSoap;
        Assert.Equal(CatalogPollOutcome.Failed, await h.Monitor.PollAsync(default));
        Assert.Same(entries, await h.Portal.Catalog.BrowseAsync(default));
        Assert.Equal(before, h.TotalSoap);
        h.Portal.Mock.RequiredCredentials[MonitorHarness.A] = true;
        // SSRS rejects unsupported credentials during an outage; metadata stays discoverable.
        h.Portal.Clock.Advance(TimeSpan.FromHours(6));
        var error = await Assert.ThrowsAsync<PortalException>(() => h.Portal.Execution.RunAsync("owner", id, new(new()), default));
        Assert.Equal("credentials", error.Code);
        Assert.Equal(1, h.Calls("LoadReport"));
        var lists = h.Calls("ListChildren");
        Assert.Contains(await h.Portal.Catalog.BrowseAsync(default), e => e.Name == "A");
        Assert.Equal(lists, h.Calls("ListChildren"));
        h.Provider.BeforeRead = null;
        // The SQL rows may match the last baseline despite an outage; recovery still revalidates.
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        Assert.Contains(await h.Portal.Catalog.BrowseAsync(default), e => e.Name == "A");
        Assert.Equal(1, h.Monitor.Status.CacheInvalidations);
        Assert.Contains(h.Logger.Messages, m => m.Contains("sql stage failed", StringComparison.Ordinal));
        Assert.All(h.Logger.Messages, m => Assert.DoesNotContain("do-not-log", m));
        Assert.Contains(h.Logger.Messages, m => m.Contains("Server=private", StringComparison.Ordinal) && m.Contains("raw query fault", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissedDeletionDuringOutageIsReconciledOnRecovery()
    {
        using var h = new MonitorHarness();
        var id = (await h.Portal.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        await h.Monitor.PollAsync(default);
        h.Provider.BeforeRead = _ => throw new TimeoutException("unavailable database");
        await h.Monitor.PollAsync(default);
        h.Delete(MonitorHarness.A);
        h.UpdateSnapshot();
        h.Provider.BeforeRead = null;
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        Assert.DoesNotContain(await h.Portal.Catalog.BrowseAsync(default), e => e.Id == id);
        await Assert.ThrowsAsync<PortalException>(() => h.Portal.Catalog.ResolveAsync(id, default));
    }

    [Fact]
    public async Task PartialSnapshotFailureNeverAdvancesBaselineAndSoapFailureKeepsReconciliationPending()
    {
        using var h = new MonitorHarness();
        await h.Portal.Catalog.BrowseAsync(default);
        await h.Monitor.PollAsync(default);
        h.Provider.BeforeRead = _ =>
        {
            // Simulate a reader that got Catalog rows before DataSource failed. It must throw,
            // rather than constructing a complete snapshot from that first result set.
            throw new CatalogSnapshotException();
        };
        Assert.Equal(CatalogPollOutcome.Failed, await h.Monitor.PollAsync(default));
        Assert.Equal(1, h.Monitor.Status.SuccessfulSnapshots);
        h.Provider.BeforeRead = null;
        h.Portal.Mock.FaultTargets["ListChildren|/Training"] = true;
        Assert.Equal(CatalogPollOutcome.Failed, await h.Monitor.PollAsync(default));
        Assert.Contains(h.Logger.Messages, m => m.Contains("soap stage failed", StringComparison.Ordinal));
        h.Portal.Mock.FaultTargets.Clear();
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        var calls = h.TotalSoap;
        Assert.Equal(CatalogPollOutcome.Unchanged, await h.Monitor.PollAsync(default));
        Assert.Equal(calls, h.TotalSoap);
        Assert.Equal(2, h.Monitor.Status.CacheInvalidations);
    }

    [Fact]
    public async Task OverlappingPollsAreSkippedAndCancelledReadCanRecoverWithoutPoisoningGate()
    {
        using var h = new MonitorHarness();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Provider.BeforeRead = ct => gate.Task.WaitAsync(ct);
        using var cancellation = new CancellationTokenSource();
        var first = h.Monitor.PollAsync(cancellation.Token);
        await MonitorHarness.Until(() => h.Provider.Active == 1);
        var overlapping = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => h.Monitor.PollAsync(default)));
        Assert.All(overlapping, result => Assert.Equal(CatalogPollOutcome.Skipped, result));
        Assert.Equal(1, h.Provider.Calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(0, h.Provider.Active);
        Assert.Equal(1, h.Provider.Cancelled);
        Assert.Equal(1, h.Provider.Peak);
        h.Provider.BeforeRead = null;
        Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        Assert.Equal(1, h.Calls("ListChildren"));
        Assert.Equal(0, h.Portal.Mock.ActiveCalls);
    }

    [Fact]
    public async Task InFlightEnumerationCannotPublishOldPositiveAfterSqlDeletion()
    {
        ResponsePauseHandler? pause = null;
        using var h = new MonitorHarness(wrapper: inner => pause = new ResponsePauseHandler(inner));
        var id = (await h.Portal.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        await h.Monitor.PollAsync(default);
        h.Portal.Clock.Advance(TimeSpan.FromSeconds(h.Portal.Options.CatalogSeconds + 1));
        pause!.Pause("ListChildren", "/Training");
        var older = h.Portal.Catalog.BrowseAsync(default);
        await pause.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Delete(MonitorHarness.A);
        h.UpdateSnapshot();
        var poll = h.Monitor.PollAsync(default);
        await MonitorHarness.Until(() => h.Monitor.Status.CacheInvalidations == 1);
        pause.Release();
        Assert.Equal(CatalogPollOutcome.Refreshed, await poll);
        Assert.DoesNotContain(await older, e => e.Id == id);
        Assert.DoesNotContain(await h.Portal.Catalog.BrowseAsync(default), e => e.Id == id);
        Assert.Equal(3, h.Calls("ListChildren")); // Cold + superseded + coalesced current enumeration.
        await Assert.ThrowsAsync<PortalException>(() => h.Portal.Catalog.ResolveAsync(id, default));
    }

    [Fact]
    public async Task RepeatedChangesDuringOneEnumerationCoalesceSoapWithoutStalePublication()
    {
        ResponsePauseHandler? pause = null;
        using var h = new MonitorHarness(wrapper: inner => pause = new ResponsePauseHandler(inner));
        await h.Portal.Catalog.BrowseAsync(default);
        pause!.Pause("ListChildren", "/Training");
        var first = h.Portal.Catalog.RefreshFromSqlAsync(new(1, 0), default);
        await pause.Captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Delete(MonitorHarness.A);
        h.Delete(MonitorHarness.B);
        var second = h.Portal.Catalog.RefreshFromSqlAsync(new(1, 0), default);
        pause.Release();
        await Task.WhenAll(first, second);
        Assert.DoesNotContain(await h.Portal.Catalog.BrowseAsync(default), e => e.Name is "A" or "B");
        Assert.Equal(3, h.Calls("ListChildren"));
    }

    [Fact]
    public async Task HostedPollingUsesTimeProviderAndWaitsAnIntervalAfterFailure()
    {
        var clock = new TimerClock();
        using var h = new MonitorHarness(clock);
        await h.Monitor.StartAsync(default);
        await MonitorHarness.Until(() => h.Provider.Calls == 1 && clock.ActiveTimers == 1);
        h.Provider.BeforeRead = _ => throw new TimeoutException("synthetic failure");
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, h.Provider.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await MonitorHarness.Until(() => h.Provider.Calls == 2 && h.Monitor.Status.Outcome == CatalogPollOutcome.Failed && clock.ActiveTimers == 1);
        Assert.Equal(0, h.TotalSoap);
        h.Provider.BeforeRead = null;
        clock.Advance(TimeSpan.FromSeconds(30));
        await MonitorHarness.Until(() => h.Monitor.Status.Outcome == CatalogPollOutcome.Refreshed && clock.ActiveTimers == 1);
        Assert.Equal(3, h.Provider.Calls);
        await h.Monitor.StopAsync(default);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Equal(0, h.Provider.Active);
        Assert.True(h.Monitor.ExecuteTask!.IsCompleted);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("soap")]
    public async Task ShutdownCancelsAndDrainsInFlightQueryAndSharedSoapProducer(string stage)
    {
        var clock = new TimerClock();
        using var h = new MonitorHarness(clock);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stage == "query")
        {
            h.Provider.BeforeRead = ct => gate.Task.WaitAsync(ct);
        }
        await h.Monitor.StartAsync(default);
        if (stage == "query")
        {
            await MonitorHarness.Until(() => h.Provider.Active == 1);
        }
        else
        {
            await MonitorHarness.Until(() => h.Provider.Calls == 1 && clock.ActiveTimers == 1);
            h.ModifyCatalog(MonitorHarness.A, i => i with { ModifiedDate = "2026-10-10T01:00:00Z" });
            h.UpdateSnapshot();
            h.Portal.Mock.TargetResponseGates["ListChildren|/Training"] = gate.Task;
            clock.Advance(TimeSpan.FromSeconds(30));
            await MonitorHarness.Until(() => h.Portal.Mock.ActiveCalls == 1);
        }
        await h.Monitor.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, h.Provider.Active);
        Assert.Equal(0, h.Portal.Mock.ActiveCalls);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.True(h.Monitor.ExecuteTask!.IsCompleted);
        Assert.Equal(stage == "query" ? 1 : 0, h.Provider.Cancelled);
    }

    [Fact]
    public async Task ShutdownRemainsIdempotentWhenServiceProviderDisposedCatalogBeforeHostedStop()
    {
        using var h = new MonitorHarness();
        h.Options.Enabled = false;
        await h.Monitor.StartAsync(default);
        h.Portal.Catalog.Dispose();
        await h.Monitor.StopAsync(default);
        await h.Monitor.StopAsync(default);
        await h.Portal.Catalog.StopAsync(default);
        Assert.Equal(0, h.Provider.Active);
        Assert.Equal(0, h.Portal.Mock.ActiveCalls);
    }

    [Fact]
    public async Task DisabledMonitorPreservesOrdinaryCatalogCacheBehaviorAndDoesNoSqlWork()
    {
        using var h = new MonitorHarness();
        h.Options.Enabled = false;
        await h.Monitor.StartAsync(default);
        Assert.Equal(CatalogPollOutcome.Disabled, await h.Monitor.PollAsync(default));
        Assert.Equal(0, h.Provider.Calls);
        Assert.Equal(0, h.TotalSoap);
        var first = await h.Portal.Catalog.BrowseAsync(default);
        var calls = h.TotalSoap;
        Assert.Same(first, await h.Portal.Catalog.BrowseAsync(default));
        Assert.Equal(calls, h.TotalSoap);
        h.Portal.Clock.Advance(TimeSpan.FromSeconds(h.Portal.Options.CatalogSeconds + 1));
        await h.Portal.Catalog.BrowseAsync(default);
        Assert.Equal(calls + 2, h.TotalSoap); // Root Hidden metadata plus ListChildren; SQL remains disabled.
        Assert.Equal(0, h.Provider.Calls);
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(3601, 5)]
    [InlineData(30, 0)]
    [InlineData(30, 121)]
    public void MonitorConfigurationBoundsAreValidated(int interval, int timeout)
    {
        Assert.Throws<InvalidOperationException>(() => new CatalogMonitorOptions { IntervalSeconds = interval, CommandTimeoutSeconds = timeout }.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Server=synthetic.invalid;Database=ReportServer;Integrated Security=true;Encrypt=false")]
    [InlineData("Server=synthetic.invalid;Database=ReportServer;Integrated Security=true;TrustServerCertificate=true")]
    [InlineData("Server=synthetic.invalid;Database=ReportServer;User ID=private-user;Password=do-not-log")]
    [InlineData("Server=synthetic.invalid;Database=ReportServer;Integrated Security=true;User ID=private-user;Password=do-not-log")]
    [InlineData("Server=synthetic.invalid;Integrated Security=true")]
    [InlineData("Server=synthetic.invalid;Database=ReportServer;Integrated Security=true;Persist Security Info=true")]
    [InlineData("Server=synthetic.invalid;Database=ReportServer;Authentication=Active Directory Integrated")]
    [InlineData("Server=do-not-log;Database=ReportServer;Integrated Security=true;UnknownOption=private-value")]
    public void EnabledMonitorRejectsInsecureOrIncompleteConnectionsWithoutEchoingSecrets(string connection)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new CatalogMonitorOptions { Enabled = true, ConnectionString = connection }.Validate());
        Assert.DoesNotContain("do-not-log", ex.ToString());
        Assert.DoesNotContain("private-user", ex.ToString());
        Assert.Null(ex.InnerException);
    }

    [Theory]
    [InlineData("Mandatory")]
    [InlineData("Strict")]
    public void ValidConnectionEnforcesReadIntentAndBoundedNonRetryingConnection(string encrypt)
    {
        var options = new CatalogMonitorOptions
        {
            Enabled = true, ConnectionString = "Server=synthetic.invalid;Database=ReportServer;Integrated Security=true;Encrypt=" + encrypt + ";TrustServerCertificate=false;Connect Timeout=0"
        };
        options.Validate();
        var sql = new SqlConnectionStringBuilder(options.ValidatedConnectionString());
        Assert.True(sql.IntegratedSecurity);
        Assert.Equal(ApplicationIntent.ReadOnly, sql.ApplicationIntent);
        Assert.False(sql.TrustServerCertificate);
        Assert.Equal(0, sql.ConnectRetryCount);
        Assert.Equal(5, sql.ConnectTimeout);
    }

    [Fact]
    public void IncompleteDuplicateOrOverLimitSnapshotsAreRejectedBeforeTheyCanBecomeBaselines()
    {
        using var h = new MonitorHarness();
        var item = h.Provider.Snapshot.Catalog.Values.First();
        var source = h.Provider.Snapshot.DataSources.Values.First();
        Assert.Throws<CatalogSnapshotException>(() => new CatalogSnapshot([item, item], [], 6000));
        Assert.Throws<CatalogSnapshotException>(() => new CatalogSnapshot([], [source, source], 6000));
        Assert.Throws<CatalogSnapshotException>(() => new CatalogSnapshot(h.Provider.Snapshot.Catalog.Values, [], 1));
        Assert.Throws<CatalogSnapshotException>(() => new CatalogSnapshot([], [source with { Fingerprint = "partial" }], 6000));
        Assert.Throws<CatalogSnapshotException>(() => new CatalogSnapshot([item with { ItemId = Guid.Empty }], [], 6000));
        Assert.Throws<CatalogSnapshotException>(() => new CatalogSnapshot([], [source with { DataSourceId = Guid.Empty }], 6000));
        Assert.Throws<CatalogSnapshotException>(() => new CatalogSnapshot([], [], 0));
        Assert.Throws<CatalogSnapshotException>(() => new CatalogSnapshot([], [], 10001));
    }
}
