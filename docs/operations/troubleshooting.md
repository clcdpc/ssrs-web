# Logging and troubleshooting

[Back to operations](README.md)

## Build-time deployment toolkit

The checked-in [toolkit lock](../../deployment/toolkit.lock.json) selects published `Clc.Deployment` `0.1.0-beta.4` for format-3 packaging. `Release.ToolkitArchivePinMismatch` means the downloaded bytes disagree with the independent archive pin; `Release.ToolkitInventoryPinMismatch` means the verified module inventory disagrees with its lock. Source/version disagreement is rejected by the authenticated distribution verifier. Do not bypass these checks or substitute a legacy module name. `Release.Prerequisite.RenamedToolkitPublicationAndRepinRequired` now indicates an old-format or missing descriptor, including the historical beta.3 PowerShell archive; beta.4 publication is no longer outstanding.

Repeat Windows packaging with actual CLC Release publish output and `scripts/release/Test-ReleasePackaging.ps1 -PublishDirectory <publish> -PublishedToolkit`. The upstream beta.4 GitHub Release reports `immutable=false`, but its exact asset digest is pinned. Successful acquisition/packaging does not establish host runtime installation or activation, enrollment, approved baseline, standing authorization or live DEV/Production acceptance. Those target-local operations require separate authority; the beta.3 ASP.NET Core companion is a separate dependency.

## Persistent file logging

`Clc.Logging` 0.1.0-beta.2 supplies reusable NLog setup and Microsoft `ILogger<T>` integration, with `Clc.Configuration` 0.1.0-beta.5, NLog 6.2.2 and NLog.Extensions.Logging 6.3.0. ssrs-web owns the SafeLogLayout output sanitizer and registers one configured NLog path. No nlog.config is required. There is no appsettings fallback, logging environment override, arbitrary log path or hot reload.

