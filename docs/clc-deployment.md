# CLC deployment integration

Use this runbook with the exact toolkit pinned in [deployment/toolkit.lock.json](../deployment/toolkit.lock.json). Source integrity, host enrollment and application release approval are separate checks; none substitutes for the others.

## Qualification status

The beta.3 companion and published toolkit passed locked restores, packaging interoperability and Windows consumer checks. The archive and inventory match the lock. **Live DEV/IIS qualification remains outstanding.** Synthetic tests do not qualify IIS, TLS, runtime identities, persistent keys, Entra, SSRS, SQL or subscription delivery. Withhold host authorization until required infrastructure approvals and host checks are complete.

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

Routine DEV release requires only creating and pushing the version tag at the selected commit:

```powershell
git tag v1.2.3-dev.4
git push origin v1.2.3-dev.4
```

[release.yml](../.github/workflows/release.yml) runs the workflow and release/deployment scripts from that exact tagged commit. DEV may select unmerged application, workflow and script changes without a PR. PR closure or branch advancement/deletion does not affect eligibility. GitHub's repository-wide Restrict actors policy and release-tag rules authorize tag creation; author/tagger metadata grants no authority.

| Tag | Source eligibility | Archive and automatic target |
| --- | --- | --- |
| `vMAJOR.MINOR.PATCH-dev.N` | Any selected commit | GitHub prerelease; `ssrs-web-dev` |
| `vMAJOR.MINOR.PATCH-beta.N` | Any selected commit | GitHub prerelease; no deployment |
| `vMAJOR.MINOR.PATCH-rc.N` | Any selected commit | GitHub prerelease; no deployment |
| `vMAJOR.MINOR.PATCH` | Commit contained in protected master history | GitHub Release; `ssrs-web-production` |

Major/minor/patch are 0..9999 and N is 1..9998, without leading zeroes. Case changes, build metadata and other suffixes are rejected. For production, select a commit contained in master and push the corresponding stable tag. This change provisions no production infrastructure.

The ordered jobs are `release -> archive -> deploy`. The new workflow name also avoids matching the old completion consumer before its removal reaches master. Build/test failure prevents publication; publication failure prevents deployment. Beta/RC stop after archive.

The hosted Windows `release` job verifies exact source bytes and tag/object identity, restores locked dependencies with the SDK in `global.json`, builds/tests, compiles attempt-specific Release identity, exports notices and packages framework-dependent `net10.0/win-x64` without apphost/debug symbols. The authenticated published packaging toolkit has no host deployment authority. Runtime packages exclude SQL/administrative setup, Mock and operational configuration.

| Actions artifact | Identity and contents |
| --- | --- |
| `ssrs-web-release-<run>-<attempt>` | One `release.zip`; current deployment transport |
| `ssrs-web-provenance-<run>-<attempt>` | Tag/object/source, workflow revision/run/attempt/job, artifact ID, transport/package/manifest hashes, SDK/dependency/toolkit pins |
| `ssrs-web-administrative-setup-<run>-<attempt>` | Separate setup tooling/licenses; never executed by routine deployment |

Only hosted Windows `archive` has `contents: write`. It authenticates the current run/attempt and its completed, successful hosted `release` job while the overall run is in progress. Artifact existence alone cannot qualify. It verifies package/manifest hashes, compiled identity and the authenticated packaging inspector, creates a draft, uploads `release.zip`, `release-provenance.json`, `release-metadata.json`, `SHA256SUMS`, `LICENSE` and `THIRD-PARTY-NOTICES.md`, verifies uploads, rechecks the tag and publishes.

Enable GitHub Release immutability before publication for service-enforced asset/tag protection. The first complete published Release reporting Boolean `immutable: true` is canonical; newly published and existing mutable Releases are deliberately rejected. Enabling the repository setting later does not automatically make previously published Releases immutable. Routine deployment never repairs historical release state.

Later builds verify canonical immutability, tag/object/source, classification, metadata and assets without changing any canonical asset. Incomplete drafts and conflicting archives fail closed. Archive concurrency and create-race validation prevent replacement.

