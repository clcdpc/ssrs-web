using Hangfire;
using Microsoft.Data.SqlClient;
using SsrsPortal.Core;
using SsrsPortal.Services;

namespace SsrsPortal.Subscriptions;

public sealed class SubscriptionDispatcher(ISubscriptionStore store, IBackgroundJobClient jobs, TimeProvider time)
{
    [Queue("subscriptions")]
    [AutomaticRetry(Attempts = 2)]
    public async Task DispatchAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var row in await store.DueAsync(now, ct))
        {
            var occurrence = row.Definition.Schedule.Latest(row.NextDueUtc!.Value, now);
            if (occurrence != null) { await store.ReconcileAsync(row, occurrence.Value, row.Definition.Schedule.Next(now), now, ct); }
        }
        foreach (var id in await store.WorkAsync(now, ct))
        { jobs.Enqueue<SubscriptionWorker>(worker => worker.ExecuteAsync(id, CancellationToken.None)); }
    }
}

public sealed class SubscriptionWorker(ISubscriptionStore store, ISubscriptionReportRunner reports, ISubscriptionDelivery delivery,
    SubscriptionOptions options, TimeProvider time, CatalogService catalog, ILogger<SubscriptionWorker> logger)
{
    [Queue("subscriptions")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(Guid executionId, CancellationToken stopping)
    {
        await using var lease = await store.AcquireAsync(executionId, time.GetUtcNow(), stopping);
        if (lease == null) { return; }
        var boundary = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.ExecutionSeconds));
            var report = await reports.PrepareAsync(lease.Subscription.Definition, timeout.Token);
            // Current catalog access and owner/definition state must survive rendering.
            await catalog.ResolvePathAsync(lease.Subscription.Definition.ReportPath, timeout.Token);
            if (!await lease.BeginSendAsync(time.GetUtcNow(), timeout.Token))
            { await Finish("Skipped", "changed-before-delivery"); return; }
            boundary = true;
            var result = await delivery.SendAsync(executionId, lease.Subscription.Definition, report, timeout.Token);
            switch (result.Outcome)
            {
                case "Accepted": await Finish("Delivered", "postmark-accepted", result.MessageId); break;
                case "Rejected": await Finish("Failed", "postmark-rejected"); break;
                case "TransientRejected": await Retry("postmark-rate-limited"); break;
                default: await Finish("Unknown", "postmark-outcome-unknown"); break;
            }
        }
        catch (Exception e)
        {
            // Persist only stable diagnostic codes. SOAP, SQL and provider exceptions
            // may contain credentials, recipients, report data or parameter values.
            if (boundary) { await Finish("Unknown", "interrupted-after-send-boundary"); }
            else if (e is OperationCanceledException or HttpRequestException or SqlException ||
                     e is PortalException { Code: "query" or "catalog" or "timeout" or "service" or "capacity" })
            { await Retry(e is PortalException portal ? portal.Code : "pre-delivery-transient"); }
            else { await Finish("Failed", e is PortalException portal ? portal.Code : e is SubscriptionValidationException ? "validation" : "pre-delivery-failure"); }
        }
        async Task Finish(string status, string code, string? messageId = null, DateTimeOffset? retryAt = null)
        {
            using var recording = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await lease.FinishAsync(status, code, messageId, time.GetUtcNow(), retryAt, recording.Token);
            logger.LogInformation("Subscription execution {ExecutionId}: {Status}, diagnostic {Diagnostic}, attempt {Attempt}.", executionId, status, code, lease.Execution.Attempts);
        }
        Task Retry(string code) => lease.Execution.Attempts < options.MaxAttempts
            ? Finish("Pending", code, retryAt: time.GetUtcNow().AddMinutes(Math.Pow(2, lease.Execution.Attempts)))
            : Finish("Failed", code);
    }
}
