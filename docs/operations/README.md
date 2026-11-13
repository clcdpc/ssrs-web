# Windows Server 2022 / IIS operations

These instructions describe host setup and live acceptance; they do not prove deployment. Only authorized operators may perform administrator, directory, Entra and live SSRS actions.

- [Host setup](#host-setup), [configuration](#external-configuration-provisioning), [Entra and catalog policy](#multi-tenant-entra-onboarding-and-global-catalog-policy), and [live gates](#live-gate-checklist) are below.
- [Troubleshooting](troubleshooting.md) covers logs, NTFS provisioning and SSRS diagnostics.
- [SQL catalog monitor](catalog-monitor.md) covers optional read-only change detection.
- [Subscriptions](subscriptions.md) covers scheduled delivery, recovery and acceptance.
- The [CLC deployment runbook](../clc-deployment.md) covers release identity, validation, configuration revisions, persistent keys and recovery.
- The runbook's [SSRS-specific DEV preflight](../clc-deployment.md#ssrs-specific-dev-preflight) and [seven-check pilot](../clc-deployment.md#planned-real-dev-seven-check-pilot) record application gates that generic toolkit enrollment does not establish.

## Host setup

Provision the external key directory and deployment configuration before deploying a Release identity. Keep keys, configuration and logs outside application content. Every configuration edit needs a new opaque revision. Local readiness does not establish live Entra/SSRS acceptance.

1. **IIS:** Install IIS and the current .NET 10 Hosting Bundle. Repair the bundle if IIS was installed afterward. Use a dedicated 64-bit pool, No Managed Code, integrated pipeline and one worker. Publish the generated `web.config` with the application. Keep ASP.NET detailed errors, stdout logging and HTTP body tracing off in normal production. See Microsoft's [IIS hosting guide](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0).
2. **Worker identity:** Use the application's regular AD service account as the pool's custom identity; a gMSA is not required. An AD/IIS administrator manages its password through the protected account/deployment process. Never put it in source or SQL connection strings. Grant only required worker logon rights and application read/execute. Keep writable, log and key locations separate. Do not grant local Administrators membership. The CLC deployment identity is separate, is a local administrator only on managed IIS servers, and runs with an enabled elevated token for application-content writes and pool control. It does not need the worker's external key or logging permissions.
3. **Service access:** Allow outbound TLS to the configured SSRS origin and HTTPS to Entra discovery/token endpoints. Allow the configured SQL endpoint when monitoring is enabled. SOAP uses process credentials through `UseDefaultCredentials`; SQL uses `Integrated Security=true`. Redirects stay disabled and certificate validation stays enabled. Enable IIS Anonymous authentication so ASP.NET Core handles OIDC. Give the AD account minimum SSRS read-properties/execute/view access to roots, ancestors, reports and required linked targets, data sources and subreports. The portal does not inspect definitions/sources to approve reports. Monitoring only detects changes; it never modifies SSRS databases.
4. **Entra:** Configure the single organizational multi-tenant confidential app and exact `ReportViewer` role [below](#multi-tenant-entra-onboarding-and-global-catalog-policy). Supply one client ID, explicit tenant GUIDs and shared catalog settings. Store and rotate the server-side client secret in the restricted external `application.json`. Keep secrets out of source, command lines, dumps and browser settings.
5. **HTTPS and proxies:** Bind the approved hostname/certificate and set `AllowedHosts`. Production enables HSTS and HTTPS redirection. In-process IIS integration handles trusted server forwarding. Added forwarded-header middleware trusts the framework's loopback allowlists for one hop. Never clear those allowlists or accept arbitrary `X-Forwarded-*` headers. Review explicit trust settings for any additional proxy. Test redirects and OIDC callbacks under the final topology.
6. **Persistent keys:** Provision `DataProtection.KeyRingPath` and `ssrs-web-dpapi-user-v1` per the [deployment runbook](../clc-deployment.md). Grant the runtime account key-directory read/write. Enable Load User Profile and setProfileEnvironment for user-scope DPAPI; see [IIS Data Protection guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0#data-protection). The stable application discriminator and retained ring preserve cookies across upgrades. The startup smoke must pass under the actual worker identity. Unmarked legacy rings need separately approved migration without overwriting keys. Infrastructure owns key/profile backup and recovery. Never commit keys or credentials. Recycling still clears report executions.
7. **Logs:** NLog writes to persistent ProgramData [Logs](troubleshooting.md#persistent-file-logging). Logs can contain report parameters, SQL diagnostics and SSRS faults; restrict them to authorized administrators/developers. Disable query/body logging, content-bearing failed-request traces and HTTP client body tracing. Omit IIS `cs-uri-query` and protect queries at upstream proxies too. Microsoft categories default to Warning; authentication protocol internals must remain at least Warning. Use browser Reference IDs for support.
8. **Limits:** Request deadlines cover metadata cascades and downloads. Set SSRS execution/data-source timeouts separately. Portal cancellation stops local requests and deletes local state, not SSRS server jobs. Tune limits after synthetic load and approved live performance tests. Measure process/parser overhead and IIS limits before raising size or capacity bounds. Rejected requests return safe errors.

Use one worker and [non-overlapping recycling](troubleshooting.md#persistent-file-logging). Subscriptions have additional [IIS hosting and shutdown requirements](subscriptions.md#production-provisioning-and-deployment).

The trusted toolkit's `Get-ClcHostEnrollment` and `Initialize-ClcHostEnrollment -Operation Plan` report generic enrollment state only. Before using the consumer deployment workflow, complete the [application-specific DEV preflight](../clc-deployment.md#ssrs-specific-dev-preflight); its live host checks remain **NOT RUN** until actual evidence is recorded.

## Report link hosting

Canonical `/report/...` routes identify catalog-authorized reports, not SSRS URL-access endpoints. Queries can contain report inputs. Disable IIS `cs-uri-query`, proxy query logs, request/body tracing and framework request-start logging, as for OIDC callbacks. Application exception and scoped execution logs retain their restricted access rules.

Link limits are:

- Kestrel request lines, total headers and HTTP/2 header fields: 256 KiB, matching the body bound.
- Link initialization and Copy report link: 224 KiB.
- Inputs: 100 by default, at most 200.
- Selected values per input: at most 5,000, also bounded by `MaxOptions`.
- Each value: 2,048 characters. Catalog paths: 512 characters.

IIS/proxies may reject smaller URLs **before ASP.NET Core runs**. Review finite URL/query/request-line limits for large selections. Kestrel success does not prove IIS support. Test encoded percent, plus, question-mark and Unicode names through actual request filtering. Do not disable filtering or change operational IIS settings merely to bypass failures. Keep strict segment validation, catalog authorization and the generated `web.config` security restrictions.

Recycling clears local catalog IDs and sessions, but canonical links remain valid. Existing-tab recovery can report expiry and restore the original link without rerunning. A fresh valid parameterized link runs through normal staff/antiforgery checks. Legacy opaque-ID addresses may fail after recycle. PDF Print and downloads use owner-bound sessions and no-store responses.

Complete [workspace live acceptance](../behavior/report-workspace.md#workspace-links-printing-and-recovery) on DEV/IIS. Optional [subscriptions](subscriptions.md) have separate delivery requirements; ordinary report execution does not depend on their database or provider.

## External configuration provisioning

Normal Development and Production startup use `CommonApplicationData\clc-ssrs-web\Config\application.json`. `Clc.Configuration` owns its shared startup snapshot, path convention and first-run example copy. On Windows, startup also checks that the package's CommonApplicationData path matches the existing native ProgramData known-folder and system-drive validation; a mismatch fails closed rather than redirecting the external files. No ConfigFile/bootstrap variable or normal path argument exists. Appsettings, user secrets and ordinary environment/command-line settings do not supply operational values. Hosting controls such as environment selection and `--urls` remain supported.

A published build supports hostless path discovery with `dotnet <published SsrsPortal.dll> --clc-appdata-directory` before configuration is provisioned. It prints only the absolute application data root. It does not create directories, load `application.json`, initialize logging or start the host. It does not establish file access, IIS enrollment or deployment readiness; continue through the separate configuration, ACL, enrollment and readiness checks below.

`SsrsPortal:TestConfigurationFile` exists only for isolated Development fixtures. Production and Staging reject it before reading a file. Never configure it in IIS.

The sanitized example is included in the published application. On a missing file, one normal startup under an appropriately permitted setup identity creates the application data and `Config` directories, copies the example byte-for-byte, then stops with an instruction to edit and restart. Run this on the target host from a trusted published build:

```powershell
$applicationDll = 'C:\Apps\SsrsPortal\SsrsPortal.dll'
$appDataDirectory = & dotnet $applicationDll --clc-appdata-directory
if ($LASTEXITCODE -ne 0) { throw 'Application data path discovery failed.' }
$configFile = Join-Path $appDataDirectory 'Config\application.json'
if (!(Test-Path -LiteralPath $configFile)) {
    & dotnet $applicationDll
    if ($LASTEXITCODE -eq 0 -or !(Test-Path -LiteralPath $configFile -PathType Leaf)) {
        throw 'First-run configuration bootstrap did not create its starter file; review startup diagnostics.'
    }
}
```

Replace every literal `REPLACE_ME` in the created file with approved portal hostnames, the real HTTPS SSRS `/ReportServer` URL, roots/exclusions, Entra ID/secret and approved tenants, revision and key path. Review and restrict its NTFS ACL before starting the runtime identity; normal runtime needs read access, not write access to `Config`. Enable the optional SQL monitor only after live acceptance. The [example](../../src/SsrsPortal/application.Example.json) is a template, not an operational file. Existing `application.json` is never overwritten or merged; startup rejects remaining placeholders and retains the SSRS-specific validation. `Portal`, `Entra` and `AllowedHosts` are required. Deployment validation and `--clc-appdata-directory` remain read-only; if validation must run before normal startup, the setup administrator must provision the file from the published example separately.

### Configuration reference

Required values have no operational default. Sizes below are bytes, with MiB equivalents. See [report link hosting](#report-link-hosting) for fixed request/link limits, the [deployment runbook](../clc-deployment.md) for `Deployment`/`DataProtection`, and [subscriptions](subscriptions.md#configuration-bounds-and-scheduling) for delivery settings.

| JSON setting | Required value, default and supported bounds |
| --- | --- |
| Hosting environment (`ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT`) | `Production` for deployment; hosting control, not an `application.json` value |
| `AllowedHosts` | Required approved portal hostname(s); example is `localhost` |
| `Portal:UseMock`, `Portal:TestAuth` | Both `false`; synthetic SOAP/authentication are Development-only, and test authentication requires mock SOAP |
| `Portal:Diagnostics:Enabled` | `false`; enables detailed server failure logging, which can include sensitive SSRS fault content |
| `Portal:Diagnostics:DetailedFailuresAtDebug` | `true`; captures bounded server failure details at Debug, independent of browser details |
| `Portal:Diagnostics:ShowDetailsInBrowser` | `false`; Development only, and requires an authorized signed-in user plus loopback peer and effective address; rejected outside Development |
| `Portal:ReportServer` | Required HTTPS `/ReportServer` URL; no user info, query, fragment or alternate path |
| `Portal:AllowedRoots` | Required list of canonical catalog roots |
| `Portal:ReportAccessPolicy` | Required explicit value `GlobalBlocklist` |
| `Portal:ExcludedPrefixes` | `[]`; blocked canonical paths. An empty list permits visible reports and linked reports under the configured roots |
| `Entra:AllowedTenantIds` | Required nonempty, distinct organizational tenant GUIDs; the consumer tenant is rejected |
| `Entra:ClientId` | Required GUID for the organizational multi-tenant confidential application |
| `Entra:ClientSecret` | Required outside synthetic Development authentication; protected server-side secret in the external file |
| `Portal:RequestSeconds` | `30`; 1–120 seconds for the request deadline; SOAP operations also use this default timeout |
| `Portal:MaxSoapBytes` | `25,165,824` (24 MiB); 1,024–67,108,864 (64 MiB) for SOAP request/response XML |
| `Portal:MaxOutputBytes` | `16,777,216` (16 MiB); 128–33,554,432 (32 MiB) for decoded report output |
| `Portal:MaxConcurrentSoap` | `8`; 1–32; caps concurrent SOAP calls and API admissions |
| `Portal:MaxMetadataItems` | `6,000`; 1–10,000 combined catalog items |
| `Portal:MaxOptions` | `5,000`; 1–10,000 choices per parameter; selected values are also capped at 5,000 |
| `Portal:MaxParameters` | `100`; 1–200 parameters per report |
| `Portal:MaxStreams` | `100`; 1–200 output streams per rendered page |
| `Portal:MaxToggleActions` | `1,000`; 1–2,000 expand/collapse actions per rendered page; mappings also consume session budgets |
| `Portal:MaxSessions` | `100`; 1–500 logical sessions per process; a pending replacement may share its source session's capacity slot |
| `Portal:MaxSessionsPerUser` | `3`; 1–10 logical sessions per user; replacements reserve capacity before SSRS `LoadReport` |
| `Portal:SessionMinutes` | `15`; 1–60 idle minutes; expiry is checked on requests and by a 30-second reaper |
| `Portal:CatalogSeconds` | `600`; 1–3,600 seconds for catalog cache lifetime before refresh when needed |
| `Portal:MaxSessionBytes` | `33,554,432` (32 MiB); 4,096–67,108,864 (64 MiB) per session, including cached HTML, input state, streams/actions and fixed allowance |
| `Portal:MaxAggregateSessionBytes` | `134,217,728` (128 MiB); at least `MaxSessionBytes`, at most 536,870,912 (512 MiB); includes session content and active-operation scratch reservations (`MaxSoapBytes` + 2 × `MaxOutputBytes`) |
| `CatalogMonitor:Enabled` | `false`; optional SQL change detection |
| `CatalogMonitor:IntervalSeconds` | `30`; 5–3,600 seconds |
| `CatalogMonitor:ConnectionString` | Required when enabled; SQL Server/database, Windows integrated authentication and validated TLS; see [SQL catalog monitor](catalog-monitor.md) |
| `CatalogMonitor:CommandTimeoutSeconds` | `5`; 1–120 seconds |
| `Logging:LogLevel:*` | `Information` default, `Debug` for `SsrsPortal`, `Warning` for `Microsoft`, `Microsoft.AspNetCore`, `Microsoft.AspNetCore.Authentication` and `System.Net.Http.HttpClient`; named levels `Trace` through `None` |
| `Logging:File:*` | Enabled, `Daily`, 52,428,800-byte (50 MiB) rollover, 30-day retention; size 1,024–1,073,741,824 bytes and retention 1–3,650 days; no path override. See [persistent file logging](troubleshooting.md#persistent-file-logging) |

Byte budgets cover accounted session content and buffers, not the CLR working set. XML/HTML parsers and framework objects add overhead. Monitor memory and size for concurrency. Active session leases reserve SOAP/output scratch space. Multiple workers or a farm require a reviewed distributed-session design.

### Protecting and changing configuration

- Administrators/SYSTEM control Config provisioning and credential rotation. The actual AD pool identity gets read/traverse only, with no file or directory writes.
- A separately approved virtual-pool setup must grant its `IIS AppPool\<pool>` identity, not an interactive account.
- Remove broad inherited Users/Authenticated Users/IIS_IUSRS access as appropriate. Inspect effective permissions and protect administrative backups equally. Check access as the worker, not just the operator. See [initial NTFS provisioning](troubleshooting.md#initial-ntfs-provisioning).
- Keep Config outside the web root, published app, release replacement tree and source checkout. Deployments must preserve it without packaging or overwriting credentials. Provisioning may need elevation; runtime must not need administrator membership.
- Never use secret-bearing environment variables or command-line settings as a fallback. Remove obsolete `Portal:ApprovedReports`, `Entra:TenantId` and `Entra:GroupId` from every old provider. Even empty values cause startup errors.

Configuration loads once with `reloadOnChange:false`. After an approved edit or rotation, restart the local process or recycle the dedicated pool. Coordinate with staff: recycling discards report sessions.

Missing, unreadable or invalid files stop Production startup before Entra/SSRS access. Diagnostics identify the file/section and remedy without exposing JSON values, credentials or parser/binder inner exceptions. Fix configuration and ACLs through [safe server diagnostics](troubleshooting.md#startup-troubleshooting-and-deployment). No appsettings or synthetic-authentication fallback is enabled.

## Multi-tenant Entra onboarding and global catalog policy

### Registration and assignment

1. In the home tenant, register one app for **Accounts in any organizational directory** (`AzureADMultipleOrgs`). Set Web redirect URI `https://<portal>/signin-oidc` and a protected confidential-client secret. All libraries share the `ClientId`. Personal accounts are unsupported. The authority is fixed at `/organizations/v2.0`; tenants also need explicit allowlisting. See Microsoft's [multi-tenant guidance](https://learn.microsoft.com/en-us/entra/identity-platform/howto-convert-app-to-be-multi-tenant).
2. Define an enabled role with exact value **ReportViewer**, a suitable display name and member type **Users/Groups** (`User` in the manifest). Its UUID belongs to the registration. The portal checks the role value in the ID token's `roles` claim; `Default Access` and group claims do not qualify. See [app roles](https://learn.microsoft.com/en-us/entra/identity-platform/howto-add-app-roles-in-apps).
3. Each member tenant's administrator creates the Enterprise Application/service principal for that registration and grants required consent. Set **Assignment required?** to **Yes**. Consent or service-principal creation alone grants neither staff access nor tenant admission.
4. Under Enterprise Application → Users and groups, assign staff or eligible security groups to **ReportViewer**. Synced AD security groups may use their Entra representation where supported; AD-only groups cannot. Group assignment needs applicable licensing, and nested memberships do not cascade. The portal does not resolve membership through AD, LDAP or Graph. See [assignment constraints](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/assign-user-or-group-access-portal).
5. Set distinct approved organizational GUIDs in nonempty `Entra:AllowedTenantIds`. Set `Entra:ClientId` and `Entra:ClientSecret` in the restricted external file. The portal denies malformed/empty tenant or object GUIDs, personal issuers, wrong audience/signature and missing exact role. Issuers must exactly match `https://login.microsoftonline.com/{tid}/v2.0`; signing-key validation binds the key issuer to that tenant/issuer. Group-overage users qualify only with the role.

### Shared catalog policy

Set canonical `Portal:AllowedRoots`, the global `Portal:ExcludedPrefixes` blocklist and explicit `Portal:ReportAccessPolicy=GlobalBlocklist`.

- A report path blocks that report. A folder blocks descendants using case-insensitive segment boundaries.
- An empty blocklist permits all visible Report and LinkedReport items within roots. Name-substring exclusions no longer exist. Explicitly list restored, archived, internal or system folders that must remain excluded.
- Use the [example](../../src/SsrsPortal/application.Example.json) and [configuration protections](#protecting-and-changing-configuration). Do not maintain an alternative allowlist. The external file is authoritative; there are no inherited operational array indices to clear. Recycle after edits.

The blocklist checks requested items, not dependencies. Native hidden reports and ancestor folders stay excluded. Unblocked reports may still use blocked, hidden or out-of-root targets, subreports and data sources through SSRS. Block linked aliases separately when needed.

SSRS controls definitions, credentials and execution, including custom code, shared datasets, external images and non-SQL sources. Unsupported credential prompts or parameter interactions remain visible but may fail safely.

All authorized staff share this catalog and may attempt unblocked reports available to the AD service account. `User!UserID` and other SSRS identity expressions use that shared identity, not Entra impersonation. Review its permissions and trusted report authors. No Entra tokens go to SSRS, and no tenant/report mapping or authorization database is added. OIDC/role/tenant checks, direct policy, owner-bound sessions, CSRF, cancellation and bounded output sanitization still apply.

### Cookies and revocation

Role/tenant checks run at sign-in, every cookie validation and endpoint authorization. Invalid protected tickets are rejected/deleted and their owner's executions cancelled. Cookies expire after 30 minutes without sliding renewal.

Roles are a sign-in snapshot. Directory changes take effect at a new Entra sign-in; existing cookies may last until expiry. There is no live directory lookup or continuous role-revocation service. Recycle clears executions, but retained keys preserve cookies. Removing a tenant from configuration denies its existing cookies after recycle. App sign-out deletes that browser's cookie and cancels its owner's executions.

### Live Entra acceptance

Test real identities in two approved tenants, an unknown tenant with a role, an approved tenant without one, group-only assignment, and overage with/without the role. Verify per-tenant consent/assignment and secure IIS callbacks, cookies and CSRF. Authorized tenants must see identical catalog IDs/results, with sessions isolated by normalized `tid:oid`.

Test tenant removal plus recycle. Test role deassignment after sign-out/new sign-in, and separately verify the existing-cookie lifetime. Complete the direct-policy and SSRS-delegation [gates below](#live-gate-checklist). Synthetic checks do not close them.

## Deployment repository integration

Release and trusted DEV workflows use [`deployment/toolkit.lock.json`](../../deployment/toolkit.lock.json) as the authority for the toolkit source, version, distribution archive SHA-256, module inventory SHA-256 and asset URI. These pins are populated; they grant no installation or deployment authority.

Operational deployment remains disabled until the host and real DEV prerequisites receive independent approval. Repository tests do not verify runner-group policy, protected host configuration, actual IIS state or DEV access. Follow the [deployment runbook](../clc-deployment.md) for release identity, validation, keys, runner admission, deployment and recovery.

## Catalog cache operations

`Portal:CatalogSeconds` defaults to 600 (range 1–3,600). It is the sole cache lifetime; separate inspection TTLs/caches, concurrency controls and retry timers were removed. Configure it in external `application.json` and recycle after changes. Metadata, SOAP-size, request-deadline and concurrency bounds still apply.

### Discovery and refresh

- `ListChildren(Recursive=true)` runs once per visible, unblocked root. `GetProperties(Hidden)` checks distinct roots and ancestors absent from that enumeration.
- Hidden roots/ancestors exclude their scopes. Missing, inaccessible or malformed responses fail refresh. Every ancestor within a root needs visible Folder metadata. Overlapping roots share a catalog and must agree on folders.
- Remaining Report and LinkedReport items are admitted; folder hierarchy is preserved. Browsing does not retrieve parameters or call `GetItemLink`, `GetItemDefinition`, `GetItemDataSources` or `GetDataSourceContents`. SSRS resolves linked targets when aliases load.

Cold browsing waits only for metadata. Callers share one atomic refresh. Cancelling one waiter does not cancel another's producer. The producer has a request-sized deadline and drains/cancels at shutdown. Failure or cancellation publishes nothing partial; a later request retries without a timer.

SQL invalidation increments the catalog generation. Older responses are discarded and shared work repeats before publication. Policy changes also prevent stale publication.

### Navigation and execution

Catalog, global search and report breadcrumbs share the header and authorized snapshot. A sole navigable top-level Folder supplies the first breadcrumb. Multiple roots or any extra root Report/LinkedReport retain All folders. Hidden, excluded and empty non-navigable folders do not count.

Explicit `/?root=true` and `/?folder=` open the catalog root with All folders current. Global search shows the meaningful root then Search results; Clear search restores normal entry behavior. No extra breadcrumb SOAP call or setting exists. See [catalog navigation](../behavior/report-workspace.md#catalog-and-navigation).

Unknown IDs, folder IDs and raw SSRS paths receive 404 before discovery. Known opaque IDs resolve through the current cache, refreshed after TTL expiry, SQL invalidation or policy change. Current roots/blocklist remain authoritative.

Parameters use bounded metadata and typed/cascading validation. Policy is checked across awaits. Membership is checked again before session reservation, preventing stale execution after a deletion/visibility refresh. Reservation also rejects reports from an older global policy. Execution calls `LoadReport`, optional `SetExecutionParameters`, `Render` and `GetExecutionInfo`. Exports reuse Render; authenticated resources use `RenderStream`.

The parameter summary follows validation and edits. It opens for blocking/browser validation before focus moves to a hidden input. Failed validation preserves successful report actions until a replacement is ready. Expand/collapse uses owner-bound opaque actions and `ToggleItem` on the same execution, then a revision-aware render. Uncertain mutations require synchronization without replay; exports/resources stay blocked until it succeeds. See [parameter behavior](../behavior/report-workspace.md#parameter-state-dependencies-and-dates) and [expansion recovery](../behavior/report-expand-collapse.md#same-execution-update-and-recovery).

### Visibility changes and diagnosis

Warm browsing makes no SOAP calls; normal execution does not force enumeration. Without a working monitor, visibility/removal changes may remain cached for up to `CatalogSeconds` on later requests. SSRS can still reject unavailable reports. Tune TTL or enable the monitor after verification for faster reconciliation.

Direct exclusions/policy changes do not rely on cached approval. Sessions retain their execution, cancellation and idle expiry while policy is unchanged. Any roots/blocklist change revokes all older sessions without inspecting dependencies. Operational edits require recycle.

Inspect operation, timing and byte-count logs. Cold/expired catalogs should show only root/ancestor GetProperties and recursive ListChildren. The synthetic 4,000-item fixture exposes 2,275 reports: two discovery calls for one root, five for sibling roots sharing an ancestor. Unchanged SQL polls add no SOAP traffic; relevant changes/recovery trigger one coalesced refresh.

Use `catalog`, `timeout`, `credentials`, `limit` and trace codes. [File logs](troubleshooting.md#detailed-ssrs-diagnostics) retain bounded parameters and filtered failures, but omit complete request bodies, SQL snapshot values, connection strings and successful output.

For an empty library, check canonical roots/exclusions, native Hidden metadata and AD catalog permissions. For visible reports that fail, inspect SSRS definitions, credentials and execution through approved administration. The portal neither prompts for data-source credentials nor approves source extensions/credential types or parses RDL. HTML/CSS/resource sanitization, iframe isolation and CSP remain mandatory after successful SSRS execution.

## Live gate checklist

| Gate | Required live evidence |
| --- | --- |
| Entra tenants + ReportViewer | Staff in two approved tenants; unknown tenant, missing role and group-only denied; role-authorized overage accepted; missing-role overage denied; consent/assignment/cookie/CSRF/callback/recycle tested |
| AD service account + SSRS/SQL | Actual worker identity accesses dev SSRS without admin membership; read-only SQL/TLS verified; User!UserID observes the shared identity; prompted/broken reports are visible and fail gracefully |
| Page 2 | Approved multi-page report, same SSRS execution, previous/next/last and unknown/known count behavior |
| RenderStream | Actual chart/image/CSS and subreport streams; browser network contains only portal-origin resource routes; unknown renderer URL variants fail closed |
| Interactions | Verify recognized single-level/nested expand/collapse with the actual DEV renderer, owner isolation, stale-action rejection, synchronization without replay and private failure diagnostics; sort/drillthrough/maps stay inert |
| Isolation/cancellation/expiry | Two real users sharing one AD service account, each other's tokens denied for every route; cancelled/aborted export, idle expiry and server-side timeout behavior |
| Fidelity/accessibility/load | Staff checks values, wide tables, chart/subreport, styles, all four usable exports, assistive technology focus, long reports, memory and concurrency |
| Catalog and SSRS execution | Approximately 4,000 real items browse with root/ancestor metadata calls only; sample custom code, datasets, external images, non-SQL sources and subreports execute according to SSRS; aliases retain native defaults; removed reports fail at SSRS or after metadata refresh |
| Global blocklist | Same catalog for both tenants; direct report/folder blocks hide descendants/search and deny old IDs/parameter requests/runs; unblocked parents/aliases can use blocked dependencies; aliases blocked separately; hidden roots/ancestors excluded; recycle clears sessions |

Complete the focused [logging/IIS](troubleshooting.md#live-iis-acceptance), [SQL monitor](catalog-monitor.md) and [subscription](subscriptions.md#rollback-and-dev-acceptance) checks too. Known-record date boundaries remain [open](../date-parameter-boundary-acceptance.md).

[SSRS HTML device information](https://learn.microsoft.com/en-us/sql/reporting-services/html-device-information-settings?view=sql-server-ver17) defines HTMLFragment and Section; [protocol fixtures](../../tests/SsrsPortal.Tests/Fixtures/README.md) define wire expectations. Mock tests and nonempty-byte checks do not establish live fidelity or infrastructure acceptance.