For a fresh build of the same tag, rerun **all jobs** of its original push run: `gh run rerun <run-id> --repo clcdpc/ssrs-web`. The attempt changes and the package gets a distinct compiled identity and artifact. Canonical assets stay intact; deployment uses the newly verified Actions artifact. Reusing a run/attempt identity with different artifact/package hashes is refused. Rerunning only archive/deploy cannot qualify a previous attempt's producer; rerun all jobs. Actions artifacts expire after 30 days; create a new deliberate version tag when rerun/artifact availability has expired. See [GitHub rerun behavior](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs).

## Tag-driven automatic deployment

Successful archival automatically routes DEV/stable tags to their target. Target, GitHub environment, runner label, host authorization ID and standing authorization ID all use `ssrs-web-dev` or `ssrs-web-production`. Runner selection is `[self-hosted, Windows, X64, <target-id>]`. Repository-scoped runners and organization runners available to this repository work without mandatory groups or routing variables.

The elevated job downloads only the fixed deployment-script list from the tagged workflow commit, checking Git blob hashes against its tree. After queueing it repeats tag/object/source, workflow/run/attempt, successful release/archive job and artifact/hash verification. It authenticates the independently installed toolkit, requires Ready enrollment and application preflight, and checks the protected target before invoking deployment. Package contents cannot replace the installed toolkit, enrollment, infrastructure configuration, standing policy, credentials or host security policy. Packaged scripts never execute.

DEV and production have separate target concurrency groups with `cancel-in-progress: false`. GitHub does not promise FIFO; a newer eligible tag supersedes an older release even if its build failed or is pending. Same-tag attempts retain toolkit ordering and downgrade protection. Identical current identity reaches fresh-readiness NoOp. Locking, backups, readiness, failure propagation and operator-directed recovery keep existing toolkit semantics.

### GitHub and host configuration prerequisites

Provision these once through separate repository/infrastructure administration:

- Repository-wide Restrict actors policy, active release-tag creation ruleset, separate tag update/deletion protection, protected master and GitHub release immutability.
- `ssrs-web-dev` and `ssrs-web-production` environments without branch/tag restrictions or required deployment reviewers. No per-release approval or per-PR exception is required.
- Dedicated Windows x64 deployment runners labeled with their target ID and independently admitted to the repository. Build/test/archive stay on GitHub-hosted Windows; exact runner-group workflow allowlists are unnecessary.
- Independently authenticated/installed compatible CLC toolkit, protected host authorization named `<target-id>`, Ready enrollment and a separately approved deployed baseline. Provision an enabled target-scoped standing authorization named `<target-id>` through infrastructure administration.

Naming grants no authority: missing, invalid or revoked host/standing authorization prevents deployment. No deployment activation or routing variables, manual dispatch, recurring GitHub changes or extra actor database are required. The optional read-only host diagnostic has its own separate opt-in, never used by release jobs. This implementation changes no live GitHub settings or infrastructure.

### Toolkit integration and host trust

