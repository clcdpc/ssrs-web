using System.Diagnostics;
using System.Text.Json;
using SsrsPortal.Services;
using Xunit.Abstractions;

namespace SsrsPortal.Tests;

public sealed class CatalogMonitorMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RecordActualSyntheticProviderAndSoapActivityAtFourThousandCatalogItems()
    {
        using var h = new MonitorHarness();
        var reports = Enumerable.Range(0, 3947).Select(i => "/Training/Reports/" + (i < 2200 ? "" : "Blocked/") + "Report " + i.ToString("D4")).ToArray();
        var shared = Enumerable.Range(0, 50).Select(i => "/Training/DataSources/Source " + i.ToString("D2")).ToArray();
        h.SetGraph(reports);
        h.Portal.Options.ExcludedPrefixes = ["/Training/Reports/Blocked"];
        h.Portal.Mock.CatalogItems = [.. h.Portal.Mock.CatalogItems!, new("/Training/DataSources", "Folder", Identity: h.ItemId("/Training/DataSources").ToString())];
        for (var i = 0; i < shared.Length; i++)
        {
            h.AddShared(shared[i]);
        }
        for (var i = 0; i < reports.Length; i++)
        {
            h.Source(reports[i], shared[i % shared.Length], "shared source reference");
        }
        h.UpdateSnapshot();
        Assert.Equal(4000, h.Provider.Snapshot.Catalog.Count);
        Assert.Equal(3997, h.Provider.Snapshot.DataSources.Count);
        var measurements = new List<object>();

        async Task<Dictionary<string, int>> Stage(string scenario, Func<Task> action)
        {
            var before = h.Portal.Mock.Calls.ToDictionary(p => p.Key, p => p.Value);
            var status = h.Monitor.Status;
            var watch = Stopwatch.StartNew();
            await action();
            watch.Stop();
            var current = h.Monitor.Status;
            var calls = h.Portal.Mock.Calls.Select(p => new { p.Key, Count = p.Value - before.GetValueOrDefault(p.Key) })
                .Where(p => p.Count != 0).ToDictionary(p => p.Key, p => p.Count);
            var operations = calls.GroupBy(p => p.Key.Split('|')[0]).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
            measurements.Add(new
            {
                scenario, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                syntheticSnapshotProviderCalls = current.QueryAttempts - status.QueryAttempts,
                completeCatalogRowsReturned = current.CatalogRows - status.CatalogRows,
                completeDataSourceRowsReturned = current.DataSourceRows - status.DataSourceRows,
                syntheticProviderLastReadMilliseconds = current.LastRead?.Duration.TotalMilliseconds,
                databaseCalls = (int?)null, databaseQueryMilliseconds = (double?)null, tdsBytesReturned = (long?)null,
                soapTotalCalls = operations.Values.Sum(), soapByOperation = operations,
                cacheInvalidations = current.CacheInvalidations - status.CacheInvalidations,
                detectionToRefreshedCatalogMilliseconds = current.LastRefresh?.TotalMilliseconds
            });
            return operations;
        }

        var startup = await Stage("initial application startup: baseline and cold browse", async () =>
        {
            Assert.Equal(CatalogPollOutcome.Baseline, await h.Monitor.PollAsync(default));
            Assert.Equal(2200, (await h.Portal.Catalog.BrowseAsync(default)).Count(e => e.Type == "Report"));
        });
        Assert.Equal(1, startup["ListChildren"]);
        Assert.Equal(1, startup["GetProperties"]);
        var unchanged = await Stage("ten unchanged SQL snapshots", async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                h.Portal.Clock.Advance(TimeSpan.FromSeconds(30));
                Assert.Equal(CatalogPollOutcome.Unchanged, await h.Monitor.PollAsync(default));
                await h.Portal.Catalog.BrowseAsync(default);
            }
        });
        Assert.Empty(unchanged);
        var report = await Stage("one changed report", async () =>
        {
            h.ModifyCatalog(reports[0], i => i with { ModifiedDate = "2026-10-10T01:00:00Z" });
            h.UpdateSnapshot();
            Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        });
        Assert.Equal(1, report["ListChildren"]);
        var source = await Stage("one changed shared data source", async () =>
        {
            h.Source(shared[1], null, "changed shared-source credential fingerprint");
            h.UpdateSnapshot();
            Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
            Assert.Contains(await h.Portal.Catalog.BrowseAsync(default), e => e.Name == "Report 0001");
        });
        Assert.Equal(1, source["ListChildren"]);
        Assert.False(source.ContainsKey("GetItemDefinition"));
        var deletion = await Stage("one report deletion", async () =>
        {
            h.Delete(reports[2]);
            h.UpdateSnapshot();
            Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        });
        Assert.Equal(1, deletion["ListChildren"]);
        Assert.False(deletion.ContainsKey("GetItemDefinition"));
        var failure = await Stage("SQL snapshot failure", async () =>
        {
            h.Provider.BeforeRead = _ => throw new TimeoutException("synthetic unavailable database");
            Assert.Equal(CatalogPollOutcome.Failed, await h.Monitor.PollAsync(default));
        });
        Assert.Empty(failure);
        var recovery = await Stage("SQL recovery with missed report modification", async () =>
        {
            h.ModifyCatalog(reports[3], i => i with { ModifiedDate = "2026-10-10T02:00:00Z" });
            h.UpdateSnapshot();
            h.Provider.BeforeRead = null;
            Assert.Equal(CatalogPollOutcome.Refreshed, await h.Monitor.PollAsync(default));
        });
        Assert.Equal(1, recovery["ListChildren"]);
        Assert.Equal(0, h.Provider.Active);
        Assert.Equal(1, h.Provider.Peak);
        Assert.Equal(0, h.Portal.Mock.ActiveCalls);
        Assert.Equal(1, h.Portal.Mock.MaxActiveCalls);
        CatalogCacheTests.MetadataOnly(h.Portal);
        var json = JsonSerializer.Serialize(new
        {
            environment = "in-process synthetic SQL snapshot provider and synthetic SSRS SOAP; no SQL Server connected",
            catalogItems = 4000, dataSourceRows = 3997, unblockedReports = 2200,
            sqlServerQueryDurationAndTdsBytes = "unmeasured; run the dev validation matrix for actual database measurements",
            catalogSeconds = h.Portal.Options.CatalogSeconds,
            maxConcurrentSnapshotReads = h.Provider.Peak, maxConcurrentSoap = h.Portal.Mock.MaxActiveCalls, measurements
        }, new JsonSerializerOptions { WriteIndented = true });
        var destination = Environment.GetEnvironmentVariable("CATALOG_MONITOR_MEASUREMENTS_OUT");
        if (!string.IsNullOrEmpty(destination))
        {
            await File.WriteAllTextAsync(destination, json);
        }
        output.WriteLine(json);
    }
}
