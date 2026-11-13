using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SsrsPortal.Core;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.Controllers;

// Deterministic browser qualification of the complete pipeline; never available
// outside the existing isolated Development mock/test-auth configuration.
[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/subscriptions/synthetic-dispatch")]
public sealed class SyntheticSubscriptionsController(PortalOptions portal, SubscriptionOptions options, IWebHostEnvironment environment,
    ISubscriptionStore store, SubscriptionWorker worker, TimeProvider time, IServiceProvider services) : ControllerBase
{
    public sealed record DispatchInput(string Outcome = "Accepted");
    [HttpPost]
    public async Task<IActionResult> Dispatch(DispatchInput input, CancellationToken ct)
    {
        if (!environment.IsDevelopment() || !portal.UseMock || !portal.TestAuth || !options.Enabled) { return NotFound(); }
        if (input.Outcome is not ("Accepted" or "Unknown" or "Rejected" or "TransientRejected")) { return BadRequest(); }
        var owner = SubscriptionOwner.FromPrincipal(User);
        var delivery = services.GetRequiredService<SyntheticSubscriptionDelivery>(); delivery.Outcome = input.Outcome;
        var now = time.GetUtcNow();
        var ownedExecutions = new HashSet<Guid>();
        foreach (var row in await store.ListAsync(owner, ct))
        {
            if (row.Enabled && row.NextDueUtc is { } due && due <= now)
            {
                var occurrence = row.Definition.Schedule.Latest(due, now);
                if (occurrence != null) { await store.ReconcileAsync(row, occurrence.Value, row.Definition.Schedule.Next(now), now, ct); }
            }
            foreach (var execution in await store.HistoryAsync(row.Id, owner, ct))
            {
                ownedExecutions.Add(execution.Id);
                if (execution.Status is "Pending" or "Running" && execution.AvailableUtc <= now) { await worker.ExecuteAsync(execution.Id, ct); }
            }
        }
        var sent = delivery.Sent.Where(item => ownedExecutions.Contains(item.Id)).ToArray();
        return Ok(new { messages = sent.Length, lastLink = sent.LastOrDefault().Report?.Link });
    }
}
