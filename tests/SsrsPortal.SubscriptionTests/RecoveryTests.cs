using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class RecoveryTests
{
    [TestMethod]
    public async Task CancelledOwnerGateWaiterDoesNotReleaseAnotherSendOrBypassRevocation()
    {
        var store = new SyntheticSubscriptionStore(SubscriptionTests.Options);
        var owner = SubscriptionTests.Owner;
        var now = DateTimeOffset.Parse("2026-11-03T13:00Z");
        async Task<Guid> Add()
        {
            var row = new SubscriptionRow { Id = Guid.NewGuid(), TenantId = owner.TenantId, UserObjectId = owner.UserObjectId, Revision = 1, Enabled = true,
                DefinitionJson = SubscriptionJson.Write(SubscriptionTests.Definition()), NextDueUtc = now };
            await store.SaveAsync(row, null, default); await store.ReconcileAsync(row, now, null, now, default);
            return (await store.HistoryAsync(row.Id, owner, default)).Single().Id;
        }
        var firstId = await Add(); var secondId = await Add();
        var first = await store.AcquireAsync(firstId, now, default); Assert.IsTrue(await first!.BeginSendAsync(now, default));
        await using (var second = await store.AcquireAsync(secondId, now, default))
        {
            using var cancellation = new CancellationTokenSource(50);
            await Assert.ThrowsAsync<OperationCanceledException>(() => second!.BeginSendAsync(now, cancellation.Token));
        }
        var revoking = store.RevokeAsync(owner, default);
        await Task.Delay(50); Assert.IsFalse(revoking.IsCompleted);
        await first.DisposeAsync(); await revoking.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [TestMethod]
    public async Task UnsentRetryAfterOutageIsSupersededByLatestOccurrence()
    {
        var store = new SyntheticSubscriptionStore(SubscriptionTests.Options); var owner = SubscriptionTests.Owner;
        var old = DateTimeOffset.Parse("2020-01-01T13:00Z"); var now = DateTimeOffset.Parse("2026-11-03T13:03Z");
        var row = new SubscriptionRow { Id = Guid.NewGuid(), TenantId = owner.TenantId, UserObjectId = owner.UserObjectId, Revision = 1, Enabled = true,
            DefinitionJson = SubscriptionJson.Write(SubscriptionTests.Definition() with { Schedule = new("interval", new DateTime(2020, 1, 1, 8, 0, 0), Every: 5) }), NextDueUtc = old };
        await store.SaveAsync(row, null, default); await store.ReconcileAsync(row, old, old.AddMinutes(5), old, default);
        var id = (await store.WorkAsync(now, default)).Single();
        await using (var lease = await store.AcquireAsync(id, old, default)) { await lease!.FinishAsync("Pending", "test-transient", null, old, old.AddMinutes(2), default); }
        row = (await store.GetAsync(row.Id, owner, default))!;
        var latest = row.Definition.Schedule.Latest(row.NextDueUtc!.Value, now)!.Value;
        await store.ReconcileAsync(row, latest, row.Definition.Schedule.Next(now), now, default);
        var history = await store.HistoryAsync(row.Id, owner, default);
        Assert.AreEqual(2, history.Length); Assert.AreEqual("Skipped", history[1].Status); Assert.AreEqual(1, (await store.WorkAsync(now, default)).Length);
        Assert.AreEqual(DateTimeOffset.Parse("2026-11-03T13:00Z"), history[0].OccurrenceUtc);
    }
}
