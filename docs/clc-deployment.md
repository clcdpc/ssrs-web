# CLC deployment integration

The general [CLC Deployment Setup Guide](https://github.com/clcdpc/clc-deployment/blob/master/docs/setup-guide.md) is the architectural authority for tag-driven releases. This runbook supplies the detailed SSRS-specific implementation and acceptance procedures. The current guide, build-time [toolkit lock](../deployment/toolkit.lock.json) and independently installed host runtime are separate dependencies.

## Qualification status

| Area | Status and remaining evidence |
| --- | --- |
| Code implementation | Tag-authoritative release/archive/deploy implemented; final architecture review found no code deviation |
| CI and synthetic validation | Reproducible checks and their scope are recorded [below](#repeatable-local-checks); they do not establish live deployment |
| External GitHub configuration | Operator verification of the [native policies, rulesets, environments and runners](#github-and-host-configuration-prerequisites) is still required; this task changes none of them |
| Compatible runtime toolkit | Publication and independent host installation of the automated standing-deployment runtime remain unestablished; the beta.3 build lock does not supply that evidence |
| Initial host enrollment/baseline | Ready enrollment and a successful independently approved baseline, followed by standing authorization, still require host evidence |
| Live DEV acceptance | NOT RUN, including the tag-driven acceptance and seven-check IIS pilot |
| Production qualification | NOT RUN; Production provisioning and acceptance require separate evidence |

The beta.3 companion and published packaging toolkit passed locked restores, packaging interoperability and Windows consumer checks; the archive and inventory match the build lock. Synthetic tests do not qualify IIS, TLS, runtime identities, persistent keys, Entra, SSRS, SQL or subscription delivery. Complete independent host setup and the approved baseline before enabling standing authorization.

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

Deployed builds require compiled `ClcIdentityKind=Release`, unique `ClcReleaseId` and full `ClcSourceCommit`. Local Synthetic builds cannot pass deployed validation/startup. Identity describes the bytes; the permitted tag push is the release decision, and independent host standing authorization determines whether an eligible routine release may deploy. Initial baseline and exceptional recovery retain separate exact authorization.

## Immutable release production

Routine DEV release requires only creating and pushing the version tag at the selected commit:

```powershell
git tag v1.2.3-dev.4
git push origin v1.2.3-dev.4
```

[release.yml](../.github/workflows/release.yml) runs the application code, workflow, release/deployment scripts and application-owned configuration selected by that exact tagged commit. DEV may select unmerged changes to any of them without PR lookup, review, approval, an open PR, current PR-head equality or master-file equality. Protected operational configuration on the host remains independently controlled. PR closure or branch advancement/deletion does not affect eligibility. GitHub's repository-wide Restrict actors policy controls workflow execution; native tag rulesets control tag creation. Author/tagger metadata grants no authority.

| Tag | Source eligibility | Archive and automatic target |
| --- | --- | --- |
| `vMAJOR.MINOR.PATCH-dev.N` | Any selected commit | GitHub prerelease; `ssrs-web-dev` |
| `vMAJOR.MINOR.PATCH-beta.N` | Any selected commit | GitHub prerelease; no deployment |
| `vMAJOR.MINOR.PATCH-rc.N` | Any selected commit | GitHub prerelease; no deployment |
| `vMAJOR.MINOR.PATCH` | Commit contained in protected master history | GitHub Release; `ssrs-web-production` |

Major/minor/patch are 0..9999 and N is 1..9998, without leading zeroes. Case changes, build metadata and other suffixes are rejected. For Production, select a commit contained in protected master history and push the corresponding stable tag; it need not equal current master HEAD. This change provisions no production infrastructure.

The single tag-push workflow has ordered jobs `release -> archive -> deploy`. Build/test failure prevents publication; publication failure prevents deployment. Beta/RC stop after archive. Routine operation requires no release dispatch, protected-master completion consumer, formal PR admission, manual deployment approval or additional CLC release-approval service/credential.

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

Successful archival automatically routes DEV/stable tags for application ID `ssrs-web` to their target. GitHub environment, dedicated runner label, deployment target, host authorization ID and standing authorization ID all use `ssrs-web-dev` or `ssrs-web-production`. These are separate protected resources sharing a naming convention. Runner selection is `[self-hosted, Windows, X64, <target-id>]`; no routing variables or runner selectors are required.

The elevated job downloads only the fixed deployment-script list from the tagged workflow commit, checking Git blob hashes against its tree. After queueing it repeats tag/object/source, workflow/run/attempt, successful release/archive job and artifact/hash verification. It authenticates the independently installed toolkit, requires Ready enrollment and application preflight, and checks the protected target before invoking deployment. Package contents cannot replace the installed toolkit, enrollment, infrastructure configuration, standing policy, credentials or host security policy. Packaged scripts never execute.

DEV and production have separate target concurrency groups with `cancel-in-progress: false`. GitHub does not promise FIFO; a newer eligible tag supersedes an older release even if its build failed or is pending. Same-tag attempts retain toolkit ordering and downgrade protection. Identical current identity reaches fresh-readiness NoOp. Locking, backups, readiness, failure propagation and operator-directed recovery keep existing toolkit semantics.

### GitHub and host configuration prerequisites

Provision these once through separate repository/infrastructure administration, following the [Setup Guide](https://github.com/clcdpc/clc-deployment/blob/master/docs/setup-guide.md):

1. In **Repository Settings > Actions > Policies**, create an **Active Restrict actors** policy covering **all repository workflows**, selecting the authorized release developers/automation. Review effective organization/enterprise policy too. This native execution policy does not grant tag-creation permission.
2. In **Repository Settings > Rules > Rulesets**, create **two Active tag rulesets**, both matching **`v*`**:

   | Ruleset | Restrictions | Bypass configuration |
   | --- | --- | --- |
   | Release tag creation | **Restrict creations** only | Authorized release-developer team with **Always allow** bypass; an empty list or pull-request-only bypass does not permit tag creation |
   | Immutable release tags | **Restrict updates** and **Restrict deletions** | No routine release-developer bypass |

   A bypass applies to **all restrictions in its ruleset**. Keep creation separate so authorized developers can create tags while ordinarily being unable to move or delete existing ones. Review inherited rulesets, patterns, exclusions and bypass membership together; a creation bypass cannot override a different applicable restriction.
3. Protect **master** against force pushes and deletion, preserving the application's existing required CI checks from [verify.yml](../.github/workflows/verify.yml). No additional PR-branch protection is required.
4. Separately enable **Repository Settings > General > Releases > Release immutability** **before publication** (or verify owner-enforced immutability). Tag rulesets alone do not establish immutable Release assets; canonical Releases must report Boolean `immutable: true` as described above.
5. Create **ssrs-web-dev** and **ssrs-web-production** environments. For **both**, set **Deployment branches and tags: No restriction**, with **no required reviewers, wait timers or additional deployment protection gates**.
6. Register dedicated **Windows x64 self-hosted** deployment runners. **Repository-scoped runners are preferred**, with labels `self-hosted`, `Windows`, `X64` and the derived target ID. They require no runner group or protected-master workflow allowlist. **Organization-scoped runners are an alternative** when their group grants this repository access. Build/test/archive stay on GitHub-hosted Windows; ordinary Linux/browser CI stays on GitHub-hosted Ubuntu with its existing trusted-contributor admission.
7. Independently authenticate/install a [compatible runtime toolkit](#toolkit-integration-and-host-trust), provision protected host configuration/target identity and host authorization named `<target-id>`, then complete [enrollment and the separately approved initial baseline](#initial-cutover-baseline-and-exceptional-recovery). **Only after the baseline succeeds**, provision enabled target-scoped [standing authorization](https://github.com/clcdpc/clc-deployment/blob/master/docs/automated-deployments.md) named `<target-id>`, bound to this repository, `release.yml`/`release`, channel, target and lineage. DEV policy must permit unmerged DEV tags.

Naming grants no authority: missing, invalid or revoked host/standing authorization prevents deployment. Standing authorization persists until changed or revoked; it is not another human approval for every version tag. No deployment activation flags, per-PR configuration, manual release dispatch, recurring GitHub changes or extra actor database are required. The optional read-only host diagnostic has its own separate opt-in, never used by release jobs. This implementation changes no live GitHub settings or infrastructure.

### Toolkit integration and host trust

The merged [verified-workflow contract at 7f814ef](https://github.com/clcdpc/clc-deployment/blob/7f814ef838abcfd12ade494c429918657598c35d/docs/verified-workflow-integration.md) defines the boundary in [AutomaticDeployment.Adapter.ps1](../scripts/release/AutomaticDeployment.Adapter.ps1):

```powershell
Invoke-ClcIisDeployment -TargetId $target -StandingAuthorizationId $target `
    -ReleaseTag $tag -ArchivePath $verifiedInboxZip -VerifiedReleasePath $verifiedIdentityJson
```

The tagged deployment adapter creates unique, create-new ZIP/identity files inside the authenticated target inbox. The strict identity binds repository/tag/source, manifest/package hashes and exact producer run/attempt/job/artifact/transport. It is evidence, not an approval record. The toolkit enforces standing scope/revocation, baseline/lineage/tag-source binding, rebuild ordering and downgrade policy under its existing lock, then performs the existing IIS/backup/readiness lifecycle. No per-release approval writer, signing producer, attestation or Sigstore dependency is used.

The adapter rejects an installed module lacking the five-parameter Automated API. The **build-time** lock remains published `0.1.0-beta.3`; do not replace it automatically or assume that distribution contains the merged automated standing-deployment runtime. [clc-deployment PR #2](https://github.com/clcdpc/clc-deployment/pull/2) merged as `7f814ef838abcfd12ade494c429918657598c35d`, but compatible runtime publication and independent host installation have not been established here. Exact toolkit implementation/contract test evidence is recorded below. Compatible runtime publication, independent installation and real-host qualification remain prerequisites for routine deployment. Application releases cannot install/replace the runtime, enroll infrastructure, modify protected host configuration, authorize downgrades or authorize exceptional recovery.

Infrastructure supplies the reviewed nonsecret [deployment configuration](../deployment/deployment.example.json), protected host authorization/enrollment lock/evidence, and independently authenticated bootstrap module/tools/`source.commit`/retained distribution at `%ProgramData%\CLC\Deployment\Bootstrap\ssrs-web`. Bootstrap, active-host and build-toolkit pins are separate.

Keep application, Config, Keys, Inbox, control state, bootstrap, authorizations, backups and Logs physically disjoint. The workflow authenticates path/owner/ACL and complete retained toolkit bytes before bootstrap execution and holds leases through use. Authenticate PowerShell 7.6.6 separately at `C:\CLC\Tools\PowerShell\7.6.6\pwsh.exe`, protect its ancestors/module paths and runner temporary scripts, and independently provision IIS/.NET/TLS/accounts. Administrators remain a procedural trust boundary.

## Read-only deployment prerequisite validation

[diagnose-deployment.yml](../.github/workflows/diagnose-deployment.yml) is separate optional read-only work pinned to reviewed upstream source. It observes GitHub and host prerequisites without deploying, enrolling or repairing infrastructure. Its READY/NOT READY report is not authorization; missing or inconclusive evidence remains FAIL, UNVERIFIED or SKIPPED and cannot establish readiness.

The older pinned checker was written for split producer/consumer orchestration. Both release/deployment selectors now point to `release.yml`; legacy trigger, activation, group and shared-channel checks may remain FAIL, UNVERIFIED or SKIPPED. Do not add removed settings or weaken host protections to obtain READY. Workflow/admission regressions verify this architecture. The diagnostic never gates release jobs.

The diagnostic runs manually from protected master with the separate `SSRS_DEV_DIAGNOSTIC_RUNNER_READY` opt-in, using conventional DEV labels without group variables. Beta.3 host-reader compatibility and the enrollment environment discrepancy remain unresolved; actual host evidence is required. Ordinary read-only `GITHUB_TOKEN` may leave administrative APIs UNVERIFIED. No administrative token or new credential is introduced.

## SSRS-specific DEV preflight

Use the Setup Guide's [installation](https://github.com/clcdpc/clc-deployment/blob/master/docs/installation.md), [enrollment](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md), [deployment/recovery](https://github.com/clcdpc/clc-deployment/blob/master/docs/deployment.md) and [IIS verification](https://github.com/clcdpc/clc-deployment/blob/master/docs/iis-verification.md) instructions alongside this checklist. Execute host commands using the exact independently authenticated installed runtime and its matching contracts, not merely the build-time lock.

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
    -Mode RestorePrevious -Reason $nonsecretIncidentReference -RecoveryApprovalId $independentExactApproval
```

The synthetic fixture's AcceptCandidate case does not establish live RestorePrevious acceptance. Record actual host observations before changing any live status.

## Planned DEV and Production tag acceptance

**Both channels remain NOT RUN.** After independent GitHub/runtime provisioning, enrollment and successful approved baseline, establish standing authorization for each target. The seven-check pilot above is separately authorized commissioning/recovery work; its explicit approvals are not the routine tag procedure.

1. **DEV:** Select an unmerged commit containing workflow, deployment-script and application-owned configuration changes, without requiring a PR or master-file equality. Create and push a new permitted `vN.N.N-dev.N` tag at that exact commit. Observe hosted `release -> archive` success, an immutable canonical prerelease, then automatic `deploy` on the dedicated `ssrs-web-dev` runner/environment. Retain tag/object/source/workflow/run/attempt/job/artifact/transport/package/manifest/compiled identities, host outcome and exact readiness. Complete the [SSRS-specific preflight](#ssrs-specific-dev-preflight), [seven-check pilot](#planned-real-dev-seven-check-pilot) and [live application gates](operations/README.md#live-gate-checklist).
2. **Failure and ordering:** In separately authorized acceptance cases, verify build/test failure blocks publication, publication failure blocks deployment, beta/RC archive without privileged deployment, same-tag full-run rebuild preserves canonical assets and uses distinct build identity, newer eligible tags supersede older ones, and revoked standing policy/downgrades fail closed. Preserve host evidence and follow [exceptional recovery](#initial-cutover-baseline-and-exceptional-recovery) for failed mutation; do not move tags or repair canonical assets.
3. **Production:** Independently qualify the Production host/configuration/keys/identities and baseline, then enable its standing authorization. Select an exact commit contained in protected master history (including an older ancestor, not just HEAD), create/push a new stable `vN.N.N` tag and observe the same ordered hosted qualification/publication followed by `ssrs-web-production` deployment. Reconcile all identities, exact readiness, backups and application acceptance with the Production target; DEV evidence alone does not qualify Production. Synthetic admission must also refuse a stable source outside master before publication/deployment.

After commissioning, routine operation for either target is only creating and pushing its permitted version tag. Retain deployment and recovery evidence beyond temporary Actions artifact retention.

## Repeatable local checks

Offline GitHub API and host fixtures cover unmerged DEV workflow/script changes without PR lookups, stable ancestry, in-progress same-run qualification, failed/incomplete jobs, exact run/attempt/artifact identity, canonical compatibility, routing, supersession and authorization rejection. Transport/package ZIP hashes and a clean-process publisher test exercise actual validation/publication code. Generated evidence stays under ignored `artifacts/`; implementation creates no real version tags or Releases.

Local Windows verification passed locked restore/Release build and 1,432 .NET tests (including deployment compatibility), 28 tag admission/deployment checks, 13 archive/publisher checks, 22 ordinary/CLC Release packaging checks, 32 host safeguards and 13 diagnostic checks. Configuration publish guard, separate administrative artifact checks, locked npm restore, npm/NuGet audits, Actionlint 1.7.12 across every workflow, PowerShell/JavaScript syntax and generated-artifact policy/tests also passed. Documentation checks verified local links/anchors and six pinned upstream paths.

Prior Windows/toolkit integration evidence at `ea379fb` records 29 packaging and 12 combined checks against exact toolkit source `7f814ef838abcfd12ade494c429918657598c35d`; those interoperability checks were not rerun for this documentation-only alignment. They used actual NTFS changes and loopback readiness with synthetic IIS, covering Deployed/NoOp, same-tag packages, downgrade/older-build rejection and revoked policies. These passes establish no live GitHub publication, installed-toolkit availability or IIS/SSRS/SQL acceptance.

The [DEV and Production tag acceptance](#planned-dev-and-production-tag-acceptance), all seven live IIS checks and known-record SSRS report-boundary acceptance remain open. Production infrastructure is unchanged.

From the repository root:

```powershell
dotnet restore SsrsPortal.slnx --locked-mode
dotnet build SsrsPortal.slnx -c Release --no-restore
dotnet test SsrsPortal.slnx -c Release --no-build
pwsh -NoProfile -File scripts/release/Test-DevDeployment.ps1
pwsh -NoProfile -File scripts/release/Test-TagDeployment.ps1
pwsh -NoProfile -File scripts/release/Test-ReleaseArchive.ps1
pwsh -NoProfile -File scripts/release/Test-DeploymentReadiness.ps1 -ActionlintPath <approved-actionlint-1.7.12-executable>
pwsh -NoProfile -File scripts/release/Test-ReleasePackaging.ps1
```

Use the [README verification](../README.md#verification) for ordinary publish/browser checks and the [Windows compatibility CI job](../.github/workflows/verify.yml) for exact `win-x64` Release publish arguments. Pass both `-PublishDirectory` and `-OrdinaryPublishDirectory` to check those actual outputs. The packaging script's optional Windows/toolkit interoperability branch additionally needs `-SyntheticToolkitArchive` and `-SyntheticToolkitLock`. Never copy its synthetic pin into production configuration.

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
