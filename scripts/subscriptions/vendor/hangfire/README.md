# Hangfire SQL installer provenance

`scripts/subscriptions/002-Hangfire.sql` copies `DefaultInstall.sql` from
[Hangfire v1.8.25](https://github.com/HangfireIO/Hangfire/blob/v1.8.25/src/Hangfire.SqlServer/DefaultInstall.sql),
matching both Hangfire packages pinned in `SsrsPortal.csproj`. It targets
`HangFire` and installs schema version 9. Only trailing whitespace and surrounding blank lines are removed
to meet the repository's whitespace check. SHA-256 of the original upstream bytes:
`0074feaaee463173e427c730bf82fe44008fe072e6ef1eb04d8e2ac3ffb8b1b6`.

Copyright Hangfire OÜ; LGPL-3.0-or-later. The upstream copyright/license header
is retained. Upstream `LICENSE.md`, `COPYING`, and `COPYING.LESSER` accompany
this source and the published Database artifacts. The SQL fixture compares the script
with `SqlServerObjectsInstaller.GetInstallScript("HangFire", true)` from the
pinned assembly, allowing only surrounding blank lines, trailing-whitespace and line-ending differences. Update the script,
package versions, expected schema version, provenance and tests together.
