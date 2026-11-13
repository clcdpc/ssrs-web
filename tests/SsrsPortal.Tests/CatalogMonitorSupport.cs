using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SsrsPortal.Core;
using SsrsPortal.Mock;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

internal sealed class SyntheticSnapshotProvider : ICatalogSnapshotProvider
{
    public required CatalogSnapshot Snapshot { get; set; }
    public Func<CancellationToken, Task>? BeforeRead { get; set; }
    public int Calls => Volatile.Read(ref _calls);
    public int Active => Volatile.Read(ref _active);
    public int Cancelled => Volatile.Read(ref _cancelled);
    public int Peak => Volatile.Read(ref _peak);
    private int _calls, _active, _cancelled, _peak;
    public async Task<CatalogSnapshotRead> ReadAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        var active = Interlocked.Increment(ref _active);
        _peak = Math.Max(_peak, active);
        var watch = Stopwatch.StartNew();
        try
        {
            if (BeforeRead != null)
            {
                await BeforeRead(ct);
            }
            ct.ThrowIfCancellationRequested();
            watch.Stop();
            // Actual synthetic provider wall time. No database connection, TDS-byte count,
            // credential material, or invented SQL-server duration exists in this fixture.
            return new CatalogSnapshotRead(Snapshot, new SnapshotMeasurement(watch.Elapsed, null));
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}

internal sealed class MonitorHarness : IDisposable
{
    public const string A = "/Training/Reports/A";
    public const string B = "/Training/Reports/B";
    public const string C = "/Training/Reports/C";
    public const string Shared = "/Training/DataSources/Shared";
    public Harness Portal { get; }
    public CatalogMonitorOptions Options { get; } = new() { Enabled = true };
    public SyntheticSnapshotProvider Provider { get; }
    public CaptureLogger<CatalogMonitor> Logger { get; } = new();
    public CatalogMonitor Monitor { get; }
    public Dictionary<string, Guid> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<Guid, SqlDataSource> Sources { get; } = [];
    private int _identity;

    public MonitorHarness(TimeProvider? time = null, Func<HttpMessageHandler, HttpMessageHandler>? wrapper = null, params string[] paths)
    {
        Portal = new Harness(wrapHandler: wrapper);
        SetGraph(paths.Length == 0 ? [A, B, C] : paths);
        Provider = new SyntheticSnapshotProvider { Snapshot = Snapshot() };
        Monitor = new CatalogMonitor(Provider, Portal.Catalog, Options, time ?? Portal.Clock, Logger);
    }

