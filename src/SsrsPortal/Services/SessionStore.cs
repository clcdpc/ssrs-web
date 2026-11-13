using System.Security.Cryptography;
using System.Text;
using SsrsPortal.Core;

namespace SsrsPortal.Services;

public sealed class ReportSession(string token, string owner, CatalogReport report, DateTimeOffset now)
{
    public string Token { get; } = token;
    public string Owner { get; } = owner;
    public CatalogReport Report { get; } = report;
    public string Policy { get; init; } = "";
    public string ExecutionId { get; set; } = "";
    public int Page { get; set; } = 1;
    public int? Pages { get; set; }
    public string Html { get; set; } = "";
    public Dictionary<string, string> Streams { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ToggleActions { get; set; } = new(StringComparer.Ordinal);
    public byte[] ActionKey { get; } = RandomNumberGenerator.GetBytes(32);
    public long Revision { get; set; }
    public bool NeedsSynchronization { get; set; }
    public bool HasToggleControls { get; set; }
    public SemaphoreSlim Gate { get; } = new(1);
    public CancellationTokenSource Stop { get; } = new();
    public DateTimeOffset LastAccess { get; set; } = now;
    public bool Removed { get; set; }
    public int Active { get; set; }
    public long Bytes { get; set; } = 4096;
    public ParameterInput Input { get; set; } = new(null);
    // One pending replacement shares its source's logical slot. Both still count
    // against the unchanged aggregate byte budget, including operation scratch.
    public string? Replaces { get; set; }
    public string? Replacement { get; set; }
}

public sealed class SessionStore(PortalOptions options, TimeProvider time, ILogger<SessionStore>? logger = null) : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ReportSession> _sessions = new(StringComparer.Ordinal);
    private long _bytes;

    public static string RandomToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public ReportSession Reserve(string owner, CatalogReport report, string? replaces = null)
    {
        lock (_sync)
        {
            Expire();
            if (report.Policy != PathPolicy.Identity(options) || !PathPolicy.Permitted(report.Path, options))
            {
                // Parameter resolution may have awaited SOAP after catalog resolution.
                // Do not stamp a new policy onto an authorization obtained under an older one.
                throw new PortalException("notfound", 404);
            }
            ReportSession? source = null;
            if (replaces != null)
            {
                if (!_sessions.TryGetValue(replaces, out source) || source.Owner != owner || source.Report.Id != report.Id ||
                    source.Replaces != null || source.Html.Length == 0)
                {
                    throw new PortalException("session", 404);
                }
                if (source.Replacement != null)
                {
                    throw new PortalException("capacity", 429);
                }
            }
            if ((source == null && (_sessions.Values.Count(s => s.Replaces == null) >= options.MaxSessions ||
                _sessions.Values.Count(s => s.Owner == owner && s.Replaces == null) >= options.MaxSessionsPerUser)) ||
                _bytes + 4096 > options.MaxAggregateSessionBytes)
            {
                throw new PortalException("capacity", 429);
            }
            var s = new ReportSession(RandomToken(), owner, report, time.GetUtcNow()) { Policy = report.Policy, Replaces = replaces };
            _sessions.Add(s.Token, s);
            if (source != null) { source.Replacement = s.Token; }
            _bytes += s.Bytes;
            logger?.LogDebug("Report session reserved for {ReportPath}; owner {Owner}; replacement {Replacement}.", report.Path, owner, source is not null);
            return s;
        }
    }

