using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SsrsPortal.Core;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/subscriptions")]
public sealed class SubscriptionsController(SubscriptionOptions options, IServiceProvider services) : ControllerBase
{
    private SubscriptionService Service => services.GetRequiredService<SubscriptionService>();
    private ISubscriptionStore Store => services.GetRequiredService<ISubscriptionStore>();
    private async Task<SubscriptionOwner> Owner(CancellationToken ct)
    {
        if (!options.Enabled) { throw new PortalException("notfound", 404); }
        var owner = SubscriptionOwner.FromPrincipal(User);
        await Store.TouchAsync(owner, ct);
        return owner;
    }
    [HttpGet]
    public async Task<SubscriptionView[]> List(CancellationToken ct) => (await StoreForList(ct)).Select(r => r.View).ToArray();
    private async Task<SubscriptionRow[]> StoreForList(CancellationToken ct)
    {
        var owner = await Owner(ct);
        return await Store.ListAsync(owner, ct);
    }
    [HttpGet("{id:guid}")]
    public async Task<SubscriptionView> Get(Guid id, CancellationToken ct) => (await Service.GetAsync(id, await Owner(ct), ct)).View;
    [HttpPost]
    public async Task<SubscriptionView> Create(SubscriptionWrite input, CancellationToken ct) => await Service.CreateAsync(await Owner(ct), input.Definition, ct);
    [HttpPut("{id:guid}")]
    public async Task<SubscriptionView> Update(Guid id, SubscriptionWrite input, CancellationToken ct) => await Service.UpdateAsync(id, await Owner(ct), input, ct);
    [HttpPost("{id:guid}/state")]
    public async Task<SubscriptionView> State(Guid id, SubscriptionState input, CancellationToken ct) => await Service.StateAsync(id, await Owner(ct), input, false, ct);
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] int revision, CancellationToken ct)
    {
        await Service.StateAsync(id, await Owner(ct), new(false, revision), true, ct);
        return NoContent();
    }
    [HttpGet("{id:guid}/history")]
    public async Task<SubscriptionExecution[]> History(Guid id, CancellationToken ct)
    {
        var owner = await Owner(ct);
        await Service.GetAsync(id, owner, ct);
        return await Store.HistoryAsync(id, owner, ct);
    }
    [HttpPost("parameters")]
    public async Task<ParameterForm> Parameters(SubscriptionDefinition input, CancellationToken ct)
    {
        await Owner(ct);
        return await Service.PreviewParametersAsync(input, ct);
    }
    [HttpPost("schedule")]
    public async Task<object> Schedule(SubscriptionSchedule input, CancellationToken ct)
    {
        await Owner(ct);
        return new { nextDueUtc = Service.PreviewSchedule(input) };
    }
}
