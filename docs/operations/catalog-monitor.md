# SQL catalog monitor

[Back to operations](README.md)

The optional monitor is disabled by default and requires SQL Server 2016 or newer. Enable it only after an administrator verifies the dev SSRS schema and permissions. SQL only invalidates the shared metadata cache. SOAP supplies metadata; SSRS executes reports. The monitor installs no application database, SQL Agent job, trigger, view or stored procedure, and changes no SSRS schema.

| JSON setting | Default and supported range |
| --- | --- |
| `CatalogMonitor:Enabled` | `false` |
| `CatalogMonitor:IntervalSeconds` | 30 seconds; 5–3,600 |
| `CatalogMonitor:ConnectionString` | Required when enabled; Windows integrated authentication and validated TLS |
| `CatalogMonitor:CommandTimeoutSeconds` | 5 seconds; 1–120 |

External configuration example (placeholders only):

```json
"CatalogMonitor": {
  "Enabled": true,
  "IntervalSeconds": 30,
  "CommandTimeoutSeconds": 5,
  "ConnectionString": "Server=tcp:<approved-sql-host>,1433;Database=ReportServer;Integrated Security=true;Encrypt=true;TrustServerCertificate=false"
}
```

Use the existing AD service account and protect the external configuration file. SQL usernames/passwords and certificate-validation bypasses are rejected. Use a trusted certificate matching the SQL hostname. Install its issuing/intermediate CA trust on the IIS host. `Encrypt=Mandatory` (`true`) with `TrustServerCertificate=false` validates the certificate. `Strict` also requires TDS 8.0 support. See Microsoft's [SqlClient TLS documentation](https://learn.microsoft.com/en-us/sql/connect/ado-net/encryption-and-certificate-validation?view=sql-server-ver17).

## SQL driver compatibility