Released DEV logs use `%ProgramData%\clc-ssrs-web-dev\Logs\`, and stable Production logs use `%ProgramData%\clc-ssrs-web-production\Logs\`. Native Windows known-folder verification and compiled tag/target metadata select the root; local Synthetic development retains `clc-ssrs-web`. Config and Logs are siblings outside website/release directories and survive deployment/recycling. Logging may create Logs if allowed, but never changes ACLs or writes to Config. Administrators should provision Logs first, avoiding service write access to the ProgramData application root.

Files use local dates: `ssrs-web-2026-11-01.log`. Extra files use numbered suffixes, such as `ssrs-web-2026-11-01.001.log`. Entries start with a millisecond timestamp, UTC offset, severity and category. Message, template, JSON properties/scopes and any exception details follow; `---` separates entries. Events use this single text format without routine duplicate files. UTC offsets distinguish repeated daylight-saving hours.

Defaults in the external application.json:

```json
"Logging": {
  "File": { "Enabled": true, "MaxFileSizeBytes": 52428800, "RetentionDays": 30 },
  "LogLevel": {
    "Default": "Information", "SsrsPortal": "Debug", "Microsoft": "Warning",
    "Microsoft.AspNetCore": "Warning", "Microsoft.AspNetCore.Authentication": "Warning",
    "System.Net.Http.HttpClient": "Warning"
  }
}
```

Named levels Trace through None are accepted; numeric or unknown levels fail safely. Daily rotation is automatic. **Before deploying this migration, manually remove the legacy `Logging:File:Rotation` key from existing application.json files**, including `"Rotation": "Daily"`; it is now rejected. Size bounds are 1 KiB–1 GiB; retention is 1–3650 days, even with file output disabled. Optional `MaxArchiveFiles` accepts 1–10000; omitting it preserves ssrs-web's historical unlimited archive count with age-based retention, overriding the shared package's count default. Unsupported options (including paths), empty/null option objects and malformed values stop startup without echoing values. Restart/recycle after changes. `Enabled=false` sends configured events through SafeLogLayout to native stderr and skips persistent-file readiness without creating a log file.

NLog rolls dates on the first event after midnight and rolls size without restarting. One bounded event or flushed batch may exceed the 50 MiB threshold before rollover. Age cleanup covers matching files, including same-day archives, at startup/rollover as events arrive. Bootstrap does not touch files or delete archives; configured retention applies only after loading the authoritative snapshot. No separate service cleans up while the app is idle/stopped. Restarts append to existing files, including the latest numbered same-day file after size rollover. Existing logs remain in place across deployment. Disk use depends on traffic; monitor free space and tune category levels.

The application wraps the package's file-to-stderr fallback route in a native NLog async queue: 256 events, blocking overflow, batch size 1 and 10 ms between batches. NLog precalculates SafeLogLayout on the producing thread while request secrets and scopes exist, for both destinations. Fatal startup and normal shutdown allow five seconds to flush. OS crashes, forced kills or IIS timeouts can still lose queued events.

Before external configuration finishes loading, bootstrap and startup failures go only to sanitized stderr. Those early records are never replayed into persistent files. After registration, enabled file logging must pass an application-owned readiness probe before host construction or serving requests: emit a unique nonsecret Info marker through the native configured pipeline, flush, then verify the marker in a bounded tail of the actual current-day file (including size suffixes). Microsoft category settings such as `None` cannot suppress this check; directory creation or stderr fallback cannot satisfy it. Failure intentionally stops startup with guidance to check Logs permissions, locks and disk space. Runtime file failures instead fall back through SafeLogLayout to native stderr and retry files on subsequent events; recovery resumes persistent output without routine duplication. The former custom fallback warning banner is no longer emitted. Retain hosting stderr if fallback records are required. IIS stdout is not the normal destination.

Use one IIS worker and **disable overlapped recycling** (`recycling.disallowOverlappingRotation=true`). The old worker must drain and close files before its replacement writes. Allow shutdown time for request/background-service draining and log flush. Recycling invalidates in-memory report sessions. Web gardens, overlapping workers and farms require coordinated file targeting/retention and distributed sessions before use. NLog's core file target supports one writer here.

### Startup troubleshooting and deployment

For initial setup follow [the devweb commissioning procedure](../clc-deployment.md); the [optional GitHub diagnostic](../../.github/workflows/diagnose-deployment.yml) is read-only, never accesses hosts and does not gate deployment. For exact release/rerun behavior see the [release reference](README.md#release-and-toolkit-reference).

Check hosting stderr first when application.json is missing or invalid: bootstrap runs before configured logging and is stderr-only. Parser/binder failures identify the file/section without secret-bearing JSON inner exceptions. After logging registration, Logs retains useful sanitized hosting/dependency failure chains; the outer fatal handler avoids rendering an already logged exception twice and exposes only a sanitized exception to hosting. Check normal IIS/Application event/hosting output for failures before managed code runs (Hosting Bundle/ANCM), or before host/log-directory selection. If Logs is unavailable, safe fallback output reports the logging failure. Fix permissions, locks or free space before restarting.

The [CLC deployment runbook](../clc-deployment.md) preserves Config and Logs outside the managed release, with Config read-only and Logs write/modify access for runtime. The single worker drains without overlapping recycle. Deployment neither packages configuration/logs nor changes their ACLs.

The consumer preflight checks that the physical Logs path is disjoint from the application and protected roots; it does not inspect or grant the runtime account's effective NTFS rights. Pre-create Logs and verify Modify access, including create/append/rollover/retention, under the actual pool identity. Do not grant that identity write access to the ProgramData application parent, Config or toolkit control state. Separately grant the runtime account read/write access to its dedicated [Data Protection key directory](README.md#persistent-data-protection-keys) for key creation and rotation. These live ACL checks remain **NOT RUN** until confirmed on the DEV host.

## Deployment failure and explicit recovery

Use upstream [failure behavior](https://github.com/clcdpc/clc-deployment/blob/master/docs/deployment.md#failure-behavior) and [operator recovery](https://github.com/clcdpc/clc-deployment/blob/master/docs/deployment.md#operator-procedure) with the exact independently authenticated active runtime for `ssrs-web-dev`. Freeze competing IIS/configuration/filesystem/deployment changes. Preserve Application, protected current/journal/attempts, approvals, original packages, verified manifests/staging/backups/indexes, configuration, keys and actual pool/worker observations. Prove exact helper processes exited. Unknown lifetime, partial installation or corrupt/unverifiable state requires independent maintenance, not a normal retry.

Recover only the **latest failed attempt**, using the independently approved compatible configuration and selected release/backup. In a fresh approved PowerShell process:

```powershell
Import-Module $independentlyVerifiedActiveManifest
Restore-ClcIisDeployment -TargetId ssrs-web-dev -AttemptId $latestFailedAttemptId `
    -Mode RestorePrevious -Reason $nonsecretIncidentReference -RecoveryApprovalId $independentExactApproval
```

For an automatically admitted recovery release, RecoveryApprovalId must select a separately authored enabled **schema-1** exact approval matching release/target/source/lineage/provenance/hashes, even if standing policy is revoked. Existing manually approved releases retain their normal recovery authority. AcceptCandidate and RestoreEmpty have separate admissibility requirements; neither is an automatic fallback. Never delete journals/locks/intents, edit state to Completed, fabricate approvals, blindly retry mutation, restart legacy code or assume reboot made recovery safe. Verify exact readiness/inventory and durable terminal state after recovery; preserve original evidence. File restoration does not roll back SQL, deliveries, configuration, keys or profiles.