    public async Task<SessionLease> AcquireAsync(string token, string owner, CancellationToken ct)
    {
        ReportSession s;
        lock (_sync)
        {
            Expire();
            if (!_sessions.TryGetValue(token, out s!) || s.Owner != owner || s.Removed)
            {
                logger?.LogWarning("Report session invalid or expired for owner {Owner}.", owner);
                throw new PortalException("session", 404);
            }
            s.Active++;
        }
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, s.Stop.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(options.RequestSeconds));
        var entered = false;
        try
        {
            await s.Gate.WaitAsync(linked.Token);
            entered = true;
            lock (_sync)
            {
                Expire();
                var scratch = options.MaxSoapBytes + 2L * options.MaxOutputBytes;
                if (s.Removed)
                {
                    throw new PortalException("session", 404);
                }
                if (_bytes + scratch > options.MaxAggregateSessionBytes)
                {
                    throw new PortalException("capacity", 429);
                }
                _bytes += scratch;
                return new SessionLease(this, s, linked, scratch);
            }
        }
        catch
        {
            linked.Dispose();
            if (entered)
            {
                s.Gate.Release();
            }
            lock (_sync)
            {
                s.Active--;
                CleanRemoved(s);
            }
            throw;
        }
    }

    public void SetPage(ReportSession s, string html, Dictionary<string, string> streams, int page, int? pages,
        Dictionary<string, string>? actions = null)
    {
        actions ??= new(StringComparer.Ordinal);
        if (actions.Count > options.MaxToggleActions || actions.Any(a => !IsToken(a.Key) || !ReportToggle.IsIdentifier(a.Value)))
        {
            throw new PortalException("limit");
        }
        var bytes = 4096L + Encoding.UTF8.GetByteCount(html) + streams.Sum(v => 128L + Encoding.UTF8.GetByteCount(v.Value)) +
            actions.Sum(v => 256L + 2L * v.Value.Length) +
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(s.Input).LongLength;
        lock (_sync)
        {
            Expire();
            if (s.Removed)
            {
                throw new PortalException("session", 404);
            }
            if (bytes > options.MaxSessionBytes || _bytes - s.Bytes + bytes > options.MaxAggregateSessionBytes)
            {
                throw new PortalException("limit");
            }
            _bytes += bytes - s.Bytes;
            s.Bytes = bytes;
            s.Html = html;
            s.Streams = streams;
            s.ToggleActions = actions;
            s.HasToggleControls |= actions.Count > 0;
            s.Page = page;
            s.Pages = pages;
            s.Revision++;
            s.NeedsSynchronization = false;
        }
    }

    public static bool IsToken(string? value) => value?.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void InvalidateView(ReportSession s)
    {
        lock (_sync)
        {
            if (s.Removed) { throw new PortalException("session", 404); }
            s.NeedsSynchronization = true;
            s.ToggleActions.Clear();
        }
    }

    // Caller holds the session lease. Invalidate the entire view BEFORE contacting
    // SSRS: a lost reply, cancellation or failed publication must never permit a retry.
    public string BeginToggle(ReportSession s, string actionToken)
    {
        lock (_sync)
        {
            Expire();
            if (s.Removed) { throw new PortalException("session", 404); }
            if (s.NeedsSynchronization) { throw new PortalException("synchronize", 409); }
            if (s.Replacement != null || s.Replaces != null || !IsToken(actionToken) || !s.ToggleActions.TryGetValue(actionToken, out var toggleId))
            {
                throw new PortalException("view-stale", 409);
            }
            s.NeedsSynchronization = true;
            s.ToggleActions.Clear();
            // Keep the old view's reservation until atomic publication or removal.
            return toggleId;
        }
    }

    public bool Cancel(string token, string owner)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(token, out var s) || s.Owner != owner)
            {
                return false;
            }
            Remove(s);
            return true;
        }
    }

    public void Commit(string token, string owner)
    {
        lock (_sync)
        {
            Expire();
            if (!_sessions.TryGetValue(token, out var session) || session.Owner != owner || session.Html.Length == 0)
            {
                throw new PortalException("session", 404);
            }
            if (session.Replaces != null)
            {
                var source = _sessions.GetValueOrDefault(session.Replaces);
                if (source == null || source.Replacement != token)
                {
                    throw new PortalException("session", 404);
                }
                session.Replaces = null;
                source.Replacement = null;
                Remove(source);
                logger?.LogDebug("Report session replacement committed for {ReportPath}; owner {Owner}.", session.Report.Path, owner);
            }
        }
    }

    public void CancelReplacement(string token, string owner)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(token, out var source) && source.Owner == owner && source.Replacement != null &&
                _sessions.TryGetValue(source.Replacement, out var pending))
            {
                Remove(pending);
            }
        }
    }

    public void CancelOwner(string owner)
    {
        lock (_sync)
        {
            foreach (var s in _sessions.Values.Where(s => s.Owner == owner).ToArray())
            {
                Remove(s);
            }
        }
    }

    public void Sweep()
    {
        lock (_sync)
        {
            Expire();
        }
    }

    private void Expire()
    {
        var policy = PathPolicy.Identity(options);
        // Any effective root/blocklist policy change revokes all sessions. Dependencies are
        // delegated to SSRS; session access needs no repeated catalog discovery.
        foreach (var s in _sessions.Values.Where(s => s.Policy != policy ||
            (s.Active == 0 && time.GetUtcNow() - s.LastAccess >= TimeSpan.FromMinutes(options.SessionMinutes))).ToArray())
        {
            if (!s.Removed)
            {
                logger?.LogDebug("Report session expired or revoked by policy for {ReportPath}; owner {Owner}.", s.Report.Path, s.Owner);
                Remove(s);
            }
        }
    }

    private void Remove(ReportSession s)
    {
        if (s.Removed) { return; }
        if (s.Replacement != null && _sessions.TryGetValue(s.Replacement, out var pending))
        {
            Remove(pending);
        }
        if (s.Replaces != null && _sessions.TryGetValue(s.Replaces, out var source))
        {
            source.Replacement = null;
        }
        _sessions.Remove(s.Token);
        s.Removed = true;
        logger?.LogDebug("Report session removed for {ReportPath}; owner {Owner}; active operations {Active}.", s.Report.Path, s.Owner, s.Active);
        try { s.Stop.Cancel(); }
        catch (AggregateException exception)
        {
            // Cancel invokes every callback before aggregating their failures.
            // A failed callback must not strand accounting or undo promotion of
            // an already rendered replacement. The logging provider filters credentials.
            logger?.LogWarning("Report session cleanup callbacks failed for {ReportPath}; details {DiagnosticDetails}.",
                s.Report.Path, Soap.FailureDiagnostics.Filter(Soap.FailureDiagnostics.ExceptionText(exception, 65536)));
        }
        _bytes -= s.Bytes;
        s.Bytes = 0;
        s.Html = "";
        s.Streams = [];
        s.ToggleActions = [];
        CleanRemoved(s);
    }

    private static void CleanRemoved(ReportSession s)
    {
        if (s.Removed && s.Active == 0)
        {
            s.Stop.Dispose();
            s.Gate.Dispose();
        }
    }

    internal void Release(ReportSession s, long scratch)
    {
        lock (_sync)
        {
            _bytes -= scratch;
            s.LastAccess = time.GetUtcNow();
            s.Gate.Release();
            s.Active--;
            CleanRemoved(s);
        }
    }

    public (int Count, long Bytes) Snapshot()
    {
        lock (_sync)
        {
            Expire();
            return (_sessions.Count, _bytes);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var s in _sessions.Values.ToArray())
            {
                Remove(s);
            }
        }
    }
}

public sealed class SessionLease(SessionStore store, ReportSession session, CancellationTokenSource linked, long scratch) : IDisposable
{
    public ReportSession Session { get; } = session;
    public CancellationToken Cancellation => linked.Token;
    public void Dispose()
    {
        linked.Dispose();
        store.Release(Session, scratch);
    }
}

public sealed class SessionReaper(SessionStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            store.Sweep();
        }
    }
}
