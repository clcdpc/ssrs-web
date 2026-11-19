# Commission SSRS Web on DEVWEB

Live Windows Server/IIS and SSRS Web commissioning status is **NOT RUN**. Use this procedure on DEV first. Production follows the same process on its own dedicated server.

## 1. Prepare GitHub and the runners

- Require pull requests for all changes to `master` and require the `mock` and `windows-deployment-compatibility` status checks to pass. Set the required number of approving reviews to zero. Block direct updates, force pushes and branch deletion.
- Limit version-tag creation to authorized release actors, protect version tags from updates/deletion, and enable immutable GitHub Releases.
- Configure the following dedicated Windows x64 servers, labels and GitHub environments. Set the nonsecret `CLC_RUNTIME_ACCOUNT` environment variable to the expected IIS pool account on each target.

  | Target | GitHub environment and additional runner label | Permitted release tags |
  | --- | --- | --- |
  | DEV | `ssrs-web-dev` | `v*-dev.*` |
  | Production | `ssrs-web-production` | Stable version tags |

- Allow the protected `master` branch for initialization and the applicable release tags in each environment. Keep routine release deployments free of required reviewers and wait timers. Verify the tag policy itself restricts tag creation and mutation. The pinned deploy workflow independently checks stable tag ancestry.
- Put the runners in restricted organization or enterprise runner groups. Limit repository access to `clcdpc/ssrs-web` and select workflows explicitly. Allow only `clcdpc/ssrs-web/.github/workflows/initialize-deployment.yml@refs/heads/master` and `clcdpc/clc-deployment/.github/workflows/application-deploy.yml@e306d730d77c1a05868f124ab2719e71ae186499`. The shared workflow directly defines the tagged job. Keep the default `self-hosted`, `Windows` and `X64` labels plus the dedicated target label. Verify no unrestricted runner advertises either target label. Repository-scoped runners and label matching alone do not enforce workflow access. Keep the runners offline until these restrictions are in place. [GitHub runner group workflow access](https://docs.github.com/en/enterprise-cloud@latest/actions/how-tos/manage-runners/self-hosted-runners/manage-access)

## 2. Prepare each server

Install IIS with IIS Management Scripts and Tools and the x64 .NET 10 Hosting Bundle on Windows Server 2022. Register the dedicated runner service with an enabled elevated local Administrators token. Verify that its service PATH resolves to stable PowerShell 7.6.6 or later within 7.x and that ordinary users cannot modify the executable or its search path. Use a separate nonadministrator service account for the IIS worker.

## 3. Preview and initialize

1. In GitHub Actions open [Initialize Deployment Host](../.github/workflows/initialize-deployment.yml) and choose `master`, target **DEV**, operation **Preview**. Supply the actual IIS site and application pool names and the approved HTTPS URI ending in `/health/ready`. Supply a URI without credentials or query parameters.
2. Review the planned resources and any findings. A name or directory conflict needs administrator review before proceeding. Preserve existing sites, configuration, key material and evidence for any cutover. The initializer requires an unused target or a matching completed scaffold.
3. Repeat the dispatch with **Initialize** and the same values. Expect `InitializedNeedsConfiguration` or `AlreadyInitialized` for a matching repeat. Confirm the `Application`, `Config`, `Keys`, `Logs`, `Inbox` and `Deployment` directories are under the native ProgramData `clc-ssrs-web` root. Confirm that the site and pool are stopped with automatic startup disabled and no HTTP or HTTPS bindings. The pool uses No Managed Code, Integrated pipeline, x64, one worker, Load User Profile and disabled overlapping recycling.

## 4. Configure the stopped site and pool

1. In IIS Manager set the approved specific service identity and credentials. Set the runtime environment to **Production** for DEV as well as Production. Leave the site and pool stopped and both startup flags disabled.
2. Configure DNS, a single dedicated HTTPS hostname/port binding matching the readiness URI, and an approved certificate with its private key in LocalMachine. Configure the required [site-specific ANCM configuration and IIS settings](operations/README.md#host-setup).
3. Apply the reviewed [post-initialization permissions](operations/README.md#post-initialization-ntfs-permissions). Provision [application.json](operations/README.md#external-configuration-provisioning) and the [persistent user-scope DPAPI key ring](operations/README.md#persistent-data-protection-keys).
4. Configure [Entra](operations/README.md#multi-tenant-entra-onboarding-and-global-catalog-policy), SSRS permissions for the worker identity and any enabled SQL or [subscription](operations/subscriptions.md#production-provisioning-and-deployment) dependencies.

## 5. Validate readiness

Dispatch **Validate** from protected `master` for the same target. It uses the selected environment's `CLC_RUNTIME_ACCOUNT` and inspects the host without making changes. Resolve findings until it reports `ReadyForFirstDeployment`. A correctly configured first-deployment target still has a stopped site and pool.

## 6. Deploy an authorized DEV tag

An authorized release actor pushes a new `vMAJOR.MINOR.PATCH-dev.N` tag at the intended source commit. The [release workflow](../.github/workflows/release.yml) qualifies the app on hosted runners, publishes an immutable GitHub Release, and verifies it before deployment on the target runner. A DEV tag can select an unmerged commit. The first successful deployment starts the site and pool for readiness and enables both startup flags after readiness succeeds. Subsequent tags use the same unattended workflow.

## 7. Record live DEV acceptance

| Check | Evidence to retain locally | Status |
| --- | --- | --- |
| Preview and initialize | Preview leaves IIS and storage unchanged. Initialize creates the expected stopped, unbound site and pool. | NOT RUN |
| Manual setup and validation | Approved identity, HTTPS, Production runtime, external paths and effective ACLs. Read-only validation returns `ReadyForFirstDeployment`. | NOT RUN |
| First deployment | Immutable release digest, successful attempt, site and pool started with automatic startup enabled, and exact anonymous readiness for the deployed release and config revision. | NOT RUN |
| Upgrade | A newer authorized DEV tag is deployed. A verified prior Application backup is retained. Config, keys and Logs survive replacement under the same worker profile. | NOT RUN |
| Failed readiness and recovery | On an isolated target, record a failed attempt and actual IIS state. A pending attempt blocks routine deployment. Verify the retained backup and use explicit recovery with the attempt ID and a nonsecret reason. Confirm restored readiness and retained attempt evidence. | NOT RUN |
| Application acceptance | Verify Entra, effective AD/SSRS identity, report execution/fidelity and required SQL and subscription checks. | NOT RUN |

Use the [release and toolkit reference](operations/README.md#release-and-toolkit-reference), [explicit recovery procedure](operations/troubleshooting.md#deployment-failure-and-explicit-recovery), and [application live gate checklist](operations/README.md#live-gate-checklist).