**Reboot limitation:** no activation fence/startup hook prevents WAS starting mixed files if a reboot/external activation interrupts replacement. Ordinary replacement/startup/readiness failure attempts a bounded stop and retains evidence; actual Stopped/zero workers must be observed, otherwise report uncertainty. Accept this operational limitation before enabling deployment. A legacy cutover backup is not prior toolkit-approved release history; restoring legacy files is separately authorized maintenance.

### Live IIS acceptance

Complete these checks under an authorized DEV host procedure; synthetic file tests do not prove live ACL or IIS behavior.

1. Verify effective Config read/traverse and Logs create/append/rotate access under the actual regular AD pool identity. Confirm it cannot modify Config or the ProgramData application root and is not a local administrator.
2. Confirm one worker and non-overlapping recycle. Verify early bootstrap entries in hosting stderr, then configured readiness/startup entries in the ProgramData Logs sibling, outside the website and release directories. Early entries must not be replayed into files.
3. Correlate a browser Reference with Trace/SOAP-reference log fields. Confirm useful bounded failure details while credentials, cookies, authorization headers, tokens, raw callbacks, outgoing SOAP requests, and successful report output remain absent.
4. On an isolated DEV log directory, exercise permitted size rollover, local-date rollover after midnight, and age retention. Confirm recent same-day archives remain, restore normal limits, and do not modify operational evidence timestamps.
5. Recycle and replace a release. Confirm normal shutdown flush/append, handles close, logs persist, and report sessions are discarded as documented.
6. In a controlled outage, verify unavailable Logs fails startup with safe process/hosting output and runtime write failures use filtered fallback output then recover after access/free space returns. Keep routine IIS stdout and request/body tracing disabled.

## Detailed SSRS diagnostics

Edit only the selected release target's protected `Config\application.json`: `%ProgramData%\clc-ssrs-web-dev` for DEV or `%ProgramData%\clc-ssrs-web-production` for stable Production. Merge the following into the existing `Portal` object, preserving the real endpoint, roots, policy and Entra configuration:

```json
"Diagnostics": {
  "Enabled": true,
  "ShowDetailsInBrowser": false
}
```

Restart the app or recycle its IIS pool after every change; there is no hot reload. `DetailedFailuresAtDebug` defaults to true, capturing bounded SSRS failure details in default Debug application logs. `Enabled=true` promotes details to Warning when Debug is disabled. `Enabled` and `ShowDetailsInBrowser` default to false. Search Logs for `Portal failure`, the operation or the browser's Reference. Structured Trace and SOAP references link shared/background work to the originating request. No HTTP body logger is needed.

For local browser details, set `ShowDetailsInBrowser=true`. Select the HTTPS Development launch profile with a registered localhost OIDC callback, then restart Visual Studio's application process. Use `UseMock=false` and `TestAuth=false` for real SSRS/Entra tests.

Browser details are independent of server logging. They require authentication, Staff tenant/`ReportViewer` authorization, and loopback addresses (IPv4 or IPv6) for both the original peer and effective remote IP. Remote clients through local proxies are ineligible; missing connection information also denies details. All non-Development environments, including Production and Staging, reject browser-details configuration at startup, even with server logging disabled. Server logging with browser details false is allowed outside Development.

Eligible page failures show encoded, preformatted diagnostics. API failures keep normal JSON fields and add `diagnostics` only for eligible requests; the parameter page displays it as text. Other HTML/JSON responses keep sanitized messages, existing status codes, security headers and the request Reference. No unauthenticated diagnostic endpoint or stored-error lookup exists.

Captures include SOAP 1.1 fault code, faultstring, detail XML and fault XML, including HTTP 500 faults. Invalid XML, content types or envelopes include a bounded body excerpt. Contract errors identify missing/invalid fields and operation/endpoint/HTTP context. Original exception objects, messages, inner exceptions and stacks remain intact. Presentation formats the chain without sending unbounded exception objects to logging providers. HTTP 401/403 keep the safe credentials category; ordinary upstream/contract failures keep 502 behavior. Timeouts retain 504 where previously applicable.

Report interaction privacy overrides detailed logging. ToggleItem, HTML render and synchronization diagnostics omit upstream bodies/faults and exception text. Export/resource failures also suppress content once an execution has toggle controls. Operation, HTTP status, timing and correlation references remain available. Never log runtime toggle IDs, execution IDs, action tokens or rendered report values, even with Debug or explicit diagnostics enabled.

