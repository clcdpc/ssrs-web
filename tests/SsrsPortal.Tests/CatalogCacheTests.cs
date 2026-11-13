using System.Collections.Concurrent;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class CatalogCacheTests
{
    private const string A = "/Training/Reports/A";
    private const string B = "/Training/Reports/B";

    internal static void Graph(Harness h, params string[] paths)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Training/Reports" };
        foreach (var path in paths)
        {
            for (var parent = path[..path.LastIndexOf('/')]; PathPolicy.Canonical(parent) && parent != SyntheticSsrsHandler.Root; parent = parent[..parent.LastIndexOf('/')])
            {
                folders.Add(parent);
            }
        }
        h.Mock.CatalogItems = [.. folders.Order(StringComparer.Ordinal).Select(p => new SyntheticCatalogItem(p, "Folder")), .. paths.Select(p => new SyntheticCatalogItem(p))];
    }

    internal static int Count(Harness h, string operation) => h.Mock.Calls.Where(p => p.Key.StartsWith(operation + "|", StringComparison.Ordinal)).Sum(p => p.Value);
    internal static void MetadataOnly(Harness h) => Assert.All(h.Mock.Calls.Keys,
        key => Assert.True(key.StartsWith("ListChildren|", StringComparison.Ordinal) || key.StartsWith("GetProperties|", StringComparison.Ordinal), key));
    internal static void NoPreflight(Harness h) => Assert.DoesNotContain(h.Mock.Calls.Keys, key =>
        key.StartsWith("GetItemDefinition|", StringComparison.Ordinal) || key.StartsWith("GetItemDataSources|", StringComparison.Ordinal) ||
        key.StartsWith("GetDataSourceContents|", StringComparison.Ordinal) || key.StartsWith("GetItemLink|", StringComparison.Ordinal));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownPathsIdsAndFolderIdsCannotStartCatalogWork(bool expired)
    {
        using var h = new Harness(); Graph(h, A, B);
        var established = await h.Catalog.BrowseAsync(default);
        if (expired) { h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1)); }
        var before = h.Mock.Calls.Values.Sum();
        foreach (var id in new[] { A, new string('a', 64), established.Single(e => e.Name == "Reports").Id })
        {
            var denied = await Assert.ThrowsAsync<PortalException>(() => h.Execution.RunAsync("one", id, new(new()), default));
            Assert.Equal("notfound", denied.Code); Assert.Equal(404, denied.Status);
        }
        Assert.Equal(before, h.Mock.Calls.Values.Sum());
        Assert.Equal(A, (await h.Catalog.ResolveAsync(established.Single(e => e.Name == "A").Id, default)).Path);
        Assert.Equal(expired ? 2 : 1, Count(h, "ListChildren"));
        MetadataOnly(h);
    }

    [Fact]
    public async Task ColdUnknownIdentifierCannotWarmTheCatalog()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<PortalException>(() => h.Catalog.ResolveAsync(new string('a', 64), default));
        Assert.Empty(h.Mock.Calls);
    }

    [Fact]
    public async Task WarmBrowsingAndExecutionReuseCatalogUntilMetadataExpiry()
    {
        using var h = new Harness(); Graph(h, A, B);
        var entries = await h.Catalog.BrowseAsync(default);
        for (var i = 0; i < 5; i++) { Assert.Same(entries, await h.Catalog.BrowseAsync(default)); }
        await h.Execution.RunAsync("one", entries.Single(e => e.Name == "A").Id, new(new()), default);
        await h.Execution.RunAsync("two", entries.Single(e => e.Name == "A").Id, new(new()), default);
        Assert.Equal(1, Count(h, "ListChildren")); Assert.Equal(1, Count(h, "GetProperties"));
        h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1));
        var refreshed = await h.Catalog.BrowseAsync(default);
        Assert.Equal(entries, refreshed); Assert.Same(refreshed, await h.Catalog.BrowseAsync(default));
        Assert.Equal(2, Count(h, "ListChildren")); Assert.Equal(2, Count(h, "GetProperties"));
        NoPreflight(h);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("hidden")]
    [InlineData("rename")]
    [InlineData("type")]
    public async Task ExpirationWithdrawsRemovedHiddenRenamedOrReconfiguredItems(string change)
    {
        using var h = new Harness(); Graph(h, A, B);
        var id = (await h.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        h.Mock.CatalogItems = h.Mock.CatalogItems!.Where(i => change != "remove" || i.Path != A).Select(i => i.Path != A ? i : change switch
        {
            "hidden" => i with { Hidden = true }, "rename" => i with { Path = A + " renamed" }, _ => i with { Type = "Resource" }
        }).ToArray();
        h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1));
        await Assert.ThrowsAsync<PortalException>(() => h.Catalog.ResolveAsync(id, default));
        Assert.Equal(0, Count(h, "LoadReport")); MetadataOnly(h);
    }

    [Fact]
    public async Task RemovedReportInWarmMetadataIsRejectedBySsrsAndNoSessionSurvives()
    {
        using var h = new Harness(); Graph(h, A);
        var id = (await h.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        h.Mock.CatalogItems = h.Mock.CatalogItems!.Where(i => i.Path != A).ToArray();
        var error = await Assert.ThrowsAsync<PortalException>(() => h.Execution.RunAsync("owner", id, new(new()), default));
        Assert.Equal("service", error.Code); Assert.Equal(0, h.Sessions.Snapshot().Count);
        Assert.Equal(1, Count(h, "ListChildren")); NoPreflight(h);
    }

    [Fact]
    public async Task ConcurrentBrowsesShareMetadataAndCancelledWaiterLeavesProducerAvailable()
    {
        using var h = new Harness(); Graph(h, A, B);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mock.TargetResponseGates["ListChildren|/Training"] = gate.Task;
        using var cancellation = new CancellationTokenSource();
        var abandoned = h.Catalog.BrowseAsync(cancellation.Token);
        var others = Enumerable.Range(0, 30).Select(_ => h.Catalog.BrowseAsync(default)).ToArray();
        await WaitUntil(() => Count(h, "ListChildren") == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        gate.TrySetResult();
        var results = await Task.WhenAll(others);
        Assert.All(results, entries => Assert.Same(results[0], entries));
        Assert.Equal(1, Count(h, "ListChildren")); Assert.Equal(0, h.Mock.ActiveCalls); MetadataOnly(h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedCatalogWaitersUseNewerMetadata(bool hidden)
    {
        using var h = new Harness(); Graph(h, A, B);
        var id = (await h.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mock.TargetResponseGates["ListChildren|/Training"] = gate.Task;
        var ordinary = h.Catalog.BrowseAsync(default);
        await WaitUntil(() => Count(h, "ListChildren") == 2);
        var paused = new PausedContinuations();
        Task<CatalogEntry[]> browse;
        Task<CatalogReport> resolve;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(paused);
            browse = h.Catalog.BrowseAsync(default);
            resolve = h.Catalog.ResolveAsync(id, default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        gate.TrySetResult();
        Assert.Contains(await ordinary, e => e.Name == "A");
        await WaitUntil(() => paused.Count == 2);
        h.Mock.CatalogItems = h.Mock.CatalogItems!.Where(i => hidden || i.Path != A)
            .Select(i => i.Path == A ? i with { Hidden = true } : i).ToArray();
        h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1));
        var current = await h.Catalog.BrowseAsync(default);
        Assert.DoesNotContain(current, e => e.Name == "A");
        await paused.ResumeAsync(browse, resolve);
        Assert.Same(current, await browse);
        Assert.Equal(404, (await Assert.ThrowsAsync<PortalException>(() => resolve)).Status);
        Assert.Equal(3, Count(h, "ListChildren")); MetadataOnly(h);
    }

    [Fact]
    public async Task DelayedResolutionRejectsCatalogReplacedAfterItsAwait()
    {
        using var h = new Harness(); Graph(h, A, B);
        var id = (await h.Catalog.BrowseAsync(default)).Single(e => e.Name == "A").Id;
        h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mock.TargetResponseGates["ListChildren|/Training"] = gate.Task;
        var ordinary = h.Catalog.BrowseAsync(default);
        await WaitUntil(() => Count(h, "ListChildren") == 2);
        var paused = new PausedContinuations();
        Task<CatalogReport> resolve;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(paused);
            resolve = h.Catalog.ResolveAsync(id, default);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        gate.TrySetResult();
        await ordinary;
        await WaitUntil(() => paused.Count == 1);
        // Let GetCatalogAsync accept A, but hold ResolveAsync's following continuation.
        Assert.True(paused.RunOne());
        await WaitUntil(() => paused.Count == 1);
        Assert.False(resolve.IsCompleted);
        h.Mock.CatalogItems = h.Mock.CatalogItems!.Where(i => i.Path != A).ToArray();
        h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1));
        Assert.DoesNotContain(await h.Catalog.BrowseAsync(default), e => e.Name == "A");
        await paused.ResumeAsync(resolve);
        Assert.Equal(404, (await Assert.ThrowsAsync<PortalException>(() => resolve)).Status);
        Assert.Equal(3, Count(h, "ListChildren")); MetadataOnly(h);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedRefreshPublishesNoPartialCatalogAndRetriesImmediately(bool cold)
    {
        using var h = new Harness(); Graph(h, A, B);
        if (!cold) { await h.Catalog.BrowseAsync(default); h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1)); }
        h.Mock.FaultTargets["ListChildren|/Training"] = true;
        Assert.Equal("service", (await Assert.ThrowsAsync<PortalException>(() => h.Catalog.BrowseAsync(default))).Code);
        h.Mock.CatalogItems = h.Mock.CatalogItems!.Where(i => i.Path != A).ToArray();
        h.Mock.FaultTargets.Clear();
        var recovered = await h.Catalog.BrowseAsync(default);
        Assert.DoesNotContain(recovered, e => e.Name == "A"); Assert.Contains(recovered, e => e.Name == "B");
        Assert.Equal(cold ? 2 : 3, Count(h, "ListChildren")); MetadataOnly(h);
    }

    [Fact]
    public async Task SharedRefreshDeadlineDrainsSoapAndCanRetryImmediately()
    {
        using var h = new Harness(o => o.RequestSeconds = 1, transportRequestSeconds: 10); Graph(h, A);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mock.TargetResponseGates["ListChildren|/Training"] = gate.Task;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Catalog.BrowseAsync(default));
        await WaitUntil(() => h.Mock.ActiveCalls == 0);
        h.Mock.TargetResponseGates.Clear();
        Assert.Contains(await h.Catalog.BrowseAsync(default), e => e.Name == "A");
        Assert.Equal(2, Count(h, "ListChildren")); MetadataOnly(h);
    }

    [Fact]
    public async Task PolicyChangeDuringRefreshCannotPublishOldMembership()
    {
        ResponsePauseHandler? pause = null;
        using var h = new Harness(wrapHandler: inner => pause = new ResponsePauseHandler(inner)); Graph(h, A, B);
        pause!.Pause("ListChildren", "/Training");
        var browse = h.Catalog.BrowseAsync(default);
        await pause.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Options.ExcludedPrefixes = [A]; pause.Release();
        Assert.Equal("catalog", (await Assert.ThrowsAsync<PortalException>(() => browse)).Code);
        var current = await h.Catalog.BrowseAsync(default);
        Assert.DoesNotContain(current, e => e.Name == "A"); Assert.Contains(current, e => e.Name == "B");
        MetadataOnly(h);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void CatalogLifetimeBoundsAreValidated(int seconds)
    {
        using var h = new Harness(); h.Options.CatalogSeconds = seconds;
        Assert.Throws<InvalidOperationException>(() => h.Options.Validate(true));
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(5, timeout.Token); }
    }

    private sealed class PausedContinuations : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public int Count => _callbacks.Count;
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public bool RunOne()
        {
            if (!_callbacks.TryDequeue(out var work)) { return false; }
            var previous = Current;
            try
            {
                // A later await must post separately so either consumer boundary can be paused.
                SetSynchronizationContext(null);
                work.Callback(work.State);
            }
            finally { SetSynchronizationContext(previous); }
            return true;
        }
        public async Task ResumeAsync(params Task[] tasks)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (tasks.Any(t => !t.IsCompleted))
            {
                while (RunOne()) { }
                if (tasks.Any(t => !t.IsCompleted)) { await Task.Delay(5, timeout.Token); }
            }
        }
    }
}
