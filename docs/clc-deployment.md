# Commission SSRS Web on DEVWEB

Live Windows Server/IIS and SSRS Web commissioning status is **NOT RUN**. Start on DEVWEB. Repeat the procedure for Production on its own dedicated host with a separate runner, deployment account and IIS worker account.

## 1. Verify repository and environment settings

1. Verify that `master` requires pull requests and passing `mock` and `windows-deployment-compatibility` checks, with zero required approving reviews. Protect it from direct updates, force pushes and deletion.
2. Verify that repository tag protections restrict creation of `v*` version tags to authorized release actors and block updates and deletion. Enable immutable GitHub Releases before publishing.
3. In `clcdpc/ssrs-web` under **Settings → Environments**, configure `ssrs-web-dev` and `ssrs-web-production`. For both, set **Deployment branches and tags** to **No restriction** and leave **Required reviewers** and **Wait timer** unset. Under **Environment variables**, set `CLC_RUNTIME_ACCOUNT` to the actual IIS worker account for that target (for example, `DOMAIN\account`). The environments associate jobs with target-specific configuration. Tag protections and workflow source checks authorize releases, and the shared workflow routes them.

## 2. Prepare the server and register its runner

1. On the dedicated Windows Server 2022 x64 host, install IIS with IIS Management Scripts and Tools, then install the x64 .NET 10 Hosting Bundle. Install PowerShell globally and put `pwsh` on the machine PATH before starting the runner service. Use stable x64 PowerShell 7.6.6 or newer within major version 7 and keep the installation and PATH protected from ordinary users. Initialization and deployment reject prereleases and PowerShell 8 or later. Confirm the host can reach GitHub Actions over outbound HTTPS. [Runner connectivity](https://docs.github.com/en/actions/reference/runners/self-hosted-runners#communication)
2. In the `clcdpc/ssrs-web` repository, open **Settings → Actions → Runners → New self-hosted runner** and select **Windows** and **x64**. On the target server, run the generated download, verification, extraction and `config.cmd` commands in an elevated PowerShell terminal. Use the repository URL and short-lived registration token from that page. [GitHub runner registration](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners#adding-a-self-hosted-runner-to-a-repository)
3. Accept the **Default** runner group if prompted and use `_work` for the work folder. On DEVWEB, enter `devweb` for the runner name and `ssrs-web-dev` for the additional label. On the Production host, use its approved runner name and the `ssrs-web-production` label. Choose to run the runner as a Windows service. Enter the approved deployment account and its password in the service setup prompts. The service account needs an enabled local Administrators token. Use a separate nonadministrator AD service account for the IIS pool. Where GitHub exposes **Workflow access**, choose **All workflows**. The privileged runner is scoped to `clcdpc/ssrs-web`. This setup trusts authorized maintainers and eligible workflows in that repository with access to the host.
4. In Windows Services, verify that the runner service is **Running** under the approved deployment account. Start that service there if needed. Use its executable path to match it to the runner installation on the intended host. In the repository's runner list, confirm the runner is online with the standard `self-hosted`, `Windows` and `X64` labels and its target label. Verify that `devweb` has `ssrs-web-dev`. Both host workflows match all four labels. Keep each target label on its intended host.
5. Inspect PowerShell resolution in a session using the deployment account and the runner service's effective PATH. The first command lists the candidates and the second reports the path and version of the executable that actually starts.

   ```powershell
   Get-Command pwsh -CommandType Application -All |
       Select-Object -ExpandProperty Source

   pwsh -NoLogo -NoProfile -NonInteractive -Command '(Get-Process -Id $PID).Path; $PSVersionTable.PSVersion.ToString()'
   ```

   Run **Preview** below to verify the PowerShell version and administrator token from an actual runner service job.

## 3. Preview and initialize

1. In **Actions**, open **Initialize Deployment Host** and choose **Run workflow**. Select protected `master`, the target (**DEV** or **Production**) and operation **Preview**. Enter the dedicated IIS site and pool names. Use up to 64 ASCII letters, digits, periods, underscores or hyphens, beginning with a letter or digit. The site name also accepts internal spaces and cannot end with a period. Enter `https://<hostname>/health/ready` using the approved DNS hostname and include the port for a nondefault HTTPS binding. Submit the URI without credentials, query parameters or a fragment. [Initialization workflow](../.github/workflows/initialize-deployment.yml)
2. In the host job's **Set up job** log, confirm the runner and machine name match the intended target. For DEV, verify `devweb` on DEVWEB. Expect `Preview` with planned resources for an unused target or `AlreadyInitialized` for an exact match. If the run reports a name or directory conflict, preserve the existing IIS resources, configuration, keys and local records for administrator review before proceeding.
3. Repeat the dispatch with **Initialize** and the same inputs. Expect `InitializedNeedsConfiguration` on the first successful run or `AlreadyInitialized` for a matching repeat. On a fresh scaffold, check that `Application`, `Config`, `Keys`, `Logs`, `Inbox` and `Deployment` are under the native ProgramData `clc-ssrs-web` root. Verify that both the site and pool are stopped with automatic startup disabled and that the site has zero bindings. The pool uses No Managed Code, Integrated pipeline, x64, one worker, Load User Profile and disabled overlapping recycling.

## 4. Configure the stopped site and pool

1. In IIS Manager, set the pool's custom identity and credentials to the approved AD service account for this target. Set `ASPNETCORE_ENVIRONMENT` to `Production` on the pool for DEV as well as Production. Leave the site and pool stopped and both startup flags disabled.
2. Configure DNS so the readiness hostname resolves to the intended site from the runner host. Set a single dedicated HTTPS binding matching the hostname and port in the readiness URI, with an approved trusted certificate and private key in LocalMachine. Apply the [site-specific ANCM policy and IIS settings](operations/README.md#host-setup).
3. Apply the reviewed [post-initialization permissions](operations/README.md#post-initialization-ntfs-permissions). Provision [application.json](operations/README.md#external-configuration-provisioning) and the [persistent user-scope DPAPI key ring](operations/README.md#persistent-data-protection-keys).
4. Configure [Entra](operations/README.md#multi-tenant-entra-onboarding-and-global-catalog-policy), SSRS permissions for the worker identity and any enabled SQL or [subscription](operations/subscriptions.md#production-provisioning-and-deployment) dependencies.

## 5. Validate readiness

Dispatch **Validate** from protected `master` for the same target. Leave the site, pool and URI inputs blank. The workflow reads the recorded values and uses the selected environment's `CLC_RUNTIME_ACCOUNT` for the read-only host check. Resolve findings and rerun until it reports `ReadyForFirstDeployment` with both the site and pool stopped.

## 6. Deploy an authorized DEV tag

An authorized release actor pushes a new `vMAJOR.MINOR.PATCH-dev.N` tag at the intended source commit containing the release workflow and consumer configuration. A DEV tag can select an unmerged commit. The [release workflow](../.github/workflows/release.yml) qualifies the app on GitHub-hosted runners and publishes an immutable GitHub Release. The pinned shared workflow verifies the release and deploys it unattended on `devweb` using `ssrs-web-dev`. The first deployment validates the application configuration, starts the site and pool, verifies exact readiness, then enables both startup flags.

Stable `vMAJOR.MINOR.PATCH` tags require protected `master` ancestry and deploy to `ssrs-web-production`. Beta and RC tags publish artifacts only.

## 7. Record live DEV acceptance

1. Keep the Preview, Initialize and Validate summaries and the corresponding local IIS and permissions evidence. Confirm Preview was read-only by comparing IIS and storage before and after. Verify the fresh scaffold was stopped and unbound and that read-only validation returned `ReadyForFirstDeployment` after manual configuration.
2. Record the immutable release digest and successful attempt. Verify the site and pool are started with automatic startup enabled, and that anonymous readiness identifies the deployed release and configuration revision. [Release and toolkit reference](operations/README.md#release-and-toolkit-reference)
3. Deploy a newer authorized DEV tag. Verify the retained prior Application backup and that Config, Keys and Logs survive replacement under the same worker profile.
4. On an isolated target, record a failed readiness attempt and actual IIS state. Confirm the pending attempt blocks routine deployment. Verify the retained backup and recover with the attempt ID and a nonsecret reason. Check restored readiness and retained attempt evidence. [Explicit recovery procedure](operations/troubleshooting.md#deployment-failure-and-explicit-recovery)
5. Verify Entra, effective AD/SSRS identity, report execution and fidelity, and required SQL and subscription behavior. [Application live gate checklist](operations/README.md#live-gate-checklist)
