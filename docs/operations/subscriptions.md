# Application-managed subscriptions

[Back to the operations guide](README.md)

Subscriptions run in the .NET 10 ASP.NET Core/IIS application. Hangfire.AspNetCore + Hangfire.SqlServer dispatch durable, minute-based reconciliation to the `subscriptions` queue, with two workers by default. The dedicated `SsrsWeb` database stores definitions and occurrences under `[subscriptions]`, and Hangfire data under `[HangFire]`. Dapper handles application CRUD.

There is no separate worker, Windows Service, native SSRS subscription or output archive. `Subscriptions.Enabled=false` avoids SQL/Postmark connections and leaves reports available. The dashboard is disabled: ReportViewer is not an administrative policy.

## Local Development initialization

Create an empty `SsrsWeb` database. As a migration administrator, run `scripts/subscriptions/001-Subscriptions.sql` in SSMS. Add the `Subscriptions` example to the protected ProgramData `application.json`: SQL connection, HTTPS portal origin, approved sender and Postmark token. Set `Enabled=true`. Select the Development launch profile or run:

```powershell
dotnet run --project src/SsrsPortal --no-launch-profile -- --environment Development --urls https://localhost:7443
```

The process uses its Windows identity. Assign its database user to `SsrsSubscriptionRuntime`. Initial Hangfire creation also requires database `CREATE SCHEMA` and `CREATE TABLE`. If another principal owns `[HangFire]`, permission to alter that schema is required. A database owner can initialize an isolated local database.

An administrator grants these permissions; the application never does. Keep the setup identity separate from the least-privilege production runtime account. Before switching identities, run `003-SubscriptionPermissions.sql` and assign its runtime role.

Startup validates configuration and application schema 1. Only in Development, with no Hangfire version row/table, it calls Hangfire 1.8.25's public `SqlServerObjectsInstaller.Install`. It reuses one readiness connection for checks and installation, then verifies Hangfire version 9 before exposing storage to the server or recurring-job manager.

Existing version 9 skips installation. Repeat starts preserve data and need no DDL permissions. Other versioned schemas fail, even in Development; upgrades require a reviewed administrator migration. Concurrent first starts use Hangfire's transactional installation lock. The application never installs `[subscriptions]`.

For manual installation, select `SsrsWeb` in SSMS. As administrator, run `scripts/subscriptions/002-Hangfire.sql`, then `scripts/subscriptions/003-SubscriptionPermissions.sql`. The pinned installer targets `[HangFire]` version 9 and includes upstream attribution and [license provenance](../../scripts/subscriptions/vendor/hangfire/README.md). The separate setup artifact's `Database/Initialize-Subscriptions.ps1` runs application → Hangfire → runtime grants. Manually initialized Development databases skip automatic installation.

SQL connection errors identify the stage and retain `SqlException` as `InnerException` for Visual Studio inspection. Schema errors identify the object and expected/observed versions. SELECT/install permission errors explain required access and manual alternatives. Protected SQL logs are administrator information; messages omit connection strings and provider tokens. Failed checks or installation stop startup before workers or recurring registration start.

## Production provisioning and deployment

