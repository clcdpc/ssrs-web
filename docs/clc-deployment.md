# CLC deployment integration

Use this runbook with the exact toolkit pinned in [deployment/toolkit.lock.json](../deployment/toolkit.lock.json). Source integrity, host enrollment and application release approval are separate checks; none substitutes for the others.

## Qualification status

The beta.3 companion and published toolkit passed locked restores, packaging interoperability and Windows consumer checks. The archive and inventory match the lock. **Live DEV/IIS qualification remains outstanding.** Synthetic tests do not qualify IIS, TLS, runtime identities, persistent keys, Entra, SSRS, SQL or subscription delivery. Keep operational deployment disabled until the required infrastructure approvals and host checks are complete.

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

Push a protected immutable version tag or start a **new build** against an existing tag:

```powershell
gh workflow run release.yml --repo clcdpc/ssrs-web --ref v1.0.0-dev.3
```

[release.yml](../.github/workflows/release.yml) supports tag push and tag-ref `workflow_dispatch` without inputs. Branch dispatches fail before repository code runs. [GitHub dispatch semantics](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_dispatch) select the commit at the requested branch/tag; the workflow must exist on the default branch and the selected tag must contain a dispatch-capable workflow. Dispatch is a new build, distinct from rerunning an old attempt.

| Tag | Initial source eligibility | Archive and automatic target |
| --- | --- | --- |
| `vMAJOR.MINOR.PATCH-dev.N` | Protected-master history or trusted open same-repository PR's current head | GitHub prerelease; DEVWEB / `ssrs-web-dev` |
| `vMAJOR.MINOR.PATCH-beta.N` | Protected-master history | GitHub prerelease and Actions artifacts; no deployment |
| `vMAJOR.MINOR.PATCH-rc.N` | Protected-master history | GitHub prerelease and Actions artifacts; no deployment |
| `vMAJOR.MINOR.PATCH` | Protected-master history | Normal GitHub Release; `ssrs-web-production`, disabled |

Major/minor/patch are 0..9999 and N is 1..9998, without leading zeroes. Case changes, build metadata and other suffixes are rejected. DEV PR authors need write/maintain/admin permission; forks, unrelated branches and obsolete PR heads cannot qualify an initial release. Producer workflow/support files must match reviewed protected-master files for an unmerged source. A PR changing those files must first have them reviewed into master and update to match.

The hosted Windows producer checks exact source bytes and tag/object identity, uses locked dependencies and the SDK in `global.json`, runs required tests, compiles Release identity, exports license notices, and packages framework-dependent `net10.0/win-x64` without apphost/debug symbols. It uses the authenticated published beta.3 packaging toolkit, with no deployment identity, write token or cross-repository secret. Runtime packages exclude SQL/administrative setup, Mock and operational configuration. Setup remains a separate artifact.

| Actions artifact | Identity and contents |
| --- | --- |
| `ssrs-web-release-<run>-<attempt>` | One `release.zip`; current deployment transport |
| `ssrs-web-provenance-<run>-<attempt>` | Exact source/tag/object, workflow/run/attempt/job, artifact ID, transport/package/manifest hashes, SDK/dependency/toolkit pins |
| `ssrs-web-administrative-setup-<run>-<attempt>` | Separate setup tooling/licenses; never executed by routine deployment |

Run/attempt-derived release IDs distinguish fresh builds of the same tag. Transport ZIP, package ZIP and manifest hashes identify different bytes. Actions retention is 30 days; GitHub Releases provide the long-term canonical archive.

## Tag-driven automatic deployment

[deploy-release.yml](../.github/workflows/deploy-release.yml) consumes successful producer `workflow_run` events using protected-master code. Its hosted jobs independently authenticate repository/workflow IDs, current producer attempt, successful hosted packaging job, source eligibility, reviewed producer files, tag/object/commit, exact artifact IDs/service digests, package and manifest. No artifact code executes during admission.

Only the hosted `archive` job has `contents: write`; it uses a master-pinned, independently authenticated toolkit to inspect the complete package and application identity. It creates a draft, uploads `release.zip`, `release-provenance.json`, `release-metadata.json`, `SHA256SUMS`, `LICENSE` and `THIRD-PARTY-NOTICES.md`, checks upload hashes, rechecks the tag and publishes. Other dependency notices/licenses remain inside the canonical package. Required tests or admission failure prevent publication.

