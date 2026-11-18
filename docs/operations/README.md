# Windows Server 2022 / IIS operations

This is the application administration and configuration reference. Commission DEV using [the six-phase devweb procedure](../clc-deployment.md); use [troubleshooting and recovery](troubleshooting.md) for failures. Only authorized operators may perform host, directory, Entra and live SSRS actions.

- [Configuration provisioning](#external-configuration-provisioning), [settings](#configuration-reference), [Entra setup](#multi-tenant-entra-onboarding-and-global-catalog-policy) and [persistent keys](#persistent-data-protection-keys).
- [Logging](troubleshooting.md#persistent-file-logging), [optional SQL catalog monitor](catalog-monitor.md) and [subscriptions](subscriptions.md).
- [Enrollment inputs](#enrollment-approval-inputs), [baseline inputs](#baseline-approval-inputs), [standing authorization inputs](#standing-authorization-inputs), [deployment contracts](#deployment-contracts-and-verification) and [live application gates](#live-gate-checklist).

## Host setup

Use Windows Server 2022 x64, IIS and the .NET 10 Hosting Bundle/ANCM 20. Repair the bundle if IIS was installed afterward. [Microsoft's IIS hosting guide](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-10.0) covers component installation. Keep the generated release `web.config`; disable detailed errors, routine stdout logging and request/body tracing.

The dedicated pool requires x64, Integrated pipeline, No Managed Code, one worker, `recycling.disallowOverlappingRotation=true`, AutoStart=true and profile loading. The dedicated site must be Started with ServerAutoStart=true, one root application/virtual directory at the canonical Application root, and the approved HTTPS hostname/binding/certificate. Inspect existing values in IIS Manager before deciding whether reuse is compatible. Neither another pool's physical-root overlap nor a shared pool is supported.

Use a conventional AD application account outside local Administrators, including indirect membership. Infrastructure configures its password/logon rights; runtime needs application read/execute and [external directory access](#initial-ntfs-provisioning). The separate deployment account needs its admitted SID and an enabled elevated local Administrators token for content/pool control. Never store passwords in source, commands or SQL connection strings. Protected infrastructure uses SYSTEM/Administrators ownership/writers, without direct protected-resource write ACEs for individual deployment SIDs; elevated access is inherited through Administrators. Independent approval remains procedural against that privileged account.

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

A child `shutdownTimeLimit` is invalid even if its value matches. Before binding a legacy cutover inventory, stop/fence the pool and independently remove the old child attribute while retaining the original separately. The workflow does not edit it. Shared-configuration redirection is refused unless enabled is absent or exactly false (case-insensitive); `configSource` and unsupported layouts are refused. Confirm effective inheritance before deployment. Enabled [subscriptions](subscriptions.md#production-provisioning-and-deployment) additionally require Application Initialization, preload, AlwaysRunning, idle timeout zero and at least 90 seconds of pool shutdown grace. The budgets remain ANCM60/Host45/Hangfire30.

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

The configuration file is `clc-ssrs-web\Config\application.json` under each server's native Windows ProgramData known folder. For the conventional `C:\ProgramData` location, the full path is `C:\ProgramData\clc-ssrs-web\Config\application.json`. Windows startup checks that the package's CommonApplicationData path agrees with the native ProgramData and system-drive checks. Compiled tag metadata independently binds a release to `ssrs-web-dev` or `ssrs-web-production`; `--environment` cannot change either identity. Beta/RC artifacts have no runnable namespace. Local Synthetic development also uses `clc-ssrs-web`.

Keep `Config`, `Keys`, `Logs`, `Application`, `Inbox` and protected `Deployment` as physically disjoint siblings. Infrastructure owns the protected deployment namespace and approvals. The application owns its operational configuration requirements. If a former target-derived directory exists, independently inspect its configuration, key ring, logs, enrollment and recovery evidence and arrange a separately approved cutover. Preserve the existing directories and evidence pending that approval.

`Clc.Configuration` owns path discovery, the shared startup snapshot and first-run example copy. There is no ConfigFile/bootstrap variable or normal operational path argument. Appsettings, user secrets and ordinary environment/command-line settings do not supply operational values. Hosting controls such as environment selection and `--urls` remain supported.

A published build supports hostless path discovery with `dotnet <published SsrsPortal.dll> --clc-appdata-directory` before configuration is provisioned. It prints only the absolute application data root. It does not create directories, load `application.json`, initialize logging or start the host. It does not establish file access, IIS enrollment or deployment readiness; continue through the separate configuration, ACL, enrollment and readiness checks below.

`SsrsPortal:TestConfigurationFile` exists only for isolated Synthetic Development fixtures. Released DLLs reject Development before configuration, logging or key setup. Production and Staging reject it before reading a file. Never configure it in IIS.

The setup administrator may copy the reviewed `application.Example.json` to the provisioned `Config\application.json` before validation. Preserve an existing file; use create-new semantics:

```powershell
# $publishedDirectory is the independently trusted published application directory.
$appDataDirectory = & dotnet (Join-Path $publishedDirectory 'SsrsPortal.dll') --clc-appdata-directory
if ($LASTEXITCODE -ne 0) { throw 'Compiled target discovery failed.' }
$configFile = Join-Path $appDataDirectory 'Config\application.json'
[IO.File]::Copy((Join-Path $publishedDirectory 'application.Example.json'), $configFile, $false)
```

The sanitized example is included in the published application. On a missing file, one normal startup under an appropriately permitted setup identity creates the application data and `Config` directories, copies the example byte-for-byte, then stops with an instruction to edit and restart. Run this on the target host from a trusted published build:

```powershell
$applicationDll = Read-Host 'Absolute path to the trusted published SsrsPortal.dll'
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

### Initial NTFS provisioning

Run this reviewed initial setup example in elevated PowerShell on Windows Server 2022. Supply the actual dedicated regular AD service account; runtime needs no local administrator membership. For existing directories, inspect effective permissions and preserve approved administrative entries instead of blindly replacing ACLs. Provision the infrastructure-owned target parent with safe traversal/replacement protection first. Run before copying configuration/bootstrap startup and fence other writers. The example refuses existing Config/Keys/Logs directories before changes; it does not authorize repair of enrolled resources.

```powershell
$ErrorActionPreference = 'Stop'
# $applicationDll is the absolute trusted published DLL path from provisioning above.
$applicationRoot = (& dotnet $applicationDll --clc-appdata-directory).Trim()
if ($LASTEXITCODE -ne 0 -or -not [IO.Path]::IsPathFullyQualified($applicationRoot)) { throw 'Compiled target discovery failed.' }
$serviceAccount = Read-Host 'Dedicated IIS application pool identity (DOMAIN\account)'
$serviceSid = ([Security.Principal.NTAccount]::new($serviceAccount)).Translate([Security.Principal.SecurityIdentifier])
$administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$entries = @(@{ Name = 'Config'; Rights = 'ReadAndExecute' }, @{ Name = 'Logs'; Rights = 'Modify' }, @{ Name = 'Keys'; Rights = 'Modify' })
foreach ($entry in $entries) {
    if (Test-Path -LiteralPath (Join-Path $applicationRoot $entry.Name)) {
        throw 'Existing external directories need independent ACL inspection; no changes made.'
    }
}
foreach ($entry in $entries) {
    $directory = Join-Path $applicationRoot $entry.Name
    New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($administrators)
    foreach ($administrator in @($administrators, $system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $administrator, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $serviceSid, $entry.Rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $directory -AclObject $acl
    Get-Acl -LiteralPath $directory | Format-List
}
```

The service gets Config read/traverse and Keys/Logs Modify, including create/write/append and deletion for rollover/expired-file cleanup. Administrators/SYSTEM retain management access. Check parent traversal and existing child-file inheritance under the actual service identity. Do not grant broad Users/Everyone access or service write access across the application root. Administrators apply these grants; application startup never sets ACLs.

### Persistent Data Protection keys

Infrastructure provisions the target's canonical Keys directory outside Application. Set `DataProtection.KeyRingPath` to that absolute path and `KeyStoreCompatibilityId=ssrs-web-dpapi-user-v1`. The application name remains **clc-ssrs-web** across targets/upgrades. Only infrastructure and the actual AD worker identity may access keys as required; runtime needs read/write for generation/rotation, without owner/DACL control or replacement rights on protected ancestors. Use [initial NTFS provisioning](#initial-ntfs-provisioning) for fresh directories and inspect effective access as the worker.

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
- A separately approved virtual-pool setup must grant its `IIS AppPool\<pool>` identity, not an interactive account.
- Remove broad inherited Users/Authenticated Users/IIS_IUSRS access as appropriate. Inspect effective permissions and protect administrative backups equally. Check access as the worker, not just the operator. See [initial NTFS provisioning](#initial-ntfs-provisioning).
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

## Enrollment approval inputs

Use upstream [deployment configuration](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#2-prepare-configdeploymentjson), [observation and candidate preparation](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#3-collect-observations-and-prepare-the-candidate) and [independent approval](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#4-independently-approve-and-install-the-handoff). The observation block runs with the authenticated bootstrap as the selected deployment account. It creates the prospective policy and disabled candidate from held, read-only observations and disposes the handles before the approver reviews it. Inspect and Plan require the completed protected handoff.

| Source | SSRS-specific values / how to obtain them |
| --- | --- |
| Application constants | In `deployment.example.json`, retain contract family/version/kind, `applicationId=ssrs-web`, `targetId=ssrs-web-dev`, `environmentName=DEV`, `runtimeEnvironment=Production`. Replace the example's five absolute paths with paths to `Application`, `Config\application.json`, `Keys`, `Inbox` and `Deployment` under the server's native ProgramData `clc-ssrs-web` directory. Write `Config\deployment.json` as protected UTF-8 JSON. [Deployment configuration example](../../deployment/deployment.example.json) |
| devweb observations | Actual dedicated `siteName`, `applicationPoolName`, approved `runtimeAccountName`, resolved `runtimeIdentitySid`, final HTTPS DNS hostname/port for `readinessUri` ending `/health/ready`. Inspect IIS Manager's pool Advanced Settings / Identity and site Bindings; do not expose passwords. Resolve the approved account with `([Security.Principal.NTAccount]::new($runtimeAccountName)).Translate([Security.Principal.SecurityIdentifier]).Value` |
| Authenticated toolkit | Independently approved version/source/archive digest and verified module inventory for `activeToolkit`; bootstrap source and executable inventory covering both module and tools for the enrollment record. Follow [initial bootstrap provisioning](https://github.com/clcdpc/clc-deployment/blob/master/docs/installation.md#initial-bootstrap-provisioning) |
| Host-policy observations | devweb SMBIOS UUID (`Get-CimInstance Win32_ComputerSystemProduct` / UUID), actual installed x64 dotnet/appcmd paths, canonical target-local policy/catalog/control/lock/version paths; observe actual infrastructure/deployment SIDs under the approved identities |
| Protected pre-state | Exact `deployment.json` byte SHA-256 after final editing; actual resource identities and security descriptor hashes, toolkit's selected IIS snapshot digest, installed certificate thumbprint and explicit binding IP. Null resource bindings mean genuinely absent paths only |
| Independent infrastructure approval | Use `$authorizationId = 'ssrs-web-dev'`. Bind the actual approving SID distinct from deployment, reference/window, initial policy generation, active pin and allowlists, exact permitted operations and binding to the final config digest. Use `ExistingOnly` for a conventional preconfigured AD pool. Legacy cutover additionally requires approved original inventory, root identity and canonical backup location. Retain the SHA-256 of the final approved authorization bytes with the private change evidence and recheck the installed bytes. The observation hashes and disabled candidate carry no approval |

On a fresh host prepare only the bootstrap and enrollment resources under `Deployment`, as described in [upstream host preparation](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#1-inspect-and-prepare-the-host). Provision `enrollment\authorizations\ssrs-web-dev.json`, `enrollment\enrollment.lock` (initially empty) and `enrollment\evidence`. The authenticated `bootstrap` contains exactly `Clc.Deployment`, `tools`, `source.commit` and `distribution.zip`. Keep the bootstrap, handoff and ancestors protected. Keep raw IIS values, private hashes and candidate files out of transcripts, CI and source control. Store drafts outside `Deployment`.

Authorized Apply creates and activates the initial installation under `Deployment\toolkit\versions\<version>` and establishes protected `toolkit\policy\host-policy.json`, target and approval catalogs, `control` state and `locks`. The local target catalog contains only its enrolled target. Verify installed bytes, exact active pin and Ready enrollment before using the toolkit for the approved application baseline. See [Phase 4](../clc-deployment.md#phase-4--prepare-bootstrap-and-enroll-clcdeployment).

## Baseline approval inputs

Use the installed toolkit's [schema-1 release approval contract](https://github.com/clcdpc/clc-deployment/blob/master/schemas/release-approval.schema.json) and [approval protection and staging requirements](https://github.com/clcdpc/clc-deployment/blob/master/docs/deployment.md#3-independently-approve-and-install). Acquire SSRS artifacts through [Phase 5](../clc-deployment.md#phase-5--establish-the-first-approved-baseline). An independent administrator writes the protected approval; qualification scripts supply test records only.

| Input | Authoritative SSRS evidence |
| --- | --- |
| Application/target/repository | `ssrs-web`, `ssrs-web-dev`, `clcdpc/ssrs-web`; compare package and enrolled target |
| `releaseId`, full `sourceCommit`, ZIP/manifest hashes | Independently authenticated producer's [provenance record](../../scripts/release/Write-ReleaseProvenance.ps1), exact ZIP/manifest and compiled SsrsPortal.dll metadata; inspect with installed `Test-ClcRelease -ArchivePath` and reconcile every identity |
| `provenance` | `workflowPath=.github/workflows/release.yml`, `buildJob=release`, exact `runId`, `runAttempt`, `artifactId` and `transportSha256`. Verify exact producer completion, time window and artifact ownership through authenticated GitHub run-attempt/jobs/artifacts APIs. Hash and retain the original service transport ZIP separately from the inner release ZIP |
| `releaseSequence` | Installed `Test-ClcRelease -ReleaseTag $tag` result; require DEV. Its tag-derived releaseId is a classification ID, **not** the attempt-specific package releaseId |
| `approvalId`, enabled/authorization | Independently chosen safe ID, enable decision, infrastructure authority SID/nonsecret change reference/approved UTC; bind the exact reviewed source/package, never sample GUIDs/hashes |
| `lineageId` | Independently selected DEV lineage for the baseline, retained by subsequent standing policy; do not infer it from a filename or create a new lineage for each release |

Authenticate the tag object and peeled commit, platform workflow revision, successful hosted producer/archive jobs and attempt-specific artifacts before approval. The canonical GitHub Release carries `release.zip`, `release-provenance.json`, `release-metadata.json`, `SHA256SUMS`, `LICENSE` and `THIRD-PARTY-NOTICES.md`. If using the canonical package for the baseline, match its bytes to the original producer's verified inner ZIP. Later same-tag builds preserve those canonical assets and produce distinct Actions packages. Bind all approval evidence to one exact attempt. The independent approval authenticates the expected hashes.

## Standing authorization inputs

After the baseline succeeds, infrastructure prepares a disabled candidate using the [standing authorization schema](https://github.com/clcdpc/clc-deployment/blob/master/schemas/standing-authorization.schema.json) and the actual enrolled target and baseline approval. Use the following values for DEV:

| Field | Value |
| --- | --- |
| `contractFamily`, `schemaVersion`, `recordKind` | `clc-file-iis-v1`, `1`, `standing-authorization` |
| `standingAuthorizationId`, `targetId`, `applicationId` | `ssrs-web-dev`, `ssrs-web-dev`, `ssrs-web` |
| `environmentName`, `channel`, `sourceRepository` | `DEV`, `DEV`, `clcdpc/ssrs-web` |
| `buildWorkflowPath`, `buildJob` | `.github/workflows/release.yml`, `release` |
| `lineageId` | The successful baseline's exact lineage |
| `authorization` | Actual admitted `authoritySid` distinct from deployment, nonsecret `reference`, `approvedUtc` |
| `enabled` | `false` during review, `true` only in the independently approved protected record |

Schema 1 omits `sourcePolicy`. Validate final bytes against the schema and use the upstream protection and read-back steps in [Configure standing authorization](https://github.com/clcdpc/clc-deployment/blob/master/docs/automated-deployments.md#configure-standing-authorization). The installed record goes in `Deployment\toolkit\policy\approvals\standing\ssrs-web-dev.json`. Production requires its own target/standing ID, `Production` environment/channel and approved baseline lineage.

## Release and toolkit reference

[release.yml](../../.github/workflows/release.yml) owns one tag-push chain `release → archive → deploy`. DEV allows unmerged tagged code/workflow/scripts/application configuration without PR admission; stable source must be contained in protected master history, including older ancestors. Beta/RC publish artifacts without host admission. Major/minor/patch are 0–9999, DEV/beta/RC N is 1–9998, without leading zeroes; other suffixes/case/build metadata are rejected. Compiled Release identity binds exact tag/target/source/attempt-specific releaseId. Local Synthetic builds cannot deploy.

The dedicated deployment job authenticates the exact scripts from the tagged commit and rechecks source, producer and artifact after queueing. It verifies Ready enrollment, the protected target and independently installed runtime before deployment. Protected host configuration and authorization stay under infrastructure control. Application packages contain neither credentials nor host setup tooling and carry no deployment approval. SQL and administrative setup ship separately for [subscription provisioning](subscriptions.md#production-provisioning-and-deployment).

DEV and Production runner services must resolve the approved, globally installed `pwsh` through their effective PATH. Use stable PowerShell 7.6.6 or newer within 7.x. Administrators can apply supported 7.x updates without changing SSRS Web. The deployment entry point rejects prereleases and PowerShell 8.x or later. GitHub-hosted release packaging uses an exact PowerShell pin for reproducibility. Follow the executable and PATH protection and read-only verification steps in [host preparation](../clc-deployment.md#phase-2--prepare-devweb).

Only hosted archive has contents-write permission. Publication verifies exact source, producer, compiled, package and builder identities, creates or verifies a draft, publishes and requires Boolean `immutable=true` on the final canonical application Release. Publication refuses conflicting or mutable published assets. The [toolkit lock](../../deployment/toolkit.lock.json) pins published Clc.Deployment PowerShell `0.1.0-beta.5` format 3. Its upstream Release reports `immutable=false`; independently authenticate the exact archive digest. The ASP.NET Core companion stays pinned to beta.3. Infrastructure separately approves the host's active pin, which may differ if it supports the application-based layout and compatible schema-1 APIs. See [licensing and binary obligations](../licensing-and-distribution.md).

For a fresh same-tag build, rerun **all jobs of the original push run** with `gh run rerun <run-id> --repo clcdpc/ssrs-web`. The attempt/package identity changes; canonical assets remain unchanged. Rerunning only archive/deploy cannot qualify another attempt's producer. Actions retention is 30 days; when unavailable, select a new deliberate unused version tag. GitHub is not FIFO: newer eligible tags supersede older ones even if their builds failed/pended. Current exact identity still needs fresh readiness for NoOp. Target concurrency groups do not cancel in-progress work.

The SSRS [adapter](../../scripts/release/AutomaticDeployment.Adapter.ps1) emits verified-release schema 1 and requires matching standing-authorization schema 1. Source eligibility comes from the authenticated [SSRS tag verifier](../../scripts/release/TagRelease.Common.ps1). Automatic deployments use tag evidence without snapshots. See the upstream [host API and version distinctions](https://github.com/clcdpc/clc-deployment/blob/master/docs/verified-workflow-integration.md#host-api-and-versions) and [SSRS standing authorization inputs](#standing-authorization-inputs).

## Deployment contracts and verification

[Application validation](../../src/SsrsPortal/Configuration/DeploymentContract.cs) uses the normal bounded configuration reader without starting logging, host/listeners, remote clients, Hangfire or key generation. Resolve the canonical application data directory from the release before invoking the validator.

```powershell
$appDataDirectory = & dotnet SsrsPortal.dll --clc-appdata-directory
if ($LASTEXITCODE -ne 0) { throw 'Compiled target discovery failed.' }
dotnet SsrsPortal.dll --validate-deployment `
    --configuration-path (Join-Path $appDataDirectory 'Config\application.json') `
    --data-protection-root (Join-Path $appDataDirectory 'Keys') --environment Production
```

The validator runs as the deployer in Production or Staging. Success returns exit 0 and one version-1 JSON object under 8 KiB. Worker startup must separately prove user-DPAPI/profile access. Configuration revision changes require a restart even with identical release bytes. Unchanged-revision drift is refused.

Anonymous HTTPS GET `/health/ready` returns no-store JSON with exactly readinessContractVersion=1, status=ready, applicationId, releaseId, sourceCommit, runtimeEnvironment and configurationRevision. Match every field to the approved release/configuration; generic 200, redirects/authentication, wrong/stale identity or TLS bypass do not qualify. Unavailable/stopping/synthetic Development returns 503. Readiness proves local startup, persistent-key smoke and ApplicationStarted, not remote Entra/SSRS/SQL behavior.

Verify installed Application bytes against the exact retained manifest inventory. Protected state is `Deployment/control/<targetKey>/current.json`, `journal.json`, `attempts/<attemptId>/deployment.json`, `releases/<approvalId>.json`, `backups/<attemptId>/index.json` plus application bytes, and staging. Obtain targetKey from the actual machine UUID/pool using the upstream [canonical state contract](https://github.com/clcdpc/clc-deployment/blob/master/docs/contracts.md#canonical-lock-and-phase-state); never guess it. Successful readiness/final inventory precedes current generation and matching Completed journal/terminal attempt. A fresh target's baseline has generation 1 and a verified empty backup. NoOp preserves current/application bytes/generation but records a new Completed attempt. Check pool Started/exactly one worker in actual IIS, not just historical state. Retain evidence independently of temporary Actions storage.

### Repeatable local checks

Use [README verification](../../README.md#verification), particularly `Test-DevDeployment.ps1`, `Test-TagDeployment.ps1`, `Test-ReleaseArchive.ps1`, `Test-ReleasePackaging.ps1`, and `Test-DeploymentReadiness.ps1 -ActionlintPath <approved-actionlint-1.7.12-executable>`. [Windows CI](../../.github/workflows/verify.yml) provides the Release and ordinary publish arguments. Packaging can additionally use `-PublishDirectory <actual-CLC-publish> -OrdinaryPublishDirectory <ordinary-publish> -PublishedToolkit`. It authenticates the pinned beta.5 archive before evaluating its verifier, then checks format-3/inventory/import/compiled/producer bindings. Historical beta.3 archive rejection uses its historical pin. Ordinary tests stay offline without `-PublishedToolkit`.

The independently pinned integration fixture uses a disposable GitHub-hosted Windows runner and CI's authenticated PowerShell 7.6.6 executable. Set `$cleanExactToolkitCheckout` to a clean checkout of the indicated raw-byte toolkit source.

```powershell
pwsh -NoProfile -File ./scripts/release/Test-DeploymentIntegration.ps1 `
    -ToolkitSourceRoot $cleanExactToolkitCheckout -ToolkitSourceCommit 785511c6ba8b60afddf0722298f96f02ba163a01 -BuildInputs
```

It builds unpublished format-3 builder dev.2/runtime dev.1 candidates, compiles distinct DEV/Production identities, and exercises the real engine/application with synthetic IIS/platform/access/runtime-identity observations. Its create-new marker-owned ProgramData fixture refuses an existing namespace. NTFS/state/backup effects, helpers, DPAPI and loopback readiness are real; IIS/TLS/identity/remote-service acceptance is not. It covers upgrade/NoOp/revision restart/readiness failure/retry refusal/AcceptCandidate, config/key/backup preservation and standing/rebuild/revocation/order checks. Without BuildInputs, supply ReleaseArchivePath/UpgradeReleaseArchivePath and optionally StandingReleaseArchivePath/RebuildReleaseArchivePath from distinct compiled synthetic packages. The optional pair shares source and has distinct run/release identities. Never copy synthetic pins/approvals into operational configuration. Missing Windows/local-SQL prerequisites are skipped or inconclusive, not passes. Generated evidence stays ignored under artifacts/.

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
