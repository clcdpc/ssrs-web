# Commission SSRS Web DEV on devweb

Use this procedure for **devweb**. Select its IIS site, pool, portal hostname and accounts from actual infrastructure. The [Clc.Deployment setup guide](https://github.com/clcdpc/clc-deployment/blob/master/docs/setup-guide.md#setup-roadmap) describes the common host lifecycle. SSRS Web uses its existing application-owned release workflow and schema-1 deployment contracts. See the [application operations reference](operations/README.md).

**Live status:** enrollment, baseline, unattended DEV deployment and the seven-check IIS pilot remain **NOT RUN**. Synthetic tests do not establish live IIS, Entra, SSRS, SQL or delivery acceptance. Production requires separate commissioning and evidence.

- [1. Configure GitHub](#phase-1--configure-github)
- [2. Prepare devweb](#phase-2--prepare-devweb)
- [3. Configure SSRS Web](#phase-3--configure-ssrs-web)
- [4. Prepare bootstrap and enroll Clc.Deployment](#phase-4--prepare-bootstrap-and-enroll-clcdeployment)
- [5. Establish the first approved baseline](#phase-5--establish-the-first-approved-baseline)
- [6. Enable and qualify unattended deployments](#phase-6--enable-and-qualify-unattended-deployments)

## Phase 1 — Configure GitHub

Apply upstream [setup sections 2–6](https://github.com/clcdpc/clc-deployment/blob/master/docs/setup-guide.md#2-restrict-github-actions) to `clcdpc/ssrs-web`. Check inherited rules and runner admission in the [GitHub setup reference](https://github.com/clcdpc/clc-deployment/blob/master/docs/github-setup.md).

- Active repository-wide **Restrict actors**, covering all workflows and authorized release developers/automation.
- Two Active `v*` tag rulesets: **Restrict creations**, with authorized creators' Always allow bypass; separately **Restrict updates/deletions**, without routine creator bypass.
- Protected `master`, retaining existing review rules and [CI checks](../.github/workflows/verify.yml), blocking force pushes and restricting deletion.
- Release immutability enabled before publication; canonical application Releases must report Boolean `immutable=true`.
- Environment `ssrs-web-dev`: **No restriction**, no reviewers, timers or extra gates.
- Dedicated Windows x64 runner with `[self-hosted, Windows, X64, ssrs-web-dev]` under the elevated deployment identity. Prefer a repository-scoped runner. For an organization-scoped runner, verify repository access and effective admission for eligible tagged workflows. Reserve it for trusted tagged deployments. Tagged deployment code and validators execute elevated.
- The existing [release.yml](../.github/workflows/release.yml) runs on version-tag pushes. Hosted `release` builds/tests and packages, hosted `archive` publishes, and dedicated `deploy` runs only after successful publication. The optional [setup diagnostic](../.github/workflows/diagnose-deployment.yml) is GET-only and cannot inspect devweb or gate deployment.

The November 18, 2026 read-only check found separate active creation and update/deletion rulesets. `ssrs-web-dev` still reported a custom policy allowing only the `master` branch. Verify current settings in GitHub and select **No restriction** if that policy is still present. Verify effective actor and `master` protections, Release immutability and runner admission with an administrator.

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

Reuse a compatible **dedicated** site/root/pool or provision new resources under infrastructure approval. Set the site root to the canonical `Application` path in Phase 3 and use the approved explicit local IP, HTTPS hostname and SNI certificate. Infrastructure configures a conventional AD pool and its credentials before enrollment. Enrollment can create a missing pool only for an approved, preinstalled managed service account. An occupied root requires [separately authorized legacy cutover](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#optional-legacy-cutover), a stopped pool with zero workers and a verified legacy backup before deleting approved originals.

Apply the [IIS settings and shutdown policy](operations/README.md#host-setup). Confirm SSRS/Entra HTTPS connectivity and runtime-account SSRS permissions. Keep subscriptions and catalog monitoring disabled until their SQL and, for subscriptions, Postmark prerequisites are qualified.

## Phase 3 — Configure SSRS Web

Application ID `ssrs-web` determines the **`C:\ProgramData\clc-ssrs-web`** directory on both separate servers. The native Windows known-folder API resolves the ProgramData parent. `ssrs-web-dev` remains the DEV deployment target, GitHub environment, runner label and enrollment/standing authorization ID. If a former target-derived directory exists, have infrastructure inspect it and obtain separate cutover authority before changing it. Preserve configuration, keys, logs and protected deployment evidence in place for review.

| Child | Purpose and runtime access |
| --- | --- |
| `Application` | Managed release; read/execute only |
| `Config` | Protected `application.json` and nonsecret `deployment.json`; read/traverse only |
| `Keys` | Persistent user-DPAPI ring; runtime read/write |
| `Inbox` | Approved packages/evidence; deployment-controlled, no runtime writes |
| `Logs` | Sensitive persistent logs; runtime Modify for rotation/retention |
| `Deployment` | Infrastructure-controlled `bootstrap`, `enrollment`, `toolkit`, `control` and `locks`; see [protected layout and provisioning](operations/README.md#enrollment-approval-inputs) for creation order |

Protect the parent against runtime replacement/deletion of these physically disjoint siblings. Follow [initial NTFS provisioning](operations/README.md#initial-ntfs-provisioning) for `Config`, `Keys` and `Logs`, or inspect existing resources under independent authority. Use the [configuration provisioning procedure](operations/README.md#external-configuration-provisioning) to copy the reviewed example without overwriting an existing file or use the supported first-run bootstrap. Configure the approved hosts, SSRS catalog policy, Entra tenants and client, revision, key path and logging. Replace every placeholder and protect credential-bearing files and backups.

DEV's runtime environment is **`Production`**. Preserve Data Protection application name `clc-ssrs-web`, compatibility ID `ssrs-web-dpapi-user-v1`, Windows **user-scope** DPAPI and IIS profile loading. Follow [persistent key provisioning](operations/README.md#persistent-data-protection-keys). Every edit or secret rotation needs a new `Deployment.ConfigurationRevision` and restart. See the [configuration reference](operations/README.md#configuration-reference) and [Entra setup](operations/README.md#multi-tenant-entra-onboarding-and-global-catalog-policy).

## Phase 4 — Prepare bootstrap and enroll Clc.Deployment

Use target and enrollment authorization ID **`ssrs-web-dev`**. Independently authenticate and approve a format-3 distribution that supports the application ID-based layout and SSRS Web's schema-1 deployment API. The published [Clc.Deployment 0.1.0-beta.5 release](https://github.com/clcdpc/clc-deployment/releases/tag/v0.1.0-beta.5) supplies that distribution.

```text
Source commit:       785511c6ba8b60afddf0722298f96f02ba163a01
Distribution asset:  761701793
Archive SHA-256:     8f2b3eb22c2aa1749fe7d002866b91d4ec0b247010e6311f804351655e21bee4
```

The upstream beta.5 Release reports `immutable=false`. Independently verify the archive digest and authenticate the exact source and verifier before use. The [build-time lock](../deployment/toolkit.lock.json) records packaging inputs. Infrastructure approves the host runtime separately. The ASP.NET Core companion remains pinned to beta.3.

1. Follow [initial bootstrap provisioning](https://github.com/clcdpc/clc-deployment/blob/master/docs/installation.md#initial-bootstrap-provisioning). In the linked native layout example, set `$applicationId = 'ssrs-web'`, `$targetId = 'ssrs-web-dev'` and `$approvedVersion` to the independently approved version. Prepare the protected namespace and place the authenticated bootstrap under `Deployment\bootstrap`. On a fresh host leave `Deployment\toolkit`, `Deployment\control` and `Deployment\locks` absent for Apply.
2. Prepare `Config\deployment.json` from the [SSRS enrollment worksheet](operations/README.md#enrollment-approval-inputs) using actual devweb values. Have the selected deployment account run the upstream [read-only observation and candidate procedure](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#3-collect-observations-and-prepare-the-candidate). Infrastructure independently reviews the observations, exact config bytes and permitted operations, then [approves and installs the protected handoff](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#4-independently-approve-and-install-the-handoff).
3. In a fresh elevated approved PowerShell process as the admitted deployment account, run [Inspect and Plan](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#5-inspect-plan-apply-and-verify) using that handoff. Use the native layout from the upstream [host preparation procedure](https://github.com/clcdpc/clc-deployment/blob/master/docs/host-enrollment.md#1-inspect-and-prepare-the-host) to set `$bootstrapRoot` and `$hostDeploymentJson`.

   ```powershell
   $enrollment = & "$bootstrapRoot\tools\host\Get-ClcHostEnrollment.ps1" `
       -DeploymentConfigurationPath $hostDeploymentJson -AuthorizationId ssrs-web-dev
   $plan = & "$bootstrapRoot\tools\host\Initialize-ClcHostEnrollment.ps1" `
       -DeploymentConfigurationPath $hostDeploymentJson -AuthorizationId ssrs-web-dev -Operation Plan
   $plan | Select-Object HostState, PlannedOperations, ResourceKindsToCreate, NextAction
   ```

4. Compare the plan with the approved observations, resource scope and authorization. For an authorized `ToolkitNotInitialized` or `TargetNotEnrolled` plan, run Apply with the independently approved archive. Apply installs and activates the initial toolkit and publishes the enrolled target. A Ready target skips Apply. For `Incomplete`, `DriftDetected` or `RecoveryRequired`, preserve evidence and reconcile through infrastructure before proceeding.

   ```powershell
   & "$bootstrapRoot\tools\host\Initialize-ClcHostEnrollment.ps1" `
       -DeploymentConfigurationPath $hostDeploymentJson -AuthorizationId ssrs-web-dev `
       -Operation Apply -DistributionPath "$bootstrapRoot\distribution.zip"
   & "$bootstrapRoot\tools\host\Get-ClcHostEnrollment.ps1" `
       -DeploymentConfigurationPath $hostDeploymentJson -AuthorizationId ssrs-web-dev
   ```

5. Require `HostState=Ready` and `ReadyForDeployment=true`. Compare the installed inventory, archive and protected `activeToolkit` pin with the approval using [installed-toolkit verification](https://github.com/clcdpc/clc-deployment/blob/master/docs/installation.md#verification-and-activation). Infrastructure must set `AutoStart=true` on any pool created by enrollment before deploying. The first application release still needs its own exact approval.

## Phase 5 — Establish the first approved baseline

1. Select an authenticated DEV release at an exact tag and commit. If a release must first be produced, use the [tag commands in Phase 6](#phase-6--enable-and-qualify-unattended-deployments) under the baseline's release scope. Hosted `release` and `archive` can publish before standing authorization exists. Expect downstream deployment refusal, or a queued job if the runner is offline. Cancel any pending deployment for this run before manual commissioning. Confirm publication succeeded even if the overall run failed downstream.
2. On a trusted review workstation, confirm the exact producer attempt and immutable canonical publication using the [baseline evidence worksheet](operations/README.md#baseline-approval-inputs). Create a new protected absolute `$reviewDirectory` for that attempt, then download its artifacts with authenticated read-only access.

   ```powershell
   # PowerShell 7.6.6. $runId/$attempt and $artifactId come from authenticated run/artifact metadata.
   $ErrorActionPreference = 'Stop'
   $PSNativeCommandUseErrorActionPreference = $true
   gh run download $runId --repo clcdpc/ssrs-web --name "ssrs-web-release-$runId-$attempt" --dir "$reviewDirectory\package"
   gh run download $runId --repo clcdpc/ssrs-web --name "ssrs-web-provenance-$runId-$attempt" --dir "$reviewDirectory\provenance"
   # Retain the outer release-artifact ZIP too; native redirection preserves bytes in this PowerShell.
   gh api "repos/clcdpc/ssrs-web/actions/artifacts/$artifactId/zip" > "$reviewDirectory\release-transport.zip"
   Get-FileHash -LiteralPath "$reviewDirectory\release-transport.zip" -Algorithm SHA256
   ```

   Compare the outer ZIP hash with the authenticated artifact service digest and provenance `artifactTransportSha256`. Confirm that it contains the same `release.zip` as the extracted download. Verify the inner ZIP and manifest separately, reconcile compiled identity and recheck tag and peeled source after download. Reject expired, wrong-attempt or conflicting artifacts.

3. Independently approve the exact package, target and configuration compatibility under the upstream [schema-1 approval contract](https://github.com/clcdpc/clc-deployment/blob/master/docs/contracts.md#existing-protected-authority-and-preflight). Infrastructure selects the approval ID and lineage and enables the independently reviewed record. Integrity inspection and synthetic fixture records carry no deployment authority.
4. Install the protected `Deployment\toolkit\policy\approvals\<approvalId>.json` and stage the unchanged approved ZIP as `Inbox\<approvalId>.zip`. Recompare its SHA-256 with the approval. In a fresh elevated admitted deployment process, import the verified active manifest:

   ```powershell
   # $root is the native ProgramData application directory and $approvalId is the exact approved ID.
   # $activeManifest is the independently verified activeToolkit.moduleManifestPath.
   Import-Module $activeManifest
   Test-ClcRelease -ArchivePath "$root\Inbox\$approvalId.zip"
   $result = Invoke-ClcIisDeployment -TargetId ssrs-web-dev -ReleaseApprovalId $approvalId
   $result
   ```

5. Require Deployed, exact anonymous HTTPS `/health/ready`, Started pool with one worker, full installed inventory, generation 1 and a verified empty baseline backup for a fresh target, and matching Completed journal and `attempts/<attemptId>/deployment.json`. Retain approval, original transport ZIP, package/provenance, installed pin, readiness, backup index/current/journal/attempt and IIS observations beyond temporary Actions retention. See [state and readiness contracts](operations/README.md#deployment-contracts-and-verification).

Stop on ambiguous mutation; follow [explicit recovery](operations/troubleshooting.md#deployment-failure-and-explicit-recovery). Application-file recovery does not restore configuration, keys or SQL.

## Phase 6 — Enable and qualify unattended deployments

After the approved baseline succeeds, infrastructure independently approves and installs `Deployment\toolkit\policy\approvals\standing\ssrs-web-dev.json`. Use the [SSRS schema-1 standing authorization fields](operations/README.md#standing-authorization-inputs) and the upstream [protection and read-back procedure](https://github.com/clcdpc/clc-deployment/blob/master/docs/automated-deployments.md#configure-standing-authorization).

Use **schema 1** without `sourcePolicy`, matching the [SSRS adapter](../scripts/release/AutomaticDeployment.Adapter.ps1). The [tag verifier](../scripts/release/TagRelease.Common.ps1) already admits unmerged DEV workflow, script and application configuration changes and requires protected `master` ancestry for stable tags.

Verify the runner is online and admitted, publication prerequisites are met and the protected host state matches the approved baseline. Review the [IIS interruption and recovery limits](operations/troubleshooting.md#deployment-failure-and-explicit-recovery) before enabling routine use. For qualification, select an unmerged commit with workflow, deployment-script and application configuration changes, then push an unused newer DEV tag at that exact commit.

```powershell
# $sourceCommit is the verified intended full source commit.
git tag v1.2.3-dev.4 $sourceCommit
if ($LASTEXITCODE -ne 0) { throw 'DEV tag creation failed.' }
git push origin v1.2.3-dev.4
```

Observe hosted build/test, immutable publication, then automatic deployment on the dedicated runner. Retain exact tag, source, workflow, run/attempt, producer job, artifact, transport, package, manifest and compiled identities with the deployment result. Match installed inventory and HTTPS readiness to the approved release and configuration revision. Routine operation is a permitted tag push without dispatch, extra approval, activation or routing variables, or PR admission. See [tag bounds and reruns](operations/README.md#release-and-toolkit-reference).

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

Use the upstream [seven-check IIS procedure](https://github.com/clcdpc/clc-deployment/blob/master/docs/iis-verification.md#seven-checks) and the [SSRS recovery procedure](operations/troubleshooting.md#deployment-failure-and-explicit-recovery). Synthetic AcceptCandidate evidence does not establish live RestorePrevious acceptance.

### Unattended DEV and application qualification

- Retain the results from [Phase 6](#phase-6--enable-and-qualify-unattended-deployments), including the tag object and peeled source commit.
- Separately qualify build/test failure blocking publication, publication failure blocking deployment, beta/RC without deployment, same-tag full-run rebuild, supersession, revoked standing policy and downgrade refusal. Preserve evidence. Published tags and canonical assets stay fixed.
- Complete the [live application gates](operations/README.md#live-gate-checklist), logging, optional services and [known-record report-boundary acceptance](date-parameter-boundary-acceptance.md); synthetic parsing/SOAP does not close them.

### Production remains separate

Commission Production on its separate server using the same application directory and independent identities, configuration, keys, runtime approvals and evidence. `ssrs-web-production` is its target, GitHub environment, runner label and enrollment/standing authorization ID. Stable `vMAJOR.MINOR.PATCH` requires protected `master` ancestry, including older ancestors. Establish its own approved baseline before standing authorization and repeat tag, real-IIS recovery and application acceptance there.
