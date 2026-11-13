# SSRS staff report portal

A .NET 10 Razor Pages portal for browsing SSRS reports, entering parameters, viewing sanitized HTML, and downloading PDF, XLSX, DOCX, or CSV.

Staff in approved Microsoft Entra tenants need the `ReportViewer` role. Everyone shares the catalog defined by `AllowedRoots` and `ExcludedPrefixes`. SSRS uses the Windows process/IIS service-account identity, not the signed-in user's identity. SSRS controls report definitions, dependencies, credentials, and execution.

Optional [subscriptions](docs/operations/subscriptions.md) use SQL Server, Hangfire, and Postmark without archiving output. The optional read-only [SQL monitor](docs/operations/catalog-monitor.md) refreshes catalog metadata; it never grants access.

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
pwsh -NoProfile -File ./scripts/release/Test-ReleasePackaging.ps1
dotnet list SsrsPortal.slnx package --vulnerable --include-transitive
npm audit --audit-level=low
node --test scripts/check-generated-artifacts.test.cjs
node scripts/check-generated-artifacts.cjs
git diff --check
```

Keep generated screenshots, logs, builds, and downloads under ignored `artifacts/`. [CI](.github/workflows/verify.yml) runs on PR updates and cancels superseded runs. Ordinary CI uses GitHub-hosted `ubuntu-latest` for Linux/browser checks and GitHub-hosted Windows for compatibility checks, admitting only trusted same-repository pull requests. The separately protected DEV deployment workflow remains disabled until infrastructure authorizes its privileged runner and host.

## Run against DEV SSRS

1. On Windows, use the published `SsrsPortal.dll --clc-appdata-directory` command to print the application data root. A normal first run under a permitted setup identity copies the published [example](src/SsrsPortal/application.Example.json) to missing `Config\application.json` and stops for editing. Follow [configuration provisioning](docs/operations/README.md#external-configuration-provisioning); replace every `REPLACE_ME`, preserve any existing file and restrict NTFS access to its credentials. Discovery and deployment preflight remain read-only.
2. Set the HTTPS `/ReportServer` URL, roots/exclusions, Entra client ID/secret, and approved tenants. The process's Windows identity needs SSRS access.
3. Allow `https://localhost:7443/signin-oidc` in Entra and assign staff the exact `ReportViewer` role. See [Entra setup](docs/operations/README.md#multi-tenant-entra-onboarding-and-global-catalog-policy).
4. Start the portal:

```powershell
dotnet dev-certs https --trust
dotnet run --project src/SsrsPortal --no-launch-profile -- --environment Development --urls https://localhost:7443
```

Open [https://localhost:7443](https://localhost:7443). Configuration loads once at startup; restart after edits. The first run creates a missing starter file and stops; placeholders or invalid configuration also stop startup. Appsettings, user secrets, and ordinary environment/command-line settings are not fallbacks. Mock authentication and temporary configuration overrides are for isolated Development tests only.

Subscriptions are disabled by default. Follow [local subscription setup](docs/operations/subscriptions.md#local-development-initialization) to enable them. Production and Staging require the [CLC deployment process](docs/clc-deployment.md), compiled release identity, and persistent Windows keys. Local checks do not qualify or authorize a CLC release.

## Documentation

- [Operations](docs/operations/README.md) and [troubleshooting](docs/operations/troubleshooting.md): configuration, IIS, Entra, logs, and diagnostics.
- [Report workspace](docs/behavior/report-workspace.md), [expand/collapse](docs/behavior/report-expand-collapse.md), and [branding](docs/behavior/ssrs-branding.md): current behavior and limits.
- [Deployment](docs/clc-deployment.md): releases, recovery, qualification status, and outstanding host checks.
- [SOAP and renderer fixtures](tests/SsrsPortal.Tests/Fixtures/README.md): protocol expectations and synthetic coverage.

## Live acceptance

Synthetic tests do not prove live IIS/Entra/AD, SSRS/SQL, delivery, export fidelity, accessibility, or load behavior. Missing Windows/local-SQL prerequisites mean skipped or inconclusive tests, not passes. Complete the [live gates](docs/operations/README.md#live-gate-checklist), including report streams and cancellation/isolation. [Known-record date boundaries](docs/date-parameter-boundary-acceptance.md) remain open. The revised SQL monitor query has not run against DEV; the SSRS product build remains unverified.

## Contributing

Branch from `master` and open a PR against it. Follow [repository guidance](AGENTS.md); do not merge without authorization. Browser checks live in `tests/browser/`, release tools in `scripts/release/`, and SQL/provisioning tools in `scripts/subscriptions/`.

## License

Central Library Consortium owns the project-authored code, licensed under [AGPL-3.0-only](LICENSE). Third-party components retain their own licenses. See [copyright](COPYRIGHT.md), the [binary distribution notice](BINARY-DISTRIBUTION-NOTICE.md), and [licensing guidance](docs/licensing-and-distribution.md).
