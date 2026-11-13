# CLC deployment integration

Use this runbook with the exact toolkit pinned in [deployment/toolkit.lock.json](../deployment/toolkit.lock.json). Source integrity, host enrollment and application release approval are separate checks; none substitutes for the others.

## Qualification status

The beta.3 companion passed normal and Release locked restores and Windows consumer checks. The published toolkit archive and inventory match the lock. **Beta.3 native-toolkit packaging interoperability and live DEV/IIS qualification remain outstanding.** Synthetic tests do not qualify IIS, TLS, runtime identities, persistent keys, Entra, SSRS, SQL or subscription delivery. Keep operational deployment disabled until the required infrastructure approvals and host checks are complete.

SSRS uses the MIT-licensed `Clc.Deployment.AspNetCore` companion; the portal remains AGPL-3.0-only. Package locks and the toolkit lock are authoritative for versions and hashes. Toolkit installation or upgrades require separate infrastructure approval.

## Application compatibility

- Operational configuration: `%ProgramData%\clc-ssrs-web\Config\application.json`. Startup and validation use the same bounded reader and application rules; Production/Staging reject temporary-file overrides.
- Change the nonsecret `Deployment.ConfigurationRevision` on **every** configuration edit or secret rotation. Unchanged-revision byte drift is refused.
- Keep `DataProtection.KeyRingPath` outside the application root. Use `KeyStoreCompatibilityId=ssrs-web-dpapi-user-v1`, application name `clc-ssrs-web`, Windows user-scope DPAPI and IIS `LoadUserProfile=true`.
- Infrastructure provisions the key directory; only infrastructure and the runtime account may access it as required. Runtime needs key read/write. Startup must decrypt every retained nonrevoked key before becoming ready.
- The required `ssrs-user-dpapi` envelope rejects unmarked legacy, machine-scope and plaintext rings. Qualify any migration separately. Application backups do not recover DPAPI profiles, keys or cookies.

The toolkit's validation command is:

```powershell
dotnet SsrsPortal.dll --validate-deployment `
    --configuration-path 'C:\ProgramData\clc-ssrs-web\Config\application.json' `
    --data-protection-root 'C:\ProgramData\clc-ssrs-web\Keys' --environment Production
```

Only Production and Staging are supported. Validation emits one version-1 JSON object under 8 KiB and exits 0 only on success. It reads configuration/key metadata without starting the host, logging, listeners, Hangfire, remote clients or key generation. It runs as the deployer, so worker-side startup must still prove DPAPI/profile access.

`GET /health/ready` bypasses authentication/cookie middleware and returns no-store JSON with seven contract fields. It passes only after local startup, persistent-key smoke and ApplicationStarted; unavailable startup, synthetic Development and stopping return 503. It does not test remote services. Other routes retain their normal authorization, and deployment never migrates SQL.

Deployed builds require compiled `ClcIdentityKind=Release`, unique `ClcReleaseId` and full `ClcSourceCommit`. Local Synthetic builds cannot pass deployed validation/startup. Identity describes the bytes; protected release approval authorizes their use.

## Immutable release production

1. Dispatch [release.yml](../.github/workflows/release.yml) on protected `master`, with `source_commit` set to the exact reviewed full SHA. Workflow and checkout must resolve to it; tag/floating dispatches are rejected. Protect workflow changes, require reviews and restrict bypass.
2. The isolated GitHub-hosted Windows producer verifies its pinned PowerShell/SDK, restores dependency locks, tests and publishes framework-dependent `net10.0/win-x64`, x64, in-process, one worker, `SsrsPortal.dll`, without apphost/debug symbols. Release builds exclude the Mock project and use `packages.release.lock.json`.
3. [Get-ReleaseToolkit.ps1](../scripts/release/Get-ReleaseToolkit.ps1) verifies the whole archive before extraction or verifier execution, then checks source/version/inventory. Optional `CLC_TOOLKIT_READ_TOKEN` is read-only and sent only to `api.github.com`, never the CDN.
4. `New-PortalRelease.ps1` uses the authenticated toolkit's `New-ClcRelease` and `Test-ClcRelease`, checks publish policy and reads compiled identity without executing the DLL.

