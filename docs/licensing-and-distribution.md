# Licensing and distribution

## Project code and copyright

Project-authored code is licensed under **GNU AGPL-3.0-only**; the complete authoritative license text is in the repository-root [LICENSE](../LICENSE). Third-party libraries, vendored materials, and generated binaries keep their own licenses and notices. The AGPL grant for this project's code does not relicense those components.

Central Library Consortium owns the project-authored code. Michael Fields has confirmed his authorization to release it on CLC's behalf. See [COPYRIGHT.md](../COPYRIGHT.md).

## Source for network users

Release builds compile the full source commit into assembly metadata. The application exposes a **Source code and license** link to that exact commit, not to a moving branch. The CLC release manifest also records the repository and full commit SHA. Before enabling network access to a release, operators must ensure that the exact source commit and its AGPL license are publicly reachable, or provide users an equivalent source copy. The MIT-licensed `Clc.Deployment.AspNetCore` companion retains separate attribution and exact source/provenance information. Its MIT license does not add AGPL network source-sharing obligations; the portal's own AGPL obligations remain. A fresh snapshot import changes the commit identity; prior private-history SHAs do not automatically resolve to the new public repository. Do not substitute a floating `master` link for a release's corresponding source.

The release pipeline remains a CLC-specific, protected process. Its compiled release identity is tied to an immutable source SHA; the repository README and [CLC deployment runbook](clc-deployment.md) describe the separate runner, approval, host, and toolkit requirements.

## Third-party and binary notices

`scripts/release/Export-ThirdPartyNotices.ps1` reads the exact application publish's `SsrsPortal.deps.json`, resolves each package's locked NuGet metadata, records package/version/copyright/license information, and includes its license text in `THIRD-PARTY-LICENSES/`. For SPDX expression licenses it uses the checked-in SPDX License List texts and substitutes package copyright metadata. The companion deployment package now uses the `MIT` license expression, so its notice carries the checked-in MIT license template with package copyright metadata. The portal's own authoritative root `LICENSE` remains AGPL-3.0-only. For package-file licenses it copies the file from the restored package. It fails closed on unknown SPDX expressions, missing package license data, unsafe license paths, or missing texts. The CLC release workflow runs this exporter before packaging; the release allowlist and archive verifier require the generated notices and texts.

The Windows x64 Release publish contains `Microsoft.Data.SqlClient` 7.1.1 (MIT) and the separate `Microsoft.Data.SqlClient.SNI.runtime` 7.1.0 package. The latter provides native `Microsoft.Data.SqlClient.SNI.dll` object code and carries Microsoft Software License Terms in its own package `LICENSE.txt`. The project includes that exact license text in source and binary distributions. The app publish's package inventory is generated from the actual `.deps.json`, not inferred from direct project references.

SNI is distributed as an unmodified native binary under its Microsoft terms. Those terms permit object-code distribution as part of an application and require downstream agreement and distributor indemnification, subject to the stated exception. Preserve the generated notices and license texts and comply with the complete Microsoft terms when redistributing binaries. The [binary distribution notice](../BINARY-DISTRIBUTION-NOTICE.md) summarizes these requirements.

Hangfire 1.8.25 is separately identified in generated notices under its package's LGPL v3 or commercial licensing terms. The vendored Hangfire SQL installer retains upstream attribution and `COPYING`, `COPYING.LESSER`, and `LICENSE.md` in both source and the separate administrative setup artifact. It is not copied into the application publish.
