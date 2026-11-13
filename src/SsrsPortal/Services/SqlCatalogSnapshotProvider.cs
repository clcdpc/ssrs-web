using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SsrsPortal.Core;

namespace SsrsPortal.Services;

public sealed class SqlCatalogSnapshotProvider(CatalogMonitorOptions monitor, PortalOptions portal, TimeProvider time) : ICatalogSnapshotProvider
{
    private static readonly Lazy<string> Command = new(() =>
    {
        using var stream = typeof(SqlCatalogSnapshotProvider).Assembly.GetManifestResourceStream("SsrsPortal.ReadSsrsCatalogSnapshot.sql")
            ?? throw new InvalidOperationException("SQL snapshot command unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public async Task<CatalogSnapshotRead> ReadAsync(CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(monitor.CommandTimeoutSeconds), time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var cancellation = deadline.Token;
        var watch = Stopwatch.StartNew();
        await using var connection = new SqlConnection(monitor.ValidatedConnectionString()) { StatisticsEnabled = true };
        await connection.OpenAsync(cancellation);
        connection.ResetStatistics();
        // SERIALIZABLE keeps both tables consistent for one small full snapshot and works without
        // changing the SSRS database's isolation settings. Bound both query and connection lifetime.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellation);
        await using var command = new SqlCommand(Command.Value, connection, transaction) { CommandTimeout = monitor.CommandTimeoutSeconds };
        command.Parameters.Add("@MaximumCatalogRows", SqlDbType.Int).Value = portal.MaxMetadataItems;
        command.Parameters.Add("@MaximumDataSourceRows", SqlDbType.Int).Value = portal.MaxMetadataItems * 3;
        CatalogSnapshot snapshot;
        await using (var reader = await command.ExecuteReaderAsync(cancellation))
        {
            snapshot = await ReadSnapshotAsync(reader, portal.MaxMetadataItems, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellation);
        watch.Stop();
        var bytes = connection.RetrieveStatistics()["BytesReceived"];
        return new CatalogSnapshotRead(snapshot, new SnapshotMeasurement(watch.Elapsed, bytes is long count ? count : null));
    }

    internal static async Task<CatalogSnapshot> ReadSnapshotAsync(DbDataReader reader, int maximumItems, CancellationToken ct)
    {
        if (maximumItems is < 1 or > 10000)
        {
            throw new CatalogSnapshotException();
        }
        var catalog = new List<SqlCatalogItem>();
        var sources = new List<SqlDataSource>();
        if (!ColumnsMatch(reader, ("ItemID", typeof(Guid)), ("Path", typeof(string)), ("Type", typeof(int)),
            ("ModifiedDate", typeof(DateTime)), ("Hidden", typeof(bool)), ("LinkSourceID", typeof(Guid))))
        {
            throw new CatalogSnapshotException();
        }
        while (await reader.ReadAsync(ct))
        {
            if (catalog.Count >= maximumItems || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
            {
                throw new CatalogSnapshotException();
            }
            catalog.Add(new SqlCatalogItem(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetBoolean(4), OptionalGuid(reader, 5)));
        }
        if (!await reader.NextResultAsync(ct) || !ColumnsMatch(reader,
            ("DSID", typeof(Guid)), ("ItemID", typeof(Guid)), ("Link", typeof(Guid)), ("Fingerprint", typeof(byte[]))))
        {
            throw new CatalogSnapshotException();
        }
        while (await reader.ReadAsync(ct))
        {
            if (sources.Count >= maximumItems * 3 || reader.IsDBNull(0))
            {
                throw new CatalogSnapshotException();
            }
            sources.Add(new SqlDataSource(reader.GetGuid(0), OptionalGuid(reader, 1), OptionalGuid(reader, 2), Fingerprint(reader, 3)));
        }
        if (await reader.NextResultAsync(ct))
        {
            throw new CatalogSnapshotException();
        }
        ct.ThrowIfCancellationRequested();
        return new CatalogSnapshot(catalog, sources, maximumItems);
    }

    private static bool ColumnsMatch(DbDataReader reader, params (string Name, Type Type)[] columns) =>
        reader.FieldCount == columns.Length && columns.Select((column, ordinal) =>
            reader.GetName(ordinal) == column.Name && reader.GetFieldType(ordinal) == column.Type).All(matches => matches);

    private static Guid? OptionalGuid(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
    private static string Fingerprint(DbDataReader reader, int ordinal) => reader.GetValue(ordinal) is byte[] { Length: 32 } bytes
        ? Convert.ToHexString(bytes) : throw new CatalogSnapshotException();
}
