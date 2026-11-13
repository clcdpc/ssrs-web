using System.Diagnostics;
using System.Text.Json;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;
using Xunit.Abstractions;

namespace SsrsPortal.Tests;

public sealed class CatalogMeasurementTests(ITestOutputHelper output)
{
    internal static SyntheticCatalogItem[] ScaleCatalog() =>
    [
        new("/Training/Reports", "Folder"), new("/Training/Shortcuts", "Folder"),
        new("/Training/Hidden", "Folder", true), new("/Training/Blocked", "Folder"),
        .. Enumerable.Range(0, 50).Select(i => new SyntheticCatalogItem("/Training/Reports/Group " + i.ToString("D2"), "Folder")),
        .. Enumerable.Range(0, 2200).Select(i => new SyntheticCatalogItem("/Training/Reports/Group " + (i % 50).ToString("D2") + "/Report " + i.ToString("D4"), Hidden: i < 25)),
        .. Enumerable.Range(0, 100).Select(i => new SyntheticCatalogItem("/Training/Shortcuts/Alias " + i.ToString("D3"), "LinkedReport")),
        .. Enumerable.Range(0, 400).Select(i => new SyntheticCatalogItem("/Training/Hidden/Hidden " + i)),
        .. Enumerable.Range(0, 800).Select(i => new SyntheticCatalogItem("/Training/Blocked/Blocked " + i)),
        .. Enumerable.Range(0, 446).Select(i => new SyntheticCatalogItem("/Training/Reports/Resource " + i, "Resource"))
    ];

    [Theory]
    [InlineData(false, 1, 1)]
    [InlineData(true, 2, 3)]
    public async Task FourThousandItemsExpose2275ReportsWithOnlyRootAndAncestorDiscovery(bool twoRoots, int lists, int properties)
    {
        using var h = new Harness();
        h.Mock.CatalogItems = ScaleCatalog();
        Assert.Equal(4000, h.Mock.CatalogItems.Length);
        h.Options.AllowedRoots = twoRoots ? ["/Training/Reports", "/Training/Shortcuts"] : ["/Training"];
        h.Options.ExcludedPrefixes = ["/Training/Blocked"];
        var rows = new List<object>();
        async Task<Dictionary<string, int>> Stage(string scenario, Func<Task> action)
        {
            var before = h.Mock.Calls.ToDictionary(p => p.Key, p => p.Value);
            var watch = Stopwatch.StartNew();
            await action(); watch.Stop();
            var operations = h.Mock.Calls.Select(p => (Operation: p.Key.Split('|')[0], Count: p.Value - before.GetValueOrDefault(p.Key)))
                .Where(p => p.Count != 0).GroupBy(p => p.Operation).ToDictionary(g => g.Key, g => g.Sum(p => p.Count));
            rows.Add(new { scenario, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, totalCalls = operations.Values.Sum(), byOperation = operations });
            return operations;
        }
        CatalogEntry[] entries = [];
        var cold = await Stage("cold browse", async () => entries = await h.Catalog.BrowseAsync(default));
        Assert.Equal(2175, entries.Count(e => e.Type == "Report"));
        Assert.Equal(100, entries.Count(e => e.Type == "LinkedReport"));
        Assert.DoesNotContain(entries, e => e.Name.StartsWith("Hidden", StringComparison.Ordinal) || e.Name.StartsWith("Blocked", StringComparison.Ordinal));
        Assert.Equal(lists, cold["ListChildren"]); Assert.Equal(properties, cold["GetProperties"]);
        Assert.Equal(2, cold.Count); CatalogCacheTests.MetadataOnly(h);
        var folders = entries.Where(e => e.Type == "Folder").Select(e => e.Id).ToHashSet();
        Assert.All(entries.Where(e => e.Type is "Report" or "LinkedReport"), e => Assert.Contains(e.ParentId!, folders));
        Assert.Empty(await Stage("five warm browses", async () =>
        {
            for (var i = 0; i < 5; i++) { Assert.Same(entries, await h.Catalog.BrowseAsync(default)); }
        }));
        h.Clock.Advance(TimeSpan.FromSeconds(h.Options.CatalogSeconds + 1));
        var expired = await Stage("catalog expiry", async () => await h.Catalog.BrowseAsync(default));
        Assert.Equal(cold.OrderBy(p => p.Key), expired.OrderBy(p => p.Key));
        var invalidated = await Stage("SQL source notification", async () => await h.Catalog.RefreshFromSqlAsync(new(0, 1), default));
        Assert.Equal(cold.OrderBy(p => p.Key), invalidated.OrderBy(p => p.Key));
        CatalogCacheTests.MetadataOnly(h);
        var run = await Stage("warm report execution", async () =>
            await h.Execution.RunAsync("owner", entries.Single(e => e.Name == "Report 0050").Id, new(new()), default));
        Assert.Equal(1, run["LoadReport"]); Assert.False(run.ContainsKey("ListChildren")); Assert.False(run.ContainsKey("GetProperties"));
        CatalogCacheTests.NoPreflight(h);
        var json = JsonSerializer.Serialize(new { synthetic = true, catalogItems = 4000, visibleReports = 2275,
            configuredRoots = h.Options.AllowedRoots.Length, maxActiveSoap = h.Mock.MaxActiveCalls, rows }, new JsonSerializerOptions { WriteIndented = true });
        var destination = Environment.GetEnvironmentVariable("CATALOG_MEASUREMENTS_OUT");
        if (!string.IsNullOrEmpty(destination)) { await File.WriteAllTextAsync(destination + (twoRoots ? ".two-roots.json" : ".one-root.json"), json); }
        output.WriteLine(json);
    }

    [Fact]
    public async Task ConcurrentScaleBrowsesShareOneCompleteCatalogAndCancelIndependently()
    {
        using var h = new Harness(); h.Mock.CatalogItems = ScaleCatalog(); h.Options.ExcludedPrefixes = ["/Training/Blocked"];
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mock.TargetResponseGates["ListChildren|/Training"] = gate.Task;
        using var cancellation = new CancellationTokenSource();
        var abandoned = h.Catalog.BrowseAsync(cancellation.Token);
        var callers = Enumerable.Range(0, 30).Select(_ => h.Catalog.BrowseAsync(default)).ToArray();
        await CatalogCacheTests.WaitUntil(() => CatalogCacheTests.Count(h, "ListChildren") == 1);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        gate.TrySetResult();
        var catalogs = await Task.WhenAll(callers);
        Assert.All(catalogs, entries => { Assert.Same(catalogs[0], entries); Assert.Equal(2275, entries.Count(e => e.Type is "Report" or "LinkedReport")); });
        Assert.Equal(1, CatalogCacheTests.Count(h, "ListChildren")); Assert.Equal(1, CatalogCacheTests.Count(h, "GetProperties"));
        Assert.Equal(0, h.Mock.ActiveCalls); CatalogCacheTests.MetadataOnly(h);
    }
}
