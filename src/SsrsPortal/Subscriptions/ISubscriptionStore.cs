namespace SsrsPortal.Subscriptions;

public interface ISubscriptionStore
{
    Task TouchAsync(SubscriptionOwner owner, CancellationToken ct);
    Task<SubscriptionRow[]> ListAsync(SubscriptionOwner owner, CancellationToken ct);
    Task<SubscriptionRow?> GetAsync(Guid id, SubscriptionOwner owner, CancellationToken ct);
    Task SaveAsync(SubscriptionRow row, int? expectedRevision, CancellationToken ct, DateTimeOffset? observedDueUtc = null, bool requireObservedDue = false);
    Task<SubscriptionExecution[]> HistoryAsync(Guid id, SubscriptionOwner owner, CancellationToken ct);
    Task<SubscriptionRow[]> DueAsync(DateTimeOffset now, CancellationToken ct);
    Task ReconcileAsync(SubscriptionRow row, DateTimeOffset occurrence, DateTimeOffset? next, DateTimeOffset now, CancellationToken ct);
    Task<Guid[]> WorkAsync(DateTimeOffset now, CancellationToken ct);
    Task<ISubscriptionLease?> AcquireAsync(Guid executionId, DateTimeOffset now, CancellationToken ct);
}
public interface ISubscriptionLease : IAsyncDisposable
{
    SubscriptionRow Subscription { get; }
    SubscriptionExecution Execution { get; }
    Task<bool> BeginSendAsync(DateTimeOffset now, CancellationToken ct);
    Task FinishAsync(string status, string diagnostic, string? providerId, DateTimeOffset now, DateTimeOffset? retryAt, CancellationToken ct);
}