The portal uses `Microsoft.Data.SqlClient` 7.1.1 and `Microsoft.Data.SqlClient.SNI.runtime` 7.1.0. Both SQL features require Windows `Integrated Security=true`, not Entra SQL authentication (`Authentication=Active Directory ...`). The [7.x Azure authentication split](https://github.com/dotnet/SqlClient/blob/main/release-notes/7.0/7.0.0.md) therefore needs no `Microsoft.Data.SqlClient.Extensions.Azure` package here. The dependency graph excludes Azure.Identity, MSAL and Microsoft.Identity.Client.NativeInterop. Portal Entra sign-in is separate; IdentityModel/Validators packages remain aligned at 8.16.0.

Keep the existing encryption and certificate policy. Do not use the removed `Type System Version=SQL Server 2000` setting or enable new pooling/async switches during this upgrade. See the [7.1 release notes](https://github.com/dotnet/SqlClient/blob/main/release-notes/7.1/7.1.0.md). The [licensing guide](../licensing-and-distribution.md) covers SNI redistribution requirements.

For upgrades, run the [README verification commands](../../README.md) on Windows with local SQL Server. `SqlSubscriptionTests` and `SqlSubscriptionStartupTests` must execute, not skip. Check ordinary and release-shaped `win-x64` publishes for SqlClient Abstractions/Logging assemblies and the native SNI DLL. Confirm Azure/MSAL/NativeInterop packages are absent from `SsrsPortal.deps.json` and output.

Local integrated-authentication and Hangfire tests do not prove host acceptance. Still verify the IIS account's SQL access, production TLS, live Entra sign-in and the actual SSRS catalog query.

## Read-only permissions

An authorized DBA must map the application's Windows service account through established procedures. Minimum permissions are `CONNECT` to `ReportServer` and `SELECT` on `[ReportServer].[dbo].[Catalog]` and `[ReportServer].[dbo].[DataSource]`. These also expose enough metadata for the optional diagnostic. The production query needs no `VIEW DEFINITION`. SOAP permissions remain separate; SQL access cannot authorize reports.

Grants are database-scoped. `GRANT ... ON OBJECT::` does not accept three-part names. The DBA must grant permissions in `ReportServer` using established tools. These instructions execute no grants; startup requires no permission changes. Do not grant write, DDL, decryption, `db_datareader`, `db_owner`, server administrator or `RSExecRole` rights.

## Verified development schema and fixed query

The supplied read-only dev observation found SQL Server 2022 engine `16.0.1000.6` and database `ReportServer`. **This is the database engine version, not the SSRS application build.** The SSRS build remains unverified. Recheck the target host's schema before enabling monitoring. These observations do not prove compatibility with the revised production query.

Relevant `[ReportServer].[dbo].[Catalog]` columns:

| Column | SQL type | Nullable |
| --- | --- | --- |
| `ItemID` | `uniqueidentifier` | No |
| `Path` | `nvarchar(425)` | No |
| `Type` | `int` | No |
| `ModifiedDate` | `datetime` | No |
| `Hidden` | `bit` | **Yes** |
| `LinkSourceID` | `uniqueidentifier` | Yes |

Known `[ReportServer].[dbo].[DataSource]` columns:

| Column | SQL type | Nullable | Snapshot use |
| --- | --- | --- | --- |
| `DSID` | `uniqueidentifier` | No | Stable row identifier |
| `ItemID` | `uniqueidentifier` | Yes | Owning catalog identifier |
| `SubscriptionID` | `uniqueidentifier` | Yes | Configuration fingerprint |
| `Name` | `nvarchar(260)` | Yes | Configuration fingerprint |
| `Extension` | `nvarchar(260)` | Yes | Configuration fingerprint |
| `Link` | `uniqueidentifier` | Yes | Shared-source catalog identifier |
| `CredentialRetrieval` | `int` | Yes | Configuration fingerprint |
| `Prompt` | `ntext` | Yes | Configuration fingerprint |
| `ConnectionString` | `image` | Yes | Configuration fingerprint |
| `OriginalConnectionString` | `image` | Yes | Configuration fingerprint |
| `OriginalConnectStringExpressionBased` | `bit` | Yes | Configuration fingerprint |
| `UserName` | `image` | Yes | Configuration fingerprint |
| `Password` | `image` | Yes | Configuration fingerprint |
| `Flags` | `int` | Yes | Configuration fingerprint |
| `Version` | `int` | No | Configuration fingerprint |
| `DSIDNum` | `bigint` | No | Configuration fingerprint |

[Read-SsrsCatalogSnapshot.sql](../../src/SsrsPortal/Services/Read-SsrsCatalogSnapshot.sql) is fixed SQL with exactly two result sets:

1. `ItemID`, `Path`, `Type`, `ModifiedDate`, `Hidden`, `LinkSourceID`, ordered by `ItemID`.
2. `DSID`, `ItemID`, `Link`, `Fingerprint`, ordered by `DSID`; `Fingerprint` is a 32-byte SHA-256 configuration hash.

Comparisons distinguish `Hidden` NULL, false and true. None authorizes visibility. The monitor only requests invalidation; SOAP validates visibility and safety.

The hash covers the 13 configuration columns above in fixed order. Each field contributes `0x00` for NULL, or `0x01` plus its 32-byte SHA-256 hash otherwise. Hashing these ordered frames preserves field boundaries and NULL versus empty values.

`image` becomes `varbinary(max)`. `ntext` becomes `nvarchar(max)`, then `varbinary(max)`. Other values use explicit binary conversions. Concatenation starts as `varbinary(max)`, avoiding 8,000-byte conversion or concatenation truncation on the verified SQL Server 2022 engine. [Microsoft's HASHBYTES reference](https://learn.microsoft.com/en-us/sql/t-sql/functions/hashbytes-transact-sql?view=sql-server-ver17) documents SHA-256 and the input-size limit on older engines.

Only row/owner/link identifiers, catalog metadata and final hashes leave SQL Server. Connection strings, prompts, usernames and passwords are never returned or logged. The query reads no `Catalog.Content`, report definitions or RDL. Credential blob changes, including key rotation, can trigger refresh even when logical credentials are unchanged.

There is no runtime metadata inspection, identifier selection, schema hash or engine/SSRS version check. Upgrades require deliberate query/test review: new configuration columns are not captured automatically. [Test-SsrsCatalogSchema.sql](../../scripts/Test-SsrsCatalogSchema.sql) is an optional read-only manual diagnostic using fully qualified metadata/data objects. The monitor never calls it; polling does not require it.

## Consistency, limits and manual SSMS execution

The application uses a short, bounded `SERIALIZABLE` read-only transaction. It fully reads both result sets and rejects extra sets, malformed/duplicate data or exceeded limits. It commits before returning a snapshot, preserving consistency without changing database isolation settings.

`SERIALIZABLE` holds shared and range locks until the transaction ends. These can block writers. The static query removes per-poll metadata work, not locks. Validate duration and contention under dev load before enabling monitoring. See [Microsoft's isolation-level reference](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql?view=sql-server-ver17).

The application supplies these limits:

- `@MaximumCatalogRows = Portal.MaxMetadataItems` (default 6,000).
- `@MaximumDataSourceRows = 3 * Portal.MaxMetadataItems` (default 18,000).

Each SELECT requests at most its limit plus one row. Overflow fails the whole poll. `CommandTimeoutSeconds` (default five seconds) bounds both command and total connection/read lifetime. Logs contain duration, row counts and received bytes, never row contents.

To verify the exact script manually:

1. Connect to the approved development SQL endpoint in SSMS using the intended Windows identity and validated TLS. Keep `master` selected; three-part references explicitly target `ReportServer`.
2. Enable **Query → SQLCMD Mode**. Set **Query Options → Execution → General → Execution time-out** to five seconds (or the configured monitor timeout). The wrapper also bounds lock waits to five seconds.
3. Execute this as one batch. `:r` inserts the unmodified production script. Change only the local checkout path and the two row limits to match configuration. Do not add `GO` within the wrapper.

```sql
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 5000;
DECLARE @MaximumCatalogRows int = 6000;
DECLARE @MaximumDataSourceRows int = 18000;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRY
    BEGIN TRANSACTION;
:r "<checkout>\src\SsrsPortal\Services\Read-SsrsCatalogSnapshot.sql"
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
    SET LOCK_TIMEOUT -1;
    THROW;
END CATCH;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
SET LOCK_TIMEOUT -1;
```

Without SQLCMD Mode, paste the entire SQL file at the `:r` line. Keep the parameter declarations and transaction wrapper.

SSMS timeout/cancellation may bypass `CATCH`. To release locks, immediately run `IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; SET TRANSACTION ISOLATION LEVEL READ COMMITTED; SET LOCK_TIMEOUT -1;` in the same window, or close the connection. The wrapper changes no database settings or data.

Validate both complete result sets for:

- Expected fields and unique nonempty IDs.
- Preserved nullable Hidden/owner/link values.
- 32-byte hashes and counts within the declared limits.

An overflow row, missing result set, error or timeout fails the snapshot. Record server/client elapsed time, both row counts, received bytes when available, and lock contention. Run the optional diagnostic from `master` in a separate SSMS window to compare metadata and relationship counts without exposing configuration values.

At the recorded validation, no suitable read-only dev connection was available. The revised query had **not** run against dev `ReportServer`. Engine execution, large-value hashes, worker read permissions, TLS, credential rotation, duration/TDS bytes and contention remain live checks. Static SQL parsing and synthetic provider/hash tests do not complete them.

## Polling, invalidation and recovery

The first complete snapshot establishes the baseline. Later polls compare full catalog/source records: additions/deletions, stable identities, paths, types, nullable visibility, dates, linked-target IDs, source owner/link IDs and fingerprints.

Unchanged polls make zero SOAP requests. Each transition requests one coalesced SOAP metadata refresh. SQL never publishes reports or admits opaque IDs. Shared-source moves are detected even when row IDs stay unchanged. Definition/source dependency maps and targeted inspection invalidation no longer exist.

Polls run serially, waiting the configured interval after completion or failure. A generation counter prevents in-flight SOAP responses from overwriting newer invalidations. Metadata work keeps request deadlines and the SOAP semaphore. Source notifications refresh the same catalog without extra report/source loops. Running sessions retain owner, cancellation and expiry behavior unless global root/blocklist policy changes.

`CatalogSeconds` provides request-driven fallback when monitoring is disabled or unavailable. A failed SQL read means unknown state, never an empty/partial baseline or proof of no changes. The next successful read forces SOAP reconciliation even if rows match the prior baseline. A failed SOAP refresh keeps reconciliation pending and blocks new resolutions through the invalidated catalog.

Logs record categories/counts/durations and filtered SQL failures with server/database/query context. They omit connection strings and fingerprints. SOAP failure/exception details use the default application Debug level; see [logging guidance](troubleshooting.md#detailed-ssrs-diagnostics). Startup rejects invalid enabled-monitor configuration.

For an SSRS upgrade, keep monitoring disabled until you:

1. Compare actual columns/types/nullability with the fixed contract.
2. Add newly relevant configuration fields to the query/tests.
3. Verify identity relationships and run the exact query under the worker account.
4. Exercise catalog add/delete/rename/move/hidden changes, direct/shared source changes, source moves and credential rotation in dev.
5. Measure full snapshot duration/rows/bytes, SOAP call counts and detection-to-refresh latency.

The fixed query, provider, bounded read-only transaction and schema contract remain the current design. See the [README](../../README.md) for setup and verification.
