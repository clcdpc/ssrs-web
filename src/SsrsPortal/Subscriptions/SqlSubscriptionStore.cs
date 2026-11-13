using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SsrsPortal.Subscriptions;

public sealed class SqlSubscriptionStore(SubscriptionOptions options) : ISubscriptionStore
{
    private const string SelectRows = """
        SELECT s.*, o.[UserEmail], h.[Status] AS [LastStatus], h.[OccurrenceUtc] AS [LastOccurrenceUtc]
        FROM [subscriptions].[Subscription] s INNER JOIN [subscriptions].[Owner] o ON o.[TenantId]=s.[TenantId] AND o.[UserObjectId]=s.[UserObjectId]
        OUTER APPLY (SELECT TOP(1) e.[Status],e.[OccurrenceUtc] FROM [subscriptions].[Execution] e WHERE e.[SubscriptionId]=s.[Id] ORDER BY e.[OccurrenceUtc] DESC) h
        """;
    private async Task<SqlConnection> Open(CancellationToken ct)
    {
        // Session-owned application locks must end with the physical session.
        // Dedicated subscription connections intentionally do not enter the pool.
        var connection = new SqlConnection(new SqlConnectionStringBuilder(options.ConnectionString) { Pooling = false }.ConnectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static CommandDefinition Cmd(string sql, object? args, CancellationToken ct, IDbTransaction? transaction = null) =>
        new(sql, args, transaction, commandTimeout: 30, cancellationToken: ct);
    internal static async Task<bool> Gate(SqlConnection connection, string resource, CancellationToken ct, int wait = 0, IDbTransaction? tx = null)
    {
        var result = await connection.QuerySingleAsync<int>(Cmd("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=@wait; SELECT @r;", new { resource, wait }, ct, tx));
        return result >= 0;
    }
    public async Task TouchAsync(SubscriptionOwner owner, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await db.ExecuteAsync(Cmd("EXEC [subscriptions].[TouchOwner] @TenantId,@UserObjectId,@UserEmail;", owner, ct));
    }
    public async Task<SubscriptionRow[]> ListAsync(SubscriptionOwner owner, CancellationToken ct)
    {
        await using var db = await Open(ct);
        return (await db.QueryAsync<SubscriptionRow>(Cmd(SelectRows + " WHERE s.[TenantId]=@TenantId AND s.[UserObjectId]=@UserObjectId AND s.[Deleted]=0 ORDER BY s.[UpdatedUtc] DESC;", owner, ct))).ToArray();
    }
    public async Task<SubscriptionRow?> GetAsync(Guid id, SubscriptionOwner owner, CancellationToken ct)
    {
        await using var db = await Open(ct);
        return await db.QuerySingleOrDefaultAsync<SubscriptionRow>(Cmd(SelectRows + " WHERE s.[Id]=@id AND s.[TenantId]=@TenantId AND s.[UserObjectId]=@UserObjectId AND s.[Deleted]=0;", new { id, owner.TenantId, owner.UserObjectId }, ct));
    }
    public async Task SaveAsync(SubscriptionRow row, int? expectedRevision, CancellationToken ct, DateTimeOffset? observedDueUtc = null, bool requireObservedDue = false)
    {
        await using var db = await Open(ct);
        if (!await Gate(db, row.Owner.LockName, ct, 10000)) { throw new SubscriptionValidationException("Delivery is in progress. Try the change again shortly."); }
        await using var tx = await db.BeginTransactionAsync(ct);
        if (await db.QuerySingleAsync<bool>(Cmd("SELECT [Revoked] FROM [subscriptions].[Owner] WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId;", row, ct, tx)))
        { throw new SubscriptionValidationException("Subscriptions for this identity have been revoked. Contact an administrator."); }
        if (expectedRevision == null)
        {
            if (await db.QuerySingleAsync<int>(Cmd("SELECT COUNT(*) FROM [subscriptions].[Subscription] WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId AND [Deleted]=0;", row, ct, tx)) >= options.MaxSubscriptionsPerOwner)
            { throw new SubscriptionValidationException("The subscription limit for this owner has been reached."); }
            await db.ExecuteAsync(Cmd("INSERT [subscriptions].[Subscription]([Id],[TenantId],[UserObjectId],[Revision],[Enabled],[Deleted],[DefinitionJson],[NextDueUtc],[UpdatedUtc]) VALUES(@Id,@TenantId,@UserObjectId,@Revision,@Enabled,@Deleted,@DefinitionJson,@NextDueUtc,SYSUTCDATETIME());", row, ct, tx));
        }
        else
        {
            var args = new { row.Id, row.TenantId, row.UserObjectId, row.Revision, row.Enabled, row.Deleted, row.DefinitionJson, row.NextDueUtc, expectedRevision, observedDueUtc, requireObservedDue };
            if (await db.ExecuteAsync(Cmd("UPDATE [subscriptions].[Subscription] SET [Revision]=@Revision,[Enabled]=@Enabled,[Deleted]=@Deleted,[DefinitionJson]=@DefinitionJson,[NextDueUtc]=@NextDueUtc,[UpdatedUtc]=SYSUTCDATETIME() WHERE [Id]=@Id AND [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId AND [Deleted]=0 AND [Revision]=@expectedRevision AND (@requireObservedDue=0 OR [NextDueUtc]=@observedDueUtc OR [NextDueUtc] IS NULL AND @observedDueUtc IS NULL);", args, ct, tx)) != 1)
            { throw new SubscriptionValidationException("This subscription changed. Reload it before saving."); }
            await db.ExecuteAsync(Cmd("UPDATE [subscriptions].[Execution] SET [Status]='Skipped',[Diagnostic]='subscription-changed',[FinishedUtc]=SYSUTCDATETIME() WHERE [SubscriptionId]=@Id AND [Status]='Pending';", row, ct, tx));
        }
        await tx.CommitAsync(ct);
    }
    public async Task<SubscriptionExecution[]> HistoryAsync(Guid id, SubscriptionOwner owner, CancellationToken ct)
    {
        await using var db = await Open(ct);
        return (await db.QueryAsync<SubscriptionExecution>(Cmd("SELECT TOP(100) e.* FROM [subscriptions].[Execution] e INNER JOIN [subscriptions].[Subscription] s ON s.[Id]=e.[SubscriptionId] WHERE s.[Id]=@id AND s.[TenantId]=@TenantId AND s.[UserObjectId]=@UserObjectId AND s.[Deleted]=0 ORDER BY e.[OccurrenceUtc] DESC;", new { id, owner.TenantId, owner.UserObjectId }, ct))).ToArray();
    }
    public async Task<SubscriptionRow[]> DueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await Open(ct);
        return (await db.QueryAsync<SubscriptionRow>(Cmd(SelectRows + " WHERE s.[Enabled]=1 AND s.[Deleted]=0 AND o.[Revoked]=0 AND s.[NextDueUtc]<=@now ORDER BY s.[NextDueUtc] OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY;", new { now }, ct))).ToArray();
    }
    public async Task ReconcileAsync(SubscriptionRow row, DateTimeOffset occurrence, DateTimeOffset? next, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await Open(ct);
        if (!await Gate(db, "ssrs-subscription:" + row.Id.ToString("D"), ct)) { return; }
        await using var tx = await db.BeginTransactionAsync(ct);
        var args = new { row.Id, row.Revision, row.NextDueUtc, occurrence, next, now, executionId = Guid.NewGuid() };
        // Serialize advancement and insertion; enqueue is recoverable from Pending.
        var changed = await db.ExecuteAsync(Cmd("""
            UPDATE [subscriptions].[Subscription] WITH (UPDLOCK) SET [NextDueUtc]=@next
            WHERE [Id]=@Id AND [Revision]=@Revision AND [NextDueUtc]=@NextDueUtc AND [Enabled]=1 AND [Deleted]=0;
            """, args, ct, tx));
        if (changed == 1)
        {
            await db.ExecuteAsync(Cmd("""
                UPDATE [subscriptions].[Execution] SET [Status]='Unknown',[Diagnostic]='interrupted-after-send-boundary',[FinishedUtc]=@now WHERE [SubscriptionId]=@Id AND [Status]='Running' AND [SendStartedUtc] IS NOT NULL;
                UPDATE [subscriptions].[Execution] SET [Status]='Skipped',[Diagnostic]='newer-missed-occurrence',[FinishedUtc]=@now WHERE [SubscriptionId]=@Id AND [Status] IN ('Pending','Running') AND [SendStartedUtc] IS NULL AND [OccurrenceUtc]<@occurrence;
                IF NOT EXISTS(SELECT 1 FROM [subscriptions].[Execution] WHERE [SubscriptionId]=@Id AND [OccurrenceUtc]=@occurrence)
                    INSERT [subscriptions].[Execution]([Id],[SubscriptionId],[Revision],[OccurrenceUtc],[Status],[AvailableUtc]) VALUES(@executionId,@Id,@Revision,@occurrence,'Pending',@now);
                """, args, ct, tx));
        }
        await tx.CommitAsync(ct);
    }
    public async Task<Guid[]> WorkAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await Open(ct);
        return (await db.QueryAsync<Guid>(Cmd("SELECT TOP(100) [Id] FROM [subscriptions].[Execution] WHERE [Status] IN ('Pending','Running') AND [AvailableUtc]<=@now ORDER BY [AvailableUtc];", new { now }, ct))).ToArray();
    }
    public async Task<ISubscriptionLease?> AcquireAsync(Guid executionId, DateTimeOffset now, CancellationToken ct)
    {
        var db = await Open(ct);
        try
        {
            var execution = await db.QuerySingleOrDefaultAsync<SubscriptionExecution>(Cmd("SELECT * FROM [subscriptions].[Execution] WHERE [Id]=@executionId;", new { executionId }, ct));
            if (execution == null || !await Gate(db, "ssrs-subscription:" + execution.SubscriptionId.ToString("D"), ct)) { await db.DisposeAsync(); return null; }
            var identity = await db.QuerySingleAsync<SubscriptionRow>(Cmd("SELECT * FROM [subscriptions].[Subscription] WHERE [Id]=@id;", new { id = execution.SubscriptionId }, ct));
            var revoked = await db.QuerySingleAsync<bool>(Cmd("SELECT [Revoked] FROM [subscriptions].[Owner] WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId;", identity, ct));
            await using var claim = await db.BeginTransactionAsync(ct);
            var row = await db.QuerySingleAsync<SubscriptionRow>(Cmd("SELECT * FROM [subscriptions].[Subscription] WITH (UPDLOCK) WHERE [Id]=@id;", new { id = execution.SubscriptionId }, ct, claim));
            execution = await db.QuerySingleAsync<SubscriptionExecution>(Cmd("SELECT * FROM [subscriptions].[Execution] WITH (UPDLOCK) WHERE [Id]=@executionId;", new { executionId }, ct, claim));
            if (execution.Status is not ("Pending" or "Running") || execution.AvailableUtc > now) { await claim.CommitAsync(ct); await db.DisposeAsync(); return null; }
            var lease = new Lease(db, row, execution);
            if (execution.SendStartedUtc != null)
            { await claim.CommitAsync(ct); await lease.FinishAsync("Unknown", "interrupted-after-send-boundary", null, now, null, ct); await lease.DisposeAsync(); return null; }
            if (!row.Enabled || row.Deleted || row.Revision != execution.Revision || execution.Attempts >= options.MaxAttempts || revoked)
            { await claim.CommitAsync(ct); await lease.FinishAsync(execution.Attempts >= options.MaxAttempts ? "Failed" : "Skipped", "ineligible-or-attempt-limit", null, now, null, ct); await lease.DisposeAsync(); return null; }
            execution.Attempts++;
            await db.ExecuteAsync(Cmd("UPDATE [subscriptions].[Execution] SET [Status]='Running',[StartedUtc]=@now,[Attempts]=[Attempts]+1 WHERE [Id]=@executionId;", new { now, executionId }, ct, claim));
            await claim.CommitAsync(ct);
            return lease;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private sealed class Lease(SqlConnection db, SubscriptionRow subscription, SubscriptionExecution execution) : ISubscriptionLease
    {
        public SubscriptionRow Subscription => subscription;
        public SubscriptionExecution Execution => execution;
        public async Task<bool> BeginSendAsync(DateTimeOffset now, CancellationToken ct)
        {
            if (!await Gate(db, subscription.Owner.LockName, ct, 10000)) { throw new SsrsPortal.Core.PortalException("capacity", 429); }
            var changed = await db.ExecuteAsync(Cmd("""
                UPDATE e SET [SendStartedUtc]=@now FROM [subscriptions].[Execution] e
                INNER JOIN [subscriptions].[Subscription] s ON s.[Id]=e.[SubscriptionId]
                INNER JOIN [subscriptions].[Owner] o ON o.[TenantId]=s.[TenantId] AND o.[UserObjectId]=s.[UserObjectId]
                WHERE e.[Id]=@id AND e.[Status]='Running' AND e.[SendStartedUtc] IS NULL AND s.[Revision]=e.[Revision] AND s.[Enabled]=1 AND s.[Deleted]=0 AND o.[Revoked]=0;
                """, new { now, id = execution.Id }, ct));
            return changed == 1;
        }
        public async Task FinishAsync(string status, string diagnostic, string? providerId, DateTimeOffset now, DateTimeOffset? retryAt, CancellationToken ct)
        {
            await db.ExecuteAsync(Cmd("UPDATE [subscriptions].[Execution] SET [Status]=@status,[Diagnostic]=@diagnostic,[ProviderMessageId]=@providerId,[FinishedUtc]=@finished,[AvailableUtc]=@available,[SendStartedUtc]=CASE WHEN @status='Pending' THEN NULL ELSE [SendStartedUtc] END WHERE [Id]=@id;",
                new { status, diagnostic, providerId, finished = retryAt == null ? (DateTimeOffset?)now : null, available = retryAt ?? now, id = execution.Id }, ct));
        }
        public ValueTask DisposeAsync() => db.DisposeAsync();
    }
}
