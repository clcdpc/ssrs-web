using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SsrsPortal.Core;

namespace SsrsPortal.Services;

public enum CatalogPollOutcome { Disabled, Skipped, Baseline, Unchanged, Refreshed, Failed }
public sealed record CatalogMonitorStatus(long QueryAttempts, long SuccessfulSnapshots, long CatalogRows, long DataSourceRows,
    long CacheInvalidations, SnapshotMeasurement? LastRead, TimeSpan? LastRefresh, CatalogPollOutcome Outcome);

public sealed class CatalogMonitor(ICatalogSnapshotProvider snapshots, CatalogService catalog, CatalogMonitorOptions options,
    TimeProvider time, ILogger<CatalogMonitor> logger, PortalOptions? portal = null) : BackgroundService
{
    private CatalogSnapshot? _baseline;
    private bool _reconcile;
    private int _polling;
    private CatalogMonitorStatus _status = new(0, 0, 0, 0, 0, null, null, CatalogPollOutcome.Disabled);
    public CatalogMonitorStatus Status => Volatile.Read(ref _status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("SQL catalog monitoring is disabled.");
            return;
        }
        logger.LogInformation("SQL catalog monitor started; interval {IntervalSeconds}s; command timeout {CommandTimeoutSeconds}s.", options.IntervalSeconds, options.CommandTimeoutSeconds);
        try
        {
            while (true)
            {
                await PollAsync(stoppingToken);
                // Wait after completion, including a failure. Slow/unavailable servers cannot
                // produce overlapping queries, queued catch-up polls or aggressive retry loops.
                await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { logger.LogInformation("SQL catalog monitor stopped."); }
    }

    public async Task<CatalogPollOutcome> PollAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!options.Enabled)
        {
            return CatalogPollOutcome.Disabled;
        }
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return CatalogPollOutcome.Skipped;
        }
        var stage = "sql";
        try
        {
            _status = _status with { QueryAttempts = _status.QueryAttempts + 1, LastRead = null, LastRefresh = null };
            var read = await snapshots.ReadAsync(ct);
            ct.ThrowIfCancellationRequested();
            _status = _status with
            {
                SuccessfulSnapshots = _status.SuccessfulSnapshots + 1,
                CatalogRows = _status.CatalogRows + read.Snapshot.Catalog.Count,
                DataSourceRows = _status.DataSourceRows + read.Snapshot.DataSources.Count,
                LastRead = read.Measurement, LastRefresh = null
            };
            var change = _reconcile ? CatalogChange.Reconcile : _baseline == null ? null : read.Snapshot.ChangesFrom(_baseline);
            if (change?.Changed == true)
            {
                stage = "soap";
                var watch = Stopwatch.StartNew();
                _status = _status with { CacheInvalidations = _status.CacheInvalidations + 1 };
                await catalog.RefreshFromSqlAsync(change, ct);
                ct.ThrowIfCancellationRequested();
                watch.Stop();
                _status = _status with { LastRefresh = watch.Elapsed };
                logger.LogInformation("SQL catalog change reconciled through SOAP; catalog changes {CatalogChanges}, source changes {SourceChanges}, recovery {Recovery}; refresh ms {Milliseconds}.",
                    change.CatalogItemsChanged, change.DataSourcesChanged, change.ForceRefresh, watch.Elapsed.TotalMilliseconds);
            }
            var outcome = change?.Changed == true ? CatalogPollOutcome.Refreshed : _baseline == null ? CatalogPollOutcome.Baseline : CatalogPollOutcome.Unchanged;
            // A snapshot becomes the baseline only after BOTH result sets and any required SOAP
            // reconciliation succeed. Failures retain the last complete baseline and a retry flag.
            _baseline = read.Snapshot;
            _reconcile = false;
            _status = _status with { Outcome = outcome };
            logger.LogInformation("SQL catalog snapshot completed; catalog rows {CatalogRows}, source rows {SourceRows}, received bytes {Bytes}, read ms {Milliseconds}; outcome {Outcome}.",
                read.Snapshot.Catalog.Count, read.Snapshot.DataSources.Count, read.Measurement.BytesReceived,
                read.Measurement.Duration.TotalMilliseconds, outcome);
            return outcome;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _reconcile = true;
            throw;
        }
        catch (Exception ex)
        {
            _reconcile = true;
            _status = _status with { Outcome = CatalogPollOutcome.Failed };
            if (stage == "soap" && portal is not null)
            {
                Soap.FailureDiagnostics.Log(logger, portal, ex, Category(ex),
                    Soap.FailureDiagnostics.Find(ex)?.Trace ?? "catalog-monitor");
            }
            var target = ConnectionTarget();
            logger.LogWarning("SQL catalog monitor {Stage} stage failed; category {Category}; server {SqlServer}; database {Database}; query {Query}; details {DiagnosticDetails}. Time-based SOAP reconciliation remains active; the next successful poll will reconcile.",
                stage, Category(ex), target.Server, target.Database, "ReadSsrsCatalogSnapshot.sql", stage == "sql" ?
                    Soap.FailureDiagnostics.Filter(Soap.FailureDiagnostics.ExceptionText(ex, 65536)) : "See correlated SOAP failure");
            return CatalogPollOutcome.Failed;
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private (string Server, string Database) ConnectionTarget()
    {
        try
        {
            var connection = new SqlConnectionStringBuilder(options.ConnectionString);
            return (connection.DataSource, connection.InitialCatalog);
        }
        catch (ArgumentException) { return ("unavailable", "unavailable"); }
    }

    private static string Category(Exception ex) => ex switch
    {
        SqlException sql when sql.Number is -2 => "timeout",
        SqlException sql when sql.Number is 18456 or 18452 => "authentication",
        SqlException sql when sql.Number is 229 or 916 => "permissions",
        SqlException sql when sql.Number is 207 or 208 or 51000 => "schema",
        SqlException sql when sql.Number is 1205 or 1222 => "locking",
        SqlException => "sql-unavailable",
        CatalogSnapshotException => "snapshot",
        OperationCanceledException or TimeoutException => "timeout",
        PortalException => "ssrs-unavailable",
        _ => "unavailable"
    };

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // Ordinary callers cancel only their shared-refresh waiter. Application shutdown also
        // cancels/drains the producer, so monitor-triggered SOAP tasks do not outlive the host.
        await catalog.StopAsync(cancellationToken);
    }
}
