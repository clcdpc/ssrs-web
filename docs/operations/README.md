# Windows Server 2022 / IIS operations

This is the application administration and configuration reference. Use authorized operators for host, directory, Entra and live SSRS actions. [SSRS Web Deployment and Commissioning Guide](../clc-deployment.md) · [Troubleshooting and recovery](troubleshooting.md)

- [Configuration provisioning](#external-configuration-provisioning), [settings](#configuration-reference), [Entra setup](#multi-tenant-entra-onboarding-and-global-catalog-policy) and [persistent keys](#persistent-data-protection-keys).
- [Logging](troubleshooting.md#persistent-file-logging), [optional SQL catalog monitor](catalog-monitor.md) and [subscriptions](subscriptions.md).
- [Release and toolkit reference](#release-and-toolkit-reference) and [live application gates](#live-gate-checklist).

## Host setup

Use Windows Server 2022 x64, IIS and the .NET 10 Hosting Bundle/ANCM 20. Repair the bundle if IIS was installed afterward. [Microsoft's IIS hosting guide](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0) covers component installation. Keep the generated release `web.config`; disable detailed errors, routine stdout logging and request/body tracing.

Use the stopped scaffold created by [Initialize Deployment Host](../clc-deployment.md#5-preview-and-initialize-iis). Retain x64, Integrated pipeline, No Managed Code, one worker, disabled overlapping recycling and profile loading. Configure the dedicated HTTPS binding with the approved hostname and certificate. In IIS Manager, use the server-level **Configuration Editor** for `system.applicationHost/applicationPools` and edit the dedicated pool's `environmentVariables` collection. Set `ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT` to `Production` on both targets. Resolve conflicting runtime overrides before validation. Leave both startup flags disabled until deployment verifies readiness.

Use the approved AD application account outside local Administrators, including indirect membership. Infrastructure configures its credentials and logon rights. Grant [runtime directory access](#post-initialization-ntfs-permissions) after initialization. The distinct runner service account requires an enabled elevated local Administrators token for protected IIS and file deployment. Keep administrative ownership of protected paths and grant management access through Administrators and SYSTEM.

Enable IIS Anonymous authentication so ASP.NET Core handles Entra OIDC. Allow outbound HTTPS to SSRS and Entra discovery/token endpoints. SOAP uses process credentials (`UseDefaultCredentials`), without redirects or TLS bypass. Grant the AD account minimum read-properties/view/execute access to roots, ancestors, reports and required linked targets, data sources and subreports. SSRS controls definitions/credentials; Entra identity is never delegated to SSRS. SQL connectivity is conditional on enabled monitoring/subscriptions; delivery also needs Postmark access.

Use the approved portal hostname/certificate and `AllowedHosts`. Production enables HSTS/HTTPS redirection. In-process IIS handles trusted server forwarding; added forwarding trusts framework loopback allowlists for one hop. Review explicit trust for any additional proxy without clearing allowlists. Test final-topology redirects/callbacks. Omit IIS `cs-uri-query` and protect proxy queries too; logs/traces must not capture callbacks, bodies or report links.

Infrastructure must set a site-specific parent policy in protected `applicationHost.config`, outside replaceable content. Substitute the **actual selected site**, retaining the exact value and attribute lock:

```xml
<location path="REPLACE_WITH_ACTUAL_SITE" overrideMode="Allow">
  <system.webServer>
    <aspNetCore shutdownTimeLimit="60" lockAttributes="shutdownTimeLimit" />
  </system.webServer>
</location>
```

The tagged publish removes the child `shutdownTimeLimit` so the application inherits the locked site policy. Confirm this policy before validation and preserve any prior site separately when planning a cutover. Shared-configuration redirection requires an absent or exactly false `enabled` value (case-insensitive). `configSource` and unsupported layouts are refused. Enabled subscriptions additionally require Application Initialization, preload, AlwaysRunning, idle timeout zero and at least 90 seconds of pool shutdown grace. The budgets remain ANCM60/Host45/Hangfire30. [Deployed-host subscription provisioning](subscriptions.md#deployed-host-subscription-provisioning)

Tune portal/SSRS deadlines and capacity only after synthetic load and approved live performance tests. Portal cancellation stops local requests/state, not remote SSRS server jobs. Pool recycling clears report executions. Local readiness does not establish remote service or live application acceptance.

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

Use `clc-ssrs-web\Config\application.json` under each server's native Windows ProgramData known folder. The initializer creates `Config`, `Keys`, `Logs`, `Application`, `Inbox` and `Deployment` as separate siblings. The compiled tag binds the application to the target. Both deployable targets and local Synthetic development use the same application directory name. Beta/RC artifacts have no runnable target.

`Clc.Configuration` discovers the location and loads the startup snapshot. Hosting flags such as environment selection and `--urls` remain available. Operational settings come from the external file. The temporary `SsrsPortal:TestConfigurationFile` override is for isolated Synthetic Development fixtures.

After initialization and ACL review, provision the file before the first deployment. Use the reviewed [application.Example.json](../../src/SsrsPortal/application.Example.json) from the selected source revision. Resolve the native ProgramData path on the host and copy the example into the existing `Config` directory. The create-new copy fails if configuration already exists.

```powershell
# Set $reviewedExample to the absolute path of application.Example.json.
$appDataDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'clc-ssrs-web'
$configFile = Join-Path $appDataDirectory 'Config/application.json'
[IO.File]::Copy($reviewedExample, $configFile, $false)
```

A trusted deployable binary can also print the exact data directory with `dotnet SsrsPortal.dll --clc-appdata-directory` without creating directories or starting the host. In local Synthetic development, normal startup can create a missing starter file and then stop with setup instructions.

Replace every literal `REPLACE_ME` with the approved portal hostname, HTTPS SSRS `/ReportServer` endpoint, roots/exclusions, Entra ID/secret and approved tenants, revision and key path. Restrict the file's NTFS ACL before storing credentials and verify runtime read access after saving. Keep existing configuration and review it in place. Startup requires `Portal`, `Entra` and `AllowedHosts` and rejects remaining placeholders. Deployment validation and `--clc-appdata-directory` are read-only. The optional SQL monitor has its own [prerequisites and live checks](catalog-monitor.md).

### Post-initialization NTFS permissions

In elevated Windows administration, inspect Security > Advanced on the existing native ProgramData `clc-ssrs-web` root and each initialized directory. Confirm administrative ownership, protected parents and expected inheritance. Review the ACL of any existing child files before making grants. Apply only the required inheritable ACE to the actual IIS worker account on each named directory. Preserve reviewed Administrators/SYSTEM entries.

| Directory | Runtime access |
| --- | --- |
| Application | Read and Execute on this folder, subfolders and files |
| Config | Read and Execute on this folder, subfolders and files |
| Keys | Modify as required for key creation and rotation |
| Logs | Modify as required for file creation, rollover and retention |
| Inbox | No runtime write access |
| Deployment | No runtime write access |

Use Effective Access to check the account and its groups, including parent traversal and any child ACL exceptions. Protect the root and all ancestors from runtime replacement, write, ownership and DACL changes. The runtime account stays outside local Administrators. Stop and investigate unexpected ownership, broad write access, existing configuration or reparse points before changing permissions. Use host **Validate** to confirm the resulting layout and runtime access.

### Persistent Data Protection keys

Infrastructure provisions the target's canonical Keys directory outside Application. Set `DataProtection.KeyRingPath` to that absolute path and `KeyStoreCompatibilityId=ssrs-web-dpapi-user-v1`. The application name remains **clc-ssrs-web** across targets/upgrades. Only infrastructure and the actual AD worker identity may access keys as required; runtime needs read/write for generation/rotation, without owner/DACL control or replacement rights on protected ancestors. Use [post-initialization permissions](#post-initialization-ntfs-permissions) and inspect effective access as the worker.

Enable IIS `LoadUserProfile=true` and `setProfileEnvironment=true`; retain the actual worker's Windows profile. The required `ssrs-user-dpapi` envelope uses **Windows user-scope DPAPI** and refuses plaintext, machine-scope and unmarked legacy rings. Do not overwrite an existing ring or copy it between identities/targets. Separately approve and qualify any legacy migration or identity/profile change. See [Microsoft's IIS profile guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0#data-protection).

Before readiness, actual worker startup must protect/unprotect a local smoke value using the persistent ring and decrypt every retained nonrevoked key. The deployer's read-only validator cannot prove user-profile decryption. Recycle and verify persistence under the actual identity. Retained ring/discriminator preserves cookies across upgrades; recycling still clears report executions. Infrastructure separately backs up and recovers keys and compatible DPAPI profile/account material; application-file backups do not restore them. Never commit key material.

### Configuration reference

Required values have no operational default. Sizes below are bytes, with MiB equivalents. See [report link hosting](#report-link-hosting) for fixed request/link limits, [persistent keys](#persistent-data-protection-keys) for `DataProtection`, and [subscriptions](subscriptions.md#configuration-bounds-and-scheduling) for delivery settings.

| JSON setting | Required value, default and supported bounds |
| --- | --- |
| Hosting environment (`ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT`) | `Production` for deployment; hosting control, not an `application.json` value |
| `Deployment:ConfigurationRevision` | Required nonsecret safe code, maximum 64 characters; change on every edit/rotation; unchanged-revision byte drift is refused |
| `DataProtection:KeyRingPath`, `DataProtection:KeyStoreCompatibilityId` | Required canonical target Keys directory and `ssrs-web-dpapi-user-v1`; see [persistent keys](#persistent-data-protection-keys) |
| `AllowedHosts` | Required approved portal hostname(s); replace the example `REPLACE_ME` |
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
| `Logging:*` | Use the single [logging configuration reference](troubleshooting.md#persistent-file-logging), including levels, rotation, retention, bounds and removal of legacy `Rotation` |

Byte budgets cover accounted session content and buffers, not the CLR working set. XML/HTML parsers and framework objects add overhead. Monitor memory and size for concurrency. Active session leases reserve SOAP/output scratch space. Multiple workers or a farm require a reviewed distributed-session design.

### Protecting and changing configuration

- Administrators/SYSTEM control Config provisioning and credential rotation. The actual AD pool identity gets read/traverse only, with no file or directory writes.
- Remove broad inherited Users/Authenticated Users/IIS_IUSRS access as appropriate. Inspect effective permissions and protect administrative backups equally. Check access as the worker, not just the operator. See [post-initialization NTFS permissions](#post-initialization-ntfs-permissions).
- Keep Config outside the web root, published app, release replacement tree and source checkout. Deployments must preserve it without packaging or overwriting credentials. Provisioning may need elevation; runtime must not need administrator membership.
- Never use secret-bearing environment variables or command-line settings as a fallback. Remove obsolete `Portal:ApprovedReports`, `Entra:TenantId` and `Entra:GroupId` from every old provider. Even empty values cause startup errors.

Configuration loads once with `reloadOnChange:false`. Change `Deployment.ConfigurationRevision` on every edit or rotation. After approval, restart the local process or recycle the dedicated pool. Coordinate with staff: recycling discards report sessions.

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

## Release and toolkit reference

Repository tag protections limit who can create release tags and keep tags fixed. Stable releases also require protected `master` ancestry. The tag workflow builds at the exact tagged source on a hosted runner and uploads a qualified package. The pinned shared publisher verifies its producer and package and publishes an immutable GitHub Release. The pinned shared deploy workflow verifies the published digest and routes DEV and stable releases by target label. Beta and RC publish only. DEV uses the Production ASP.NET Core runtime. [Tag workflow](../../.github/workflows/release.yml)

The consumer uses contract 3 and produces contract-3 packages. Shared workflows, initialization and compatibility checks use commit `e306d730d77c1a05868f124ab2719e71ae186499` from Clc.Deployment `v0.1.0-beta.8`. The toolkit uses distribution format 4. The locked `Clc.Deployment.AspNetCore` `0.1.0-beta.8` companion uses validation and readiness protocol 1. [Consumer configuration](../../.clc/release.json) · [Immutable toolkit release](https://github.com/clcdpc/clc-deployment/releases/tag/v0.1.0-beta.8)

The downloadable toolkit is stamped with the release version. SSRS package manifests record the builder's exact source commit and the module version committed at that revision.

SSRS Web qualifies the application with its ordinary .NET and npm locks and publishes it with a separate Windows Release lock. The prequalified builder records a null dependency lock hash because it looks for `packages.lock.json` at the source root when the consumer configuration supplies no project. All locks are available at the exact source commit recorded in the manifest.

The deployment engine obtains an exclusive lock, validates the application and exact package identity, records intent, and verifies a backup before replacing managed Application files. On success it verifies readiness and the final inventory before recording current state. A lower tag or different bytes under a recorded tag are rejected. An exact package retry can run the full lifecycle if no attempt is pending. Recovery retains the release ordering high-water mark.

Keep the safe Actions summary and the corresponding local attempt evidence. The native ProgramData `clc-ssrs-web\Deployment` directory holds local host, state and attempt records. A pending attempt requires investigation and may require [explicit recovery](troubleshooting.md#deployment-failure-and-explicit-recovery). Protect Config, Keys, Logs and any SQL state with their normal backup procedures.

See the [upstream six-command contract](https://github.com/clcdpc/clc-deployment/blob/e306d730d77c1a05868f124ab2719e71ae186499/docs/contracts.md) and [repository verification](../../README.md#verification). Record live results using the [commissioning acceptance checks](../clc-deployment.md#9-verify-live-application-acceptance).

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
| AD service account + SSRS | Actual nonadministrator worker identity accesses SSRS. User!UserID observes the shared identity. Prompted/broken reports are visible and fail gracefully |
| Page 2 | Approved multi-page report, same SSRS execution, previous/next/last and unknown/known count behavior |
| RenderStream | Actual chart/image/CSS and subreport streams; browser network contains only portal-origin resource routes; unknown renderer URL variants fail closed |
| Interactions | Verify recognized single-level/nested expand/collapse with the actual DEV renderer, owner isolation, stale-action rejection, synchronization without replay and private failure diagnostics; sort/drillthrough/maps stay inert |
| Isolation/cancellation/expiry | Two real users sharing one AD service account, each other's tokens denied for every route; cancelled/aborted export, idle expiry and server-side timeout behavior |
| Fidelity/accessibility/load | Staff checks values, wide tables, chart/subreport, styles, all four usable exports, assistive technology focus, long reports, memory and concurrency |
| Catalog and SSRS execution | Approximately 4,000 real items browse with root/ancestor metadata calls only; sample custom code, datasets, external images, non-SQL sources and subreports execute according to SSRS; aliases retain native defaults; removed reports fail at SSRS or after metadata refresh |
| Global blocklist | Same catalog for both tenants; direct report/folder blocks hide descendants/search and deny old IDs/parameter requests/runs; unblocked parents/aliases can use blocked dependencies; aliases blocked separately; hidden roots/ancestors excluded; recycle clears sessions |

Complete the focused [logging and IIS checks](troubleshooting.md#live-iis-acceptance). For enabled optional services, verify worker-account SQL access and TLS, then complete the [SQL monitor](catalog-monitor.md) and [subscription](subscriptions.md#rollback-and-dev-acceptance) checks as applicable. Known-record date boundaries remain [open](../date-parameter-boundary-acceptance.md).

[SSRS HTML device information](https://learn.microsoft.com/en-us/sql/reporting-services/html-device-information-settings?view=sql-server-ver17) defines HTMLFragment and Section; [protocol fixtures](../../tests/SsrsPortal.Tests/Fixtures/README.md) define wire expectations. Mock tests and nonempty-byte checks do not establish live fidelity or infrastructure acceptance.