Diagnostic character limits:

- Body/fault excerpts: 8,192 each; fault code: 1,024; fault message: 2,048. Error pages are read only to a bounded prefix when status/type establishes failure.
- Browser presentation: 32,768.
- Server failure blocks: 98,304; ordinary file exceptions: 65,536; whole entries: 131,072 plus the entry delimiter.
- Nested exceptions: up to 16, with explicit `[truncated]` markers. Normal messages/stacks are no longer capped at 8 KiB.

Structured values have depth, item and total traversal/character budgets. Parameter names/values remain available within those bounds. Full XML responses retain `MaxSoapBytes`, DTD prohibition and normal deadlines.

Logs omit complete outgoing SOAP requests, authentication secrets and successful report output. Filtering covers XML/encoded fields, credential assignments, Bearer/Basic/Negotiate/NTLM values, JWTs, known configured secrets, request cookies/authorization/antiforgery values, structured secret keys and scopes. It also covers raw exceptions sent to ILogger; originals remain intact in memory. Do not supplement diagnostics with request/body tracing.

**Protect detailed logs as sensitive operational data.** Upstream faults may contain catalog paths, account/server/database names, SQL text, data-source errors and echoed report values. Authentication filtering does not make faults safe to distribute broadly. Restrict log access, retention and support extracts. Never commit raw production faults or logs. TLS validation, Windows integrated authentication and Entra authorization are unchanged.

Ordinary failures have one detailed block at the SOAP producer or consuming boundary. Other boundaries emit correlation lines; default Debug details also retain a Warning summary. SOAP producers log failures even if every HTTP waiter disconnects. Shared catalog work may have another originating SOAP reference. Per-request trace/SOAP-reference lines link each browser Reference to its producer.

Context includes report selection/parameters, execution/page/export timing, cancellation, session replacement/expiry/cleanup and application lifecycle. SQL monitor failures retain safe server/database/query identifiers, filtered exception chains, categories, schema/timeout/permission classification and refresh/recovery decisions. Connection strings are never logged. Authentication logs include sign-in/rejection and tenant/role/session decisions with identity/reference context, but no raw callbacks or tokens.

| Diagnostic evidence | Investigation |
| --- | --- |
| HTTP 401/403, `http-credentials` | Check the actual Windows process/app-pool AD identity and SSRS access. Entra sign-in does not supply outbound SSRS credentials. |
| SOAP `soap:Server`, detail `ErrorCode=rsAccessDenied` | Check SSRS permissions for the reported operation and root/ancestor/report. An HTTP 500 SOAP permission fault is different from an HTTP authentication rejection. |
| `redirect` or `content-type`, HTML excerpt | Confirm the configured HTTPS `/ReportServer` origin routes to `ReportService2010.asmx` or `ReportExecution2005.asmx`; investigate login/proxy/error pages. Redirects remain disabled. |
| `response-contract`, missing `Values` or unexpected wrapper/namespace | Compare the upstream response with the Microsoft WSDL. GetProperties requests use `Properties`; responses require `Values` for this application's visibility decision. No invalid legacy response fallback is accepted. |
| `xml`, `XmlException` and response excerpt | Investigate malformed/truncated SOAP, proxy content or incompatible services. DTDs remain prohibited. |
| `NameResolutionError`, `ConnectionError`, nested socket errors | Check DNS, host/port reachability and service availability from the actual worker identity/host. |
| `SecureConnectionError`, nested `AuthenticationException` | Check certificate name, chain, expiry and trusted roots under the worker. Correct trust/server configuration; never disable HTTPS validation. |
| `timeout`, cancellation cause and elapsed time | Compare the request/SOAP deadline with SSRS execution and data-source timeouts; check server load and reduce the requested data range. |

Example: Entra sign-in succeeds and the portal opens, but catalog loading reports “The report service is unavailable.” Enable both flags for a local Development test and restart. Diagnostics can identify `Operation: GetProperties`, the full management endpoint, and either:

- HTTP 200 with `GetPropertiesResponse is missing required Values`.
- HTTP 500 with the actual `rsAccessDenied` fault and detail.

This distinguishes a contract mismatch from AD-account SSRS permissions. Later report failures identify their operation and underlying error in the same display/logs. These are expected diagnostics, not live-server test results.

After troubleshooting, set `Enabled=false` and `ShowDetailsInBrowser=false` and restart/recycle. Confirm the next error returns only the safe message and Reference, and retain or remove the sensitive logs according to operational policy.
