# SSRS staff report portal

A .NET 10 Razor Pages portal for browsing SSRS reports, entering parameters, viewing sanitized HTML, and downloading PDF, XLSX, DOCX, or CSV.

Staff in approved Microsoft Entra tenants need the `ReportViewer` role. Everyone shares the catalog defined by `AllowedRoots` and `ExcludedPrefixes`. SSRS uses the Windows process/IIS service-account identity, not the signed-in user's identity. SSRS controls report definitions, dependencies, credentials, and execution.

Optional [subscriptions](docs/operations/subscriptions.md) use SQL Server, Hangfire, and Postmark without archiving output. The optional read-only [SQL monitor](docs/operations/catalog-monitor.md) refreshes catalog metadata; it never grants access.

To commission a DEV or Production IIS host, follow the [SSRS Web Deployment and Commissioning Guide](docs/clc-deployment.md). Use [operations](docs/operations/README.md) for application configuration, Entra and keys, and [troubleshooting](docs/operations/troubleshooting.md) for logging and recovery.

## Verification

Install the SDK in [global.json](global.json), PowerShell 7+, and Node.js 24 (the CI version). Run from the repository root:

```powershell
dotnet restore SsrsPortal.slnx --locked-mode
dotnet build SsrsPortal.slnx -c Release --no-restore
dotnet test SsrsPortal.slnx -c Release --no-build
pwsh -NoProfile -File ./scripts/Test-ConfigurationPublishGuard.ps1
npm ci
npx --no-install playwright install chromium
pwsh -NoProfile -File ./scripts/Test-Portal.ps1
```

The browser runner starts an isolated synthetic portal with temporary configuration and an available HTTPS port, then cleans up. It never uses ProgramData configuration or live Entra/SSRS services. On Linux, use `npx --no-install playwright install --with-deps chromium` to install the required libraries and fonts as CI does.

Use `-Port` or `-BrowserExecutable` to override the runner defaults. Browser selection is: explicit argument, `PORTAL_BROWSER_EXECUTABLE`, Windows Edge if available, then Playwright Chromium. For an already running synthetic portal, use `npm run test:browser`; `PORTAL_BASE_URL` defaults to `https://localhost:7443`.

Run the remaining packaging, dependency, and repository checks:

```powershell
dotnet publish src/SsrsPortal/SsrsPortal.csproj -c Release --no-build --no-restore -o artifacts/publish
pwsh -NoProfile -File ./scripts/release/Export-ThirdPartyNotices.ps1 -PublishDirectory artifacts/publish
pwsh -NoProfile -File ./scripts/release/New-SubscriptionSetupArtifact.ps1 -OutputPath "$PWD/artifacts/administrative-setup.zip" -SourceCommit (git rev-parse HEAD)
pwsh -NoProfile -File ./scripts/release/Test-SubscriptionPublish.ps1 -PublishDirectory artifacts/publish -AdministrationArchive artifacts/administrative-setup.zip
dotnet list SsrsPortal.slnx package --vulnerable --include-transitive
npm audit --audit-level=low
node --test scripts/check-generated-artifacts.test.cjs
node scripts/check-generated-artifacts.cjs
git diff --check
```

Keep generated screenshots, logs, builds, and downloads under ignored `artifacts/`. [CI](.github/workflows/verify.yml) runs on PR updates and cancels superseded runs. Ordinary CI uses GitHub-hosted `ubuntu-latest` for Linux/browser checks and GitHub-hosted Windows for compatibility checks, admitting only trusted same-repository pull requests. The hosted Windows job checks the consumer contract, release packaging, deployment compatibility and workflow syntax against the pinned toolkit. It uses synthetic fixtures. Run `actionlint` from the repository root to check GitHub Actions syntax locally.

To reproduce the Windows contract checks, check out the exact revision in the consumer configuration and set `$toolkit` to that checkout. Use stable PowerShell 7.6.6 or later within 7.x. The toolkit selects verified writable NTFS for native packaging. [Consumer configuration](.clc/release.json)

```powershell
. (Join-Path $toolkit 'tools/HostedScratch.ps1')
$scratch = New-ClcHostedScratchDirectory
./scripts/release/Test-ConsumerDeployment.ps1 -ToolkitRoot $toolkit
pwsh -NoProfile -File ./scripts/release/Test-QualifiedPackage.ps1 -ToolkitRoot $toolkit -OutputDirectory (Join-Path $scratch 'qualification')
```