The first complete published Release is canonical. Later builds verify its tag, commit, classification, metadata and asset identities, make **no writes** to it, and continue using the new Actions artifact. Different package bytes are permitted and are reported separately from the archived package hash. Per-tag non-cancelling archive concurrency and create-race validation prevent asset replacement. An incomplete draft or conflicting archive fails closed for separate administrative reconciliation; this code never deletes/releases/replaces historical assets.

A closed/advanced DEV PR may qualify a tag dispatch through an existing **GitHub immutable Release** that locks the tag and assets. The archive must retain original source eligibility and a successful hosted archive job from a reviewed protected-master consumer commit. That historical consumer revision supplies producer-code trust. Old Actions artifacts or GitHub Deployment records are unnecessary. Missing, mutable, inconsistent or unverifiable historical evidence is rejected explicitly; initial push eligibility is unchanged.

Deployment depends on successful archival **and** hosted admission/activation. The elevated runner reloads only the explicit deployment-script list from its own protected-master workflow SHA, repeats producer/artifact/source/tag checks after queueing, verifies archival again, authenticates the installed toolkit/bootstrap and requires Ready enrollment plus application preflight. It never checks out tagged source or performs builds, SQL setup, enrollment, cutover or automatic recovery.

DEV and production use separate target concurrency groups with `cancel-in-progress: false`. GitHub does not promise FIFO; a newer eligible tag supersedes an older one even if its build failed or is pending. A rebuild of an older tag cannot evade this check or toolkit downgrade protection. Same-tag fresh builds use the toolkit's run/attempt ordering; identical current identity reaches fresh-readiness NoOp.

### GitHub and host configuration prerequisites

- Protect master with reviews/checks and restricted bypass. Restrict version-tag creation and prohibit update/deletion/bypass. Enable [GitHub release immutability](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases) before publication for durable historical DEV rebuild eligibility.
- Restrict privileged runner groups to `clcdpc/ssrs-web/.github/workflows/deploy-release.yml@refs/heads/master`. Labels and YAML guards cannot enforce admission. No routine manually dispatched IIS workflow remains.
- Configure separate `ssrs-web-dev` and `ssrs-web-production` environments allowing only protected-master deployment code, separate runner groups/hosts and infrastructure-owned standing policies. Reconcile environment reviewer policy explicitly with unattended routine delivery; first cutover and recovery retain separate authority.
- DEV needs `SSRS_DEV_DEPLOYMENT_ENABLED=true`, `SSRS_DEV_AUTOMATIC_DEPLOYMENT_ENABLED=true`, `SSRS_DEV_RUNNER_ADMISSION_READY=true`, `SSRS_DEV_RUNNER_GROUP`, JSON `SSRS_DEV_RUNNER_LABELS`, `SSRS_DEV_HOST_AUTHORIZATION_ID` and `SSRS_DEV_STANDING_AUTHORIZATION_ID`.
- Production has separate `SSRS_PRODUCTION_AUTOMATIC_DEPLOYMENT_ENABLED`, `SSRS_PRODUCTION_RUNNER_ADMISSION_READY`, group, labels, host-authorization and standing-authorization variables. Its runner group must differ from DEV. **Production remains disabled; this implementation changes no activation setting.**
- Merge/publish the compatible toolkit separately, independently authenticate and install it, and provision/revoke standing policies through infrastructure administration. The published build pin does not authorize or perform host upgrades.

### Toolkit integration and host trust

