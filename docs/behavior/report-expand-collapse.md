# SSRS report expand/collapse

This reference records the supported expand/collapse behavior and its live acceptance boundary. Browser, SOAP, and layout evidence uses synthetic reports. The fixture `dev-reportviewer-toggle.html` is a structural fixture based on a real DEV standard-viewer capture; it is not a capture of the portal's SOAP HTML5 fragment. No live acceptance of the permanent portal build is claimed.

## Renderer and SOAP contract

Recognized controls can use one of three narrowly validated forms:

- The portal ActionScript callback with the unpredictable per-render callback name and exact Toggle grammar.
- An `rs:ShowHideToggle` URL tied to the configured ReportServer origin, current report path, and current execution's `rs:SessionID`.
- ReportViewer's guarded `rp.control.InvokeReportAction('Toggle', runtimeId, event)` form, paired with the bounded ReportViewer control ID, one validated plus/minus resource, and consistent `role` / `aria-expanded` state.

The parser requires the complete expected source shape. Runtime identifiers are bounded ASCII values. Reserved ReportViewer images must use the exact endpoint and resource name, one each of the allowed `OpType`, numeric `Version`, and full image-name parameters. Arbitrary JavaScript, extra handlers, duplicate/ambiguous query parameters, alternate paths, foreign origins, and unrecognized renderer variants remain inert. No guessed IDs or label-based state are used.

`ToggleItem` uses the existing SOAP 1.1 ReportExecution2005 endpoint, execution namespace, quoted SOAPAction, and current `ExecutionHeader/ExecutionID`. The request contains the recognized runtime ID. The response must contain exactly one required `ToggleItemResponse/Found` XML Boolean. `true` / `1` and `false` / `0` are accepted with schema whitespace handling; missing, duplicate, malformed, or wrongly namespaced values fail closed. `Found=true` means the renderer recognized the ID; it does not report the resulting expanded state. The independent literal protocol fixture and other required SOAP fields are documented in the [fixture reference](../../tests/SsrsPortal.Tests/Fixtures/README.md).

Only `Render` requests for `HTML5` add the fixed compatible ReportViewer User-Agent observed by the operator. The header is request-local. PDF, Excel, Word, CSV, `RenderStream`, `ToggleItem`, execution metadata, and management requests keep their existing headers and contracts. This does not select an SSRS host, forward browser identity, or grant execution access.

## Sanitization, authorization, and diagnostics

Recognized source anchors become native portal buttons. Callback code is parsed as text and removed; it is never defined or executed. Source scripts, event handlers, navigation, forms, embeds, and unrecognized controls remain subject to the existing HTML/CSS/resource allowlists. The iframe remains `sandbox="allow-same-origin"` with `script-src 'none'`; no script, navigation, or generic report-command permission is added.

Each button receives a random opaque action token mapped server-side to one runtime ID in the current owner-bound execution. The browser never receives the SSRS execution ID or runtime ID in its action metadata. The session-scoped endpoints are protected by existing Staff, CSRF, ownership, expiry, cancellation, policy-revocation, and request-deadline checks. Tokens authorize only ToggleItem. `Portal:MaxToggleActions` defaults to 1,000 and is bounded to 2,000; action mappings also count against the session and aggregate byte budgets.

With diagnostics enabled, logs may include bounded action counts, result state, and revision, but no raw report HTML, resource URLs, runtime/control/execution IDs, upstream exception text, or output. For sessions with expanders, ToggleItem and render/synchronization failures suppress upstream content; browser details omit the SSRS endpoint. Detailed logging remains sensitive and follows the access, redaction, and retention rules in [operations](../operations/troubleshooting.md#detailed-ssrs-diagnostics).

## Same-execution update and recovery

An update consumes the current action tokens before contacting SSRS, calls ToggleItem on the existing execution, obtains current execution metadata, renders HTML5 again, sanitizes it, and atomically publishes the page, resources, action map, page information, and new revision. Expansion never reloads the report definition, resends parameters, creates another execution, or maintains a client-side group tree. SSRS owns expanded state, including what happens to descendants when a parent collapses.

If Render changes a known page count, the current page is clamped to the last valid page. When page count becomes known only after rendering, at most one corrective render selects the valid page. Unknown counts remain unknown. Revisions and response-abort guards reject stale frame/resource/action responses. Old action/resource tokens expire on publication.

ToggleItem and Render are not transactional. A timeout, cancellation, lost reply, SOAP fault, invalid response, Render failure, or publication failure leaves the previous frame visible but marks its output out of date. The old actions cannot be replayed, and exports/resources remain unavailable until recovery. **Reload report output** synchronizes by rendering the same execution without issuing ToggleItem again; browser refresh uses the same path. Another tab's stale actions are rejected and can recover by synchronization. A canceled or removed session cannot be resurrected by a late response.

The staged iframe stays in place while its replacement is prepared; moving it would reload its browsing context and lose the verified document/handlers. Adoption preserves the latest focus owner, pointer-versus-keyboard modality, frame/inner-container scroll when practical, and outer-page scroll. Keyboard activation uses Enter/Space; pointer focus does not create a false keyboard ring. Routine progress and completion use a clipped polite status region; real errors remain visible assertive feedback. Focus moved to parameters or toolbar during a slow update is not stolen.

## Authored report layout

The sanitizer reconstructs `cellpadding` and `cellspacing` only on table elements, accepting bounded unsigned integer hints from 0 through 1,000 CSS pixels and defaulting malformed values to zero. Authored safe CSS retains precedence. The report document uses 10pt Times New Roman, serif as fallback; allowed authored fonts, sizes, alignment, padding, borders, column widths, and wrapping remain effective. No global cell-padding reset, scaling, or report-width rewrite is applied. Wide tables scroll inside the viewer without overflowing the application shell.

A synthetic five-table, eighteen-column fixture reconstructs operator measurements: 1,881.9px total width, with reference columns near 66px, 312px, 229px, and 64px. Other content and dimensions are invented. Chromium checks both the raw reference and sanitized rendering, including the 36px regression when spacing hints are stripped. These checks do not prove all live reports use that markup or measure identically on staff machines.

## Live DEV acceptance

An authorized operator must test the permanent build against the installed renderer and compare with stock SSRS using the same report and parameters:

1. Confirm the actual SOAP HTML5 fragment contains one of the supported control forms. Standard-viewer markup alone does not establish SOAP fragment compatibility.
2. Expand and collapse a real group. Confirm `Found=true`, added/removed content, unchanged execution and parameters, and no duplicate ToggleItem request.
3. Test independent rows and a nested report if available; compare descendant behavior with stock SSRS.
4. Use mouse, Enter, and Space. Check focus appearance/restoration, focus moved elsewhere during a slow update, announcements, report/outer scroll, resize, mobile/touch and screen-reader speech.
5. Exercise paging, streams, PDF/Excel/Word/CSV, a failed/uncertain update, recovery by synchronization, refresh and session expiry.
6. Compare native table widths, text and authored formatting at a viewport where the report fits, then verify both ends remain reachable when it is wider.

The operator's temporary User-Agent experiment and automated fixtures are not permanent-build acceptance. Redact report values and IDs from any minimal diagnostic excerpt; do not log full HTML/SOAP, add a production debug route, or weaken isolation.
