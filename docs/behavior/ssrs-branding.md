# SSRS branding

SSRS branding is an optional, read-only enhancement derived from the existing `Portal.ReportServer` setting. It adds no configuration keys, credentials, dependency, database query, worker, or arbitrary proxy route. Automated tests use synthetic responses; the installed DEV REST endpoint, format, permissions, and live presentation remain unverified.

## Retrieval and identity boundary

The service derives the same-origin `/Reports` base from the configured HTTPS `/ReportServer` URL and issues only these fixed requests:

```text
GET /Reports/api/v2.0/SafeGetSystemResourceContent(type='UniversalBrand',key='logo')
GET /Reports/api/v2.0/SafeGetSystemResourceContent(type='UniversalBrand',key='colors')
```

The service reuses the SOAP HTTP client and process/app-pool Windows identity. Redirects and cookies are disabled. It sends no Entra token, user cookie, or interactive credential. A resource-supplied URL or context is never followed. Nonstandard ReportServer/Reports layouts are unsupported until separately qualified.

Raw PNG and UTF-8 colors JSON are supported, including bounded OData JSON binary envelopes with a base64 or base64url `value`. Empty successful bodies, HTTP 204, and null/empty binary values mean that component is absent. A 404, other failure status, transport error, timeout, or invalid content is an availability/validation failure and preserves the last good component; a failed request is not treated as removal.

## First response, cache, and browser delivery

`SsrsBrandingService` starts one shared refresh through its hosted-service lifecycle without blocking application startup. The first authenticated Razor response waits for that same initial task for at most `min(5 seconds, Portal.RequestSeconds)`. A canceled page stops only its own wait; shared requests remain bounded and shutdown-cancelable. If initial reads time out or fail, the page renders the fallback or last good resources and the completed initial result is cached, so each page does not wait again.

A usable snapshot lives for ten minutes. Missing resources and failures retry after one minute. Expired pages render the existing snapshot immediately while one coalesced background refresh runs. Logo and palette resolve independently; failures keep last-good components, while a confirmed empty successful response removes only that component. A snapshot publishes both components together, although SSRS does not transact the two separate resource reads.

HTML references versioned same-origin routes such as `/branding/{hash}/logo.png` and `/branding/{hash}/theme.css`. The current and three previous changed snapshots remain in bounded process memory. These routes require the existing Staff policy and only serve validated PNG or fixed generated CSS; they never contact SSRS or trigger refresh. Unknown/evicted versions return 404. Responses retain no-store, nosniff, resource-policy, and CSP protections.

Logs contain the fixed resource name and bounded failure code/status only. They omit response bodies, URLs, image bytes, credentials, exception text, and authentication headers. The diagnostic log limits, access, and retention rules are in [operations](../operations/troubleshooting.md#detailed-ssrs-diagnostics).

## Validation and presentation

Logo bodies are limited to 1 MiB. Validation checks PNG signatures, chunk lengths/order/CRC, dimensions, color/bit-depth rules, bounded decompression and scanlines, and supported filters. Each side is at most 4,096 pixels and the image at most 4,194,304 pixels. Animated PNG, unsupported critical chunks, SVG/HTML, and unexpected encodings are rejected. Browser delivery uses `image/png`.

Colors are limited to 64 KiB, with bounded JSON depth and unique relevant properties. `interface.secondary` accepts only `#RRGGBB`. A supplied `secondaryContrast` must also be that form and reach a 4.5:1 contrast ratio; otherwise the better black/white foreground is selected. Only fixed CSS properties enter the generated stylesheet; functions, variables, declarations, and URLs from SSRS cannot enter CSS.

A valid package logo keeps its original pixels and transparency inside 440×48 CSS pixels, shrinks to available width, and is never enlarged. It has no added backing, padding, border, opacity, or filter. The brand link stays no wider than the displayed image and keeps the accessible name **Staff reports**. Both headers remain compact and responsive; missing or broken images reveal the fallback brand. Branding changes only header surface/foreground/link/focus colors, not report content, parameters, execution, or iframe policy.

## Live acceptance

An authorized operator must verify the exact configured DEV service identity and endpoint:

1. Request the two fixed resources with redirects disabled. Record only status, MIME, byte count, encoding, and raw-versus-envelope format. Do not retain full payloads or authentication headers.
2. Confirm the colors shape and optional logo behavior. Verify absent resources via an empty successful response/204/null result; 404 is intentionally treated as failure and retains last-good data.
3. Start a cold portal process and check that the first authenticated page includes available branding within the bounded wait. Repeat with an unavailable endpoint to confirm startup and catalog use remain available.
4. Confirm expiry refresh does not block pages, outages retain the last good component, and a successful empty response removes only the corresponding component.
5. Compare header, transparent logo, hit area, breadcrumbs, focus, mobile widths, account controls, default catalog navigation, sign-in, and sign-out at normal and enlarged text/zoom settings.

The cold-process browser checks use the real application Program/Razor/service with synthetic HTTP responses. They distinguish first-page loading from later cache hits but do not prove DEV REST compatibility, Windows permissions, live package contents, or staff visual/accessibility acceptance.