The revised [verified-workflow contract at dd93d0d](https://github.com/clcdpc/clc-deployment/blob/dd93d0df515d914f692b7aefea6e06dd5056e8d8/docs/verified-workflow-integration.md) defines the boundary in [AutomaticDeployment.Adapter.ps1](../scripts/release/AutomaticDeployment.Adapter.ps1):

```powershell
Invoke-ClcIisDeployment -TargetId $target -StandingAuthorizationId $standingId `
    -ReleaseTag $tag -ArchivePath $verifiedInboxZip -VerifiedReleasePath $verifiedIdentityJson
```

The trusted consumer creates unique, create-new ZIP/identity files inside the authenticated target inbox. The strict identity binds repository/tag/source, manifest/package hashes and exact producer run/attempt/job/artifact/transport. It is evidence, not an approval record. The toolkit enforces standing scope/revocation, baseline/lineage/tag-source binding, rebuild ordering and downgrade policy under its existing lock, then performs the existing IIS/backup/readiness lifecycle. No per-release approval writer, signing producer, attestation or Sigstore dependency is used.

The adapter rejects an installed module lacking the five-parameter Automated API. The build lock remains published `0.1.0-beta.3`; an unpublished candidate is never represented as installed production software. Exact toolkit implementation/contract test evidence is recorded below. A compatible toolkit's merge, publication, independent host installation and real-host qualification remain activation prerequisites.

Infrastructure supplies the reviewed nonsecret [deployment configuration](../deployment/deployment.example.json), protected host authorization/enrollment lock/evidence, and independently authenticated bootstrap module/tools/`source.commit`/retained distribution at `%ProgramData%\CLC\Deployment\Bootstrap\ssrs-web`. Bootstrap, active-host and build-toolkit pins are separate.

Keep application, Config, Keys, Inbox, control state, bootstrap, authorizations, backups and Logs physically disjoint. The workflow authenticates path/owner/ACL and complete retained toolkit bytes before bootstrap execution and holds leases through use. Authenticate PowerShell 7.6.6 separately at `C:\CLC\Tools\PowerShell\7.6.6\pwsh.exe`, protect its ancestors/module paths and runner temporary scripts, and independently provision IIS/.NET/TLS/accounts. Administrators remain a procedural trust boundary.

## SSRS-specific DEV preflight

Use the locked toolkit's [installation](https://github.com/clcdpc/clc-deployment/blob/a3a94cd26daa583d392f73e99e56872876a88467/docs/installation.md), [enrollment](https://github.com/clcdpc/clc-deployment/blob/a3a94cd26daa583d392f73e99e56872876a88467/docs/host-enrollment.md), [deployment/recovery](https://github.com/clcdpc/clc-deployment/blob/a3a94cd26daa583d392f73e99e56872876a88467/docs/deployment.md) and [IIS verification](https://github.com/clcdpc/clc-deployment/blob/a3a94cd26daa583d392f73e99e56872876a88467/docs/iis-verification.md) guides alongside this checklist.

`Get-ClcHostEnrollment` and `Initialize-ClcHostEnrollment -Operation Plan` are read-only. `Ready` skips initialization; `ToolkitNotInitialized` or `TargetNotEnrolled` requires separate exact setup authorization. `Incomplete`, `DriftDetected` or `RecoveryRequired` stops for reconciliation, not Apply-based repair. Enrollment readiness does not prove application approval, settings or readiness.

| Check | Required host evidence |
| --- | --- |
| Platform/IIS | Windows Server 2022 x64, pinned PowerShell, .NET 10 Hosting Bundle/ANCM 20; dedicated site/root/pool, 64-bit Integrated/No Managed Code, one worker, AutoStart, Load User Profile and no overlapping rotation |
| Identities | Conventional AD runtime account outside Administrators: application read/execute, configuration read, keys read/write, Logs Modify; no writes to application/toolkit/control. Separate elevated CLC deployment account for content/pool control |
| Configuration/keys | Protected external files; target label DEV but application environment **Production**, never Development; new revision for every edit; required user-DPAPI policy and worker key persistence across recycle |
| Authorization | Protected bootstrap/host authorization, independently approved baseline and enabled target-scoped standing authorization for routine releases. Build success or toolkit Ready is not deployment authority |
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

## Initial cutover, baseline and exceptional recovery

Initial host provisioning, IIS enrollment, first cutover, toolkit installation/upgrades and exceptional recovery are separate administrative procedures. They are never release-workflow dispatch inputs. Use the exact authenticated installed toolkit's installation, host-enrollment and recovery guides linked above.

Inspect enrollment and Plan first. Apply requires independently provisioned exact host/operation authority. Before adopting legacy content, independently stop/fence the pool with zero workers and authorize its full inventory/physical identity/disjoint backup. The toolkit removes only approved legacy files. Unknown/partial state requires reconciliation. A legacy backup cannot become prior toolkit release history.

Standing authorization requires a successfully deployed, independently approved baseline on a Ready target. Infrastructure uses a separately approved exact release for that initial deployment. Routine workflows neither generate this approval nor establish their own baseline.

Recovery remains operator-directed against the latest failed attempt. Retain authoritative journals, exact approvals, verified packages, backups, configuration and keys outside temporary Actions retention:

```powershell
Import-Module $independentlyVerifiedActiveManifest
Restore-ClcIisDeployment -TargetId ssrs-web-dev -AttemptId $latestAttempt `
    -Mode RestorePrevious -Reason $incidentReference -RecoveryApprovalId $independentExactApproval
```

A generated-release recovery needs its independently authorized matching schema-1 recovery approval. AcceptCandidate/RestoreEmpty require their own admissibility and authority. Never automatically restore, retry ambiguous mutation, restart legacy code, delete journals/locks or fabricate approvals. Application recovery does not imply database rollback safety.

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

Current repository evidence: locked restore/Release build and 1,334 .NET tests passed; tag admission/activation passed 29/29, archive/historical eligibility 9/9, retained administrative/host safeguards 32/32, and ordinary/Release beta.3 packaging 29/29. Full synthetic browser and subscription suites passed with 376 accessibility scans. Actionlint 1.7.12, PowerShell syntax, configuration publish guard, generated-artifact policy/tests (2/2), npm audit and whitespace checks passed.

The real consumer adapter and unmodified toolkit source at `dd93d0df515d914f692b7aefea6e06dd5056e8d8` passed **12/12** combined Windows checks with compiled portal packages, actual NTFS changes and loopback readiness. Coverage includes the existing deployment/recovery sequence plus standing authorization, provenance rejection, Deployed/NoOp, same-tag fresh packages, older-build/downgrade rejection and revoked retries. The candidate was checked out at its exact commit and every tracked byte authenticated before import. This establishes compatibility with that unmerged candidate, not availability in a published/installed host toolkit.

Generated evidence stays under ignored `artifacts/`. The fixture uses an exclusively owned short temporary control root to fit the toolkit's unchanged path limit. These results do not qualify live IIS/SQL or DEVWEB/production deployment. All seven live checks above remain NOT RUN. Protected-master push/dispatch publication still needs GitHub qualification after merge and configuration; no real application release was published during implementation.

From the repository root:

```powershell
dotnet restore SsrsPortal.slnx --locked-mode
dotnet restore src/SsrsPortal/SsrsPortal.csproj --locked-mode -p:ClcIdentityKind=Release
dotnet build SsrsPortal.slnx -c Release --no-restore
dotnet test SsrsPortal.slnx -c Release --no-build
pwsh -NoProfile -File scripts/release/Test-DevDeployment.ps1
pwsh -NoProfile -File scripts/release/Test-TagDeployment.ps1
pwsh -NoProfile -File scripts/release/Test-ReleaseArchive.ps1
pwsh -NoProfile -File scripts/release/Test-ReleasePackaging.ps1
```

The packaging script's Windows interoperability branch additionally needs `-PublishDirectory`, `-SyntheticToolkitArchive` and `-SyntheticToolkitLock`. Never copy its synthetic pin into production configuration.

For the combined Windows fixture:

```powershell
pwsh -NoProfile -File scripts/release/Test-DeploymentIntegration.ps1 `
    -ToolkitSourceRoot <clean-exact-toolkit-clone> `
    -ToolkitSourceCommit <exact-toolkit-commit> `
    -ReleaseArchivePath <compiled-synthetic-initial.zip> `
    -UpgradeReleaseArchivePath <distinct-compiled-synthetic-upgrade.zip> `
    -StandingReleaseArchivePath <compiled-synthetic-run-101.zip> `
    -RebuildReleaseArchivePath <same-source-compiled-run-102.zip>
```

It owns an absent-only conventional ProgramData fixture and exercises the real module/compiled app with synthetic IIS and loopback readiness, not live IIS/SQL. It checks upgrade/NoOp/configuration restart, failed readiness/retry refusal, AcceptCandidate and backup/key/configuration preservation. The optional standing/rebuild pair exercises the actual adapter with missing/revoked policy, provenance mismatch, Deployed/NoOp, distinct same-tag packages, old-build/downgrade rejection and revocation after admission. Both packages must share a source commit and have distinct compiled release/run identities. Windows DPAPI checks skip on Linux; missing platform prerequisites are not passes.
