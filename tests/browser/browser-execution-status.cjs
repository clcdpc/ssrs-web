"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");

module.exports = async ({ page, context, inputsReady, scan }) => {
    const catalog = await (await context.request.get("/api/catalog")).json();
    const sizes = [[1440, 900], [1024, 768], [768, 1024], [390, 844], [320, 640]];
    const samples = [];
    const collapseSamples = [];
    const token = () => page.evaluate(() => history.state?.ssrsReport?.token ?? null);
    const idle = () => page.waitForFunction(() => document.querySelector("#run").dataset.executing === "false");
    const ready = previous => page.waitForFunction(old => {
        const current = (history.state?.ssrsReport?.token ?? null);
        return current && current !== old && document.querySelector("#parameters").getAttribute("aria-busy") === "false";
    }, previous);
    const open = async name => {
        await page.goto("/Report?id=" + catalog.find(e => e.name === name).id); await inputsReady();
        await page.evaluate(async () => {
            await document.fonts.ready;
            await Promise.all([...document.images].map(image => image.decode().catch(() => {})));
        });
    };
    const submit = () => page.locator("#parameters").evaluate(form => form.requestSubmit());
    const run = async () => { const previous = (await token()); await submit(); await ready(previous); };
    const close = async () => { await page.locator("#cancel-session").click(); await page.waitForURL(/\/\?folder=/); };
    const api = async value => (await context.request.get("/api/executions/" + value + "/info")).status();
    const within = async promise => {
        let timer;
        try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error("Delayed execution did not reach its gate.")), 20000); })]); }
        finally { clearTimeout(timer); }
    };
    // Gate SOAP's reply, staged HTML and commit independently. Later matching
    // requests fall through, allowing an obsolete reply to race a newer run.
    const delay = async (pattern, fetchFirst = true) => {
        const entered = Promise.withResolvers(), proceed = Promise.withResolvers(), finished = Promise.withResolvers();
        let intercepted = false;
        const handler = async route => {
            if (intercepted) { await route.fallback(); return; }
            intercepted = true;
            let response;
            try { response = fetchFirst ? await route.fetch() : null; }
            catch (error) { entered.reject(error); finished.resolve(); return; }
            entered.resolve(response);
            await proceed.promise;
            try {
                if (response) { await route.fulfill({ response }).catch(() => {}); } // Cancellation may already have aborted the browser request.
                else { await route.continue(); }
            } finally { finished.resolve(); }
        };
        await page.route(pattern, handler);
        return { entered: () => within(entered.promise), release: () => proceed.resolve(),
            dispose: async () => { proceed.resolve(); if (intercepted) { await within(finished.promise); } await page.unroute(pattern, handler); } };
    };
    const layout = () => page.evaluate(() => {
        const rect = n => { const r = n.getBoundingClientRect(); return { x: r.x + scrollX, y: r.y + scrollY, width: r.width, height: r.height }; };
        const get = s => rect(document.querySelector(s));
        return { section: get(".parameter-section"), divider: get(".parameter-divider"), actions: get(".report-actions"),
            run: get("#run"), cancel: get("#cancel-run"), fields: [...document.querySelectorAll(".parameter-field")].map(rect),
            toolbar: document.querySelector("#viewer").hidden ? null : get(".viewer-toolbar"),
            frame: document.querySelector("#viewer").hidden ? null : get("#report-frame"),
            feedback: get(".workspace-feedback").height, overflow: document.documentElement.scrollWidth > innerWidth + 1, scrollX, scrollY };
    });
    const near = (a, b, label) => assert.ok(Math.abs(a - b) <= 1, `${label}: ${a}, expected ${b} ±1px`);
    const stable = async (before, label) => {
        const after = await layout();
        const compare = (actual, expected, name) => { for (const key of ["x", "y", "width", "height"]) { near(actual[key], expected[key], `${label} ${name}.${key}`); } };
        for (const name of ["section", "divider", "actions", "run", "cancel", "toolbar", "frame"]) {
            if (before[name]) { compare(after[name], before[name], name); }
        }
        assert.equal(after.fields.length, before.fields.length);
        after.fields.forEach((field, i) => compare(field, before.fields[i], "field " + i));
        assert.equal(after.overflow, false, label + " page overflow");
        assert.equal(after.feedback, 0, label + " routine feedback occupies no row");
        near(after.scrollX, before.scrollX, label + " horizontal scroll");
        near(after.scrollY, before.scrollY, label + " vertical scroll");
        const separate = (a, b) => a.x + a.width <= b.x + 1 || b.x + b.width <= a.x + 1 || a.y + a.height <= b.y + 1 || b.y + b.height <= a.y + 1;
        for (const field of after.fields) { assert.ok(separate(field, after.actions), label + " actions must not overlap fields"); }
        if (after.toolbar) {
            near(after.toolbar.y, after.divider.y + after.divider.height, label + " toolbar follows divider");
            near(after.frame.y, after.toolbar.y + after.toolbar.height, label + " iframe follows toolbar");
        }
        samples.push({ label, ...after });
    };
    const routine = async text => {
        assert.equal(await page.locator("#status").textContent(), text);
        assert.equal(await page.getByRole("status").count(), 1);
        assert.deepEqual(await page.locator("#status").evaluate(n => {
            const s = getComputedStyle(n), r = n.getBoundingClientRect();
            return [n.getAttribute("aria-live"), n.getAttribute("aria-atomic"), n.hidden, s.display === "none", s.position, r.width <= 1 && r.height <= 1, s.clipPath];
        }), ["polite", "true", false, false, "absolute", true, "inset(50%)"]);
        assert.ok((await page.getByRole("status").ariaSnapshot()).includes(text), "Progress stays in the accessibility tree.");
    };
    const buttonState = async running => {
        assert.equal(await page.getByRole("button", { name: running ? "Running…" : "View report", exact: true }).count(), 1);
        assert.equal(await page.getByRole("button", { name: running ? "View report" : "Running…", exact: true }).count(), 0);
        assert.equal(await page.locator("#run").isDisabled(), running);
        assert.equal(await page.getByRole("button", { name: "Cancel request", exact: true }).count(), running ? 1 : 0);
        assert.equal(await page.locator("#cancel-run").isHidden(), !running);
        assert.equal(await page.locator(running ? ".run-label" : ".run-pending").isHidden(), true);
        assert.equal(await page.locator(running ? ".run-pending" : ".run-label").isVisible(), true);
        assert.equal(await page.locator(".run-spinner").getAttribute("aria-hidden"), "true");
        assert.equal(await page.locator("#run").getAttribute("aria-live"), null, "The button must not duplicate the live-region announcement.");
        assert.equal(await page.locator("#run").evaluate(n => n.form.id === "parameters" && n.type === "submit"), true);
        if (!running) { assert.equal(await page.locator(".run-spinner").evaluate(n => n.getAnimations().length), 0, "Idle buttons do not animate."); }
        assert.equal(await page.locator(running ? ".run-pending" : ".run-label").evaluate(n => {
            const label = n.getBoundingClientRect(), button = n.closest("button").getBoundingClientRect();
            return label.left >= button.left && label.right <= button.right && label.top >= button.top && label.bottom <= button.bottom;
        }), true, "The complete label remains readable inside the button.");
    };
    const retained = async (previous, frame) => {
        assert.equal((await token()), previous); assert.equal(await api(previous), 200);
        assert.equal(await frame.evaluate(n => n === document.querySelector("#report-frame") && n.isConnected), true);
        assert.equal(await page.locator("#report-frame").isVisible(), true);
        await page.frameLocator("#report-frame").getByRole("heading", { name: "Page 1", exact: true }).waitFor();
    };
    const collapsed = async (cancellable, label) => {
        assert.equal(await page.locator("#toggle-parameters").getAttribute("aria-expanded"), "false");
        assert.equal(await page.locator("#toggle-parameters").getAttribute("aria-controls"), "parameters");
        assert.equal(await page.getByRole("button", { name: "Show parameters", exact: true }).isVisible(), true);
        assert.equal(await page.locator("#parameter-controls,#run,#dirty").evaluateAll(nodes => nodes.every(n => !n.checkVisibility())), true);
        assert.equal(await page.getByRole("button", { name: /^(View report|Running…)$/ }).count(), 0, "The hidden submit button is absent from the accessibility tree.");
        assert.equal(await page.getByRole("button", { name: "Cancel request", exact: true }).count(), cancellable ? 1 : 0);
        assert.equal(await page.locator("#cancel-run").isVisible(), cancellable);
        assert.equal(await page.locator("#parameters").isVisible(), cancellable);
        const g = await layout();
        assert.equal(g.overflow, false, label + " page overflow");
        assert.equal(g.feedback, 0, label + " routine progress has no height");
        if (cancellable) {
            assert.equal(await page.locator("#cancel-run").isEnabled(), true);
            near(g.section.height, g.cancel.height, label + " only Cancel occupies height");
            near(g.actions.width, g.cancel.width, label + " no reserved View report width");
            assert.ok(g.cancel.y + g.cancel.height <= g.divider.y, "Cancel cannot overlap the separator or output.");
            if (await page.evaluate(() => innerWidth <= 600 || matchMedia("(pointer:coarse)").matches)) { assert.ok(g.cancel.height >= 44); }
        } else {
            assert.equal(g.section.height, 0, label + " complete idle/commit interface reclaimed");
            assert.equal(g.actions.height, 0, label + " no empty action row");
        }
        if (g.toolbar) {
            near(g.toolbar.y, g.divider.y + g.divider.height, label + " toolbar follows separator");
            near(g.frame.y, g.toolbar.y + g.toolbar.height, label + " frame follows toolbar");
            near(g.frame.width, g.divider.width, label + " full report width");
        }
        const centered = await page.locator("#toggle-parameters").evaluate(n => {
            const a = n.getBoundingClientRect(), b = n.parentElement.getBoundingClientRect(); return Math.abs(a.x + a.width / 2 - b.x - b.width / 2) <= .5;
        });
        assert.equal(centered, true);
        assert.equal(await page.evaluate(() => document.activeElement === document.body || document.activeElement.checkVisibility()), true, "Focus cannot remain in hidden content.");
        collapseSamples.push({ label, cancellable, ...g });
    };

    // One and many real parameters, initially empty and already-rendered viewers.
    for (const name of ["One Input", "Default Showcase"]) {
        for (const [width, height] of sizes) {
            await page.setViewportSize({ width, height }); await open(name); await buttonState(false);
            assert.equal((await token()), null); await routine("Report inputs updated.");
            if (width <= 600) { assert.ok((await layout()).run.height >= 44); }
            for (const rerun of [false, true]) {
                const label = `${name} ${width} ${rerun ? "rerun" : "initial"}`;
                const before = await layout(), previous = (await token());
                const oldFrame = previous ? await page.locator("#report-frame").elementHandle() : null;
                const runGate = await delay("**/run"), frameGate = await delay("**/executions/*/viewer*"), commitGate = await delay("**/executions/*/commit", false);
                try {
                    await scan(label + " idle");
                    await submit(); await page.waitForFunction(() => document.querySelector("#run").dataset.executing === "true");
                    await buttonState(true); await routine("Running report…"); await stable(before, label + " immediate");
                    await runGate.entered(); await stable(before, label + " server pending");
                    assert.equal(await page.locator("#cancel-run").isEnabled(), true);
                    if (previous) { await retained(previous, oldFrame); }
                    runGate.release(); await frameGate.entered();
                    await routine("Loading report output…"); await buttonState(true); await stable(before, label + " output loading");
                    assert.equal(await page.locator("#cancel-run").isEnabled(), true);
                    if (previous) { await retained(previous, oldFrame); }
                    await page.screenshot({ path: `artifacts/browser/execution-status-${name === "One Input" ? "one" : "many"}-${width}-${rerun ? "rerun" : "initial"}.png`, fullPage: true });
                    await scan(label + " output loading");
                    const checkCommitFocus = name === "One Input" && width === 1440 && rerun;
                    if (checkCommitFocus) { await page.locator("#cancel-run").focus(); }
                    frameGate.release(); await commitGate.entered();
                    await buttonState(true); assert.equal(await page.locator("#cancel-run").isDisabled(), true);
                    if (checkCommitFocus) { assert.equal(await page.locator("#toggle-parameters").evaluate(n => n === document.activeElement), true, "Commit moves focus before disabling Cancel."); }
                    await stable(before, label + " commit pending");
                    if (previous) { await retained(previous, oldFrame); }
                    commitGate.release(); await ready(previous); await buttonState(false); await routine("Report loaded.");
                    await stable(before, label + " complete");
                    if (previous) { assert.equal(await api(previous), 404); }
                } finally { await runGate.dispose(); await frameGate.dispose(); await commitGate.dispose(); }
            }
            await close();
        }
    }

    // Keep the original expanded matrix above. Exercise full collapse through
    // both cancellable stages, cancellation and the noncancellable commit gate.
    for (const name of ["One Input", "Default Showcase"]) {
        for (const [width, height] of sizes) {
            await page.setViewportSize({ width, height }); await open(name); await run();
            const values = await page.locator("#fields input,#fields select,#fields textarea").evaluateAll(nodes => nodes.map(n => ({ value: n.value, checked: n.checked, disabled: n.disabled })));
            for (const stage of ["run", "viewer"]) {
                const previous = (await token()), frame = await page.locator("#report-frame").elementHandle();
                const gate = await delay(stage === "run" ? "**/run" : "**/executions/*/viewer*");
                try {
                    await submit(); await gate.entered(); await buttonState(true);
                    await page.locator("#cancel-run").focus(); await page.locator("#toggle-parameters").evaluate(n => n.click());
                    assert.equal(await page.locator("#cancel-run").evaluate(n => n === document.activeElement), true, "Collapse preserves focus on available Cancel.");
                    await collapsed(true, `${name} ${width} ${stage} cancellable`); await retained(previous, frame);
                    await scan(`${name} ${width} collapsed ${stage} cancellation`);
                    await page.keyboard.press("Space"); await idle();
                    assert.equal(await page.locator("#toggle-parameters").evaluate(n => n === document.activeElement), true, "Cancel completion focuses the visible toggle.");
                    await collapsed(false, `${name} ${width} ${stage} cancelled`); await retained(previous, frame);
                    await routine("Request cancelled. Select View report to try again.");
                    await page.locator("#toggle-parameters").press("Enter"); await buttonState(false);
                } finally { await gate.dispose(); }
            }
            const previous = (await token()), frame = await page.locator("#report-frame").elementHandle();
            const runGate = await delay("**/run"), frameGate = await delay("**/executions/*/viewer*"), commitGate = await delay("**/executions/*/commit", false);
            try {
                await submit(); await runGate.entered(); await page.locator("#toggle-parameters").click();
                await collapsed(true, `${name} ${width} run pending`); await retained(previous, frame);
                runGate.release(); await frameGate.entered();
                await collapsed(true, `${name} ${width} output loading`); await retained(previous, frame);
                await page.locator("#cancel-run").focus(); frameGate.release(); await commitGate.entered();
                await collapsed(false, `${name} ${width} commit pending`);
                assert.equal(await page.locator("#run").getAttribute("data-executing"), "true", "Running state lasts through commit even while hidden.");
                assert.equal(await page.locator("#cancel-run").isDisabled(), true);
                assert.equal(await page.locator("#toggle-parameters").evaluate(n => n === document.activeElement), true, "Commit moves focus before hiding Cancel.");
                await retained(previous, frame); await scan(`${name} ${width} collapsed commit`);
                await page.screenshot({ path: `artifacts/browser/execution-status-collapsed-${name === "One Input" ? "one" : "many"}-${width}.png`, fullPage: true });
                commitGate.release(); await ready(previous);
                await collapsed(false, `${name} ${width} completed`); await routine("Report loaded.");
                assert.equal(await api(previous), 404);
                assert.equal(await frame.evaluate(n => n.isConnected), false, "The completed execution publishes its replacement output.");
                await page.frameLocator("#report-frame").getByRole("heading", { name: "Page 1", exact: true }).waitFor();
                await page.locator("#toggle-parameters").press("Space"); await buttonState(false);
                assert.deepEqual(await page.locator("#fields input,#fields select,#fields textarea").evaluateAll(nodes => nodes.map(n => ({ value: n.value, checked: n.checked, disabled: n.disabled }))), values);
            } finally { await runGate.dispose(); await frameGate.dispose(); await commitGate.dispose(); }
            await close();
        }
    }

    await page.setViewportSize({ width: 1440, height: 900 }); await open("One Input"); await run();
    // Keyboard cancellation at both cancellable stages returns focus safely.
    for (const stage of ["run", "viewer"]) {
        const before = await layout(), previous = (await token()), frame = await page.locator("#report-frame").elementHandle();
        const gate = await delay(stage === "run" ? "**/run" : "**/executions/*/viewer*");
        try {
            await submit(); await gate.entered(); await buttonState(true); await stable(before, stage + " before cancel");
            await page.locator("#toggle-parameters").focus(); await page.keyboard.press("Shift+Tab");
            assert.equal(await page.locator("#cancel-run").evaluate(n => n === document.activeElement), true);
            assert.deepEqual(await page.locator("#cancel-run").evaluate(n => { const s = getComputedStyle(n); return [s.outlineStyle, s.outlineWidth]; }), ["solid", "3px"]);
            await page.keyboard.press("Enter"); await idle(); await buttonState(false);
            assert.equal(await page.locator("#run").evaluate(n => n === document.activeElement), true);
            await routine("Request cancelled. Select View report to try again."); await retained(previous, frame);
            await stable(before, stage + " cancelled"); await scan(stage + " cancelled with focus restored");
        } finally { await gate.dispose(); }
    }

    // Errors deliberately take space, retain old output and remain assertive.
    const beforeFailure = await layout(), preserved = (await token()), preservedFrame = await page.locator("#report-frame").elementHandle();
    const failure = route => route.fulfill({ status: 502, json: { code: "timeout", message: "The report request timed out. Try a smaller range or run it again.", trace: "execution-status", diagnostics: "Synthetic authorized diagnostic details." } });
    await page.route("**/run", failure);
    try {
        await submit(); await page.locator("#status[role=alert]").waitFor(); await idle(); await buttonState(false);
        assert.match(await page.locator("#status").textContent(), /Try a smaller range or run it again\. Reference: execution-status/);
        assert.equal(await page.locator("#status").getAttribute("aria-live"), "assertive");
        assert.equal(await page.locator("#status").evaluate(n => !n.classList.contains("visually-hidden")), true);
        assert.equal(await page.locator("#status").isVisible(), true);
        assert.ok((await layout()).feedback > 0);
        assert.equal(await page.locator("#diagnostic-details").textContent(), "Synthetic authorized diagnostic details.");
        await page.locator("#toggle-parameters").click();
        assert.equal(await page.locator("#parameters").isHidden(), true);
        assert.equal(await page.getByRole("alert").isVisible(), true, "Collapse preserves essential failure feedback.");
        assert.equal(await page.locator("#diagnostic-details").isVisible(), true);
        await scan("collapsed execution error and diagnostics");
        await page.locator("#toggle-parameters").click();
        await retained(preserved, preservedFrame); await scan("visible execution failure with retained report");
    } finally { await page.unroute("**/run", failure); }
    const retryGate = await delay("**/run");
    try {
        await submit(); await retryGate.entered(); await routine("Running report…");
        assert.equal(await page.locator("#diagnostic-details").count(), 0); await stable(beforeFailure, "retry clears obsolete error");
        retryGate.release(); await ready(preserved); await buttonState(false); await stable(beforeFailure, "retry complete");
    } finally { await retryGate.dispose(); }

    const cancelFailure = route => route.fulfill({ status: 503, json: { message: "Synthetic cancellation failed. Select View report to try again." } });
    const cancelFrame = await delay("**/executions/*/viewer*");
    const beforeCancelFailure = (await token());
    await page.route("**/executions/*/replacement/cancel", cancelFailure);
    try {
        await submit(); await cancelFrame.entered(); await page.locator("#cancel-run").click(); await idle(); await buttonState(false);
        assert.equal(await page.getByRole("alert").isVisible(), true);
        assert.match(await page.getByRole("alert").textContent(), /cancellation failed/);
        assert.equal((await token()), beforeCancelFailure); assert.equal(await api(beforeCancelFailure), 200);
        await scan("visible cancellation failure");
    } finally { await cancelFrame.dispose(); await page.unroute("**/executions/*/replacement/cancel", cancelFailure); }
    await run();

    // An ignored abort forces an obsolete result's cleanup/finally to finish
    // while its replacement is still pending, rather than after it completed.
    await page.evaluate(() => {
        window.executionStatusFetch = window.fetch;
        window.fetch = (url, init) => window.executionStatusFetch(url, /\/run$/.test(String(url)) ? { ...init, signal: undefined } : init);
    });
    const oldGate = await delay("**/run");
    let newGate;
    try {
        await submit(); const oldToken = (await (await oldGate.entered()).json()).token;
        await page.locator("#cancel-run").click(); await idle();
        newGate = await delay("**/run"); const previous = (await token()), before = await layout();
        await submit(); await newGate.entered();
        await page.locator("#toggle-parameters").click(); await collapsed(true, "newer run before obsolete cleanup");
        const cleaned = page.waitForResponse(r => r.url().endsWith("/executions/" + oldToken + "/cancel"));
        oldGate.release(); await (await cleaned).finished(); await oldGate.dispose();
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        await collapsed(true, "obsolete cleanup cannot expand or hide newer Cancel");
        await page.locator("#toggle-parameters").click();
        await buttonState(true); await routine("Running report…"); await stable(before, "obsolete run cannot reset newer run");
        assert.equal(await page.locator("#cancel-run").isEnabled(), true); assert.equal(await api(oldToken), 404);
        await page.locator("#toggle-parameters").click(); await collapsed(true, "newer run remains collapsible after obsolete cleanup");
        newGate.release(); await ready(previous); await collapsed(false, "newer run completed while collapsed");
        await page.locator("#toggle-parameters").click(); await buttonState(false);
    } finally {
        await oldGate.dispose(); if (newGate) { await newGate.dispose(); }
        await page.evaluate(() => { window.fetch = window.executionStatusFetch; delete window.executionStatusFetch; });
    }
    await close();

    // Real external CSS responses emulate 200% text; no product styles or CSP
    // are changed. The reserved action group may naturally wrap before Run.
    const enlargedText = async route => {
        const response = await route.fetch(); await route.fulfill({ response, body: (await response.text()).replace("font-size:16px", "font-size:32px") });
    };
    await page.route("**/css/site.css*", enlargedText);
    try {
        for (const [width, height] of [sizes[0], sizes[3], sizes[4]]) {
            await page.setViewportSize({ width, height }); await open("One Input");
            assert.equal(await page.locator("html").evaluate(n => getComputedStyle(n).fontSize), "32px");
            const before = await layout(), gate = await delay("**/run");
            try {
                await submit(); await gate.entered(); await buttonState(true); await stable(before, width + " enlarged text pending");
                gate.release(); await ready(null); await buttonState(false); await stable(before, width + " enlarged text complete");
                await scan(width + " enlarged execution text");
            } finally { await gate.dispose(); }
            await close();
        }
    } finally { await page.unroute("**/css/site.css*", enlargedText); }
    await page.setViewportSize({ width: 1440, height: 900 });
    for (const reducedMotion of ["reduce", "no-preference"]) {
        await page.emulateMedia({ reducedMotion }); await open("One Input"); const gate = await delay("**/run");
        try {
            const before = await layout(); await submit(); await gate.entered(); await buttonState(true);
            const animation = await page.locator(".run-spinner").evaluate(n => getComputedStyle(n).animationName);
            assert.equal(animation, reducedMotion === "reduce" ? "none" : "report-running");
            await stable(before, reducedMotion + " spinner pending"); gate.release(); await ready(null); await buttonState(false);
        } finally { await gate.dispose(); }
        await close();
    }
    await page.emulateMedia({ reducedMotion: null });
    await fs.writeFile("artifacts/browser/execution-status-verification.json", JSON.stringify({ samples, collapseSamples, viewports: sizes, fixtures: ["One Input", "Default Showcase"], tolerancePx: 1 }, null, 2));
    return { executionStatusGeometryChecks: samples.length, executionStatusViewports: sizes.map(([width]) => width),
        stableInitialExecution: true, stableReportRerun: true, runningThroughOutputAndCommit: true, reservedCancelSlot: true,
        keyboardCancelFocus: true, visibleExecutionAndCancellationErrors: true, hiddenLiveProgress: true,
        obsoleteRunCannotResetNewerRun: true, enlargedExecutionText: true, reducedMotionSpinner: true,
        completeParameterCollapse: true, collapsedExecutionGeometryChecks: collapseSamples.length,
        collapsedCancellationAndCommitFocus: true, collapsedExecutionPreservesValues: true };
};
