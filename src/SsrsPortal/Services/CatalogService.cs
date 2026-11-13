using System.Security.Cryptography;
using System.Text;
using SsrsPortal.Core;
using SsrsPortal.Soap;

namespace SsrsPortal.Services;

public sealed class CatalogService(SsrsClient soap, PortalOptions options, TimeProvider time, ILogger<CatalogService>? logger = null) : IDisposable
{
    private sealed record Catalog(CatalogEntry[] Entries, Dictionary<string, CatalogReport> Reports, Dictionary<string, CatalogReport> Paths,
        string Policy, long Generation, DateTimeOffset Until);
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Catalog? _catalog;
    private Task<Catalog>? _refreshing;
    private long _generation;
    private bool _disposed;

    private string Id(string path) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(path))).ToLowerInvariant();

    public async Task<CatalogEntry[]> BrowseAsync(CancellationToken ct) => (await GetCatalogAsync(ct)).Entries;

    public async Task<CatalogReport> ResolvePathAsync(string path, CancellationToken ct)
    {
        if (!PathPolicy.Permitted(path, options)) { throw new PortalException("notfound", 404); }
        while (true)
        {
            var snapshot = await GetCatalogAsync(ct);
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                if (!ReferenceEquals(snapshot, _catalog) || snapshot.Generation != _generation ||
                    snapshot.Policy != PathPolicy.Identity(options) || time.GetUtcNow() >= snapshot.Until) { continue; }
                if (!snapshot.Paths.TryGetValue(path, out var report) || !PathPolicy.Permitted(report.Path, options))
                {
                    throw new PortalException("notfound", 404);
                }
                return report;
            }
        }
    }

    public async Task<(CatalogReport Report, BreadcrumbNavigation Breadcrumbs)> WorkspaceAsync(string id, CancellationToken ct)
    {
        // Retain the opaque-ID/policy gate and build ancestry from one authorized
        // snapshot, never from path segments or caller-supplied names.
        await ResolveAsync(id, ct);
        while (true)
        {
            var snapshot = await GetCatalogAsync(ct);
            lock (_sync)
            {
                if (!ReferenceEquals(snapshot, _catalog) || snapshot.Generation != _generation || snapshot.Policy != PathPolicy.Identity(options))
                {
                    continue;
                }
                if (!snapshot.Reports.TryGetValue(id, out var report)) { throw new PortalException("notfound", 404); }
                return (report, BreadcrumbNavigation.ForReport(snapshot.Entries, report));
            }
        }
    }

    public async Task RefreshFromSqlAsync(CatalogChange change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!change.Changed)
        {
            return;
        }
        lock (_sync)
        {
            // SQL only invalidates metadata. It cannot admit report IDs or publish catalog items.
            _generation++;
        }
        await GetCatalogAsync(ct);
    }

    public async Task<CatalogReport> ResolveAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            // Unknown IDs (including folder IDs and raw paths) cannot start any SOAP work.
            if (_catalog == null || !_catalog.Reports.TryGetValue(id, out var known) || !PathPolicy.Permitted(known.Path, options))
            {
                throw new PortalException("notfound", 404);
            }
        }
        while (true)
        {
            var catalog = await GetCatalogAsync(ct);
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                // A TTL refresh can replace the snapshot without changing policy/generation.
                // Resolution may resume after that publication even if its catalog await was current.
                if (!ReferenceEquals(catalog, _catalog) || catalog.Generation != _generation ||
                    catalog.Policy != PathPolicy.Identity(options) || time.GetUtcNow() >= catalog.Until)
                {
                    continue;
                }
                if (catalog.Reports.TryGetValue(id, out var report) && PathPolicy.Permitted(report.Path, options))
                {
                    return report;
                }
                throw new PortalException("notfound", 404);
            }
        }
    }

    private async Task<Catalog> GetCatalogAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task<Catalog> task;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _lifetime.Token.ThrowIfCancellationRequested();
                if (_catalog != null && _catalog.Generation == _generation &&
                    _catalog.Policy == PathPolicy.Identity(options) && time.GetUtcNow() < _catalog.Until)
                {
                    return _catalog;
                }
                if (_refreshing == null || _refreshing.IsCompleted)
                {
                    _refreshing = RefreshCoreAsync();
                }
                task = _refreshing;
            }
            // A caller cancels its wait, not the refresh shared by other callers. The producer
            // has its own bounded deadline and is cancelled/drained when the application stops.
            var result = await task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (ReferenceEquals(result, _catalog) && result.Generation == _generation &&
                    result.Policy == PathPolicy.Identity(options) && time.GetUtcNow() < result.Until)
                {
                    return result;
                }
            }
        }
    }

    private async Task<Catalog> RefreshCoreAsync()
    {
        try
        {
            return await RefreshSnapshotAsync();
        }
        catch (Exception exception)
        {
            // Observe parsing/contract failures even when the last HTTP waiter has
            // disconnected. Transport failures already emitted their detailed block.
            if (logger is not null)
            {
                FailureDiagnostics.Log(logger, options, exception, exception is PortalException portal ? portal.Code : "catalog",
                    FailureDiagnostics.Find(exception)?.Trace ?? System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "catalog-refresh");
            }
            throw;
        }
    }

    private async Task<Catalog> RefreshSnapshotAsync()
    {
        await Task.Yield(); // Register shared work under the lock, then send/parse SOAP outside it.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestSeconds));
        var ct = deadline.Token;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long generation;
            lock (_sync)
            {
                generation = _generation;
            }
            var policy = PathPolicy.Identity(options);
            var items = new Dictionary<string, CatalogItem>(StringComparer.OrdinalIgnoreCase);
            var folderVisibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var synthesizedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in options.AllowedRoots.ToArray())
            {
                if (!PathPolicy.Permitted(root, options))
                {
                    continue;
                }
                // ListChildren omits the queried folder and ancestors above it. Never invent a
                // visible root: verify each distinct ancestor through authoritative SOAP metadata.
                var hidden = false;
                for (var ancestor = root; ancestor.Length > 0; ancestor = ancestor[..ancestor.LastIndexOf('/')])
                {
                    if (!folderVisibility.TryGetValue(ancestor, out var visibleFolder))
                    {
                        visibleFolder = await soap.VisibleFolderAsync(ancestor, ct);
                        folderVisibility.Add(ancestor, visibleFolder);
                    }
                    if (!visibleFolder)
                    {
                        hidden = true;
                        break;
                    }
                }
                // An overlapping outer root may already have supplied this folder's full item
                // metadata. Keep it, but reject a contradictory type/visibility observation.
                if (items.TryGetValue(root, out var existingRoot))
                {
                    if (existingRoot.Type != "Folder" || existingRoot.Hidden != hidden)
                    {
                        throw new PortalException("service", message: "Catalog refresh received contradictory root folder type or visibility metadata.");
                    }
                }
                else
                {
                    Add(new CatalogItem(root, root.Split('/').Last(), "Folder", hidden));
                    synthesizedRoots.Add(root);
                }
                if (hidden)
                {
                    continue;
                }
                foreach (var item in await soap.ListAsync(root, ct))
                {
                    Add(item);
                }
            }
            void Add(CatalogItem item)
            {
                if (items.TryGetValue(item.Path, out var previous) && previous != item)
                {
                    if (!synthesizedRoots.Remove(item.Path) || previous.Type != "Folder" || item.Type != "Folder" || previous.Hidden != item.Hidden)
                    {
                        throw new PortalException("service", message: "Catalog refresh received contradictory duplicate item metadata.");
                    }
                    // An outer enumeration replaces a verified inner-root placeholder with the
                    // actual folder item. Other contradictory duplicate metadata still fails closed.
                }
                items[item.Path] = item;
                if (items.Count > options.MaxMetadataItems)
                {
                    throw new PortalException("limit");
                }
            }
            var visible = items.Values.Where(i => !i.Hidden && PathPolicy.Permitted(i.Path, options)).ToArray();
            var folders = visible.Where(i => i.Type == "Folder").Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A report with omitted or non-folder ancestor metadata has unknown visibility.
            // Ancestors above a configured root were separately verified by GetProperties.
            visible = visible.Where(i => KnownAncestors(i.Path)).ToArray();
            bool KnownAncestors(string path)
            {
                for (var parent = path[..path.LastIndexOf('/')]; PathPolicy.Permitted(parent, options); parent = parent[..parent.LastIndexOf('/')])
                {
                    if (!folders.Contains(parent))
                    {
                        return false;
                    }
                }
                return true;
            }
            var reports = visible.Where(i => i.Type is "Report" or "LinkedReport").ToDictionary(i => Id(i.Path),
                i => new CatalogReport(Id(i.Path), i.Name, i.Path, policy,
                    folders.Contains(i.Path[..i.Path.LastIndexOf('/')]) ? Id(i.Path[..i.Path.LastIndexOf('/')]) : null), StringComparer.Ordinal);
            // Retain the established navigation behavior: folders containing visible reports.
            var reportFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var report in reports.Values)
            {
                for (var parent = report.Path[..report.Path.LastIndexOf('/')]; folders.Contains(parent); parent = parent[..parent.LastIndexOf('/')])
                {
                    reportFolders.Add(parent);
                }
            }
            var allowed = visible.Where(i => i.Type is "Report" or "LinkedReport" ||
                (i.Type == "Folder" && reportFolders.Contains(i.Path))).ToArray();
            var paths = allowed.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = allowed.Select(i =>
            {
                var parent = i.Path[..i.Path.LastIndexOf('/')];
                return new CatalogEntry(Id(i.Path), paths.Contains(parent) ? Id(parent) : null, i.Name, i.Type,
                    i.Type is "Report" or "LinkedReport" ? ReportUrl.For(reports[Id(i.Path)]) : null);
            }).ToArray();
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (generation != _generation)
                {
                    continue; // Discard metadata superseded by a SQL notification.
                }
                if (policy != PathPolicy.Identity(options))
                {
                    throw new PortalException("catalog", 503);
                }
                _catalog = new Catalog(entries, reports, reports.Values.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase),
                    policy, generation, time.GetUtcNow().AddSeconds(options.CatalogSeconds));
                return _catalog;
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Task? task;
        lock (_sync)
        {
            if (!_disposed)
            {
                _lifetime.Cancel();
            }
            task = _refreshing;
        }
        if (task == null)
        {
            return;
        }
        try
        {
            await task.WaitAsync(ct);
        }
        catch (Exception) when (task.IsCompleted)
        {
            // The producer has drained; request callers observe its original failure.
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                _lifetime.Cancel();
                _lifetime.Dispose();
            }
        }
    }
}
