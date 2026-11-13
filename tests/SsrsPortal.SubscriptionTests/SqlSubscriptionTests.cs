using System.Text.RegularExpressions;
using Dapper;
using Hangfire.SqlServer;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using SsrsPortal.Tests;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class SqlSubscriptionTests
{
    private static string database = "";
    private static string connectionString = "";
    private SubscriptionOptions options = null!;
    private SqlSubscriptionStore store = null!;
    private SubscriptionOwner owner = null!;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T13:00Z");
    [ClassInitialize]
    public static async Task Initialize(TestContext context)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Optional integrated SQL Server fixture requires a local Windows SQL Server administrator. Run on the documented SQL qualification host."); }
        var admin = "Server=localhost;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=2;Pooling=false";
        await using var db = new SqlConnection(admin);
        try { await db.OpenAsync(); }
        catch (SqlException) { Assert.Inconclusive("Local SQL Server unavailable; run SQL qualification on the documented host."); }
        if (await db.QuerySingleAsync<int>("SELECT IS_SRVROLEMEMBER('sysadmin');") != 1) { Assert.Inconclusive("SQL fixture requires permission to create/drop its isolated test database."); }
        database = "SsrsWeb_SubscriptionTests_" + Guid.NewGuid().ToString("N");
        await db.ExecuteAsync($"CREATE DATABASE [{database}];");
        connectionString = new SqlConnectionStringBuilder(admin) { InitialCatalog = database }.ConnectionString;
        await using var target = new SqlConnection(connectionString); await target.OpenAsync();
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "001-Subscriptions.sql"));
        for (var repeat = 0; repeat < 2; repeat++)
        {
            foreach (var batch in Regex.Split(script, @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            { if (!string.IsNullOrWhiteSpace(batch)) { await target.ExecuteAsync(batch); } }
        }
        await target.ExecuteAsync("CREATE USER [SubscriptionRuntimeTest] WITHOUT LOGIN; ALTER ROLE [SsrsSubscriptionRuntime] ADD MEMBER [SubscriptionRuntimeTest];");
        // The administrator performs Hangfire installation. Runtime hosting turns DDL off.
        _ = new SqlServerStorage(connectionString, new SqlServerStorageOptions { PrepareSchemaIfNecessary = true });
    }
    [ClassCleanup]
    public static async Task Cleanup()
    {
        if (database.Length == 0) { return; }
        if (!Regex.IsMatch(database, @"^SsrsWeb_SubscriptionTests_[a-f0-9]{32}$")) { throw new InvalidOperationException("Unexpected test database."); }
        SqlConnection.ClearAllPools();
        await using var db = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString);
        await db.OpenAsync();
        await db.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }
    [TestInitialize]
    public async Task Before()
    {
        options = SubscriptionTests.Options; options.ConnectionString = connectionString; store = new(options);
        owner = SubscriptionTests.Owner with { UserObjectId = Guid.NewGuid() };
        await store.TouchAsync(owner, default);
    }
    private async Task<SubscriptionRow> Row(bool recurring = false)
    {
        var definition = SubscriptionTests.Definition();
        if (recurring) { definition = definition with { Schedule = new("interval", new DateTime(2020, 1, 1, 8, 0, 0), Every: 5) }; }
        var row = new SubscriptionRow { Id = Guid.NewGuid(), TenantId = owner.TenantId, UserObjectId = owner.UserObjectId, Revision = 1, Enabled = true,
            DefinitionJson = SubscriptionJson.Write(definition), NextDueUtc = recurring ? DateTimeOffset.Parse("2020-01-01T13:00Z") : Now.AddHours(-1) };
        await store.SaveAsync(row, null, default); return row;
    }
    private async Task<Guid> Queue(SubscriptionRow row, DateTimeOffset? next = null)
    {
        await store.ReconcileAsync(row, row.NextDueUtc!.Value, next, Now, default);
        return (await store.HistoryAsync(row.Id, owner, default)).Single().Id;
    }
    [TestMethod]
    public async Task MigrationsIdempotentAndRuntimeCannotRevokeOrAlterOwner()
    {
        await using var db = new SqlConnection(connectionString); await db.OpenAsync();
        Assert.AreEqual(1, await db.QuerySingleAsync<int>("SELECT [Version] FROM [subscriptions].[SchemaVersion] WHERE [Id]=1;"));
        Assert.AreEqual(9, await db.QuerySingleAsync<int>("SELECT [Version] FROM [HangFire].[Schema];"));
        var permissions = (await db.QueryAsync<int>("""
            EXECUTE AS USER='SubscriptionRuntimeTest';
            SELECT HAS_PERMS_BY_NAME('dbo.RevokeSubscriptionsForEntraUser','OBJECT','EXECUTE')
            UNION ALL SELECT HAS_PERMS_BY_NAME('subscriptions.Owner','OBJECT','UPDATE')
            UNION ALL SELECT HAS_PERMS_BY_NAME('subscriptions.SchemaVersion','OBJECT','UPDATE')
            UNION ALL SELECT HAS_PERMS_BY_NAME('subscriptions.TouchOwner','OBJECT','EXECUTE')
            UNION ALL SELECT HAS_PERMS_BY_NAME('subscriptions.Subscription','OBJECT','UPDATE');
            REVERT;
            """)).ToArray();
        CollectionAssert.AreEqual(new[] { 0, 0, 0, 1, 1 }, permissions);
    }
    [TestMethod]
    public async Task ConcurrentReconciliationCreatesOneLatestCatchup()
    {
        var row = await Row(true); var latest = row.Definition.Schedule.Latest(row.NextDueUtc!.Value, Now)!.Value;
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => store.ReconcileAsync(row, latest, row.Definition.Schedule.Next(Now), Now, default)));
        var history = await store.HistoryAsync(row.Id, owner, default);
        Assert.AreEqual(1, history.Length); Assert.AreEqual(Now, history[0].OccurrenceUtc);
        Assert.AreEqual(Now.AddMinutes(5), (await store.GetAsync(row.Id, owner, default))!.NextDueUtc);
    }
    [TestMethod]
    public async Task ClaimedThenInterruptedOldOccurrenceIsSupersededAfterLongOutage()
    {
        var row = await Row(true); var original = row.NextDueUtc!.Value;
        var id = await Queue(row, original.AddMinutes(5));
        await using (var claimed = await store.AcquireAsync(id, Now, default)) { Assert.IsNotNull(claimed); }
        var fresh = (await store.GetAsync(row.Id, owner, default))!;
        await store.ReconcileAsync(fresh, Now, Now.AddMinutes(5), Now, default);
        var history = await store.HistoryAsync(row.Id, owner, default);
        Assert.AreEqual("Pending", history[0].Status); Assert.AreEqual(Now, history[0].OccurrenceUtc);
        Assert.AreEqual("Skipped", history[1].Status);
        Assert.IsNull(await store.AcquireAsync(id, Now, default));
    }
    [TestMethod]
    public async Task ActiveWorkerPreventsConcurrentClaimAndReconciliation()
    {
        var row = await Row(); var id = await Queue(row, Now);
        await using var first = await store.AcquireAsync(id, Now, default); Assert.IsNotNull(first);
        var competitors = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.AcquireAsync(id, Now, default)));
        Assert.IsTrue(competitors.All(c => c == null));
        var fresh = (await store.GetAsync(row.Id, owner, default))!;
        await store.ReconcileAsync(fresh, Now, Now.AddHours(1), Now, default);
        Assert.AreEqual(1, (await store.HistoryAsync(row.Id, owner, default)).Length);
        Assert.IsTrue(await first.BeginSendAsync(Now, default));
        await first.FinishAsync("Delivered", "test-accepted", "provider-id", Now, null, default);
    }
    [TestMethod]
    public async Task RestartAfterSendMarkerBecomesUnknownWithoutSecondSend()
    {
        var row = await Row(); var id = await Queue(row);
        await using (var first = await store.AcquireAsync(id, Now, default)) { Assert.IsTrue(await first!.BeginSendAsync(Now, default)); }
        Assert.IsNull(await store.AcquireAsync(id, Now, default));
        var history = (await store.HistoryAsync(row.Id, owner, default)).Single(); Assert.AreEqual("Unknown", history.Status); Assert.AreEqual(1, history.Attempts);
    }
    [TestMethod]
    public async Task RevocationDisablesAndPreventsCreationAndInProgressUnsentWork()
    {
        var row = await Row(); var id = await Queue(row);
        var first = await store.AcquireAsync(id, Now, default);
        await Revoke(); await Revoke();
        Assert.IsFalse(await first!.BeginSendAsync(Now, default));
        await first.DisposeAsync();
        Assert.IsFalse((await store.GetAsync(row.Id, owner, default))!.Enabled);
        await store.TouchAsync(owner with { UserEmail = "updated@example.invalid" }, default);
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => Row());
        await using var db = new SqlConnection(connectionString);
        Assert.AreEqual(2, await db.QuerySingleAsync<int>("SELECT COUNT(*) FROM [subscriptions].[OwnerAudit] WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId;", owner));
    }
    private async Task Revoke()
    {
        await using var db = new SqlConnection(connectionString);
        await db.ExecuteAsync("EXEC [dbo].[RevokeSubscriptionsForEntraUser] @TenantId,@UserObjectId,@Reason;", new { owner.TenantId, owner.UserObjectId, Reason = "Synthetic qualification" });
    }
    [TestMethod]
    public async Task RevocationWaitsForAlreadyAdmittedSendAndThenFencesFurtherSends()
    {
        var row = await Row(); var id = await Queue(row);
        var lease = await store.AcquireAsync(id, Now, default); Assert.IsTrue(await lease!.BeginSendAsync(Now, default));
        var revocation = Revoke();
        await Task.Delay(150); Assert.IsFalse(revocation.IsCompleted);
        await lease.FinishAsync("Delivered", "test-accepted", "provider-id", Now, null, default); await lease.DisposeAsync();
        await revocation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse((await store.GetAsync(row.Id, owner, default))!.Enabled);
        Assert.IsNull(await store.AcquireAsync(id, Now, default));
    }
    [TestMethod]
    public async Task RevisionCheckAndOwnerIdentityCannotBeBypassed()
    {
        var row = await Row();
        Assert.IsNull(await store.GetAsync(row.Id, owner with { TenantId = Guid.NewGuid() }, default));
        Assert.IsNull(await store.GetAsync(row.Id, owner with { UserObjectId = Guid.NewGuid() }, default));
        row.Revision = 2; await store.SaveAsync(row, 1, default);
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => store.SaveAsync(row, 1, default));
    }
    [TestMethod]
    public async Task ConcurrentEditAndClaimCannotReviveCancelledPendingWork()
    {
        for (var repeat = 0; repeat < 10; repeat++)
        {
            var row = await Row(); var id = await Queue(row);
            row.Enabled = false; row.NextDueUtc = null; row.Revision++;
            var claiming = store.AcquireAsync(id, Now, default);
            await store.SaveAsync(row, 1, default);
            await using var lease = await claiming;
            if (lease != null) { Assert.IsFalse(await lease.BeginSendAsync(Now, default)); }
            Assert.IsFalse((await store.GetAsync(row.Id, owner, default))!.Enabled);
        }
    }
    [TestMethod]
    public async Task SameOwnerGateContentionIsRetryableWithoutSkippingOccurrence()
    {
        var row = await Row(); var otherRow = await Row(); var firstId = await Queue(row); var secondId = await Queue(otherRow);
        var first = await store.AcquireAsync(firstId, Now, default); Assert.IsTrue(await first!.BeginSendAsync(Now, default));
        await using var second = await store.AcquireAsync(secondId, Now, default);
        try
        {
            var failure = await Assert.ThrowsAsync<SsrsPortal.Core.PortalException>(() => second!.BeginSendAsync(Now, default));
            Assert.AreEqual("capacity", failure.Code);
            var waiting = (await store.HistoryAsync(otherRow.Id, owner, default)).Single(); Assert.AreEqual("Running", waiting.Status); Assert.IsNull(waiting.SendStartedUtc);
        }
        finally { await first.DisposeAsync(); }
        Assert.IsTrue(await second!.BeginSendAsync(Now, default));
        await second.FinishAsync("Delivered", "test-accepted", "provider-id", Now, null, default);
    }
    [TestMethod]
    public async Task ReinstatementLeavesExistingSubscriptionsPausedAndAllowsNewOnes()
    {
        var row = await Row(); await Revoke();
        await using var db = new SqlConnection(connectionString);
        await db.ExecuteAsync("EXEC [dbo].[ReinstateSubscriptionsForEntraUser] @TenantId,@UserObjectId,@Reason;", new { owner.TenantId, owner.UserObjectId, Reason = "Synthetic reinstatement" });
        Assert.IsFalse((await store.GetAsync(row.Id, owner, default))!.Enabled);
        Assert.IsNotNull(await Row());
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DispatcherAdvancementDuringManagementCannotRewindDueStateOrCancelClaimedOccurrence(bool enable)
    {
        using var h = new Harness(); h.Clock.Advance(Now - h.Clock.GetUtcNow());
        var row = await Row(true); await h.Catalog.BrowseAsync(default);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Mock.TargetResponseGates["GetItemParameters|" + row.Definition.ReportPath] = release.Task;
        var service = new SubscriptionService(store, SubscriptionTests.Runner(h, options), options, h.Clock, h.Catalog, h.Parameters);
        var edit = enable ? service.StateAsync(row.Id, owner, new(true, row.Revision), false, default)
            : service.UpdateAsync(row.Id, owner, new(row.Definition with { Name = "Racing edit" }, row.Revision), default);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!h.Mock.Observations.Any(observation => observation.Operation == "GetItemParameters")) { await Task.Delay(10, deadline.Token); }
            await store.ReconcileAsync(row, Now, Now.AddMinutes(5), Now, default);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => edit);
        var committed = (await store.GetAsync(row.Id, owner, default))!;
        Assert.AreEqual(Now.AddMinutes(5), committed.NextDueUtc); Assert.AreEqual(row.Revision, committed.Revision);
        Assert.AreEqual(row.Definition.Name, committed.Definition.Name);
        Assert.AreEqual("Pending", (await store.HistoryAsync(row.Id, owner, default)).Single().Status);
    }
    private sealed class WorkerActivator(SubscriptionWorker worker) : JobActivator
    {
        public override object ActivateJob(Type jobType) => jobType == typeof(SubscriptionWorker) ? worker : throw new InvalidOperationException("Unexpected test job.");
    }
    [TestMethod]
    public async Task DurableHangfireQueueProcessesSubscriptionThroughRealSqlAndSyntheticDelivery()
    {
        using var h = new Harness(); h.Clock.Advance(Now - h.Clock.GetUtcNow());
        var row = await Row(); var id = await Queue(row);
        var delivery = new SyntheticSubscriptionDelivery();
        var worker = new SubscriptionWorker(store, SubscriptionTests.Runner(h, options), delivery, options, h.Clock, h.Catalog, NullLogger<SubscriptionWorker>.Instance);
        var storage = new SqlServerStorage(connectionString, new SqlServerStorageOptions { PrepareSchemaIfNecessary = false, QueuePollInterval = TimeSpan.FromSeconds(1) });
        var client = new BackgroundJobClient(storage);
        var jobId = client.Enqueue<SubscriptionWorker>(job => job.ExecuteAsync(id, CancellationToken.None));
        // Enqueue while no processor is running, then start the real persistent queue.
        using var server = new BackgroundJobServer(new BackgroundJobServerOptions
        {
            WorkerCount = 2, Queues = ["subscriptions"], Activator = new WorkerActivator(worker),
            ShutdownTimeout = TimeSpan.FromSeconds(5), CancellationCheckInterval = TimeSpan.FromSeconds(1)
        }, storage);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var jobs = storage.GetConnection();
        while (jobs.GetJobData(jobId)?.State != "Succeeded") { await Task.Delay(50, deadline.Token); }
        Assert.AreEqual(1, delivery.Sent.Count);
        Assert.AreEqual("Delivered", (await store.HistoryAsync(row.Id, owner, default)).Single().Status);
        await worker.ExecuteAsync(id, default); Assert.AreEqual(1, delivery.Sent.Count);
    }
}
