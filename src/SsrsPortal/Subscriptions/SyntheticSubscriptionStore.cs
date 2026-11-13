using System.Collections.Concurrent;

namespace SsrsPortal.Subscriptions;

// Test double selected only behind the existing Development mock + test-auth guards.
// It mirrors the store contract; SQL integration tests independently exercise the real locks.
public sealed class SyntheticSubscriptionStore(SubscriptionOptions options) : ISubscriptionStore
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, SubscriptionRow> rows = [];
    private readonly Dictionary<Guid, SubscriptionExecution> executions = [];
    private readonly HashSet<string> revoked = [];
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private SemaphoreSlim Gate(string key) => gates.GetOrAdd(key, _ => new(1));
    private static T Copy<T>(T value) => SubscriptionJson.Read<T>(SubscriptionJson.Write(value));
    public Task TouchAsync(SubscriptionOwner owner, CancellationToken ct)
    {
        lock (sync)
        {
            foreach (var row in rows.Values.Where(r => r.TenantId == owner.TenantId && r.UserObjectId == owner.UserObjectId))
            { row.UserEmail = owner.UserEmail ?? row.UserEmail; }
        }
        return Task.CompletedTask;
    }
    public Task<SubscriptionRow[]> ListAsync(SubscriptionOwner owner, CancellationToken ct)
    {
        lock (sync) { return Task.FromResult(rows.Values.Where(r => Own(r, owner) && !r.Deleted).Select(View).ToArray()); }
    }
    private SubscriptionRow View(SubscriptionRow row)
    {
        var result = Copy(row);
        var last = executions.Values.Where(e => e.SubscriptionId == row.Id).OrderByDescending(e => e.OccurrenceUtc).FirstOrDefault();
        result.LastStatus = last?.Status; result.LastOccurrenceUtc = last?.OccurrenceUtc;
        return result;
    }
    private static bool Own(SubscriptionRow row, SubscriptionOwner owner) => row.TenantId == owner.TenantId && row.UserObjectId == owner.UserObjectId;
    public Task<SubscriptionRow?> GetAsync(Guid id, SubscriptionOwner owner, CancellationToken ct)
    {
        lock (sync) { return Task.FromResult(rows.TryGetValue(id, out var row) && Own(row, owner) && !row.Deleted ? View(row) : null); }
    }
    public async Task SaveAsync(SubscriptionRow row, int? expectedRevision, CancellationToken ct, DateTimeOffset? observedDueUtc = null, bool requireObservedDue = false)
    {
        var gate = Gate(row.Owner.LockName);
        await gate.WaitAsync(ct);
        try
        {
            lock (sync)
            {
                if (revoked.Contains(row.Owner.LockName)) { throw new SubscriptionValidationException("Subscriptions for this identity have been revoked. Contact an administrator."); }
                if (expectedRevision == null)
                {
                    if (rows.Values.Count(r => Own(r, row.Owner) && !r.Deleted) >= options.MaxSubscriptionsPerOwner) { throw new SubscriptionValidationException("The owner subscription limit has been reached."); }
                }
                else if (!rows.TryGetValue(row.Id, out var current) || !Own(current, row.Owner) || current.Deleted || current.Revision != expectedRevision || requireObservedDue && current.NextDueUtc != observedDueUtc)
                { throw new SubscriptionValidationException("This subscription changed. Reload before saving."); }
                rows[row.Id] = Copy(row);
                foreach (var execution in executions.Values.Where(e => e.SubscriptionId == row.Id && e.Status == "Pending"))
                { execution.Status = "Skipped"; execution.Diagnostic = "subscription-changed"; }
            }
        }
        finally { gate.Release(); }
    }
    public Task<SubscriptionExecution[]> HistoryAsync(Guid id, SubscriptionOwner owner, CancellationToken ct)
    {
        lock (sync) { return Task.FromResult(rows.TryGetValue(id, out var row) && Own(row, owner) && !row.Deleted
            ? executions.Values.Where(e => e.SubscriptionId == id).OrderByDescending(e => e.OccurrenceUtc).Take(100).Select(Copy).ToArray() : []); }
    }
    public Task<SubscriptionRow[]> DueAsync(DateTimeOffset now, CancellationToken ct)
    {
        lock (sync) { return Task.FromResult(rows.Values.Where(r => r.Enabled && !r.Deleted && !revoked.Contains(r.Owner.LockName) && r.NextDueUtc <= now).Take(100).Select(Copy).ToArray()); }
    }
    public async Task ReconcileAsync(SubscriptionRow row, DateTimeOffset occurrence, DateTimeOffset? next, DateTimeOffset now, CancellationToken ct)
    {
        var gate = Gate("subscription:" + row.Id);
        if (!await gate.WaitAsync(0, ct)) { return; }
        try
        {
        lock (sync)
        {
            if (!rows.TryGetValue(row.Id, out var current) || !current.Enabled || current.Deleted || current.Revision != row.Revision || current.NextDueUtc != row.NextDueUtc) { return; }
            current.NextDueUtc = next;
            foreach (var uncertain in executions.Values.Where(e => e.SubscriptionId == row.Id && e.Status == "Running" && e.SendStartedUtc != null))
            { uncertain.Status = "Unknown"; uncertain.Diagnostic = "interrupted-after-send-boundary"; uncertain.FinishedUtc = now; }
            foreach (var pending in executions.Values.Where(e => e.SubscriptionId == row.Id && e.Status is "Pending" or "Running" && e.SendStartedUtc == null && e.OccurrenceUtc < occurrence))
            { pending.Status = "Skipped"; pending.Diagnostic = "newer-missed-occurrence"; pending.FinishedUtc = now; }
            if (!executions.Values.Any(e => e.SubscriptionId == row.Id && e.OccurrenceUtc == occurrence))
            {
                var execution = new SubscriptionExecution { Id = Guid.NewGuid(), SubscriptionId = row.Id, Revision = row.Revision, OccurrenceUtc = occurrence, AvailableUtc = now };
                executions.Add(execution.Id, execution);
            }
        }
        }
        finally { gate.Release(); }
    }
    public Task<Guid[]> WorkAsync(DateTimeOffset now, CancellationToken ct)
    {
        lock (sync) { return Task.FromResult(executions.Values.Where(e => e.Status is "Pending" or "Running" && e.AvailableUtc <= now).Take(100).Select(e => e.Id).ToArray()); }
    }
    public async Task<ISubscriptionLease?> AcquireAsync(Guid executionId, DateTimeOffset now, CancellationToken ct)
    {
        Guid subscriptionId;
        lock (sync) { if (!executions.TryGetValue(executionId, out var found)) { return null; } subscriptionId = found.SubscriptionId; }
        var gate = Gate("subscription:" + subscriptionId);
        if (!await gate.WaitAsync(0, ct)) { return null; }
        lock (sync)
        {
            var execution = executions[executionId];
            var row = rows[subscriptionId];
            if (execution.Status is not ("Pending" or "Running") || execution.AvailableUtc > now) { gate.Release(); return null; }
            if (execution.SendStartedUtc != null) { execution.Status = "Unknown"; execution.Diagnostic = "interrupted-after-send-boundary"; execution.FinishedUtc = now; gate.Release(); return null; }
            if (!row.Enabled || row.Deleted || row.Revision != execution.Revision || revoked.Contains(row.Owner.LockName) || execution.Attempts >= options.MaxAttempts)
            { execution.Status = execution.Attempts >= options.MaxAttempts ? "Failed" : "Skipped"; execution.FinishedUtc = now; gate.Release(); return null; }
            execution.Status = "Running"; execution.StartedUtc = now; execution.Attempts++;
            return new Lease(this, Copy(row), execution, gate);
        }
    }
    public async Task RevokeAsync(SubscriptionOwner owner, CancellationToken ct)
    {
        var gate = Gate(owner.LockName); await gate.WaitAsync(ct);
        try
        {
            lock (sync)
            {
                revoked.Add(owner.LockName);
                foreach (var row in rows.Values.Where(r => Own(r, owner))) { row.Enabled = false; row.NextDueUtc = null; row.Revision++; }
            }
        }
        finally { gate.Release(); }
    }
    private sealed class Lease(SyntheticSubscriptionStore store, SubscriptionRow row, SubscriptionExecution execution, SemaphoreSlim gate) : ISubscriptionLease
    {
        private SemaphoreSlim? ownerGate;
        public SubscriptionRow Subscription => row;
        public SubscriptionExecution Execution => execution;
        public async Task<bool> BeginSendAsync(DateTimeOffset now, CancellationToken ct)
        {
            var candidate = store.Gate(row.Owner.LockName); await candidate.WaitAsync(ct); ownerGate = candidate;
            lock (store.sync)
            {
                var current = store.rows[row.Id];
                if (!current.Enabled || current.Deleted || current.Revision != execution.Revision || store.revoked.Contains(row.Owner.LockName)) { return false; }
                execution.SendStartedUtc = now; return true;
            }
        }
        public Task FinishAsync(string status, string diagnostic, string? providerId, DateTimeOffset now, DateTimeOffset? retryAt, CancellationToken ct)
        {
            lock (store.sync)
            {
                execution.Status = status; execution.Diagnostic = diagnostic; execution.ProviderMessageId = providerId;
                execution.FinishedUtc = retryAt == null ? now : null; execution.AvailableUtc = retryAt ?? now;
                if (status == "Pending") { execution.SendStartedUtc = null; }
            }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { ownerGate?.Release(); gate.Release(); return ValueTask.CompletedTask; }
    }
}
public sealed class SyntheticSubscriptionDelivery : ISubscriptionDelivery
{
    public ConcurrentQueue<(Guid Id, SubscriptionDefinition Definition, PreparedSubscriptionReport Report)> Sent { get; } = new();
    public string Outcome { get; set; } = "Accepted";
    public Task<DeliveryResult> SendAsync(Guid executionId, SubscriptionDefinition definition, PreparedSubscriptionReport report, CancellationToken ct)
    {
        Sent.Enqueue((executionId, definition, report));
        return Task.FromResult(new DeliveryResult(Outcome, Outcome == "Accepted" ? Guid.NewGuid().ToString("D") : null));
    }
}
