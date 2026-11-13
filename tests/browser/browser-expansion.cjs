"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");

module.exports = async ({ page, context, baseURL, inputsReady, scan, selectSingle }) => {
    page.setDefaultTimeout(20000);
    const report = (await (await context.request.get("/api/catalog")).json()).find(e => e.name === "Expandable Groups");
    const entry = "/Report?id=" + report.id;
    const token = () => page.evaluate(() => history.state?.ssrsReport?.token ?? null);
    const frame = () => page.frameLocator("#report-frame");
    const control = label => frame().locator("tr").filter({ has: frame().locator("td:first-child").filter({ hasText: new RegExp("^[+−]\\s+" + label + "$") }) }).getByRole("button");
    const currentRevision = () => page.locator("#report-frame").evaluate(f => Number(f.contentDocument.querySelector('meta[name="ssrs-portal-revision"]').content));
    const ready = () => page.waitForFunction(() => document.querySelector("#report-frame").dataset.toggleBridge === "installed" && document.querySelector("#viewer").getAttribute("aria-busy") === "false");
    const updated = before => page.waitForFunction(previous => Number(document.querySelector("#report-frame").contentDocument?.querySelector('meta[name="ssrs-portal-revision"]')?.content || 0) > previous && document.querySelector("#viewer").getAttribute("aria-busy") === "false", before);
    const focusAppearance = locator => locator.evaluate(n => {
        const css = getComputedStyle(n);
        return { focused: n.ownerDocument.activeElement === n && n.matches(":focus") && n.ownerDocument.hasFocus(), visible: n.matches(":focus-visible"),
            style: css.outlineStyle, width: css.outlineWidth, color: css.outlineColor, offset: css.outlineOffset,
            shadow: css.boxShadow, border: css.borderColor };
    });
    const focusChecks = [];
    const checkFocus = async (locator, keyboard, phase) => {
        const actual = await focusAppearance(locator);
        assert.equal(actual.focused, true, phase + ": focus returns to the expander");
        if (keyboard) {
            assert.equal(actual.visible, true, phase + ": keyboard focus is visible");
            assert.equal(actual.style, "solid", phase);
            assert.equal(actual.width, "3px", phase);
            assert.equal(actual.color, "rgb(171, 92, 0)", phase);
            assert.equal(actual.offset, "2px", phase);
        } else {
            assert.ok(actual.style === "none" || parseFloat(actual.width) === 0,
                phase + ": pointer focus must have no outline: " + JSON.stringify(actual));
            assert.equal(actual.shadow, "none", phase + ": no substitute focus shadow");
            assert.equal(actual.border, "rgb(104, 124, 144)", phase + ": normal control border");
        }
        focusChecks.push({ phase, keyboard, ...actual });
    };
    // Use the browser's real sequential navigation, including crossing the
    // sandbox boundary. Calling focus() does not establish keyboard modality.
    const keyboardTo = async locator => {
        await page.bringToFront();
        const focused = () => locator.evaluate(n => n.ownerDocument.activeElement === n && n.matches(":focus") && n.ownerDocument.hasFocus());
        if (await focused()) {
            await page.keyboard.press("Tab"); await page.keyboard.press("Shift+Tab");
        }
        for (let count = 0; count < 60; count++) {
            if (await focused()) { return; }
            await page.keyboard.press("Tab");
        }
        assert.fail("Tab navigation did not reach the requested expander");
    };
    let pointerPaintChecks = 0;
    const watchPointerFocus = () => page.evaluate(() => {
        const oldDoc = document.querySelector("#report-frame").contentDocument;
        const samples = [];
        let started = false, animation;
        const sample = button => {
            button ||= document.querySelector("#report-frame")?.contentDocument?.activeElement;
            if (!started || !button?.matches("button[data-portal-toggle]")) { return; }
            const css = button.ownerDocument.defaultView.getComputedStyle(button);
            samples.push({ style: css.outlineStyle, width: css.outlineWidth, color: css.outlineColor, shadow: css.boxShadow });
        };
        const pointer = event => { started = true; sample(event.target.closest("button")); };
        const paint = () => { sample(); animation = requestAnimationFrame(paint); };
        const observer = new MutationObserver(() => sample());
        observer.observe(document.querySelector("#viewer"), { childList: true, subtree: true, attributes: true });
        oldDoc.addEventListener("pointerdown", pointer, true);
        animation = requestAnimationFrame(paint);
        window.expansionStopFocusWatch = () => {
            cancelAnimationFrame(animation); observer.disconnect(); oldDoc.removeEventListener("pointerdown", pointer, true);
            delete window.expansionStopFocusWatch;
            return samples;
        };
    });
    const finishPointerWatch = async phase => {
        // Give the adopted document an actual paint opportunity.
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        const samples = await page.evaluate(() => window.expansionStopFocusWatch());
        assert.ok(samples.length > 0, phase + ": sampled actual pointer and painted focus");
        for (const actual of samples) {
            assert.ok(actual.style === "none" || parseFloat(actual.width) === 0, phase + ": outline flash: " + JSON.stringify(actual));
            assert.equal(actual.shadow, "none", phase);
        }
        pointerPaintChecks += samples.length;
    };
    const change = async (label, key) => {
        const before = await currentRevision();
        if (key) { await keyboardTo(control(label)); await checkFocus(control(label), true, label + " before " + key); await page.keyboard.press(key); }
        else { await control(label).click(); }
        await updated(before);
        await checkFocus(control(label), Boolean(key), label + " after " + (key || "mouse"));
    };
    const run = async () => {
        const old = (await token());
        await page.getByRole("button", { name: "View report", exact: true }).click();
        await page.waitForFunction(previous => { const next = (history.state?.ssrsReport?.token ?? null); return next && next !== previous && !document.querySelector("#run").disabled; }, old);
        await ready();
    };
    const behavior = async label => { await selectSingle("Behavior", label); await run(); };
    const post = async (path, body = {}) => {
        const csrf = (await (await context.request.get("/api/csrf")).json()).token;
        return context.request.post(path, { data: body, headers: { RequestVerificationToken: csrf } });
    };
    const quietAnnouncement = async message => {
        const state = await page.locator("#status").evaluate(n => {
            const css = getComputedStyle(n), box = n.getBoundingClientRect();
            return { text: n.textContent, role: n.getAttribute("role"), live: n.getAttribute("aria-live"), atomic: n.getAttribute("aria-atomic"),
                accessible: !n.hidden && !n.closest('[aria-hidden="true"]') && css.display !== "none" && css.visibility === "visible",
                clipped: css.position === "absolute" && css.overflow === "hidden" && css.clipPath === "inset(50%)" && box.width <= 1 && box.height <= 1 };
        });
        assert.deepEqual(state, { text: message, role: "status", live: "polite", atomic: "true", accessible: true, clipped: true });
    };
    const portalPosition = () => page.evaluate(() => {
        const frame = document.querySelector("#report-frame"), toolbar = document.querySelector(".viewer-toolbar").getBoundingClientRect(), box = frame.getBoundingClientRect();
        return { toolbarTop: toolbar.top, toolbarBottom: toolbar.bottom, iframeTop: box.top,
            outerX: scrollX, outerY: scrollY, reportX: frame.contentWindow.scrollX, reportY: frame.contentWindow.scrollY };
    });
    const stablePosition = (before, after, phase) => {
        for (const key of ["toolbarTop", "toolbarBottom", "iframeTop", "outerX", "outerY", "reportX", "reportY"]) {
            assert.ok(Math.abs(before[key] - after[key]) <= 1, key + " moved during " + phase + ": " + before[key] + " → " + after[key]);
        }
    };
    const stableLayouts = [];
    let runs = 0, parameterRequests = 0, toggles = 0, syncs = 0, reportAxeScans = 0;
    const requests = [];
    const count = request => {
        const url = request.url();
        // The browser's downloads UI can emit its own internal resource requests.
        // Keep every request initiated by portal/report frames in the origin check.
        if (/^(edge|chrome):/.test(url)) {
            try {
                const source = request.frame().url();
                if (/^(edge|chrome):/.test(source) || url === "edge://downloads-hub/" && source === "about:blank") { return; }
            }
            catch { /* Requests with no browser-owned frame remain subject to the check. */ }
        }
        requests.push(url);
        if (/\/run$/.test(url)) { runs++; }
        if (/\/(parameters|options\/[^/]+)$/.test(url)) { parameterRequests++; }
        if (/\/toggle\//.test(url)) { toggles++; }
        if (/\/synchronize$/.test(url)) { syncs++; }
    };
    context.on("request", count);
    try {
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.goto(entry); await inputsReady(); await run();
        await quietAnnouncement("Report loaded.");
        const originalToken = (await token()), originalUrl = page.url(), initialParameters = parameterRequests;
        assert.equal(await control("Single level").getAttribute("aria-expanded"), "false");
        assert.equal(await control("Library").count(), 1); assert.equal(await control("Branch East").count(), 0);
        assert.equal(await page.locator("#report-frame").getAttribute("sandbox"), "allow-same-origin");
        const html = await (await context.request.get("/api/executions/" + originalToken + "/viewer")).text();
        assert.doesNotMatch(html, /<script|onclick|onkeypress|javascript:|synthetic-execution|synthetic\.invalid|SsrsPortalAction_|[1-5]iT0/);
        assert.equal(await frame().getByText("Unsafe action", { exact: true }).getAttribute("aria-disabled"), "true");
        assert.equal(await frame().locator("script,iframe,form,a[href]").count(), 0);
        const policy = await context.request.get("/api/executions/" + originalToken + "/viewer");
        assert.match(policy.headers()["content-security-policy"], /script-src 'none'/);
        assert.match(policy.headers()["content-security-policy"], /sandbox allow-same-origin/);
        // Parent-installed handlers really run in Chromium's script-free frame.
        await change("Single level");
        assert.equal(await frame().getByText("Single detail", { exact: true }).count(), 1);
        assert.equal(await control("Single level").evaluate(n => n.ownerDocument.activeElement === n), true);
        await checkFocus(control("Single level"), false, "first real mouse click after iframe replacement");
        await page.keyboard.press("Tab");
        await checkFocus(control("Library"), true, "mouse then Tab to independent expander");
        await page.keyboard.press("Shift+Tab");
        await checkFocus(control("Single level"), true, "Shift+Tab back to pointer-restored expander");
        assert.equal(await frame().locator("[data-portal-pointer-focus]").count(), 0, "Keyboard navigation clears pointer state");
        await change("Single level", "Space");
        assert.equal(await frame().getByText("Single detail", { exact: true }).count(), 0);
        for (let repeat = 0; repeat < 4; repeat++) { await change("Single level"); }
        // A programmatic/assistive click without a pointerdown must not inherit
        // the previous mouse activation's suppression.
        const fallbackRevision = await currentRevision();
        await control("Single level").evaluate(n => n.click()); await updated(fallbackRevision);
        await checkFocus(control("Single level"), true, "click without pointer evidence");
        await change("Single level", "Enter");
        // Deliver Chromium's trusted detail-zero activation without keydown
        // while pointer evidence is still present. This models an independent
        // assistive activation during an unfinished pointer sequence.
        await change("Single level");
        const abandonedRevision = await currentRevision(), abandonedToggles = toggles;
        const abandonedBox = await control("Single level").boundingBox();
        const cdp = await context.newCDPSession(page);
        try {
            // Native mouse dispatch avoids Playwright's drag-and-drop helpers,
            // whose injected callbacks cannot run in a script-free child.
            await cdp.send("Input.dispatchMouseEvent", { type: "mousePressed", x: abandonedBox.x + abandonedBox.width / 2, y: abandonedBox.y + abandonedBox.height / 2, button: "left", buttons: 1, clickCount: 1 });
            assert.equal(toggles, abandonedToggles, "Pointerdown alone does not activate the button");
            await page.locator("#report-frame").evaluate(f => {
                f.contentDocument.addEventListener("click", event => {
                    window.expansionFallbackClick = { trusted: event.isTrusted, detail: event.detail, pointerType: event.pointerType };
                }, { once: true, capture: true });
            });
            await cdp.send("Input.dispatchKeyEvent", { type: "char", text: "\r", unmodifiedText: "\r", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
            await updated(abandonedRevision);
            assert.deepEqual(await page.evaluate(() => window.expansionFallbackClick), { trusted: true, detail: 0, pointerType: "" });
            await checkFocus(control("Single level"), true, "trusted detail-zero activation with unfinished pointer");
            assert.equal(toggles, abandonedToggles + 1);
        } finally {
            await cdp.send("Input.dispatchMouseEvent", { type: "mouseReleased", x: 1430, y: 5, button: "left", buttons: 0, clickCount: 1 });
            await cdp.detach(); await page.evaluate(() => { delete window.expansionFallbackClick; });
        }
        const obsoleteKey = await control("Library").getAttribute("data-portal-focus");
        await page.evaluate(key => {
            window.expansionObsoleteControl = [...document.querySelector("#report-frame").contentDocument.querySelectorAll("button")].find(n => n.dataset.portalFocus === key);
        }, obsoleteKey);
        await change("Library", "Enter");
        assert.equal(await control("Branch East").count(), 1); assert.equal(await frame().getByText("Detail East", { exact: true }).count(), 0);
        const beforeObsolete = toggles;
        await page.evaluate(() => { window.expansionObsoleteControl.click(); delete window.expansionObsoleteControl; });
        assert.equal(toggles, beforeObsolete, "Detached frame handlers are removed.");
        await change("Branch East", "Space");
        assert.equal(await frame().getByText("Detail East", { exact: true }).count(), 1);
        assert.equal(await control("Branch East").evaluate(n => n.ownerDocument.activeElement === n), true);
        await change("Branch East", "Enter"); assert.equal(await frame().getByText("Detail East", { exact: true }).count(), 0);
        await change("Branch East"); await change("Branch West"); await change("Library South");
        await change("Branch West");
        assert.equal(await frame().getByText("Detail East", { exact: true }).count(), 1);
        assert.equal(await frame().getByText("Detail West", { exact: true }).count(), 0);
        assert.equal(await frame().getByText("Detail South", { exact: true }).count(), 1);
        await change("Library"); assert.equal(await control("Branch East").count(), 0);
        assert.equal(await control("Library").evaluate(n => n.ownerDocument.activeElement === n), true);
        await change("Library"); assert.equal(await frame().getByText("Detail East", { exact: true }).count(), 1, "Synthetic descendants retain server state.");
        assert.equal((await token()), originalToken); assert.equal(page.url(), originalUrl); assert.equal(runs, 1);
        assert.equal(parameterRequests, initialParameters, "Toggle interactions do not resolve parameters or load executions.");
        // Scroll restoration is independent of the focus scroll performed by automation.
        await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(0, 80));
        const scrollBefore = await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollY);
        assert.ok(scrollBefore >= 60);
        const revision = await currentRevision();
        await control("Single level").evaluate(n => n.click()); await updated(revision);
        assert.ok(Math.abs(await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollY) - scrollBefore) <= 2);
        await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(0, 0));
        for (const [width, height] of [[1440, 900], [1024, 768], [768, 1024], [390, 844], [320, 640]]) {
            await page.setViewportSize({ width, height });
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true, "Expansion shell fits " + width);
            assert.ok(await control("Library").evaluate(n => n.getBoundingClientRect().width >= 24 && n.getBoundingClientRect().height >= 24));
            await change("Single level", "Enter");
            await scan("expandable report at " + width);
            if (width === 1440 || width === 390) { await page.screenshot({ path: "artifacts/browser/expansion-nested-" + width + ".png", fullPage: true }); }
        }
        await page.setViewportSize({ width: 1440, height: 900 });
        // The sandbox blocks the auditor's child-realm async callbacks. Audit an
        // exact copy of the actual sanitized output in a test-only document; the
        // production frame retains its original CSP and sandbox throughout.
        const auditHtml = await (await context.request.get("/api/executions/" + originalToken + "/viewer")).text();
        const auditPage = await context.newPage();
        try {
            await auditPage.route("**/__report_content_audit", route => route.fulfill({ contentType: "text/html", body: auditHtml }));
            await auditPage.goto("/__report_content_audit");
            const reportAudit = await new (require("@axe-core/playwright").default)({ page: auditPage }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
            assert.deepEqual(reportAudit.violations.map(v => ({ id: v.id, nodes: v.nodes.length })), []); reportAxeScans++;
        } finally { await auditPage.close(); }
        await page.getByRole("button", { name: "Last", exact: true }).click(); await ready();
        await page.waitForFunction(() => document.querySelector("#page-label").textContent === "Page 4 of 4");
        await change("Library");
        assert.equal(await page.locator("#page-label").textContent(), "Page 2 of 2", "Collapse clamps a removed current page.");
        await change("Library");
        await page.getByRole("button", { name: "First", exact: true }).click();
        await page.waitForFunction(() => document.querySelector("#page-label").textContent === "Page 1 of 4"); await ready();
        assert.equal(await frame().getByText("Detail East", { exact: true }).count(), 1);
        const loads = runs;
        await page.reload(); await inputsReady(); await ready(); assert.equal((await token()), originalToken); assert.equal(runs, loads);
        assert.equal(await frame().getByText("Detail East", { exact: true }).count(), 1);
        for (const name of ["PDF", "Excel", "Word", "CSV"]) {
            const downloadEvent = page.waitForEvent("download"); await page.getByRole("link", { name, exact: true }).click(); const download = await downloadEvent;
            assert.equal(await download.failure(), null);
            if (name === "CSV") { assert.match(await fs.readFile(await download.path(), "utf8"), /Visibility: 1iT0;2iT0;3iT0/); }
        }
        // Duplicate-tab history can retain the same execution. Plain copied URLs
        // intentionally contain no session; explicitly duplicate state for this race.
        const tab = await context.newPage();
        await tab.goto(page.url());
        await tab.evaluate(state => history.replaceState(state, ""), await page.evaluate(() => history.state));
        await tab.reload(); await tab.waitForFunction(() => document.querySelector("#report-frame").dataset.toggleBridge === "installed");
        await change("Single level");
        const otherControl = tab.frameLocator("#report-frame").locator("tr").filter({ hasText: "Single level" }).getByRole("button");
        await otherControl.click(); await tab.locator("#resynchronize:visible").waitFor();
        assert.match(await tab.locator("#status").textContent(), /view.*changed|out of date/i);
        await tab.getByRole("button", { name: "Reload report output", exact: true }).click();
        await tab.waitForFunction(() => document.querySelector("#viewer").getAttribute("aria-busy") === "false" && document.querySelector("#resynchronize").hidden);
        await tab.close(); await page.reload(); await inputsReady(); await ready();
        // Independent authenticated user cannot act on this session.
        const otherContext = await context.browser().newContext({ baseURL, ignoreHTTPSErrors: true });
        const other = await otherContext.newPage(); await other.goto("/TestLogin"); await other.getByLabel("Test identity").selectOption("staff-two");
        await other.getByRole("button", { name: "Sign in to demonstration" }).click(); await other.waitForURL(baseURL + "/");
        const foreignAction = await control("Library").getAttribute("data-portal-toggle");
        const csrf = (await (await otherContext.request.get("/api/csrf")).json()).token;
        assert.equal((await otherContext.request.post("/api/executions/" + (await token()) + "/toggle/" + foreignAction, { data: {}, headers: { RequestVerificationToken: csrf } })).status(), 404);
        await otherContext.close();
        // New executions use report defaults or explicitly selected initial state.
        await page.getByLabel("Initially expanded", { exact: true }).selectOption("true"); await inputsReady(); const oldToken = (await token()); await run();
        assert.notEqual((await token()), oldToken); assert.equal((await context.request.get("/api/executions/" + oldToken + "/info")).status(), 404);
        assert.equal(await frame().getByText("Detail East", { exact: true }).count(), 1); assert.equal(await frame().getByText("Single detail", { exact: true }).count(), 1);
        await page.getByLabel("Initially expanded", { exact: true }).selectOption("false"); await inputsReady(); await behavior("Slow toggle");
        for (const [width, height] of [[1440, 900], [390, 640]]) {
            await page.setViewportSize({ width, height });
            let baseline;
            for (const [index, activation] of ["mouse", "Enter", "Space", "mouse"].entries()) {
                await control("Library").scrollIntoViewIfNeeded();
                if (activation !== "mouse") { await keyboardTo(control("Library")); }
                await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(0, 80));
                await page.evaluate(() => window.scrollTo(0, 80));
                const beforeDuplicate = toggles, beforeLoading = await currentRevision(), before = await portalPosition();
                assert.ok(before.reportY >= 60, "Exercise a scrolled report at " + width);
                if (width === 390) { assert.ok(before.outerY >= 60, "Exercise a scrolled portal on mobile."); }
                if (baseline) { stablePosition(baseline, before, "repeated toggle at " + width); } else { baseline = before; }
                await page.locator("#report-frame").evaluate(f => {
                    window.expansionOldFrame = f;
                    window.expansionOldContent = f.contentDocument.body.innerText;
                });
                // The synthetic SOAP ToggleItem already delays its reply. Hold
                // the resulting HTTP response too, so pending-state checks never
                // race the server or depend on the speed of the CI browser.
                let release;
                const gate = new Promise(resolve => { release = resolve; });
                let inFlight;
                const delayedToggle = route => {
                    inFlight = (async () => { const response = await route.fetch(); await gate; await route.fulfill({ response }); })();
                    return inFlight;
                };
                await page.route("**/toggle/*", delayedToggle);
                try {
                    if (activation === "mouse") { await watchPointerFocus(); }
                    else { await checkFocus(control("Library"), true, "before pending " + activation + " at " + width); }
                    const started = page.waitForRequest(r => /\/toggle\//.test(r.url()));
                    try {
                        await Promise.all([started, activation === "mouse" ? control("Library").click() : page.keyboard.press(activation)]);
                    } catch (error) {
                        error.message += " (" + activation + " at " + width + ", " + JSON.stringify(await focusAppearance(control("Library"))) + ")";
                        throw error;
                    }
                    await page.waitForFunction(() => document.querySelector("#viewer").getAttribute("aria-busy") === "true");
                    await checkFocus(control("Library"), activation !== "mouse", "pending " + activation + " at " + width);
                    const pending = await portalPosition(); stablePosition(before, pending, "pending " + activation + " at " + width);
                    await quietAnnouncement("Updating report group…");
                    assert.equal(await control("Library").isDisabled(), true);
                    assert.equal(await page.locator("#next").isDisabled(), true); assert.equal(await page.locator("#run").isDisabled(), true);
                    assert.equal(await page.locator("#pdf").getAttribute("aria-disabled"), "true");
                    assert.equal(await page.locator("#report-frame").isVisible(), true);
                    assert.equal(await page.evaluate(() => window.expansionOldFrame === document.querySelector("#report-frame") &&
                        window.expansionOldContent === window.expansionOldFrame.contentDocument.body.innerText), true, "Old output remains unchanged during mutation.");
                    await control("Library").evaluate(n => { n.click(); n.click(); });
                    if (activation === "mouse") {
                        const box = await control("Library").boundingBox();
                        await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
                    } else { await page.keyboard.press("Enter"); await page.keyboard.press("Space"); }
                    assert.equal(toggles, beforeDuplicate + 1, "Mouse and keyboard duplicates are blocked while busy.");
                    release(); await updated(beforeLoading);
                    assert.equal(toggles, beforeDuplicate + 1);
                    assert.equal(await control("Library").getAttribute("aria-expanded"), index % 2 === 0 ? "true" : "false");
                    assert.equal(await control("Branch East").count(), index % 2 === 0 ? 1 : 0);
                    assert.equal(await control("Library").evaluate(n => n.ownerDocument.activeElement === n), true);
                    await checkFocus(control("Library"), activation !== "mouse", "completed " + activation + " at " + width);
                    if (activation === "mouse") { await finishPointerWatch("replacement at " + width); }
                    await quietAnnouncement("Report group updated.");
                    const after = await portalPosition(); stablePosition(before, after, "completed " + activation + " at " + width);
                    stableLayouts.push({ width, height, activation, expanded: index % 2 === 0, before, pending, after });
                    if (index < 2) { await page.screenshot({ path: "artifacts/browser/expansion-focus-" + (activation === "mouse" ? "pointer" : "keyboard") + "-" + width + ".png", fullPage: true }); }
                } finally {
                    release(); await inFlight; await page.unroute("**/toggle/*", delayedToggle);
                    await page.evaluate(() => { delete window.expansionOldFrame; delete window.expansionOldContent; });
                    await page.evaluate(() => window.expansionStopFocusWatch?.());
                }
            }
            await scan("unobtrusive toggle completion at " + width);
        }
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(0, 0));
        for (const newer of ["parameter", "toolbar", "keyboard activation", "keyboard sibling", "pointer sibling", "unrelated pointer", "report body"]) {
            let releaseFocus, focusInFlight;
            const focusGate = new Promise(resolve => { releaseFocus = resolve; });
            const holdFocus = route => {
                focusInFlight = (async () => { const response = await route.fetch(); await focusGate; await route.fulfill({ response }); })();
                return focusInFlight;
            };
            await page.route("**/toggle/*", holdFocus);
            try {
                const focusRevision = await currentRevision(), before = toggles;
                const started = page.waitForRequest(r => /\/toggle\//.test(r.url()));
                await control("Single level").click(); await started;
                await checkFocus(control("Single level"), false, "pending before " + newer);
                let outside, frameOnly = false;
                if (newer === "parameter") {
                    outside = page.getByLabel("Initially expanded", { exact: true });
                    await outside.focus();
                } else if (newer === "toolbar") {
                    await page.keyboard.press("Shift+Tab");
                    assert.equal(await page.locator("#report-frame").evaluate(f => document.activeElement === f), false);
                    outside = page.locator(":focus");
                } else if (newer === "keyboard activation") {
                    await page.keyboard.press("Enter");
                    await checkFocus(control("Single level"), true, "keyboard supersedes pending pointer intent");
                } else if (newer === "keyboard sibling") {
                    await page.keyboard.press("Tab");
                    await checkFocus(control("Library"), true, "keyboard on sibling during pending update");
                } else if (newer === "pointer sibling") {
                    // aria-disabled keeps controls in the focus order; a real
                    // pointer can move focus but must not start another mutation.
                    const box = await control("Library").boundingBox();
                    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
                    await checkFocus(control("Library"), false, "pointer on sibling during pending update");
                } else {
                    const blank = await page.locator("#report-frame").evaluate(f => {
                        const frameBox = f.getBoundingClientRect(), spacer = f.contentDocument.querySelector(".spacer").getBoundingClientRect();
                        return { x: frameBox.left + 300, y: frameBox.top + spacer.top + 20 };
                    });
                    if (newer === "unrelated pointer") {
                        // Model scrollbar input: genuine pointer events in the
                        // report whose default action leaves the expander focused.
                        await page.locator("#report-frame").evaluate(f => f.contentDocument.addEventListener("pointerdown", event => event.preventDefault(), { once: true, capture: true }));
                        await page.mouse.click(blank.x, blank.y);
                        await checkFocus(control("Single level"), false, "unrelated pointer preserves pending focus appearance");
                    } else {
                        await page.mouse.click(blank.x, blank.y);
                        assert.equal(await page.locator("#report-frame").evaluate(f => f.contentDocument.activeElement === f.contentDocument.body), true);
                        frameOnly = true;
                    }
                }
                const outsideHandle = outside && await outside.elementHandle();
                assert.equal(toggles, before + 1, "Newer input does not bypass the duplicate guard");
                releaseFocus(); await updated(focusRevision);
                if (outside) {
                    assert.equal(await outsideHandle.evaluate(n => n.ownerDocument.activeElement === n), true, newer + ": completion must not steal focus");
                    assert.equal(await frame().locator("[data-portal-pointer-focus]").count(), 0, "No stale suppression when focus left the frame");
                    await outsideHandle.dispose();
                } else if (frameOnly) {
                    assert.equal(await page.locator("#report-frame").evaluate(f => document.activeElement === f && f.contentDocument.activeElement === f.contentDocument.body), true,
                        "Explicit report-body focus departure must not restore the old expander");
                    assert.equal(await frame().locator("[data-portal-pointer-focus]").count(), 0);
                } else {
                    const label = newer.endsWith("sibling") ? "Library" : "Single level";
                    await checkFocus(control(label), newer.startsWith("keyboard"), "restored newer " + newer);
                    assert.equal(await control(label === "Library" ? "Single level" : "Library").getAttribute("data-portal-pointer-focus"), null);
                }
                await keyboardTo(control("Single level"));
                await checkFocus(control("Single level"), true, "keyboard navigation after " + newer);
                assert.equal(await frame().locator("[data-portal-pointer-focus]").count(), 0);
            } finally { releaseFocus(); await focusInFlight; await page.unroute("**/toggle/*", holdFocus); }
        }
        // Test-only nested containers are present in both documents, so the
        // production index-based scroll capture exercises two independent axes.
        const nestedScroll = async route => {
            const response = await route.fetch();
            const fixture = '<div id="scroll-outer" style="width:300px;height:60px;overflow:auto"><div style="width:600px;height:180px"><div id="scroll-inner" style="width:300px;height:60px;overflow:auto"><div style="width:600px;height:200px">Nested scroll fixture</div></div></div></div>';
            await route.fulfill({ response, body: (await response.text()).replace("</body>", fixture + "</body>") });
        };
        const nestedPosition = () => page.locator("#report-frame").evaluate(f => ["scroll-outer", "scroll-inner"].map(id => {
            const n = f.contentDocument.getElementById(id);
            return { id, x: n.scrollLeft, y: n.scrollTop };
        }));
        await page.route("**/viewer?revision=*", nestedScroll);
        try {
            await behavior("Normal");
            for (const key of [null, "Enter"]) {
                if (key) { await keyboardTo(control("Single level")); }
                await page.locator("#report-frame").evaluate(f => {
                    f.contentDocument.getElementById("scroll-outer").scrollTo(25, 30);
                    f.contentDocument.getElementById("scroll-inner").scrollTo(35, 40);
                });
                const before = await nestedPosition();
                assert.deepEqual(before, [{ id: "scroll-outer", x: 25, y: 30 }, { id: "scroll-inner", x: 35, y: 40 }]);
                await change("Single level", key);
                assert.deepEqual(await nestedPosition(), before, "Nested container scroll survives " + (key || "mouse") + " replacement");
            }
        } finally { await page.unroute("**/viewer?revision=*", nestedScroll); }
        // After a successful mutation with failed Render, retain and mark the old
        // frame. Recovery only renders; it never retries ToggleItem.
        for (const mode of ["Render fails once after toggle", "Lost toggle reply", "Toggle not found"]) {
            await page.setViewportSize(mode === "Lost toggle reply" ? { width: 390, height: 640 } : { width: 1440, height: 900 });
            await behavior(mode); const before = toggles, beforeSync = syncs;
            await control("Library").click(); await page.locator("#resynchronize:visible").waitFor();
            await checkFocus(control("Library"), false, "pointer focus after " + mode);
            assert.equal(await frame().getByText("Branch East", { exact: true }).count(), 0);
            assert.equal(await page.locator("#report-frame").getAttribute("data-stale"), "true");
            assert.equal(await page.locator("#report-frame").evaluate(f => getComputedStyle(f).borderLeftWidth === "0px" && getComputedStyle(f).borderRightWidth === "0px"), true, "Stale output retains zero side borders");
            assert.equal(await page.locator("#status").getAttribute("role"), "alert");
            assert.equal(await page.locator("#status").getAttribute("aria-live"), "assertive");
            assert.equal(await page.locator("#status").evaluate(n => {
                const box = n.getBoundingClientRect(), css = getComputedStyle(n);
                return Boolean(n.textContent.trim()) && !n.hidden && css.visibility === "visible" && css.clipPath === "none" && box.height > 5 && box.width > 5;
            }), true, "Real errors remain visible and announced.");
            assert.equal(await control("Library").isDisabled(), true, "Stale actions stay disabled until recovery.");
            assert.equal(await page.locator("#pdf").getAttribute("aria-disabled"), "true");
            const focused = await page.evaluate(() => document.activeElement.id);
            await page.locator("#toggle-parameters").evaluate(n => n.click());
            assert.equal(await page.evaluate(() => document.activeElement.id), focused, "Collapsing cannot steal focus from report output.");
            assert.equal(await page.locator("#parameters").isHidden(), true);
            assert.equal(await page.getByRole("button", { name: "Reload report output", exact: true }).isVisible(), true, "Essential recovery remains accessible with collapsed parameters.");
            assert.equal(await page.getByRole("alert").isVisible(), true);
            await scan("recoverable " + mode);
            let releaseRecovery, recoveryInFlight;
            const recoveryGate = new Promise(resolve => { releaseRecovery = resolve; });
            const delayedRecovery = route => {
                recoveryInFlight = (async () => { const response = await route.fetch(); await recoveryGate; await route.fulfill({ response }); })();
                return recoveryInFlight;
            };
            await page.route("**/synchronize", delayedRecovery);
            try {
                await page.getByRole("button", { name: "Reload report output", exact: true }).click();
                await page.waitForFunction(() => document.querySelector("#viewer").getAttribute("aria-busy") === "true");
                await quietAnnouncement("Reloading report output…");
                assert.equal(await page.locator("#report-frame").isVisible(), true);
                assert.equal(await page.locator("#resynchronize").isDisabled(), true);
                releaseRecovery(); await ready();
            } finally { releaseRecovery(); await recoveryInFlight; await page.unroute("**/synchronize", delayedRecovery); }
            assert.equal(syncs, beforeSync + 1); assert.equal(toggles, before + 1);
            await quietAnnouncement("Report output synchronized.");
            assert.equal(await page.locator("#toggle-parameters").getAttribute("aria-expanded"), "false", "Recovery preserves the user's collapsed state.");
            assert.equal(await page.locator("#parameters").isHidden(), true);
            await page.locator("#toggle-parameters").click();
            assert.equal(await frame().locator("[data-portal-pointer-focus]").count(), 0, "Recovery does not transfer stale suppression");
            await keyboardTo(control("Library"));
            await checkFocus(control("Library"), true, "keyboard after recovery from " + mode);
            assert.equal(await page.locator("#resynchronize").isHidden(), true);
            assert.equal(await control("Branch East").count(), mode === "Toggle not found" ? 0 : 1);
        }
        await behavior("Lost toggle reply"); await control("Library").click(); await page.locator("#resynchronize:visible").waitFor();
        const dirtyToken = (await token()), beforeRefresh = toggles; await page.reload(); await inputsReady(); await ready();
        assert.equal((await token()), dirtyToken); assert.equal(toggles, beforeRefresh); assert.equal(await control("Branch East").count(), 1, "Refresh recovers uncertain mutation with pure rendering.");
        await behavior("Normal");
        let releasePage, pageStarted;
        const pageGate = new Promise(resolve => { releasePage = resolve; });
        const pageResponseReady = new Promise(resolve => { pageStarted = resolve; });
        const delayedPage = async route => { const response = await route.fetch(); pageStarted(); await pageGate; await route.fulfill({ response }).catch(() => {}); };
        const failedRerun = route => route.fulfill({ status: 502, contentType: "application/json", json: { code: "service", message: "Synthetic rerun failure." } });
        await page.evaluate(() => {
            window.expansionNativeFetch = window.fetch;
            window.fetch = (url, init) => window.expansionNativeFetch(url, String(url).endsWith("/page/2") ? { ...init, signal: undefined } : init);
        });
        await page.route("**/page/2", delayedPage); await page.route("**/run", failedRerun);
        try {
            const previous = (await token());
            await page.getByRole("button", { name: "Next", exact: true }).click(); await pageResponseReady;
            await page.getByRole("button", { name: "View report", exact: true }).click(); await page.locator("#resynchronize:visible").waitFor();
            assert.equal((await token()), previous); assert.equal(await frame().getByRole("heading", { name: "Page 1", exact: true }).count(), 1);
            assert.equal(await page.locator("#pdf").getAttribute("aria-disabled"), "true");
            await scan("interrupted page and failed replacement");
            await page.getByRole("button", { name: "Reload report output", exact: true }).click(); await ready();
            assert.equal(await page.locator("#page-label").textContent(), "Page 2 of 2");
            releasePage();
            assert.equal(await frame().getByRole("heading", { name: "Page 2", exact: true }).count(), 1);
        } finally {
            releasePage(); await page.unroute("**/page/2", delayedPage); await page.unroute("**/run", failedRerun);
            await page.evaluate(() => { window.fetch = window.expansionNativeFetch; delete window.expansionNativeFetch; });
        }
        await behavior("Unknown page count"); assert.match(await page.locator("#page-label").textContent(), /total not yet known/);
        await change("Library"); assert.match(await page.locator("#page-label").textContent(), /Page 1.*total not yet known/);
        // Server session removal uses the same viewer path as timed expiry; the
        // .NET clock-driven tests independently exercise real timed expiry.
        const unavailable = (await token()); assert.equal((await post("/api/executions/" + unavailable + "/cancel")).status(), 204);
        await control("Library").click(); await page.locator("#status[role=alert]").waitFor();
        assert.match(await page.locator("#status").textContent(), /expired or is unavailable/);
        assert.equal(await page.locator("#next").isDisabled(), true);
        await checkFocus(control("Library"), false, "session expired after pointer-restored focus");
        assert.equal(await frame().locator("[data-portal-pointer-focus]").count(), 1);
        const expiredToggles = toggles;
        await page.keyboard.press("Enter");
        await checkFocus(control("Library"), true, "keyboard focus remains visible on retained expired output");
        assert.equal(toggles, expiredToggles, "Expired output cannot process keyboard activation");
        assert.equal(await frame().locator("[data-portal-pointer-focus]").count(), 0);
        await behavior("Normal");
        // Hold an actual completed response while Close cancels the session. A
        // late response from the removed frame cannot replace the new page.
        let release, started;
        const gate = new Promise(resolve => { release = resolve; });
        const responseReady = new Promise(resolve => { started = resolve; });
        const delayed = async route => { const response = await route.fetch(); started(); await gate; await route.fulfill({ response }).catch(() => {}); };
        await page.route("**/toggle/*", delayed);
        const closedToken = (await token()); await control("Library").click(); await responseReady;
        await page.getByRole("button", { name: "Close", exact: true }).click(); await page.waitForURL(/\/\?folder=/);
        release(); await page.unroute("**/toggle/*", delayed);
        assert.equal((await context.request.get("/api/executions/" + closedToken + "/viewer")).status(), 404);
        assert.equal(await page.locator("#report-frame").count(), 0);
        // Add the real standard-viewer source grammar without replacing the
        // original nonce callback and ShowHideToggle scenarios above.
        await page.goto(entry); await inputsReady(); await behavior("ReportViewer capture");
        const numericToken = (await token()), numericRuns = runs, numericParameters = parameterRequests;
        const numeric = value => frame().locator("table.system-items").getByRole("button", { name: new RegExp("^(Expand|Collapse) report group: " + value + "$") });
        const numericChange = async (value, key) => {
            const before = await currentRevision();
            if (key) { await keyboardTo(numeric(value)); await checkFocus(numeric(value), true, "numeric " + value + " before " + key); await page.keyboard.press(key); }
            else { await numeric(value).click(); }
            await updated(before);
            assert.equal(await numeric(value).evaluate(n => n.ownerDocument.activeElement === n), true);
            await checkFocus(numeric(value), Boolean(key), "numeric " + value + " after " + (key || "mouse"));
            assert.equal(await numeric(value).evaluate(n => n.nextElementSibling.textContent), value);
        };
        assert.equal(await numeric("15").getAttribute("aria-expanded"), "false");
        assert.equal(await numeric("23").count(), 1);
        assert.equal(await frame().getByText("Ordinary numeric cell", { exact: true }).count(), 1);
        const nativeHtml = await (await context.request.get("/api/executions/" + numericToken + "/viewer")).text();
        assert.doesNotMatch(nativeHtml, /InvokeReportAction|\$get|onclick|onkeypress|ReportControl|23[012]iT0R0x11|Reserved\.ReportViewerWebControl/);
        await numericChange("15"); assert.equal(await numeric("15").getAttribute("aria-expanded"), "true");
        await numericChange("14", "Enter");
        assert.equal(await frame().getByText("Nested numeric detail", { exact: true }).count(), 1);
        await numericChange("23");
        await numericChange("15", "Space");
        assert.equal(await numeric("14").count(), 0);
        assert.equal(await frame().getByText("Independent numeric detail", { exact: true }).count(), 1);
        await numericChange("15");
        assert.equal(await frame().getByText("Nested numeric detail", { exact: true }).count(), 1);
        const fullWidthLayouts = [];
        for (const [width, height] of [[1440, 900], [1024, 768], [768, 1024], [390, 844], [320, 640]]) {
            await page.setViewportSize({ width, height });
            await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(0, 0));
            if (width === 1440 || width === 390) {
                // Test-only reconstruction of the previous spacing rules, using
                // the same current fixture. Removed before every layout assertion.
                await page.evaluate(inset => {
                    const sheet = [...document.styleSheets].find(s => s.href?.includes("/css/site.css"));
                    window.expansionPriorRuleCount = sheet.cssRules.length;
                    for (const rule of [`main.report-main{padding-inline:${inset}}`, "#report .parameter-section{padding-inline:0}", "#report .viewer-toolbar{padding-inline:0}", "#report iframe{border-inline:1px solid #b6c6d8}"]) { sheet.insertRule(rule, sheet.cssRules.length); }
                }, width <= 600 ? ".6rem" : ".9rem");
                await page.locator("#report-frame").evaluate(f => { const s = f.contentDocument.createElement("style"); s.id = "previous-spacing-fixture"; s.textContent = "html,body{margin:8px;padding:0}"; f.contentDocument.head.append(s); });
                if (width === 390) { await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(150, 230)); }
                await page.screenshot({ path: "artifacts/browser/expansion-padding-before-" + width + ".png", fullPage: true });
                await page.evaluate(() => {
                    const sheet = [...document.styleSheets].find(s => s.href?.includes("/css/site.css"));
                    while (sheet.cssRules.length > window.expansionPriorRuleCount) { sheet.deleteRule(sheet.cssRules.length - 1); }
                    delete window.expansionPriorRuleCount;
                });
                await page.locator("#report-frame").evaluate(f => f.contentDocument.getElementById("previous-spacing-fixture").remove());
                await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(0, 0));
            }
            const layout = await page.locator("#report-frame").evaluate(f => {
                const box = f.getBoundingClientRect(), workspace = document.querySelector("main").getBoundingClientRect();
                const doc = f.contentDocument, body = f.contentWindow.getComputedStyle(doc.body), root = f.contentWindow.getComputedStyle(doc.documentElement);
                const table = doc.querySelector("table.system-items"), cell = table.querySelector("td"), button = table.querySelector("button"), value = button.nextElementSibling;
                const buttonBox = button.getBoundingClientRect(), valueBox = value.getBoundingClientRect(), cellBox = cell.getBoundingClientRect();
                return { width: innerWidth, left: box.left, right: box.right, workspaceLeft: workspace.left, workspaceRight: workspace.right,
                    shellOverflow: document.documentElement.scrollWidth - innerWidth, bodyMargin: body.margin, bodyPadding: body.padding,
                    rootMargin: root.margin, rootPadding: root.padding, borderLeft: getComputedStyle(f).borderLeftWidth, borderRight: getComputedStyle(f).borderRightWidth,
                    tableWidth: table.getBoundingClientRect().width, cellPadding: f.contentWindow.getComputedStyle(cell).padding,
                    valueWidth: valueBox.width, adjacent: Math.abs(buttonBox.right - valueBox.left) < 1,
                    compact: buttonBox.width <= 18 && buttonBox.height <= 18, cellHeight: cellBox.height };
            });
            assert.ok(Math.abs(layout.left - layout.workspaceLeft) <= 1 && Math.abs(layout.right - layout.workspaceRight) <= 1);
            assert.equal(layout.left, 0); assert.ok(Math.abs(layout.right - width) <= 1);
            for (const name of ["bodyMargin", "bodyPadding", "rootMargin", "rootPadding", "borderLeft", "borderRight"]) { assert.equal(layout[name], "0px", name + " at " + width); }
            assert.ok(layout.shellOverflow <= 1); assert.equal(layout.cellPadding, "8px");
            assert.ok(Math.abs(layout.tableWidth - 1200) <= 1 && Math.abs(layout.valueWidth - 12.45 * 96 / 25.4) < 1);
            assert.ok(layout.adjacent && layout.compact); assert.ok(layout.cellHeight < 42, "Compact controls retain native row height");
            if (width < 1200) {
                await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(10000, 0));
                assert.ok(await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollX > 100));
                await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(0, 0));
            }
            await numericChange("14", "Space"); await numericChange("14", "Enter");
            await scan("ReportViewer numeric cells and full width at " + width);
            assert.equal(await page.getByRole("button", { name: "View report", exact: true }).isEnabled(), true);
            assert.equal(await page.getByRole("link", { name: "CSV", exact: true }).isVisible(), true);
            if (width === 1440 || width === 390) {
                if (width === 390) { await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(150, 230)); }
                await page.screenshot({ path: "artifacts/browser/expansion-padding-after-" + width + ".png", fullPage: true });
                await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(0, 0));
            }
            fullWidthLayouts.push(layout);
        }
        assert.equal((await token()), numericToken); assert.equal(runs, numericRuns); assert.equal(parameterRequests, numericParameters);
        assert.equal(await page.locator("#report-frame").getAttribute("sandbox"), "allow-same-origin");
        assert.equal(await page.evaluate(() => document.body.dataset.pwned), undefined);
        // The new representation also travels through the existing paging/export path.
        await page.getByRole("button", { name: "Next", exact: true }).click();
        await page.waitForFunction(() => document.querySelector("#page-label").textContent === "Page 2 of 2"); await ready();
        assert.equal(await numeric("15").getAttribute("aria-expanded"), "true");
        for (const name of ["PDF", "Excel", "Word", "CSV"]) {
            const downloadEvent = page.waitForEvent("download"); await page.getByRole("link", { name, exact: true }).click();
            assert.equal(await (await downloadEvent).failure(), null);
        }
        // Audit an exact copy containing the new native-cell representation too.
        const numericAudit = await context.newPage();
        try {
            const copy = await (await context.request.get("/api/executions/" + numericToken + "/viewer")).text();
            await numericAudit.route("**/__numeric_report_audit", route => route.fulfill({ contentType: "text/html", body: copy }));
            await numericAudit.goto("/__numeric_report_audit");
            const result = await new (require("@axe-core/playwright").default)({ page: numericAudit }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
            assert.deepEqual(result.violations.map(v => ({ id: v.id, nodes: v.nodes.length })), []); reportAxeScans++;
        } finally { await numericAudit.close(); }
        // A hasTouch context produces a genuine touch pointerdown and synthesized
        // click in the same script-free report, followed by real keyboard input.
        const touchContext = await context.browser().newContext({ baseURL, ignoreHTTPSErrors: true, hasTouch: true,
            viewport: { width: 390, height: 844 }, storageState: await context.storageState() });
        touchContext.on("request", count);
        try {
            const touch = await touchContext.newPage();
            touch.on("pageerror", error => assert.fail(error.message));
            await touch.goto(page.url());
            await touch.evaluate(state => history.replaceState(state, ""), await page.evaluate(() => history.state));
            await touch.reload();
            await touch.waitForFunction(() => document.querySelector("#report-frame").dataset.toggleBridge === "installed" && document.querySelector("#viewer").getAttribute("aria-busy") === "false");
            const target = touch.frameLocator("#report-frame").getByRole("button", { name: /^(Expand|Collapse) report group: 15$/ });
            const before = await touch.locator("#report-frame").evaluate(f => Number(f.contentDocument.querySelector('meta[name="ssrs-portal-revision"]').content));
            let releaseTouch, touchInFlight;
            const touchGate = new Promise(resolve => { releaseTouch = resolve; });
            const holdTouch = route => {
                touchInFlight = (async () => { const response = await route.fetch(); await touchGate; await route.fulfill({ response }); })();
                return touchInFlight;
            };
            await touch.route("**/toggle/*", holdTouch);
            try {
                await target.tap();
                await touch.waitForFunction(() => document.querySelector("#viewer").getAttribute("aria-busy") === "true");
                await checkFocus(target, false, "touch while pending at 390px");
                const pendingTouchCdp = await touchContext.newCDPSession(touch);
                try {
                    const sibling = touch.frameLocator("#report-frame").getByRole("button", { name: /^(Expand|Collapse) report group: 23$/ });
                    await sibling.scrollIntoViewIfNeeded();
                    const box = await sibling.boundingBox(), beforeDuplicate = toggles;
                    await pendingTouchCdp.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [{ x: box.x + box.width / 2, y: box.y + box.height / 2 }] });
                    assert.equal(await sibling.getAttribute("data-portal-pointer-focus"), "true");
                    await target.evaluate(n => n.click());
                    await pendingTouchCdp.send("Input.dispatchTouchEvent", { type: "touchCancel", touchPoints: [] });
                    assert.equal(await sibling.getAttribute("data-portal-pointer-focus"), null, "Blocked fallback click cannot orphan an unfocused pointer marker");
                    await checkFocus(target, false, "blocked fallback during sibling touch keeps focused pointer styling");
                    assert.equal(toggles, beforeDuplicate, "Fallback click during pending touch does not duplicate ToggleItem");
                } finally { await pendingTouchCdp.detach(); }
                releaseTouch();
                await touch.waitForFunction(revision => Number(document.querySelector("#report-frame").contentDocument.querySelector('meta[name="ssrs-portal-revision"]').content) > revision && document.querySelector("#viewer").getAttribute("aria-busy") === "false", before);
                await checkFocus(target, false, "touch after replacement at 390px");
            } finally { releaseTouch(); await touchInFlight; await touch.unroute("**/toggle/*", holdTouch); }
            const touchCdp = await touchContext.newCDPSession(touch);
            try {
                const sibling = touch.frameLocator("#report-frame").getByRole("button", { name: /^(Expand|Collapse) report group: 23$/ });
                await sibling.scrollIntoViewIfNeeded();
                const box = await sibling.boundingBox();
                const point = await touch.locator("#report-frame").evaluate(f => {
                    const box = f.getBoundingClientRect();
                    return { x: box.right - 10, y: Math.max(10, box.top + 15) };
                });
                for (const [label, location] of [["unrelated", point], ["sibling", { x: box.x + box.width / 2, y: box.y + box.height / 2 }]]) {
                    await touchCdp.send("Input.dispatchTouchEvent", { type: "touchStart", touchPoints: [location] });
                    if (label === "sibling") { assert.equal(await sibling.getAttribute("data-portal-pointer-focus"), "true", "Native sibling touch creates a candidate before cancellation"); }
                    await checkFocus(target, false, label + " touchstart preserves the currently focused control");
                    await touchCdp.send("Input.dispatchTouchEvent", { type: "touchCancel", touchPoints: [] });
                    await checkFocus(target, false, label + " canceled touch preserves pointer focus");
                    assert.equal(await sibling.getAttribute("data-portal-pointer-focus"), null, "Canceled unfocused target has no stale suppression");
                }
            } finally { await touchCdp.detach(); }
            await touch.keyboard.press("Tab"); await touch.keyboard.press("Shift+Tab");
            await checkFocus(target, true, "keyboard after touch at 390px");
            const revision = await touch.locator("#report-frame").evaluate(f => Number(f.contentDocument.querySelector('meta[name="ssrs-portal-revision"]').content));
            await touch.keyboard.press("Enter");
            await touch.waitForFunction(previous => Number(document.querySelector("#report-frame").contentDocument.querySelector('meta[name="ssrs-portal-revision"]').content) > previous && document.querySelector("#viewer").getAttribute("aria-busy") === "false", revision);
            await checkFocus(target, true, "keyboard replacement after touch at 390px");
            assert.equal(await touch.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
        } finally { touchContext.off("request", count); await touchContext.close(); }
        assert.deepEqual([...new Set(requests.filter(url => new URL(url).origin !== new URL(baseURL).origin))], [], "No direct SSRS or external requests.");
        const evidence = { expandableReports: true, singleLevel: true, nestedLevels: 3, independentSiblings: true, keyboardToggle: true,
            focusRestored: true, scrollPreserved: true, parentBridgeInScriptFreeFrame: true, duplicateToggleProtected: true, staleFramesRejected: true,
            mutationRecovery: true, sharedTabRecovery: true, unknownPagination: true, reportAxeScans, expansionViewports: 5, toggleRequests: toggles,
            reportViewerSourceGrammar: true, numericSiblingPreserved: true, fullWidthLayouts, stableLayouts,
            quietToggleAnnouncements: true, visibleRecoveryErrors: true,
            focusStyleChecks: focusChecks.length, keyboardFocusChecks: focusChecks.filter(check => check.keyboard).length,
            pointerFocusChecks: focusChecks.filter(check => !check.keyboard).length, pointerPaintChecks, pendingFocusTransitions: 7,
            nestedScrollPreserved: true, touchFocusVerified: true, trustedDetailZeroClickVerified: true,
            paddingBeforeScreenshots: "Test-only reconstruction of prior spacing on the current structural fixture" };
        await fs.writeFile("artifacts/browser/expansion-verification.json", JSON.stringify({ ...evidence, focusChecks }, null, 2));
        return evidence;
    } finally { context.off("request", count); }
};