1. Build through the [immutable release workflow](../clc-deployment.md#immutable-release-production). `release.zip` excludes SQL and administrative PowerShell. Download `ssrs-web-administrative-setup-<run>-<attempt>` from the same reviewed source. Its ZIP contains:
   - `Database/001-Subscriptions.sql`, pinned Hangfire 1.8.25 `Database/002-Hangfire.sql`, `Database/003-SubscriptionPermissions.sql` and `Database/Initialize-Subscriptions.ps1`.
   - Hangfire attribution/licenses under `Database/Licenses/Hangfire`; project `LICENSE` and `COPYRIGHT.md` under `Database/Licenses/Project`.
   - `Operations/Configure-SubscriptionIis.ps1`.

   The deterministic manifest records exact source and file hashes. The artifact does not authorize execution. For local verification, generate it with `New-SubscriptionSetupArtifact.ps1`, then run `Test-SubscriptionPublish.ps1 -PublishDirectory artifacts/publish -AdministrationArchive artifacts/administrative-setup.zip`. Original SQL bytes and validation are preserved. No operational JSON or credentials ship.

2. As SQL migration administrator, provision a dedicated `SsrsWeb` database, backup/recovery policy, TLS and IIS service-account login/user. Run `pwsh ./Database/Initialize-Subscriptions.ps1 -SqlServer sql.example.invalid` with integrated administrative authentication. Add `-TrustServerCertificate` only to deliberately trust that server's certificate. Alternatively, run all three numbered scripts in order in SSMS.

   The application migration uses a transaction/application lock, explicit version 1 and deterministic, idempotent creation. Unsupported versions fail. The pinned Hangfire script installs version 9. Non-Development environments, including Production and Staging, verify both versions at enabled startup. They never perform runtime DDL or automatic upgrades.

3. Assign the IIS database user to `SsrsSubscriptionRuntime`. It permits application SELECT/INSERT/UPDATE, the email-touch procedure and Hangfire DML. It forbids changes to revocations, owner audit or application schema versions, revocation/reinstatement execution, and DDL. Assign a separate operator identity to `SsrsSubscriptionAdministrator`. Neither role needs db_owner; keep migration and runtime/operator identities separate.

   Integrated SQL credentials use the existing AD pool identity. Enabled configuration requires `Encrypt=Mandatory/Strict`; it rejects SQL credentials, local attach/user instances and persisted security information. `TrustServerCertificate=false` validates the certificate. `true` is accepted in every environment for deliberately trusted infrastructure/self-signed certificates. **`Encrypt=Strict` always validates the certificate. This allowance does not relax the read-only SQL monitor's certificate requirements.**

4. Add the `Subscriptions` section from `application.Example.json` to protected ProgramData `application.json`, preserving existing settings. Typical connection: `Server=sql.example.invalid;Database=SsrsWeb;Integrated Security=true;Encrypt=true;TrustServerCertificate=false`. Keep `Enabled=false` until database, IIS and provider acceptance are ready. Set the real HTTPS portal **origin**, verified Postmark sender, server token and message stream. No environment-variable/application-setting providers or operational appsettings files are added. Restart after edits.

5. Allow outbound TLS to `https://api.postmarkapp.com/email`, plus existing SSRS/Entra and SQL access. Verify the Postmark sender/domain and use an approved transactional stream. Each message defaults to 20 unique recipients, configurable from 1–50 across To/Cc/Bcc. To wins duplicates, then Cc, then Bcc. Owners default to 100 subscriptions. Attachments default to, and cannot exceed, 6 MiB. Base64 plus bounded text stays below Postmark's [10 MB Email API limit](https://postmarkapp.com/developer/api/overview). Provider open/link tracking is disabled.

6. As IIS administrator, configure the dedicated root site/pool from the setup artifact: `pwsh ./Operations/Configure-SubscriptionIis.ps1 -SiteName 'Staff reports' -ApplicationPool 'Staff reports'`. It checks pool assignment and installed Application Initialization, then sets AlwaysRunning, preload, initialization after restart, idle timeout zero and a 90-second pool shutdown limit. Use the current .NET 10 Hosting Bundle and in-process hosting.

   The merged toolkit's exact web.config whitelist forbids the former packaged ANCM `shutdownTimeLimit=60`. Infrastructure must provision an inherited 60-second ANCM setting outside the replaceable root. Prove it applies to a candidate without an override; the old root web.config's effective value is insufficient. Deployment must fail closed until this preservation check passes.

   Every application configuration also requires IIS `AutoStart=true`. With `Subscriptions.Enabled=true`, verify the globally registered Application Initialization module uses the canonical full path to `warmup.dll` with no global precondition and is present in the site's effective module collection with empty `type` and `preCondition`; verify `preloadEnabled=true` and effective `doAppInitAfterRestart=true`. The consumer preflight checks these settings read-only and stops before deployment if they are missing; it does not configure IIS. Follow the [SSRS-specific DEV preflight](../clc-deployment.md#ssrs-specific-dev-preflight) and [shutdown/subscription hosting details](../clc-deployment.md#preserving-shutdown-and-subscription-hosting).

   The application host retains 45 seconds; Hangfire retains 30 seconds and checks cancellation every second. Test app-offline/recycle/stop with running work. SQL recovers abruptly terminated work. See Microsoft's [.NET 10 IIS hosting and initialization](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0) and Hangfire's [always-running ASP.NET Core hosting](https://docs.hangfire.io/en/latest/deployment-to-production/making-aspnet-app-always-running-on-iis).

7. Enable subscriptions, restart once, and verify schema validation/Hangfire startup in protected ProgramData NLog files and Windows Event Viewer. Recycles and deployments interrupt work. Avoid needless scheduled recycling, keep preload active, and test OS restart/cold start without an interactive user. Instances share SQL occurrence uniqueness and application locks, but worker limits apply per instance. Pause/disable subscriptions before maintenance when delivery must stop.

Local inspection found `Clc.Postmark.Api` 1.0.2's public Send methods synchronous, with no cancellation-token contract. A minimal async, fixed-endpoint adapter instead provides bounded cancellation and explicit ambiguous outcomes, without its extra REST client. It follows the [Postmark Email API contract](https://postmarkapp.com/developer/api/email-api). Hangfire's old transitive Newtonsoft.Json floor is overridden with patched 13.0.4; security auditing remains enabled.

## Configuration bounds and scheduling

All settings belong under `Subscriptions` in the existing external file. Defaults and ranges:

- `Workers`: 2 (1–8).
- `ExecutionSeconds`: 600 (30–3600).
- `DeliverySeconds`: 60 (5–120).
- `MaxRecipients`: 20 (1–50).
- `MaxSubscriptionsPerOwner`: 100 (1–1000).
- `MaxAttachmentBytes`: 6291456 (1024–6291456).
- `MaxLinkCharacters`: 16000 (1024–229376).
- `MaxAttempts`: 3 (1–5).

`PostmarkServerToken` and `From` are required only when enabled. `PortalBaseUrl` must be an HTTPS origin with no path, query or credentials. Connection `Initial Catalog` must be `SsrsWeb`.

Schedule definitions are independent of Hangfire job IDs. Options include one-time; elapsed-minute intervals (5–10080 minutes); local daily/every N days; selected weekdays/every N weeks; selected month days; relative weekdays (first through fourth or last); and five-field [Cronos cron](https://github.com/HangfireIO/Cronos).

Weekly groups start Sunday, anchored to the start week. Monthly groups anchor to the start month; absent day 31 is skipped. Start and optional end are inclusive local wall times in the selected zone. Eastern (`America/New_York`) is the default.

Calendar schedules move a DST gap to the first valid minute. Ambiguous wall times run once, at the earlier UTC instant. Elapsed intervals preserve elapsed minutes through both transitions. Advanced cron follows Cronos's DST semantics, which can differ from calendar schedules. Calendar intervals are not disguised as cron strings.

Creating/editing a recurrence starts at its next occurrence at or after the change; it never backfills before creation. Unchanged schedules retain their next due time. Pausing cancels pending work. Resuming starts from now; a one-time occurrence without an expired end may catch up once. Recipient/parameter edits cancel pending older revisions and fence rendering work before send. Subscription/occurrence uniqueness prevents resuming or editing from resending the same one-time occurrence.

After a prolonged outage, reconciliation selects the **latest** eligible missed occurrence and advances to the next normal occurrence. It supersedes abandoned unsent older/retry work under the execution gate. Active workers keep their gate and are not concurrently replaced. Repeated dispatch, enqueue, restart or reconciliation cannot duplicate occurrence rows or deliver one subscription concurrently.

Dedicated unpooled application connections tie session-owned SQL locks to physical sessions. Hangfire uses separate storage connections. No second timer or scheduling service runs alongside it.

## Execution, links and delivery outcomes

Validated Entra `TenantId` + `UserObjectId` authorize every management operation. Validated email/preferred_username refresh informational `UserEmail` when available; browser owner fields are ignored. POST/PUT/DELETE retain antiforgery and Staff authorization. Paths are checked against the current catalog before save, during execution and before delivery. Revoked owners cannot create, update or resume subscriptions.

Each configurable parameter stores Use Default (no frozen values) or explicit values. Missing, explicit null, blank and selected multivalues retain ParameterService semantics. SSRS reevaluates defaults and cascades on every execution. Unattended execution reuses SOAP resolution/load/set/info/render and export MIME/signature/size checks. It has its own bounded deadline and reserves no interactive ReportSession.

Background SOAP fault content is suppressed. Execution diagnostics/job logs contain stable codes and IDs, never output, parameter values or recipients. User-driven parameter previews retain interactive diagnostic policies. Protect logs and disable query/body tracing.

Links use permanent portal catalog routes, effective visible inputs, case-sensitive repeated keys, null suffixes and URL encoding. Opening them runs fresh data and requires normal Entra/catalog authorization. SSRS metadata cannot distinguish literal hidden defaults from expressions. Any hidden effective input therefore disables link/both delivery with feedback; attachment-only remains available. Oversized/unrepresentable links fail before send, including defaults that grow after creation.

Attachment recipients need no portal authentication to open delivered bytes. Output stays only in bounded processing memory, never archived in SQL or on disk.

Execution statuses:

- **Pending** or **Running**.
- **Delivered**: Postmark accepted the message, not proof of inbox delivery or no later bounce.
- **Failed**: failed before accepted delivery.
- **Unknown**: the provider may have accepted it.
- **Skipped**.

Known pre-send transient failures and explicit HTTP 429 rejections retry with bounded 2/4-minute exponential backoff, up to `MaxAttempts`. Uncertain transport failures, malformed/oversized success replies, server failures and interrupted sends are **Unknown and never automatically resent**.

The send marker commits before HTTP. Even a crash immediately before HTTP can conservatively leave Unknown, favoring duplicate prevention over automatic recovery. Inspect provider activity manually using the stored MessageID, where confirmed, or execution metadata. Resolve the cause before creating a deliberate new occurrence. **Never reset send markers or blindly retry Unknown rows.**

## Revocation and inspection

Operators use their separate SQL identity in `SsrsSubscriptionAdministrator`:

```sql
EXEC [dbo].[RevokeSubscriptionsForEntraUser]
    @TenantId='00000000-0000-0000-0000-000000000001',
    @UserObjectId='00000000-0000-0000-0000-000000000011',
    @Reason=N'Approved administrative revocation';
SELECT [TenantId],[UserObjectId],[Revoked],[UpdatedUtc] FROM [subscriptions].[Owner];
SELECT [TenantId],[UserObjectId],[Enabled],[NextDueUtc] FROM [subscriptions].[Subscription];
SELECT TOP(100) [Id],[SubscriptionId],[OccurrenceUtc],[Status],[Attempts],[Diagnostic],[ProviderMessageId]
FROM [subscriptions].[Execution] ORDER BY [OccurrenceUtc] DESC;
SELECT TOP(100) * FROM [subscriptions].[OwnerAudit] ORDER BY [Id] DESC;
-- After approval; subscriptions remain paused until their owner resumes them:
EXEC [dbo].[ReinstateSubscriptionsForEntraUser]
    @TenantId='00000000-0000-0000-0000-000000000001',
    @UserObjectId='00000000-0000-0000-0000-000000000011',
    @Reason=N'Approved reinstatement';
```

Every revocation invocation records an audit. Revocation safely works before a user's first subscription, disables existing subscriptions and cancels queued pending work. Execution checks it before running and immediately before delivery.

The final owner gate stays held through provider acceptance/outcome recording. Revocation waits for an admitted send to finish. Once the procedure succeeds, later deliveries cannot pass the gate. It cannot recall accepted messages. A lock/timeout error means revocation failed to complete; investigate and retry. No Microsoft Graph credentials or administrative web UI are involved.

Authorized SQL operators can inspect `[HangFire].[Server]`, `[HangFire].[JobQueue]`, `[HangFire].[Job]` and `[HangFire].[State]` alongside application execution IDs. Do not edit Hangfire tables to resolve delivery ambiguity.

Back up definition, owner, audit and history tables with the database. History stores diagnostics, not output. Apply organizational retention through a reviewed future migration; this version does not destructively purge subscription/audit history.

## Rollback and DEV acceptance

Before rollback, set `Enabled=false` in the protected external file. Restart/drain the old version across all instances, then use the existing deployment rollback mechanism. Preserve the database and backups; no down migration deletes definitions or history. Disabled hosts do not process lingering Hangfire work.

The IIS toolkit restores managed application files only. It does not roll back the `SsrsWeb` or `HangFire` schemas/data, Postmark deliveries, external configuration or Data Protection keys. Back up subscription SQL state, configuration and keys through their separate infrastructure procedures; the planned DEV `RestorePrevious` check verifies application-file recovery only.

Reenabling reuses durable state, catches up only the latest missed occurrence, and never automatically resends Unknown. Reenable only an application matching application schema 1 and Hangfire schema 9.

Before declaring DEV acceptance, use the separate administrator-managed setup artifact and verify:

1. Migrations, TLS and least-privilege runtime/operator roles on dedicated DEV `SsrsWeb`. Confirm Hangfire version 9, application schema 1, restart persistence and no production runtime DDL.
2. Actual IIS identity and enabled startup configuration. Exercise query-default dates; explicit, null, blank, multiselect and cascading parameters; hidden-input restrictions; all delivery modes/formats; recipient normalization; attachments; and canonical links under real authentication.
3. Schedules in the selected zone and across DST; pause/resume/edit; recycle/stop/restart without an interactive user; latest missed occurrence recovery; and multiple instances, if deployed.
4. Revocation during rendering and provider acceptance; reinstatement while paused; transient rejection retries; and ambiguous outcomes that stay Unknown and are never automatically resent.
5. Bounded logs with codes/IDs but no output, parameter/recipient content, tokens or provider secrets. Verify durable history, owner audit and rollback preserving definitions/backups.

SQL integration suites use GUID-named isolated local databases, never operational `SsrsWeb`. Without required Windows/local-SQL access, cases are **skipped or inconclusive, not passing**. Mock delivery and synthetic SQL do not prove live SSRS, IIS, Postmark delivery or inbox receipt.