The merged [verified-workflow contract at 7f814ef](https://github.com/clcdpc/clc-deployment/blob/7f814ef838abcfd12ade494c429918657598c35d/docs/verified-workflow-integration.md) defines the boundary in [AutomaticDeployment.Adapter.ps1](../scripts/release/AutomaticDeployment.Adapter.ps1):

```powershell
Invoke-ClcIisDeployment -TargetId $target -StandingAuthorizationId $target `
    -ReleaseTag $tag -ArchivePath $verifiedInboxZip -VerifiedReleasePath $verifiedIdentityJson
```

The tagged deployment adapter creates unique, create-new ZIP/identity files inside the authenticated target inbox. The strict identity binds repository/tag/source, manifest/package hashes and exact producer run/attempt/job/artifact/transport. It is evidence, not an approval record. The toolkit enforces standing scope/revocation, baseline/lineage/tag-source binding, rebuild ordering and downgrade policy under its existing lock, then performs the existing IIS/backup/readiness lifecycle. No per-release approval writer, signing producer, attestation or Sigstore dependency is used.

The adapter rejects an installed module lacking the five-parameter Automated API. The build lock remains published `0.1.0-beta.3`. [clc-deployment PR #2](https://github.com/clcdpc/clc-deployment/pull/2) merged as `7f814ef838abcfd12ade494c429918657598c35d`; compatible API publication and host installation have not been established here. Exact toolkit implementation/contract test evidence is recorded below. Compatible toolkit publication, independent host installation and real-host qualification remain activation prerequisites.

Infrastructure supplies the reviewed nonsecret [deployment configuration](../deployment/deployment.example.json), protected host authorization/enrollment lock/evidence, and independently authenticated bootstrap module/tools/`source.commit`/retained distribution at `%ProgramData%\CLC\Deployment\Bootstrap\ssrs-web`. Bootstrap, active-host and build-toolkit pins are separate.

Keep application, Config, Keys, Inbox, control state, bootstrap, authorizations, backups and Logs physically disjoint. The workflow authenticates path/owner/ACL and complete retained toolkit bytes before bootstrap execution and holds leases through use. Authenticate PowerShell 7.6.6 separately at `C:\CLC\Tools\PowerShell\7.6.6\pwsh.exe`, protect its ancestors/module paths and runner temporary scripts, and independently provision IIS/.NET/TLS/accounts. Administrators remain a procedural trust boundary.

## Read-only deployment prerequisite validation

[diagnose-deployment.yml](../.github/workflows/diagnose-deployment.yml) is separate optional read-only work pinned to reviewed upstream source. It observes GitHub and host prerequisites without deploying, enrolling or repairing infrastructure. Its READY/NOT READY report is not authorization; missing or inconclusive evidence remains FAIL, UNVERIFIED or SKIPPED and cannot establish readiness.

The older pinned checker was written for split producer/consumer orchestration. Both release/deployment selectors now point to `release.yml`; legacy trigger, activation, group and shared-channel checks may remain FAIL, UNVERIFIED or SKIPPED. Do not add removed settings or weaken host protections to obtain READY. Workflow/admission regressions verify this architecture. The diagnostic never gates release jobs.

The diagnostic runs manually from protected master with the separate `SSRS_DEV_DIAGNOSTIC_RUNNER_READY` opt-in, using conventional DEV labels without group variables. Beta.3 host-reader compatibility and the enrollment environment discrepancy remain unresolved; actual host evidence is required. Ordinary read-only `GITHUB_TOKEN` may leave administrative APIs UNVERIFIED. No administrative token or new credential is introduced.

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

Offline GitHub API and host fixtures cover unmerged DEV workflow/script changes without PR lookups, stable ancestry, in-progress same-run qualification, failed/incomplete jobs, exact run/attempt/artifact identity, canonical compatibility, routing, supersession and authorization rejection. Transport/package ZIP hashes and a clean-process publisher test exercise actual validation/publication code. Generated evidence stays under ignored `artifacts/`; implementation creates no real version tags or Releases.

Current local Windows verification passed locked restore/Release build and 1,432 .NET tests, 28 tag admission/deployment checks, 13 archive/publisher checks, 29 packaging checks, 32 host safeguards, 13 diagnostic checks and 12 combined Windows/toolkit checks against exact source `7f814ef838abcfd12ade494c429918657598c35d`. Combined checks use actual NTFS changes and loopback readiness with synthetic IIS, including Deployed/NoOp, same-tag packages, downgrade/older-build rejection and revoked policies. The current synthetic browser suite passed 363 accessibility scans (historical broader coverage included 376). Configuration publish guard, locked npm restore, npm/NuGet audits, Actionlint 1.7.12 across every workflow, PowerShell syntax and generated-artifact checks also passed. These passes establish no live GitHub publication, installed-toolkit availability or IIS/SQL acceptance.

Real DEV acceptance must separately push an authorized DEV tag containing unmerged workflow/script changes, observe successful release/archive/deploy jobs, reconcile package/outcome evidence and exercise failure/supersession/rebuild cases. Production infrastructure is unchanged. All seven live checks above and known-record SSRS report-boundary acceptance remain NOT RUN.

From the repository root:

```powershell
dotnet restore SsrsPortal.slnx --locked-mode
dotnet restore src/SsrsPortal/SsrsPortal.csproj --locked-mode -p:ClcIdentityKind=Release
dotnet build SsrsPortal.slnx -c Release --no-restore
dotnet test SsrsPortal.slnx -c Release --no-build
pwsh -NoProfile -File scripts/release/Test-DevDeployment.ps1
pwsh -NoProfile -File scripts/release/Test-TagDeployment.ps1
pwsh -NoProfile -File scripts/release/Test-ReleaseArchive.ps1
pwsh -NoProfile -File scripts/release/Test-DeploymentReadiness.ps1 -ActionlintPath <approved-actionlint-1.7.12-executable>
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