    public void SetGraph(params string[] paths)
    {
        CatalogCacheTests.Graph(Portal, paths);
        Portal.Mock.CatalogItems = Portal.Mock.CatalogItems!.Select(i => i with { Identity = ItemId(i.Path).ToString() }).ToArray();
        Sources.Clear();
        foreach (var path in paths)
        {
            Source(path, null, "stored direct source");
        }
    }
    public Guid ItemId(string path)
    {
        if (!Ids.TryGetValue(path, out var id))
        {
            id = new Guid(++_identity, 0, 0, new byte[8]);
            Ids[path] = id;
        }
        return id;
    }
    public void AddShared(string path, params string[] reports)
    {
        Portal.Mock.CatalogItems = [.. Portal.Mock.CatalogItems!, new(path, "DataSource", Identity: ItemId(path).ToString())];
        Source(path, null, "stored shared source");
        foreach (var report in reports)
        {
            Source(report, path, "shared reference");
        }
    }
    public void Source(string owner, string? link, string configuration)
    {
        var ownerId = ItemId(owner);
        var row = Sources.Values.SingleOrDefault(s => s.ItemId == ownerId);
        var sourceId = row?.DataSourceId ?? new Guid(++_identity, 1, 0, new byte[8]);
        Sources[sourceId] = new SqlDataSource(sourceId, ownerId, link == null ? null : ItemId(link), Fingerprint(configuration));
    }
    public void ModifyCatalog(string path, Func<SyntheticCatalogItem, SyntheticCatalogItem> change)
    {
        Portal.Mock.CatalogItems = Portal.Mock.CatalogItems!.Select(i => i.Path == path ? change(i) : i).ToArray();
    }
    public void Delete(string path)
    {
        Portal.Mock.CatalogItems = Portal.Mock.CatalogItems!.Where(i => i.Path != path).ToArray();
        foreach (var row in Sources.Values.Where(s => s.ItemId == ItemId(path)).ToArray())
        {
            Sources.Remove(row.DataSourceId);
        }
    }
    public CatalogSnapshot Snapshot()
    {
        var catalog = Portal.Mock.CatalogItems!.Select(i => new SqlCatalogItem(Guid.Parse(i.Identity!), i.Path, i.Type switch
        {
            "Folder" => 1, "Report" => 2, "LinkedReport" => 4, "DataSource" => 5, _ => 99
        }, i.ModifiedDate == null ? null : DateTime.Parse(i.ModifiedDate, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal), i.Hidden));
        return new CatalogSnapshot(catalog, Sources.Values, Portal.Options.MaxMetadataItems);
    }
    public void UpdateSnapshot() => Provider.Snapshot = Snapshot();
    public int Calls(string operation, string? path = null) => path == null
        ? Portal.Mock.Calls.Where(p => p.Key.StartsWith(operation + "|", StringComparison.Ordinal)).Sum(p => p.Value)
        : Portal.Mock.Calls.GetValueOrDefault(operation + "|" + path);
    public int TotalSoap => Portal.Mock.Calls.Values.Sum();
    public static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static async Task Until(Func<bool> condition)
    {
        var watchdog = Stopwatch.StartNew();
        while (!condition())
        {
            if (watchdog.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("Deterministic test did not reach its gate.");
            }
            await Task.Yield();
        }
    }
    public void Dispose()
    {
        Monitor.Dispose();
        Portal.Dispose();
    }
}

// This gate holds a response AFTER SOAP produced it, so releasing it can prove that
// an old positive result is rejected. Real sleeps do not define the test's ordering.
internal sealed class ResponsePauseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public TaskCompletionSource Captured { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _key = "";
    private int _remaining;
    public void Pause(string operation, string path)
    {
        _key = operation + "|" + path;
        Captured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _remaining = 1;
    }
    public void Release() => _release.TrySetResult();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = XDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).Root!
            .Element(SyntheticSsrsHandler.Soap + "Body")!.Elements().Single();
        var path = body.Elements().FirstOrDefault(e => e.Name.LocalName is "ItemPath" or "DataSource")?.Value;
        var response = await base.SendAsync(request, ct);
        if (body.Name.LocalName + "|" + path == _key && Interlocked.CompareExchange(ref _remaining, 0, 1) == 1)
        {
            Captured.TrySetResult();
            try { await _release.Task.WaitAsync(ct); }
            catch { response.Dispose(); throw; }
        }
        return response;
    }
}

internal sealed class TimerClock : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<Timer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() { lock (_sync) { return _now; } }
    public int ActiveTimers { get { lock (_sync) { return _timers.Count(t => t.Due != null); } } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }
    public void Advance(TimeSpan amount)
    {
        List<Timer> due;
        lock (_sync)
        {
            _now += amount;
            due = _timers.Where(t => t.Due <= _now).ToList();
            foreach (var timer in due)
            {
                timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? null : _now + timer.Period;
            }
        }
        foreach (var timer in due) { timer.Fire(); }
    }
    private sealed class Timer(TimerClock owner, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public DateTimeOffset? Due { get; set; }
        public TimeSpan Period { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (_disposed) { return false; }
                Period = period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                if (!owner._timers.Contains(this)) { owner._timers.Add(this); }
                return true;
            }
        }
        public void Fire() { if (!_disposed) { callback(state); } }
        public void Dispose()
        {
            lock (owner._sync) { _disposed = true; Due = null; owner._timers.Remove(this); }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
