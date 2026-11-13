namespace SsrsPortal.Services;

public sealed record SqlCatalogItem(Guid ItemId, string Path, int Type, DateTime? ModifiedDate, bool? Hidden, Guid? LinkSourceId = null);
public sealed record SqlDataSource(Guid DataSourceId, Guid? ItemId, Guid? Link, string Fingerprint);
public sealed record SnapshotMeasurement(TimeSpan Duration, long? BytesReceived);
public sealed record CatalogSnapshotRead(CatalogSnapshot Snapshot, SnapshotMeasurement Measurement);

public interface ICatalogSnapshotProvider
{
    Task<CatalogSnapshotRead> ReadAsync(CancellationToken ct);
}

public sealed class CatalogSnapshot
{
    public IReadOnlyDictionary<Guid, SqlCatalogItem> Catalog { get; }
    public IReadOnlyDictionary<Guid, SqlDataSource> DataSources { get; }
    public CatalogSnapshot(IEnumerable<SqlCatalogItem> catalog, IEnumerable<SqlDataSource> dataSources, int maximumItems)
    {
        if (maximumItems is < 1 or > 10000)
        {
            throw new CatalogSnapshotException();
        }
        var items = new Dictionary<Guid, SqlCatalogItem>();
        var sources = new Dictionary<Guid, SqlDataSource>();
        foreach (var item in catalog)
        {
            if (item.ItemId == Guid.Empty || item.Path == null || item.Path.Length > 512 ||
                !items.TryAdd(item.ItemId, item) || items.Count > maximumItems)
            {
                throw new CatalogSnapshotException();
            }
        }
        foreach (var source in dataSources)
        {
            if (source.DataSourceId == Guid.Empty || !Hash(source.Fingerprint) ||
                !sources.TryAdd(source.DataSourceId, source) || sources.Count > maximumItems * 3)
            {
                throw new CatalogSnapshotException();
            }
        }
        // Own the complete snapshot; callers cannot alter an admitted baseline through an array.
        Catalog = new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, SqlCatalogItem>(items);
        DataSources = new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, SqlDataSource>(sources);
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public CatalogChange ChangesFrom(CatalogSnapshot previous)
    {
        var catalogChanges = Catalog.Keys.Concat(previous.Catalog.Keys).Distinct()
            .Count(id => Catalog.GetValueOrDefault(id) != previous.Catalog.GetValueOrDefault(id));
        var sourceChanges = DataSources.Keys.Concat(previous.DataSources.Keys).Distinct()
            .Count(id => DataSources.GetValueOrDefault(id) != previous.DataSources.GetValueOrDefault(id));
        // Every catalog/source transition requests one authoritative SOAP metadata refresh.
        // SQL relationships and fingerprints never become report access decisions.
        return new CatalogChange(catalogChanges, sourceChanges);
    }
}

public sealed class CatalogSnapshotException : Exception
{
    public CatalogSnapshotException() : base("SQL catalog snapshot is incomplete or incompatible.") { }
}

public sealed record CatalogChange(int CatalogItemsChanged, int DataSourcesChanged, bool ForceRefresh = false)
{
    public static CatalogChange Reconcile { get; } = new(0, 0, true);
    public bool Changed => ForceRefresh || CatalogItemsChanged != 0 || DataSourcesChanged != 0;
}