The producer has no DEV/IIS deployment authority. Ordinary CI stays on `clc-linux` for Linux/browser checks while private, with trusted-contributor admission and runner-group restrictions; Windows compatibility stays GitHub-hosted. Switch Linux to hosted only at public cutover.

| Artifact | Contents |
| --- | --- |
| `ssrs-web-release-<run>-<attempt>` | One immutable `release.zip` |
| `ssrs-web-provenance-<run>-<attempt>` | Source/workflow/run identity, artifact ID, transport and inner ZIP hashes, manifest/compiled identity, SDK/dependency and toolkit pins |
| `ssrs-web-administrative-setup-<run>-<attempt>` | Separate SQL/PowerShell setup and licenses with exact inventory/source |

The transport ZIP and inner release ZIP have different hashes. Provenance is not approval. Retain approved bytes and records outside GitHub before the 30-day artifact retention expires.

Every publish includes project and dependency notices; packaging validates them. The runtime **Source (AGPL)** link identifies the compiled commit. Before network use, make that exact source/license accessible or supply an equivalent source copy. See [licensing and distribution](licensing-and-distribution.md). Administrative setup remains separately authorized and never runs during file deployment.

Release packaging omits ANCM `shutdownTimeLimit`; ordinary publishes retain 60 seconds. The host must supply the protected inherited setting described [below](#preserving-shutdown-and-subscription-hosting).

## Trusted DEV workflow

[deploy-dev.yml](../.github/workflows/deploy-dev.yml) is manual-only, restricted to the exact protected `master` workflow/source in `clcdpc/ssrs-web`. PRs cannot run it. It queues DEV runs without cancelling an active deployment and performs no checkout, build, SQL setup, approval creation or automatic recovery on the elevated host.

Before registering/routing its privileged runner:

- Restrict the runner group to **Selected workflows**: `clcdpc/ssrs-web/.github/workflows/deploy-dev.yml@refs/heads/master`, denying other workflows/repositories. If the plan lacks this control, keep deployment disabled until an equivalent external admission control is reviewed. Labels and YAML guards are not admission controls.
- Protect `master` and configure environment `ssrs-web-dev` with independent reviewers, protected-branch rules and no self-approval.
- Authenticate the official PowerShell 7.6.6 archive and complete extracted inventory independently. Install at `C:\CLC\Tools\PowerShell\7.6.6\pwsh.exe`; protect ancestors, all existing runtime files and module search paths from deployment/runtime/untrusted writes. A version check after launch cannot establish this trust. Protect the runner's temporary scripts so only its service can write them. Administrators remain a procedural trust boundary.
- Provision protected .NET 10/ANCM 20/IIS helpers, an elevated deployment identity, a distinct nonadministrator runtime AD account/profile, HTTPS and safe NTFS paths. Account/password, certificates, Entra/SSRS/Postmark and SQL provisioning are separate administration.

| Repository variable | Set only after |
| --- | --- |
| `SSRS_DEV_DEPLOYMENT_ENABLED=true` | Infrastructure approves activation |
| `SSRS_DEV_RUNNER_ADMISSION_READY=true` | External workflow admission is enforced |
| `SSRS_DEV_RUNNER_GROUP` | A dedicated restricted Windows IIS runner group exists |
| `SSRS_DEV_RUNNER_LABELS` | Its routing labels are known, e.g. `["self-hosted","Windows","X64","ssrs-web-dev"]` |

Infrastructure supplies:

- Reviewed nonsecret `%ProgramData%\clc-ssrs-web\Config\deployment.json`, following [deployment.example.json](../deployment/deployment.example.json). Replace fictional identities and paths through the infrastructure process.
- Protected `%ProgramData%\CLC\Deployment\host-authorizations\<authorization_id>.json`, its empty `host-enrollment.lock` and evidence directory, owned by SYSTEM/Administrators with protected ancestors. Authorization binds configuration bytes, machine, SIDs, paths/ACLs, IIS pre-state, finite scopes/window and bootstrap/installed-toolkit identity.
- Independently authenticated bootstrap at `%ProgramData%\CLC\Deployment\Bootstrap\ssrs-web`: module, required infrastructure tools, `source.commit` and retained `distribution.zip`.

Keep application, Config, Keys, Inbox, enrollment/control, bootstrap, authorization, backups and Logs physically disjoint as required. The workflow checks protected paths/owners, reparses/hard links, executable inventory and retained distribution hashes **before** evaluating bootstrap code, holding leases through execution. A sidecar or self-verifier cannot establish initial trust. Build, bootstrap and active-host toolkit pins are separate; changing the build pin never upgrades the host.

## SSRS-specific DEV preflight

Use the locked toolkit's [installation](https://github.com/clcdpc/clc-deployment/blob/a3a94cd26daa583d392f73e99e56872876a88467/docs/installation.md), [enrollment](https://github.com/clcdpc/clc-deployment/blob/a3a94cd26daa583d392f73e99e56872876a88467/docs/host-enrollment.md), [deployment/recovery](https://github.com/clcdpc/clc-deployment/blob/a3a94cd26daa583d392f73e99e56872876a88467/docs/deployment.md) and [IIS verification](https://github.com/clcdpc/clc-deployment/blob/a3a94cd26daa583d392f73e99e56872876a88467/docs/iis-verification.md) guides alongside this checklist.

`Get-ClcHostEnrollment` and `Initialize-ClcHostEnrollment -Operation Plan` are read-only. `Ready` skips initialization; `ToolkitNotInitialized` or `TargetNotEnrolled` requires separate exact setup authorization. `Incomplete`, `DriftDetected` or `RecoveryRequired` stops for reconciliation, not Apply-based repair. Enrollment readiness does not prove application approval, settings or readiness.

| Check | Required host evidence |
| --- | --- |
| Platform/IIS | Windows Server 2022 x64, pinned PowerShell, .NET 10 Hosting Bundle/ANCM 20; dedicated site/root/pool, 64-bit Integrated/No Managed Code, one worker, AutoStart, Load User Profile and no overlapping rotation |
| Identities | Conventional AD runtime account outside Administrators: application read/execute, configuration read, keys read/write, Logs Modify; no writes to application/toolkit/control. Separate elevated CLC deployment account for content/pool control |
| Configuration/keys | Protected external files; target label DEV but application environment **Production**, never Development; new revision for every edit; required user-DPAPI policy and worker key persistence across recycle |
| Authorization | Protected bootstrap/host authorization plus separate enabled, target-scoped approvals for each release, binding exact identity/hashes/provenance. Build success or toolkit Ready is not release approval |
| TLS/readiness | Approved HTTPS hostname/binding/SNI, certificate access/SAN/chain/revocation and correct `/health/ready` routing. No TLS bypass, authentication redirects or report-route warmup |
| ANCM/subscriptions | Inherited locked ANCM 60-second shutdown setting. When subscriptions are enabled: canonical Application Initialization module, preload, initialization after restart, AlwaysRunning, idle timeout zero, pool shutdown grace at least 90 seconds |
| SQL/delivery | Separately provision approved SsrsWeb/Hangfire schemas, runtime grants and provider settings; qualify delivery. Deployment does not migrate or restore database state |
| Logs/backups | `%ProgramData%\clc-ssrs-web\Logs` disjoint from managed/protected paths; verify runtime Modify access for rotation/retention. Retain application backups/failure evidence; separately back up configuration, keys and SQL |

The workflow observes settings; it does not repair them or ACLs. A protected read-only toolkit token is needed only for private asset access; it is never sent to the CDN. Details: [host setup](operations/README.md#host-setup), [subscription provisioning](operations/subscriptions.md#production-provisioning-and-deployment), [Logs ACLs](operations/troubleshooting.md#initial-ntfs-provisioning).

## IIS prerequisites and reboot limitation

Enrollment creates a new pool with `AutoStart=false`. An independently authorized administrator must set it true before deployment; the workflow refuses false/unavailable values and never changes them.

For enabled subscriptions, install Application Initialization separately. The global `ApplicationInitializationModule` image must be the canonical full IIS `warmup.dll` path without a precondition, and the effective site module must have empty type/precondition. Macros such as `%IIS_BIN%`, relative paths and unresolved images are refused. Global registration alone is insufficient. Missing prerequisites stop deployment; correct them through administration, then rerun.

**Reboot limitation:** the toolkit has no activation fence/startup hook. Reboot or external WAS activation during physical replacement can start mixed files and require manual recovery. Ordinary replacement failure attempts a bounded stop and preserves backup/stage/attempt state. Do not enable deployment without accepting this operational tradeoff.

## Preserving shutdown and subscription hosting

Infrastructure must set this site-specific parent policy in protected `applicationHost.config`, outside the application root:

```xml
<location path="ExampleSsrsWeb" overrideMode="Allow">
  <system.webServer>
    <aspNetCore shutdownTimeLimit="60" lockAttributes="shutdownTimeLimit" />
  </system.webServer>
</location>
```

The workflow requires the enrolled site, exact value and attribute lock. Shared-configuration redirection is refused unless enabled is absent or exactly false (case-insensitive); `configSource` and unsupported layouts are refused.

A locked child attribute is invalid even when its value matches. Before binding the legacy cutover inventory, infrastructure must stop/fence the pool and remove its old child `shutdownTimeLimit`, preserving the original separately. The workflow does not edit it. This retains the ANCM60/Host45/Hangfire30 budget; verify actual inheritance and graceful shutdown on the host.

Preflight rejects obsolete/test configuration keys from machine/pool/application settings without logging values. Subscription requirements are conditional on enabled configuration; after separate administration, rerun the workflow. Alternatively start with reviewed subscriptions-disabled configuration and enable later with a new revision. Full validation remains authoritative and preflight never connects to SQL.

## Dispatch, artifact selection and recovery

Dispatch `deploy-dev.yml` from the exact reviewed protected `master` SHA with existing protected `authorization_id`, `release_approval_id` and normally `first_cutover=false`. The release approval binds target/application, source/release identity, lineage/sequence, inner ZIP/manifest hashes and producer/artifact provenance.

The workflow inspects enrollment, performs only separately authorized missing setup, reinspects to Ready, then checks application prerequisites. Missing release approval can stop an otherwise completed enrollment; supply it and rerun without repeating setup.

For first migration of legacy content, independently stop/fence the pool with zero workers and authorize its complete inventory, physical identity and disjoint backup; then use `first_cutover=true`. The toolkit backs up and removes only identity-bound approved legacy files, publishing the target last. Unknown/partial content or first-cutover on an already Ready target is refused. A legacy backup is not a prior toolkit release and cannot become RestorePrevious history.

Artifact admission verifies the exact historical producer run attempt/jobs, source, successful completion, artifact and transport digest. It verifies the complete transport ZIP before opening its single `release.zip`, then checks inner hash/manifest and `Test-ClcRelease` before create-new publication to the approved inbox. Matching cached bytes may be reused offline; conflicts and failed-download evidence are retained. API credentials never go to signed artifact CDN downloads.

The workflow imports only the authenticated active module and calls `Invoke-ClcIisDeployment -TargetId ssrs-web-dev -ReleaseApprovalId <id>`. It records Deployed/NoOp, attempt/generation/identity and observed readiness/pool state in nonsecret JSON. Retain authoritative journals, approvals, ZIPs and backups independently of the 30-day workflow evidence.

Recovery is explicit operator-directed work with the installed toolkit and exact latest failed attempt:

```powershell
Import-Module $independentlyVerifiedActiveManifest
Restore-ClcIisDeployment -TargetId ssrs-web-dev -AttemptId $latestAttempt `
    -Mode AcceptCandidate -Reason approved-incident-reference
# Use RestorePrevious or RestoreEmpty only when specifically authorized
# and admissible in the retained journal.
```

Never automatically recover, retry an ambiguous mutation, restart legacy code, delete journals/locks or fabricate approvals. Do not import privileged toolkit code from a checkout. Toolkit upgrades are separate infrastructure operations preserving old versions.

## Planned real-DEV seven-check pilot

**All seven live checks remain NOT RUN.** Run each operation separately in an authorized maintenance window and inspect its result before proceeding. Releases A/B must be healthy and distinct; separately approved application-owned C must pass validation but fail readiness **after replacement/startup**. Preflight rejection does not test recovery. Without a suitable C, checks 5–7 remain NOT RUN; do not add a production failure switch or use the generic FailureApp instead.

| Check | Required result |
| --- | --- |
| 1. Deploy A to an authorized empty target | Record initial pool state; Deployed; exact A readiness/inventory; Started/one worker; verified empty baseline backup, generation 1 and matching Completed journal/snapshot |
| 2. Upgrade to B | Exact B readiness/inventory; next generation; complete verified A backup/index; unchanged external configuration/keys |
| 3. Reapply B | NoOp with fresh readiness; unchanged application/current bytes, timestamps and generation; no stop/copy/start/stage/backup; new Completed attempt |
| 4. Change configuration to revision 2, apply B | Deployed despite identical release bytes; revision-2 readiness, next generation and backup. Separately verify changed bytes with the old revision are refused |
| 5. Apply C | Bounded Clc.ReadinessFailed after replacement/startup; C not current; retained stage/manifest/backup and RecoveryRequired journal; bounded stop proves zero workers or records uncertainty; ordinary retry refused |
| 6. Inspect backups before recovery | Earlier backups/indexes byte-identical; failed attempt's verified B backup matches approval/manifest/inventory; all failure evidence retained; configuration/keys remain external |
| 7. Restore B from latest failed attempt | Authorized RestorePrevious with compatible revision-2 configuration and current approvals; verified backup/manifest, exited helpers before effects, exact B readiness/inventory, Started/one worker, next generation, before-recovery snapshot/terminal record; original evidence retained |

```powershell
Import-Module $independentlyVerifiedActiveManifest
Invoke-ClcIisDeployment -TargetId ssrs-web-dev -ReleaseApprovalId $approvedA
Invoke-ClcIisDeployment -TargetId ssrs-web-dev -ReleaseApprovalId $approvedB
Invoke-ClcIisDeployment -TargetId ssrs-web-dev -ReleaseApprovalId $approvedB # NoOp
# Infrastructure changes configuration to approved revision 2.
Invoke-ClcIisDeployment -TargetId ssrs-web-dev -ReleaseApprovalId $approvedB
Invoke-ClcIisDeployment -TargetId ssrs-web-dev -ReleaseApprovalId $approvedC
# Inspect failed attempt and all backups before explicitly restoring B.
Restore-ClcIisDeployment -TargetId ssrs-web-dev -AttemptId $latestFailedAttemptId `
    -Mode RestorePrevious -Reason $nonsecretIncidentReference
```

The synthetic fixture's AcceptCandidate case does not establish live RestorePrevious acceptance. Record actual host observations before changing any live status.

## Repeatable local checks

From the repository root:

```powershell
dotnet restore SsrsPortal.slnx --locked-mode
dotnet restore src/SsrsPortal/SsrsPortal.csproj --locked-mode -p:ClcIdentityKind=Release
dotnet build SsrsPortal.slnx -c Release --no-restore
dotnet test SsrsPortal.slnx -c Release --no-build
pwsh -NoProfile -File scripts/release/Test-DevDeployment.ps1
pwsh -NoProfile -File scripts/release/Test-ReleasePackaging.ps1
```

The packaging script's Windows interoperability branch additionally needs `-PublishDirectory`, `-SyntheticToolkitArchive` and `-SyntheticToolkitLock`. Never copy its synthetic pin into production configuration.

For the combined Windows fixture:

```powershell
pwsh -NoProfile -File scripts/release/Test-DeploymentIntegration.ps1 `
    -ToolkitSourceRoot <clean-exact-toolkit-clone> `
    -ToolkitSourceCommit <exact-toolkit-commit> `
    -ReleaseArchivePath <compiled-synthetic-initial.zip> `
    -UpgradeReleaseArchivePath <distinct-compiled-synthetic-upgrade.zip>
```

It owns an absent-only conventional ProgramData fixture and exercises the real module/compiled app with synthetic IIS and loopback readiness, not live IIS/SQL. It checks upgrade/NoOp/configuration restart, failed readiness/retry refusal, AcceptCandidate and backup/key/configuration preservation. Windows DPAPI checks skip on Linux; missing platform prerequisites are not passes.
