# Repository guidance

## Workflow and conventions

- `master` is the integration branch. Use short-lived feature or cleanup branches and open PRs against `master`; do not merge unless explicitly instructed.
- Follow the existing .NET, PowerShell, native JavaScript, testing and security conventions. Use braces for C# and PowerShell conditional blocks.
- Use the SDK in `global.json`, locked npm dependencies and the verification commands in [README.md](README.md).
- Keep Linux application/browser CI on `clc-linux` while the repository is private, preserving trusted-code admission checks and runner-group restrictions. Defer its GitHub-hosted runner switch to the public cutover. Retain the GitHub-hosted Windows compatibility job; DEV deployment remains separately gated and disabled until infrastructure authorizes it.
- Keep operational configuration and credentials out of source control.

## Generated files and visual evidence

- Generate browser screenshots under ignored `artifacts/` (normally `artifacts/browser/`). Upload visual regression evidence as CI artifacts when needed; their retention is temporary.
- Do not commit routine or exploratory browser screenshots, intermediate UI designs, or screenshots whose sole purpose is to prove a test passed.
- Do not create tracked documentation screenshot directories for individual tasks or PRs. Do not copy generated screenshots into `docs/` to make them available in a PR.
- Build outputs, generated reports, browser recordings, test logs and temporary files also belong outside tracked source.
- Intentional runtime assets, embedded or loaded automated test fixtures, and explicitly justified permanent documentation illustrations may be committed in appropriate source directories. Keep permanent source assets separate from generated evidence; use a location such as `docs/images/` for justified documentation illustrations and reference them from documentation.
- Preserve the six required PNG fixtures under `src/SsrsPortal.Mock/Branding/`, their bytes, `SyntheticBranding.cs` and the mock project's embedded resource declarations. PNGs and other image formats are legitimate source assets when required; there is no blanket image ban.
- `node scripts/check-generated-artifacts.cjs` checks tracked paths, including forced additions to ignored directories. Generated `docs/*-screenshots/`, `docs/**/screenshots/` and `artifacts/` directories must stay untracked.

## Before committing

Inspect all three views:

```text
git status --short
git diff --stat
git diff --cached --stat
```

Check specifically for unexpected PNG/JPG/JPEG/WebP/GIF images, screenshots, browser recordings, generated reports, build outputs, test logs, temporary files, local configuration and credentials. Remove unintended generated files from the staged changes before committing. Do not use `git add -f` to bypass artifact ignore rules except for an explicitly justified intentional asset.

## Documentation and verification

- Prefer concise Markdown evidence, reproducible tests, and links to source code and verification procedures over screenshot galleries.
- Preserve documentation accuracy when removing artifacts, including recorded test results, design decisions and operational instructions. Historical images remain in their original Git revisions; CI artifact URLs are not permanent documentation.
- Update an existing verification record where practical; avoid accumulating redundant historical verification documents.
- Do not claim synthetic browser evidence proves live DEV SSRS behavior.

## Documentation status

- Keep `README.md` as the current entry point and `docs/operations/README.md` as the operational source. Keep retained verification documents concise, with current contracts, reproducible checks, and unfinished acceptance; do not accumulate PR-by-PR diaries or screenshot galleries.
- Distinguish synthetic/local fixtures and automated passes from live IIS, Entra, SSRS, SQL, delivery, accessibility, or output-fidelity acceptance. Report Windows/local-SQL integration prerequisites that are unavailable as skipped or inconclusive, not passing.
- Leave explicitly open acceptance work open until its stated evidence exists. In particular, synthetic date parsing or SOAP tests do not complete known-record report-boundary acceptance.
