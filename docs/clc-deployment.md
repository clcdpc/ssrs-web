# SSRS Web Deployment and Commissioning Guide

Commission DEV first, then repeat the procedure on the dedicated Production server. Each host has its own runner, configuration, permissions and deployment state. Use the approved identities for each host. The runner service uses a deployment account with an enabled local Administrators token. The IIS pool uses a distinct nonadministrator AD service account.

Live commissioning and application acceptance remain open. Use the checks in [phase 9](#9-verify-live-application-acceptance) to record the results as you complete them.

## 1. Verify GitHub configuration

Use repository administrator access in `clcdpc/ssrs-web`. DEV uses `ssrs-web-dev` and Production uses `ssrs-web-production`. Use each logical identifier as the deployment target ID, GitHub environment name and additional runner label. Use the actual Windows hostname as the GitHub runner name.

1. Under **Settings → Rules → Rulesets**, verify that active rules for `master` require a pull request, block force pushes and protect the branch from deletion.
2. Check that `mock` and `windows-deployment-compatibility` are required status checks and **Required approvals** is `0`. [GitHub ruleset reference](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets)
3. Verify the active tag creation ruleset for `v*` restricts creation to authorized release actors.
4. Verify the separate `v*` tag protection ruleset blocks updates and deletion, including for actors permitted to create version tags.
5. Under **Settings → General → Releases**, verify **Enable release immutability** is selected. [GitHub release immutability settings](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes)
6. Under **Settings → Environments**, open or create both target environments and verify these settings for each. [GitHub environment settings](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments)

   - **Deployment branches and tags** is **No restriction**.
   - **Required reviewers** and **Wait timer** are unset.

7. Under **Environment variables** in each environment, verify `CLC_RUNTIME_ACCOUNT` contains the full Windows logon name of the approved IIS worker for that host. [GitHub configuration variables](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-variables#creating-configuration-variables-for-an-environment)

Proceed when both environments match. The tag rules and workflow source checks authorize releases. The environments supply configuration for both protected-branch initialization and unattended tag deployment.

## 2. Prepare the Windows server

Use an elevated x64 Windows PowerShell console on the intended host.

1. Verify that the host runs Windows Server 2022 x64 (build `20348` and `ProductType` `3`).

   ```powershell
   Get-CimInstance -ClassName Win32_OperatingSystem |
       Select-Object Caption, BuildNumber, ProductType, OSArchitecture
   ```

2. Install IIS and IIS Management Scripts and Tools.

   ```powershell
   Install-WindowsFeature -Name Web-Server, Web-Scripting-Tools -IncludeManagementTools
   ```

3. Install the x64 .NET 10 Hosting Bundle after IIS. Repair the bundle if it was installed before IIS. [Microsoft Hosting Bundle instructions](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0)
4. Verify that the x64 .NET host lists stable `10.0.x` versions of `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App`. Verify IIS registers `AspNetCoreModuleV2` and the DLL at its registered `image` path is x64 with file major version `20`.

   ```powershell
   $dotnet = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet\dotnet.exe'
   & $dotnet --list-runtimes

   $appCmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
   & $appCmd list config /section:system.webServer/globalModules |
       Select-String -SimpleMatch 'AspNetCoreModuleV2'
   ```

5. Install or verify stable x64 PowerShell `7.6.6` or newer within major version `7`. For a new installation, use the global x64 installer with its PATH option. Keep the installation and machine PATH under administrative control. [Microsoft PowerShell installation instructions](https://learn.microsoft.com/en-us/powershell/scripting/install/install-powershell-on-windows?view=powershell-7.6)
6. Open a new console and verify that `pwsh` resolves to the approved global installation. Phase 5 confirms resolution in the service job.

   ```powershell
   Get-Command pwsh -CommandType Application | Select-Object -ExpandProperty Source
   ```

7. Confirm outbound HTTPS connectivity on port 443 to the required GitHub Actions destinations through the host's approved network or proxy configuration. [GitHub runner connectivity requirements](https://docs.github.com/en/actions/reference/runners/self-hosted-runners#communication)

## 3. Register or verify the GitHub runner

The approved repository-scoped runner configuration trusts eligible workflows in `clcdpc/ssrs-web`. Their jobs run under the deployment service account with local administrator privileges on the host. This is an accepted operational security tradeoff.

For a fresh installation, confirm there is no matching repository runner or configured local installation. If one exists, use the conditional procedure below.

1. On the Windows server, record the hostname returned by either command.

   ```powershell
   hostname
   # PowerShell alternative
   $env:COMPUTERNAME
   ```

2. In `clcdpc/ssrs-web`, open **Settings → Actions → Runners → New self-hosted runner**. Select **Windows** and **x64**. [GitHub runner registration](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners#adding-a-self-hosted-runner-to-a-repository)
3. In an elevated PowerShell console, follow GitHub's download, checksum and extraction instructions into the dedicated runner installation directory. After a supported removal, use the existing installation.
4. From that directory, run GitHub's generated `config.cmd` registration command with the repository URL and short-lived token. Use these settings at the prompts.

   - **Scope** is repository `clcdpc/ssrs-web`.
   - **Runner group** is **Default**. Where **Workflow access** is shown, use **All workflows**.
   - **Runner name** is the hostname from step 1, for DEV and Production alike.
   - **Work folder** is `_work`.
   - **Additional label** is the target's runner label from phase 1. GitHub adds `self-hosted`, `Windows` and `X64`.
   - **Run as a service** is **Yes**. Enter the approved deployment account and its password at the service prompts.

Proceed to phase 4 once the service is installed.

### Existing runner only

1. Match the GitHub entry to the local installation. Inspect its `.runner` file for the runner name and repository URL. Check Windows Services for a service pointing into that installation.
2. If the matching service is configured, verify it in phase 4 and continue with that registration. Resolve any repository, hostname or installation mismatch with the infrastructure owner before proceeding.
3. If the matching registration lacks a complete service setup, wait for any job to finish and stop any interactive session for that runner. On the runner's GitHub details page, select **Remove**.
4. From the verified installation directory in an elevated console, run the Windows `config.cmd remove` command shown by GitHub with its removal token. It removes the repository registration, local runner configuration and any service managed by that installation. Keep the extracted runner, `_work` and unrelated files. [GitHub runner removal](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/remove-runners#removing-a-runner-from-a-repository)
5. Return to registration step 2 for a fresh token. Choose Windows service operation during configuration, then verify the service. [GitHub Windows service setup](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/configure-the-application?platform=windows)

## 4. Verify the runner service

1. Open **Services** on the deployment host and locate the GitHub Actions runner service.
2. Confirm **Path to executable** points into the intended runner installation.
3. Confirm **Log On As** is the approved deployment account.
4. Confirm the service is **Running**. Start the matching service if needed.
5. In the repository's **Settings → Actions → Runners**, confirm the runner is online and shows **Idle** when free. [GitHub runner status](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/monitor-and-troubleshoot#checking-the-status-of-a-self-hosted-runner)
6. Compare the registered runner name with the Windows hostname from phase 3.
7. Verify all four labels. Only the intended host should carry its target label. Both host workflows select on `self-hosted`, `Windows`, `X64` and that target label.
8. Check the PowerShell path and version locally. If the installation or machine PATH changed, restart the runner service when idle and recheck resolution in the job.

   ```powershell
   pwsh -NoLogo -NoProfile -NonInteractive -Command 'Get-Process -Id $PID | Select-Object -ExpandProperty Path
       $PSVersionTable.PSVersion.ToString()
       [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture'
   ```

9. Use **Preview** in phase 5 for the runner-service checks. In **Set up job**, match the runner and machine names to the hostname. In **Run the requested upstream host operation**, match the shell path to the approved installation. The job verifies the PowerShell version policy, x64 process and elevated administrator token.

## 5. Preview and initialize IIS

Start with the verified runner online. Have the intended IIS names and approved portal DNS hostname available.

1. In **Actions**, open **Initialize Deployment Host** and choose **Run workflow**. [Initialization workflow](../.github/workflows/initialize-deployment.yml)
2. Select protected `master` in the branch selector.
3. Select **DEV** or **Production** for the intended host.
4. Select **Preview**.
5. Enter `site-name`. Use 1–64 ASCII letters, digits, periods, underscores, hyphens or internal spaces. Begin with a letter or digit and omit a trailing period or space.
6. Enter `pool-name`. Use 1–64 ASCII letters, digits, periods, underscores or hyphens. Begin with a letter or digit.
7. Enter `readiness-uri` as `https://<portal-hostname>/health/ready`, using the approved DNS hostname and the port if it differs from 443. The URI is at most 256 characters, with no credentials, query or fragment.
8. Run **Preview**.
9. Review the job identity from phase 4 and the run summary. Preview is read-only.

   - `Preview` reports the planned paths, stopped unbound IIS resources and pool settings. Proceed to **Initialize**.
   - `AlreadyInitialized` means the inputs match the existing recorded host. Verify its site, pool and root. Continue with configuration review, or use phase 7 to validate an already deployed host.
   - `Blocked` or a failed run requires review of the findings and local evidence before proceeding. Preserve any existing IIS resources, files and records while the administrator reconciles the conflict.

10. For a fresh host, run **Initialize** from protected `master` with the same target, site, pool and readiness URI. Expect `InitializedNeedsConfiguration`. A matching repeat reports `AlreadyInitialized`. Preserve any partial scaffold if initialization fails.
11. Verify the fresh scaffold on the host before adding configuration.

    - The site root points to `Application` in the native ProgramData `clc-ssrs-web` directory and uses the dedicated pool.
    - `Application`, `Config`, `Keys`, `Logs`, `Inbox` and `Deployment` exist as siblings. `Deployment` contains the host and initial state records.
    - The site and pool are stopped, both automatic startup flags are disabled and the site has zero bindings.
    - The pool uses `ApplicationPoolIdentity` until configured. It has No Managed Code, Integrated pipeline, x64, one worker, Load User Profile enabled and overlapping recycling disabled.

## 6. Configure the application and IIS

Keep the site and pool stopped with both automatic startup flags disabled through first-deployment validation. Basic portal operation requires the following IIS, configuration, Data Protection, Entra, SSRS and file logging settings.

### Configure IIS

1. In IIS Manager, open the dedicated pool's **Advanced Settings → Identity**, select **Custom account**, and enter the approved AD worker credentials. This identity must match `CLC_RUNTIME_ACCOUNT`.
2. Set the pool variable `ASPNETCORE_ENVIRONMENT` to `Production` on either target. Review effective environment settings for overrides. [IIS hosting settings](operations/README.md#host-setup)
3. Add one HTTPS binding with the exact readiness hostname and port. Select an unexpired, trusted certificate that matches the hostname and has a private key in the Local Computer store. Confirm that DNS resolves the portal hostname to the intended host from the runner.
4. Apply the mandatory site-specific ANCM shutdown policy in protected `applicationHost.config`. Set and lock `shutdownTimeLimit="60"` for the actual site. [ANCM policy XML and hosting settings](operations/README.md#host-setup)
5. Enable IIS Anonymous Authentication so ASP.NET Core can serve readiness and handle Entra sign-in. Confirm the one-worker in-process hosting settings and keep routine stdout logging, detailed errors and request/body tracing disabled. The release supplies the in-process `web.config`.

### Prepare storage and permissions

1. Resolve the application root on the server using the native ProgramData known folder. Keep this variable for configuration provisioning.

   ```powershell
   $appDataDirectory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'clc-ssrs-web'
   $appDataDirectory
   ```

2. Inspect the initialized directories and their ownership and inheritance in **Security → Advanced**. Use the [NTFS permission reference](operations/README.md#post-initialization-ntfs-permissions).
3. Grant the approved worker inheritable **Read and Execute** on `Application` and `Config`, and **Modify** on `Keys` and `Logs`. Use **Effective Access** to confirm these grants and that `Config` is read-only to the worker.
4. Verify administrative control of the root, `Inbox` and `Deployment`. Confirm the worker cannot modify or replace protected directories and is outside local Administrators, including through group membership.

### Provision external application configuration

Provision the file before deploying the managed application.

1. Obtain `src/SsrsPortal/application.Example.json` from a checkout of the approved source revision for the intended release, or save the raw file from that commit in GitHub. [Configuration example](../src/SsrsPortal/application.Example.json)
2. For a fresh installation, create `Config\application.json` in the initialized application root. Supply the absolute path to the example. The copy fails if the destination exists. [External configuration provisioning](operations/README.md#external-configuration-provisioning)

   ```powershell
   $reviewedExample = Read-Host 'Enter the absolute path to the reviewed application.Example.json'
   $configFile = Join-Path $appDataDirectory 'Config\application.json'
   [IO.File]::Copy($reviewedExample, $configFile, $false)
   ```

   **Existing configuration only.** Skip the copy and review the file in place. Remove the rejected legacy `Logging:File:Rotation` setting, including `"Rotation": "Daily"`. [Logging reference](operations/troubleshooting.md#persistent-file-logging)

3. Restrict the file to administrative control and worker read access before entering credentials. Store secrets in the protected external file. Keep them out of command lines, GitHub variables, source control and the managed `Application` directory.
4. Set `AllowedHosts` to the approved portal hostname or hostnames. Use the hostname from the IIS binding for this target.
5. Configure the SSRS endpoint and catalog. [SSRS access and catalog policy](operations/README.md#shared-catalog-policy)

   - Set `Portal:ReportServer` to the approved HTTPS `/ReportServer` endpoint.
   - Set `Portal:ReportAccessPolicy` to `GlobalBlocklist` and enter `AllowedRoots` and `ExcludedPrefixes`.
   - Keep `Portal:UseMock` and `Portal:TestAuth` false.
   - Confirm the actual worker account has view and execute permissions on the approved catalog and its dependencies.

6. Configure the approved organizational multi-tenant Entra registration. [Entra registration and assignment](operations/README.md#registration-and-assignment)

   - Register the HTTPS portal origin plus `/signin-oidc` as a Web redirect URI. Include the portal port if nondefault.
   - Confirm tenant consent and assignment of the exact `ReportViewer` role.
   - Set `Entra:ClientId`, `Entra:ClientSecret` and the approved organizational GUIDs in `Entra:AllowedTenantIds`.

7. Confirm outbound HTTPS access to Entra and SSRS from the IIS host.
8. Configure persistent Data Protection. [Persistent Data Protection keys](operations/README.md#persistent-data-protection-keys)

   - Set `DataProtection:KeyRingPath` to the absolute `Keys` directory under the native application root. Escape Windows backslashes in JSON.
   - Set `DataProtection:KeyStoreCompatibilityId` to `ssrs-web-dpapi-user-v1`.
   - Verify `LoadUserProfile=true` and `setProfileEnvironment=true` on the pool for user-scope DPAPI. Preserve the worker profile and any existing key ring across upgrades. The worker creates keys as needed and verifies the ring during startup.

9. Confirm `Logging:File:Enabled` is `true`. The worker verifies its write path in `Logs` at startup. [Persistent file logging](operations/troubleshooting.md#persistent-file-logging)
10. Confirm no `REPLACE_ME` placeholders remain. Set `Deployment.ConfigurationRevision` to a nonsecret revision code before saving. Change the revision on every configuration edit, including secret rotation. [Configuration reference](operations/README.md#configuration-reference)
11. Recheck effective permissions on `Config` and `application.json`. The worker needs read access and administrators retain write control. [Configuration protections](operations/README.md#protecting-and-changing-configuration)

### Optional services

Leave `CatalogMonitor:Enabled` and `Subscriptions:Enabled` false until their prerequisites are provisioned and verified. The portal can serve reports with both disabled.

- For SQL catalog monitoring, verify the SSRS schema, TLS and read-only grants before enabling it. [SQL catalog monitor](operations/catalog-monitor.md)
- For subscriptions, use the approved administrative setup artifact from the reviewed source. Provision the database, roles and Postmark settings and apply the subscription-specific IIS settings. Keep a new site and pool stopped with automatic startup disabled. Verify Hangfire and actual delivery after startup. [Deployed-host subscription provisioning](operations/subscriptions.md#deployed-host-subscription-provisioning)

## 7. Validate the deployment host

1. Open **Initialize Deployment Host → Run workflow** and select protected `master`.
2. Select the same target used for initialization.
3. Select **Validate**.
4. Leave `site-name`, `pool-name` and `readiness-uri` blank. The workflow reads the recorded values and uses the selected environment's `CLC_RUNTIME_ACCOUNT`.
5. Run the workflow.
6. Review the summary. For a first deployment, expect `ReadyForFirstDeployment` with the site and pool stopped. A previously deployed host that passes returns `ReadyForDeployment`.
7. Resolve findings and repeat **Validate**. If it reports `RecoveryRequired`, review the pending attempt through the [explicit recovery procedure](operations/troubleshooting.md#deployment-failure-and-explicit-recovery).

Validate is read-only. It checks the recorded site and pool, worker identity, HTTPS binding, nonempty external configuration file up to 4 MiB, runtime permissions, hosting environment and deployment state. A first-deployment pass requires both startup flags disabled. The deployment later validates application configuration and starts the worker to check Data Protection and logging. Live acceptance covers Entra sign-in, SSRS execution and any enabled SQL or delivery services.

## 8. Preflight and deploy an authorized release

Before an authorized release actor pushes a tag, confirm:

- The intended GitHub environment has the required settings and `CLC_RUNTIME_ACCOUNT`. Its runner is online with the correct target label.
- Host **Validate** passed and the required external configuration is provisioned.
- The exact intended source commit contains the expected tagged release workflow and consumer configuration. The intended version tag is unused locally and remotely.

Use the version format for the target you validated. For an upgrade, choose a version above the highest one the target has recorded. The version components range from 0 to 9999 and prerelease ordinals range from 1 to 9998, without leading zeroes.

- `vMAJOR.MINOR.PATCH-dev.N` deploys to DEV. Authorized DEV tags may select unmerged commits.
- `vMAJOR.MINOR.PATCH` deploys to Production and requires protected `master` ancestry.
- `vMAJOR.MINOR.PATCH-beta.N` and `vMAJOR.MINOR.PATCH-rc.N` publish artifacts only.

1. In a clone with `origin` pointing to `clcdpc/ssrs-web` for fetch and push, select the target that passed host validation and enter the full SHA of the reviewed commit. Use the same PowerShell session through step 6 and stop on any error.

   ```powershell
   $target = Read-Host 'Enter the validated target (DEV or Production)'
   switch -CaseSensitive ($target) {
       'DEV' { git fetch --no-tags origin }
       'Production' { git fetch --no-tags origin refs/heads/master:refs/remotes/origin/master }
       default { throw 'Enter DEV or Production.' }
   }
   if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the release source from origin.' }

   $sourceCommit = Read-Host 'Enter the full reviewed source commit SHA'
   $resolvedCommit = git rev-parse --verify --end-of-options "$sourceCommit^{commit}"
   if ($LASTEXITCODE -ne 0 -or $resolvedCommit -cne $sourceCommit) {
       throw 'Enter the full SHA of an available commit.'
   }
   ```

   If the approved DEV commit is missing after the fetch, fetch a branch (`refs/heads/<branch>`) or pull request head (`refs/pull/<number>/head`) on `origin` that contains it. Then repeat step 1 with the same reviewed SHA. If the commit is only in another clone, have the source owner push it to a branch you can fetch from `origin`.

   ```powershell
   $sourceRef = Read-Host 'Enter the full ref containing the approved DEV commit'
   git fetch --no-tags -- origin $sourceRef
   if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the DEV source ref.' }
   ```

2. Verify the commit and inspect its release workflow and consumer configuration, including the shared revision pin. Stop if either file is missing or the contents differ from the intended release.

   ```powershell
   git show --no-patch --format=fuller $sourceCommit
   if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the source commit.' }

   git show "$($sourceCommit):.github/workflows/release.yml" "$($sourceCommit):.clc/release.json"
   if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the release workflow and consumer configuration.' }
   ```

3. Enter the authorized tag for the selected target. Production checks ancestry against the freshly fetched `origin/master` before prompting for the tag. Use a full clone or deepen a shallow clone if Git lacks the history to verify ancestry.

   ```powershell
   $part = '(?:0|[1-9][0-9]{0,3})'
   $pattern = "\Av$part\.$part\.$part"
   switch -CaseSensitive ($target) {
       'DEV' { $pattern += '-dev\.(?!9999\z)[1-9][0-9]{0,3}' }
       'Production' {
           git merge-base --is-ancestor $sourceCommit refs/remotes/origin/master
           if ($LASTEXITCODE -ne 0) {
               throw 'Could not verify that the source commit is an ancestor of origin/master.'
           }
       }
       default { throw 'Select DEV or Production in step 1.' }
   }

   $tag = Read-Host "Enter the authorized $target tag"
   if ($tag -cnotmatch "$pattern\z") { throw "Invalid $target tag. Use the format and bounds above." }
   ```

4. Check the exact tag ref locally and remotely. Proceed only if both checks succeed and find no matching ref.

   ```powershell
   $tagRef = "refs/tags/$tag"
   $localTag = git for-each-ref --format='%(refname)' $tagRef
   if ($LASTEXITCODE -ne 0 -or $localTag) { throw 'Local tag check failed or the tag exists.' }

   $remoteTag = git ls-remote --refs origin $tagRef
   if ($LASTEXITCODE -ne 0 -or $remoteTag) { throw 'Remote tag check failed or the tag exists.' }
   ```

5. Create the tag at the verified commit.

   ```powershell
   git tag -- $tag $sourceCommit
   if ($LASTEXITCODE -ne 0) { throw 'Could not create the tag.' }
   ```

6. Push only that tag. Keep published tags fixed and use a new authorized version if release bytes must change.

   ```powershell
   git push --no-follow-tags origin "$($tagRef):$tagRef"
   if ($LASTEXITCODE -ne 0) { throw 'Tag push failed. Inspect both refs before retrying.' }
   ```

7. Open the **Release and deploy SSRS Web tag** run in **Actions**. Match its tag and source commit. [Tagged release workflow](../.github/workflows/release.yml)
8. Verify the GitHub-hosted `package` job qualified the application and uploaded its release package.
9. Verify publication succeeded and the GitHub Release for that tag is marked **Immutable**. Record the published `release.zip` SHA-256 digest. [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)
10. Verify the shared deployment ran unattended on the Windows runner named for the intended host and returned `Deployed`. For a failure, preserve the run summary and consult [explicit recovery](operations/troubleshooting.md#deployment-failure-and-explicit-recovery).
11. Confirm the site and pool are **Started**, one worker is running and both automatic startup flags are enabled. The deployment checks exact readiness before enabling startup and again afterward. [Release and toolkit reference](operations/README.md#release-and-toolkit-reference)
12. From the deployment host, request the exact HTTPS readiness URI anonymously. Expect HTTP 200, `readinessContractVersion` `1`, `status` `ready`, `applicationId` `ssrs-web` and `runtimeEnvironment` `Production`. Match `releaseId` and `sourceCommit` to the release and `configurationRevision` to the provisioned configuration. The release ID is the tag with its periods replaced by hyphens.

    ```powershell
    $readinessUri = Read-Host 'Enter the recorded HTTPS readiness URI'
    $response = Invoke-WebRequest -Uri $readinessUri -MaximumRedirection 0
    $response.StatusCode
    $response.Content | ConvertFrom-Json
    ```

## 9. Verify live application acceptance

Record these milestones as they occur:

1. **Host initialized.** Retain the initialization summaries and local IIS and storage observations. Verify Preview left IIS and storage untouched and the fresh scaffold was stopped and unbound.
2. **Host validated.** Retain the `ReadyForFirstDeployment` summary and the evidence for runtime permissions and stopped IIS resources.
3. **First deployment successful.** Retain the release digest, attempt ID, actual IIS startup and exact HTTPS readiness result from phase 8.
4. **Live application acceptance completed.** Record the evidence for each applicable check below.

- Confirm the effective AD worker identity, Entra sign-in and authorization, SSRS catalog access and report execution with the actual IIS topology. Verify render and export fidelity, accessibility, isolation and known-record date boundaries. [Application live gates](operations/README.md#live-gate-checklist)
- Confirm the worker can use the persistent DPAPI ring across recycle and verify the actual file logging and rollover behavior. [Persistent keys](operations/README.md#persistent-data-protection-keys) · [Live IIS and logging checks](operations/troubleshooting.md#live-iis-acceptance)
- Deploy a newer authorized release in DEV. Verify the retained prior Application backup and preservation of external Config, Keys and Logs under the same worker profile.
- Exercise failed deployment and explicit recovery under the controlled DEV conditions below.
- Where enabled, complete the live [SQL catalog monitor](operations/catalog-monitor.md) and [subscription and delivery](operations/subscriptions.md#rollback-and-dev-acceptance) checks.

### Controlled DEV recovery acceptance

Use an approved DEV acceptance window on an already commissioned DEV host with a known-good prior release. Confirm the test can be isolated from staff traffic, scheduled work and other deployments. Leave the recovery check open if that isolation is unavailable.

1. Run the approved readiness-failure scenario within the reserved window. Preserve the failed attempt ID, local evidence and observed IIS state.
2. Verify the retained backup for the prior release.
3. Confirm read-only **Validate** reports `RecoveryRequired`. The pending attempt blocks ordinary deployment.
4. Resolve the test-induced failure before restoring the prior release. Use the failed attempt ID and a nonsecret reason. [Explicit recovery procedure](operations/troubleshooting.md#deployment-failure-and-explicit-recovery)
5. Verify the recovered managed files, IIS startup state and exact readiness for the prior release. Retain the original attempt evidence.