## Tagged releases

The [tag workflow](.github/workflows/release.yml) qualifies and publishes an immutable GitHub Release on hosted runners. The shared deploy workflow verifies the artifact and deploys to the dedicated Windows target. Authorized DEV tags may select unmerged source. Stable tags require protected `master` ancestry. Beta and RC publish artifacts only.

The shared workflows and toolkit are pinned to commit `e306d730d77c1a05868f124ab2719e71ae186499`. The application locks `Clc.Deployment.AspNetCore` at `0.1.0-beta.8`. [Clc.Deployment v0.1.0-beta.8](https://github.com/clcdpc/clc-deployment/releases/tag/v0.1.0-beta.8)

Deployed DEV and Production both run on IIS with the `Production` ASP.NET Core environment, a compiled release identity and persistent Windows keys. Commission each host through the protected-master initialization, configuration and read-only validation procedure. An authorized tag push triggers routine deployment. [Commissioning guide](docs/clc-deployment.md) · [Release reference](docs/operations/README.md#release-and-toolkit-reference)

## Run locally against DEV SSRS

This workflow runs a local build in the `Development` environment with the local process's Windows identity.

1. Use the native ProgramData application directory `clc-ssrs-web` on the development machine. Preserve existing files and protect credentials. If the file is absent, the first local startup creates a starter file and stops. [Configuration provisioning](docs/operations/README.md#external-configuration-provisioning)
2. Set the DEV HTTPS `/ReportServer` URL, roots and exclusions, Entra client ID and secret, and approved tenants. Keep `Portal:UseMock` and `Portal:TestAuth` false. Grant the local process identity the approved DEV SSRS access.
3. Allow `https://localhost:7443/signin-oidc` in Entra and assign staff the exact `ReportViewer` role. See [Entra setup](docs/operations/README.md#multi-tenant-entra-onboarding-and-global-catalog-policy).
4. Start the portal:

```powershell
dotnet dev-certs https --trust
dotnet run --project src/SsrsPortal --no-launch-profile -- --environment Development --urls https://localhost:7443
```

Open [https://localhost:7443](https://localhost:7443). The external file supplies operational settings and loads once at startup. Restart after edits. Placeholders or invalid configuration stop startup. Use mock authentication and temporary configuration overrides only for isolated Development tests.

Subscriptions are disabled by default. Follow the [local subscription setup](docs/operations/subscriptions.md#local-development-initialization) to enable them in Development.

## Documentation

- [Operations](docs/operations/README.md) and [troubleshooting](docs/operations/troubleshooting.md): configuration, IIS, Entra, logs, and diagnostics.
- [Report workspace](docs/behavior/report-workspace.md), [expand/collapse](docs/behavior/report-expand-collapse.md), and [branding](docs/behavior/ssrs-branding.md): current behavior and limits.
- [SSRS Web Deployment and Commissioning Guide](docs/clc-deployment.md) for GitHub setup, Windows runners, IIS initialization, tagged deployment and live acceptance.
- [SOAP and renderer fixtures](tests/SsrsPortal.Tests/Fixtures/README.md): protocol expectations and synthetic coverage.

## Live acceptance

Synthetic tests do not prove live IIS/Entra/AD, SSRS/SQL, delivery, export fidelity, accessibility, or load behavior. Missing Windows/local-SQL prerequisites mean skipped or inconclusive tests, not passes. Complete the [live gates](docs/operations/README.md#live-gate-checklist), including report streams and cancellation/isolation. [Known-record date boundaries](docs/date-parameter-boundary-acceptance.md) remain open. The revised SQL monitor query has not run against DEV; the SSRS product build remains unverified.

## Contributing

Branch from `master` and open a PR against it. Follow [repository guidance](AGENTS.md); do not merge without authorization. Browser checks live in `tests/browser/`, release tools in `scripts/release/`, and SQL/provisioning tools in `scripts/subscriptions/`.

## License

Central Library Consortium owns the project-authored code, licensed under [AGPL-3.0-only](LICENSE). Third-party components retain their own licenses. See [copyright](COPYRIGHT.md), the [binary distribution notice](BINARY-DISTRIBUTION-NOTICE.md), and [licensing guidance](docs/licensing-and-distribution.md).
