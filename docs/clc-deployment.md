# Commission SSRS Web DEV on devweb

Use this procedure for the Windows host **devweb**. Select its IIS site, pool, portal hostname and accounts from actual infrastructure; none is implied by the machine name. Generic mechanics belong to the upstream [Clc.Deployment setup guide](https://github.com/clcdpc/clc-deployment/blob/master/docs/setup-guide.md); application settings belong to the [operations reference](operations/README.md).

**Live status:** enrollment, baseline, unattended DEV deployment and the seven-check IIS pilot remain **NOT RUN**. Synthetic tests do not establish live IIS, Entra, SSRS, SQL or delivery acceptance. Production requires separate commissioning and evidence.

## Phase 1 — Configure GitHub

Apply the upstream [GitHub setup procedure](https://github.com/clcdpc/clc-deployment/blob/master/docs/github-setup.md) to `clcdpc/ssrs-web`:

- Active repository-wide **Restrict actors**, covering all workflows and authorized release developers/automation.
- Two Active `v*` tag rulesets: **Restrict creations**, with authorized creators' Always allow bypass; separately **Restrict updates/deletions**, without routine creator bypass.
- Protected `master`, retaining [existing CI checks](../.github/workflows/verify.yml), blocking force pushes/deletion.
- Release immutability enabled before publication; canonical application Releases must report Boolean `immutable=true`.
- Environment `ssrs-web-dev`: **No restriction**, no reviewers, timers or extra gates.
- Dedicated Windows x64 runner: `[self-hosted, Windows, X64, ssrs-web-dev]`, elevated deployment identity; repository-scoped preferred.
- Existing [release.yml](../.github/workflows/release.yml): tag push → hosted `release` → hosted `archive` → dedicated `deploy`. The optional [setup diagnostic](../.github/workflows/diagnose-deployment.yml) is GET-only, independent of deployment and cannot inspect devweb.

**Administrative corrections outstanding (observed November 17, 2026):** `Protect version tags` still combines creation/update/deletion restrictions with one Always bypass. Split it as above. `ssrs-web-dev` still has a custom deployment branch/tag policy; change it to No restriction. Documentation changes neither setting. Actor policy, effective master protections, Release immutability and runner admission still require administrative verification; inaccessible APIs are unverified.

## Phase 2 — Prepare devweb

Confirm Windows Server 2022 x64, IIS, .NET 10 Hosting Bundle/ANCM 20, trusted HTTPS certificate/binding/DNS and approved PowerShell **7.6.6** at `C:\CLC\Tools\PowerShell\7.6.6\pwsh.exe`. Protect PowerShell, installed helpers and runner temporary-script locations. Use separate elevated deployment and nonadministrator AD application identities.

Inspect actual IIS resources and certificate selectors in IIS Manager or with these read-only commands in **Windows PowerShell 5.1** (native WebAdministration/IIS provider). Use PowerShell 7.6.6 separately for toolkit commands. Retain observations in restricted infrastructure records:

```powershell
Import-Module WebAdministration
Get-Website | Select-Object Name, State, PhysicalPath, ApplicationPool
Get-WebApplication | Select-Object Site, Path, PhysicalPath, ApplicationPool
Get-WebBinding -Protocol https | Select-Object bindingInformation, certificateHash, certificateStoreName, sslFlags
Get-ChildItem IIS:\AppPools | Select-Object Name, State, managedRuntimeVersion, managedPipelineMode, enable32BitAppOnWin64
Get-ChildItem Cert:\LocalMachine\My | Select-Object Subject, Thumbprint, NotAfter
dotnet --list-runtimes
```

Reuse a compatible **dedicated** site/root/pool, or provision new resources under infrastructure approval. Conventional AD pool credentials must already be configured by infrastructure; enrollment creates a missing pool only for an approved, preinstalled managed service account. An occupied root requires the upstream [independently authorized first cutover](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#first-cutover-and-failure-handling), stopped with zero workers and a verified legacy backup.

Apply the [IIS settings/shutdown policy](operations/README.md#host-setup), [directory permissions](operations/README.md#initial-ntfs-provisioning) and [persistent-key procedure](operations/README.md#persistent-data-protection-keys). Confirm SSRS/Entra HTTPS connectivity and runtime-account SSRS permissions. SQL/Postmark access is needed when enabling optional features; keep subscriptions and catalog monitoring disabled initially.

## Phase 3 — Configure SSRS Web

Use the native ProgramData location, normally **`%ProgramData%\clc-ssrs-web-dev`**. Application/toolkit verification prevents relocation through environment overrides.

| Child | Purpose and runtime access |
| --- | --- |
| `Application` | Managed release; read/execute only |
| `Config` | Protected `application.json` and nonsecret `deployment.json`; read/traverse only |
| `Keys` | Persistent user-DPAPI ring; runtime read/write |
| `Inbox` | Approved packages/evidence; deployment-controlled, no runtime writes |
| `Logs` | Sensitive persistent logs; runtime Modify for rotation/retention |
| `Deployment` | Infrastructure-controlled bootstrap, toolkit, policies, approvals, enrollment and deployment state |

Protect the parent against runtime replacement/deletion of these physically disjoint siblings. Follow [configuration provisioning](operations/README.md#external-configuration-provisioning): copy the reviewed example without overwriting an existing file, or use the supported first-run bootstrap. Fill `Config\application.json` with approved `AllowedHosts`, HTTPS SSRS `/ReportServer`, roots/exclusions, `GlobalBlocklist`, Entra tenant GUIDs/client ID/protected secret, configuration revision, key path and reviewed logging settings. Replace every placeholder; protect credential-bearing files/backups.

DEV's runtime environment is **`Production`**. Preserve Data Protection application name `clc-ssrs-web`, compatibility ID `ssrs-web-dpapi-user-v1`, Windows **user-scope** DPAPI, persistent Keys permissions and IIS profile loading. Every edit/secret rotation needs a new `Deployment.ConfigurationRevision` and restart. `Clc.Configuration` loads one external snapshot without operational appsettings, user-secret or ordinary environment/command-line fallbacks. Use the [configuration reference](operations/README.md#configuration-reference) for options; separately qualify [subscriptions](operations/subscriptions.md#production-provisioning-and-deployment) and the [catalog monitor](operations/catalog-monitor.md) before enabling them.

## Phase 4 — Install and enroll Clc.Deployment

Use target/enrollment authorization ID **`ssrs-web-dev`**. Initial runtime example: published [Clc.Deployment 0.1.0-beta.4](https://github.com/clcdpc/clc-deployment/releases/tag/v0.1.0-beta.4), format 3. Release metadata, manifest and downloaded bytes were checked against:

```text
Source:            557192eff5eea651d6f1a393eb3b196053a985e5
Distribution SHA:  89459d9ad42e112aea475d51a596d944c683f957aa33cae1a3f0ad2d693a1654
Module inventory:  0c5ba7f3b1b884e2f9320b7e81fa7e3431b25c4aa25a255a8b6a1b83a2360e0d
```

The matching [build-time lock](../deployment/toolkit.lock.json) grants no runtime installation/activation authority. Upstream beta.4 reports `immutable=false`; authenticate exact pins independently. The separately pinned ASP.NET Core companion remains **beta.3**.

Map [deployment.example.json](../deployment/deployment.example.json) to `Config\deployment.json` using the [SSRS input worksheet](operations/README.md#enrollment-approval-inputs): application constants, devweb observations, authenticated toolkit facts and independent approvals. Never copy example SIDs/IIS names.

Follow upstream [first-import verification](https://github.com/clcdpc/clc-deployment/blob/master/docs/installation.md#trust-before-first-import), [bootstrap provisioning](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#trust-and-prerequisites), [observations/independent authorization](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#independent-initialization-authorization), then [Inspect/Plan/Apply/readiness](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#inspection-planning-and-apply). The worksheet identifies an upstream preapproval-observation documentation gap; infrastructure needs a reviewed collection procedure before executing enrollment.

After independently provisioning the protected handoff, run in fresh approved PowerShell:

```powershell
$root = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'clc-ssrs-web-dev'
$bootstrap = Join-Path $root 'Deployment\bootstrap'
$deploymentJson = Join-Path $root 'Config\deployment.json'
& "$bootstrap\tools\host\Get-ClcHostEnrollment.ps1" -DeploymentConfigurationPath $deploymentJson -AuthorizationId ssrs-web-dev
& "$bootstrap\tools\host\Initialize-ClcHostEnrollment.ps1" -DeploymentConfigurationPath $deploymentJson -AuthorizationId ssrs-web-dev -Operation Plan
# Only for approved missing resources and the exact reviewed plan:
& "$bootstrap\tools\host\Initialize-ClcHostEnrollment.ps1" -DeploymentConfigurationPath $deploymentJson -AuthorizationId ssrs-web-dev -Operation Apply -DistributionPath "$bootstrap\distribution.zip"
& "$bootstrap\tools\host\Get-ClcHostEnrollment.ps1" -DeploymentConfigurationPath $deploymentJson -AuthorizationId ssrs-web-dev
```

Require `HostState=Ready` and `ReadyForDeployment=true`; Ready skips Apply. Incomplete/DriftDetected/RecoveryRequired needs reconciliation, never Apply-based repair. Enrollment does not establish application readiness. Infrastructure must independently set newly created pools' AutoStart from false to true before deployment.

## Phase 5 — Perform the first deployment

1. Obtain an authenticated DEV release at an exact tag/commit. If necessary, use Phase 6's tag commands now: hosted `release`/`archive` can publish without standing authorization. Automatic `deploy` then refuses missing enrollment/policy/baseline; an offline runner can leave it queued. Cancel queued deployment before manual commissioning. Standing authority cannot establish its own baseline.
2. Verify successful hosted `release`/`archive` jobs for that run/attempt, immutable canonical publication, tag object/peeled source, compiled identity, producer/artifact identity and transport/package/manifest hashes. Download the selected attempt's artifacts to a new absolute review directory:

   ```powershell
   # PowerShell 7.6.6. $runId/$attempt and $artifactId come from authenticated run/artifact metadata.
   gh run download $runId --repo clcdpc/ssrs-web --name "ssrs-web-release-$runId-$attempt" --dir "$reviewDirectory\package"
   gh run download $runId --repo clcdpc/ssrs-web --name "ssrs-web-provenance-$runId-$attempt" --dir "$reviewDirectory\provenance"
   # Retain the outer release-artifact ZIP too; native redirection preserves bytes in this PowerShell.
   gh api "repos/clcdpc/ssrs-web/actions/artifacts/$artifactId/zip" > "$reviewDirectory\release-transport.zip"
   Get-FileHash -LiteralPath "$reviewDirectory\release-transport.zip" -Algorithm SHA256
   ```

   Compare the outer ZIP hash with the authenticated artifact service digest and provenance artifactTransportSha256; compare inner release.zip and manifest separately. Reject expired, wrong-run/attempt or conflicting artifacts.

3. Prepare an independent **schema-1 exact release approval** under the upstream [approval contract](https://github.com/clcdpc/clc-deployment/blob/master/docs/contracts.md#existing-protected-authority-and-preflight), using the [SSRS evidence mapping](operations/README.md#baseline-approval-inputs). Infrastructure selects the approval ID/lineage and publishes the enabled approval. Integrity inspection grants no authority; synthetic fixture identities are never operational approvals.
4. Protect `Deployment\toolkit\policy\approvals\<approvalId>.json`; stage the unchanged approved ZIP as `Inbox\<approvalId>.zip`. Verify the installed active pin from protected `host-policy.json`, then use its runtime:

   ```powershell
   # $activeManifest is the independently verified activeToolkit.moduleManifestPath.
   Import-Module $activeManifest
   Test-ClcRelease -ArchivePath "$root\Inbox\$approvalId.zip"
   $result = Invoke-ClcIisDeployment -TargetId ssrs-web-dev -ReleaseApprovalId $approvalId
   $result
   ```

5. Require Deployed, exact anonymous HTTPS `/health/ready`, Started pool/one worker, installed inventory, generation 1 for a fresh target, matching Completed journal and `attempts/<attemptId>/deployment.json`. Retain approval, package/provenance, installed pin, readiness, backup index/current/journal/attempt and IIS observations beyond temporary Actions retention. See [state/readiness contracts](operations/README.md#deployment-contracts-and-verification).

Stop on ambiguous mutation; follow [explicit recovery](operations/troubleshooting.md#deployment-failure-and-explicit-recovery). Application-file recovery does not restore configuration, keys or SQL.

## Phase 6 — Enable unattended DEV tags

After baseline success, independently create protected `Deployment\toolkit\policy\approvals\standing\ssrs-web-dev.json` under upstream [standing authorization](https://github.com/clcdpc/clc-deployment/blob/master/docs/automated-deployments.md#independent-infrastructure-setup). Bind `ssrs-web`, `ssrs-web-dev`, environment/channel `DEV`, `clcdpc/ssrs-web`, producer `.github/workflows/release.yml` / `release`, and the **baseline's actual lineage**.

Use **schema 1**, matching the [current adapter](../scripts/release/AutomaticDeployment.Adapter.ps1). Its [tag verifier](../scripts/release/TagRelease.Common.ps1) permits unmerged DEV workflow/script/configuration changes and requires protected master ancestry for stable tags; snapshots are not emitted. Schema-2 `sourcePolicy` requires schema-2 evidence, which this adapter does not emit. Preserve this existing source policy without adding flags.

Verify runner online/admitted, publication prerequisites and protected host state. Push a new unused, newer DEV tag at the intended commit:

```powershell
# $sourceCommit is the verified intended full source commit.
git tag v1.2.3-dev.4 $sourceCommit
git push origin v1.2.3-dev.4
```

Observe hosted build/test → immutable publication → automatic deployment; reconcile installed release/source/configuration identity and readiness. Routine operation is only tag creation/push. No dispatch, extra approval, activation/routing variable or PR admission is needed. See [tag bounds/reruns](operations/README.md#release-and-toolkit-reference).

## Separate DEV acceptance

**All live checks below remain NOT RUN.** Successful first deployment, unattended DEV qualification and complete failure/recovery acceptance are distinct milestones. Run disruptive cases separately in approved maintenance windows; ordinary setup does not require failure simulation.

### Seven-check real-IIS pilot

Use distinct healthy A/B. Independently approved application-owned C must pass validation and fail readiness **after replacement/startup**; preflight rejection or generic FailureApp does not qualify checks 5–7. Without C, leave them open.

| Check | Expected live result |
| --- | --- |
| 1. Initial A on authorized empty target | Record initial pool state; Deployed; exact A readiness/inventory; Started/one worker; verified empty baseline backup, generation 1, matching Completed journal/attempt |
| 2. Upgrade B | Exact B readiness/inventory; next generation; complete verified A backup/index; external configuration/keys preserved |
| 3. Healthy B repeat | NoOp with fresh readiness; unchanged application/current bytes, timestamps and generation; no stop/copy/start/stage/backup; new Completed attempt |
| 4. Configuration revision 2, apply B | Deployed despite identical release bytes; revision-2 readiness, next generation/backup. Separately prove changed bytes with old revision are refused |
| 5. Apply C | Bounded Clc.ReadinessFailed after replacement/startup; C not current; stage/manifest/backup retained, RecoveryRequired journal; bounded stop proves zero workers or records uncertainty; retry refused |
| 6. Verify backups | Earlier backups/indexes byte-identical; failed attempt's verified B backup matches approval/manifest/inventory; failure evidence and external configuration/keys preserved |
| 7. Explicit RestorePrevious to B | Independently authorized latest-attempt recovery with compatible revision-2 configuration/approvals; verified backup/manifest and helper exit before effects; exact B readiness/inventory; Started/one worker; next generation, before-recovery snapshot/terminal record; original evidence retained |

Use the Invoke/Restore commands in [upstream deployment/recovery](https://github.com/clcdpc/clc-deployment/blob/master/docs/deployment.md#operator-procedure) and the [SSRS recovery boundary](operations/troubleshooting.md#deployment-failure-and-explicit-recovery). Synthetic AcceptCandidate evidence does not establish live RestorePrevious acceptance.

### Unattended DEV and application qualification

- Tag an unmerged commit containing workflow, deployment-script and application configuration changes; retain exact tag/object/source/workflow/run/attempt/job/artifact/transport/package/manifest/compiled identities and installed outcome/readiness.
- Separately qualify failure ordering, immutable publication, beta/RC without deployment, same-tag full-run rebuild, supersession, revoked standing policy and downgrade refusal. Preserve evidence; never move tags or repair canonical assets.
- Complete the [live application gates](operations/README.md#live-gate-checklist), logging, optional services and [known-record report-boundary acceptance](date-parameter-boundary-acceptance.md); synthetic parsing/SOAP does not close them.

### Production remains separate

Independently provision Production identities/configuration/keys/runtime, enroll and approve its baseline before standing authority. Stable `vMAJOR.MINOR.PATCH` selects `ssrs-web-production` and requires protected master ancestry, including older ancestors. Repeat tag, real-IIS recovery and application acceptance there; DEV evidence does not qualify Production.
