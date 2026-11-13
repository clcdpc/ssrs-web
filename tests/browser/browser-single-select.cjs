"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");

module.exports = async ({ page, context, baseURL, scan, inputsReady, single, selectSingle }) => {
    const catalog = await (await context.request.get("/api/catalog")).json();
    const url = "/Report?id=" + catalog.find(e => e.name === "Single Choice Showcase").id;
    const branch = () => single("branch").locator(".single-trigger");
    const summary = prompt => single(prompt).locator(".single-trigger span").first().textContent();
    const list = prompt => single(prompt).getByRole("listbox");
    const search = prompt => single(prompt).getByRole("searchbox");
    const open = async (prompt = "branch", key) => {
        const trigger = single(prompt).locator(".single-trigger");
        if (key) { await trigger.focus(); await trigger.press(key); } else { await trigger.click(); }
        await list(prompt).getByRole("option").last().waitFor();
    };
    const active = prompt => list(prompt).evaluate(n => document.getElementById(n.getAttribute("aria-activedescendant"))?.textContent);
    const shot = name => page.screenshot({ path: "artifacts/browser/single-" + name + ".png", fullPage: true });
    const bounds = async prompt => {
        const box = await single(prompt).locator(".single-popup").boundingBox(), view = page.viewportSize();
        assert.ok(box.x >= 7 && box.y >= 7 && box.x + box.width <= view.width - 7 && box.y + box.height <= view.height - 7, JSON.stringify({box,view}));
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
    };
    const requests = [], optionRequests = [];
    let runs = 0;
    const observe = request => {
        if (/\/parameters$/.test(request.url())) { requests.push(request.postDataJSON()); }
        if (/\/options\//.test(request.url())) { optionRequests.push(request.url()); }
        if (/\/run$/.test(request.url())) { runs++; }
    };
    page.on("request", observe);
    try {
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.goto(url); await inputsReady();
        assert.equal(await branch().getAttribute("aria-expanded"), "false");
        assert.equal(await branch().getAttribute("aria-haspopup"), "listbox");
        assert.equal(await page.locator(".single-select select,.single-choice").count(), 0, "Closed controls do not build choices.");
        assert.equal(optionRequests.length, 0, "No eager choice requests.");
        assert.equal(await summary("branch"), "Synthetic branch 152");
        assert.equal(await summary("Workstation"), "Synthetic workstation 3500");
        assert.equal(await summary("Required choice"), "Select Required choice…");
        await scan("searchable single initial defaults"); await shot("collapsed-desktop");
        const initial = requests.length;
        await open();
        assert.equal(await search("branch").evaluate(n => n === document.activeElement), true);
        assert.equal(await list("branch").getByRole("option").count(), 153);
        assert.equal(await list("branch").getByRole("option", { selected: true }).textContent(), "Synthetic branch 152");
        assert.equal(await list("branch").evaluate(n => n.scrollTop > 0), true, "Current off-screen selection scrolls into view.");
        assert.equal(await single("branch").locator("input[type=checkbox],input[type=radio],.select-all,.dropdown-actions").count(), 0);
        assert.equal(await single("branch").getByRole("button").count(), 1, "No permanent actions or footer.");
        assert.equal(await list("branch").locator("[tabindex]").count(), 0);
        assert.equal(await search("branch").evaluate(n => n.closest('[role=listbox]') === null), true);
        const rowHeight = await list("branch").getByRole("option").last().evaluate(n => n.getBoundingClientRect().height);
        assert.ok(rowHeight >= 30 && rowHeight <= 34);
        await scan("single selected plain rows"); await shot("selected-desktop"); await bounds("branch");
        await list("branch").getByRole("option").last().evaluate(n => { window.retainedSingle = n; });
        await search("branch").fill("cLc TeSt");
        assert.equal(await list("branch").getByRole("option").count(), 1);
        assert.equal(await list("branch").getByRole("option").textContent(), "CLC Test branch");
        assert.equal(await summary("branch"), "Synthetic branch 152");
        await search("branch").press("Enter"); assert.equal(runs, 0);
        await scan("single local search"); await shot("search-desktop");
        await search("branch").fill("NO MATCH");
        assert.equal(await single("branch").getByText("No matching choices.", { exact: true }).isVisible(), true);
        await scan("single no matching choices"); await shot("no-match-desktop");
        await search("branch").fill("");
        assert.equal(await list("branch").getByRole("option").count(), 153);
        assert.equal(await list("branch").getByRole("option").last().evaluate(n => n === window.retainedSingle), true);
        assert.equal(optionRequests.length, 1); assert.equal(requests.length, initial);
        await page.keyboard.press("Escape");
        assert.equal(await branch().evaluate(n => n === document.activeElement), true);
        await open(); assert.equal(optionRequests.length, 1);
        await list("branch").getByRole("option", { selected: true }).click();
        assert.equal(requests.length, initial, "Selecting the default preserves provenance with no refresh.");
        assert.equal(await branch().getAttribute("aria-expanded"), "false");

        // Navigation changes only the active descendant, including after filtering.
        await open("branch", "Space");
        await search("branch").press("ArrowDown");
        await list("branch").press("Home"); assert.equal(await active("branch"), "Select branch…");
        await list("branch").press("ArrowDown"); assert.equal(await active("branch"), "Synthetic branch 001");
        await list("branch").press("End"); assert.equal(await active("branch"), "Synthetic branch 152");
        await list("branch").press("ArrowUp"); assert.equal(await active("branch"), "Synthetic branch 151");
        assert.equal(await list("branch").getByRole("option", { selected: true }).textContent(), "Synthetic branch 152");
        await scan("single keyboard active distinct from selected"); await shot("keyboard-desktop");
        await page.keyboard.press("Escape"); assert.equal(requests.length, initial);
        for (const method of ["outside", "focus", "Tab", "another", "trigger"]) {
            await open("branch", "Enter"); await search("branch").fill("CLC"); await search("branch").press("ArrowDown");
            if (method === "outside") { await page.getByLabel("Unrelated note", { exact: true }).click(); }
            if (method === "focus") { await page.getByLabel("Unrelated note", { exact: true }).focus(); }
            if (method === "Tab") { await list("branch").press("Tab"); }
            if (method === "another") { await open("Workstation"); await page.keyboard.press("Escape"); }
            if (method === "trigger") { await branch().click(); }
            assert.equal(await branch().getAttribute("aria-expanded"), "false", method);
            assert.equal(await summary("branch"), "Synthetic branch 152");
            assert.equal(await page.locator(".single-popup[hidden]").evaluateAll(nodes => nodes.every(n => !n.contains(document.activeElement))), true);
        }
        assert.equal(requests.length, initial);
        await selectSingle("branch", "CLC Test branch");
        assert.equal(requests.length, initial + 1);
        assert.deepEqual(requests.at(-1).values.branch, ["21"]);
        assert.ok(requests.at(-1).defaulted.includes("branch" ) === false);
        assert.ok(requests.at(-1).defaulted.includes("workstation"));
        assert.equal(await summary("Department"), "Team for branch 21");
        assert.equal(await branch().evaluate(n => n === document.activeElement), true);
        const loaded = optionRequests.length;
        await open("Department"); await page.keyboard.press("Escape");
        assert.equal(optionRequests.length, loaded + 1);
        await selectSingle("Department", "Shared team");
        await selectSingle("branch", "Synthetic branch 022");
        assert.equal(await summary("Department"), "Shared team");
        await open("Department"); await page.keyboard.press("Escape");
        assert.equal(optionRequests.length, loaded + 2, "Only the changed dependency reloads; unrelated choices stay cached.");

        // Full 4,192-choice collection, stable DOM during navigation/search and reopen.
        const perf = {};
        let started = Date.now(); await open("Workstation", "ArrowDown"); perf.open4192Ms = Date.now() - started;
        assert.equal(await list("Workstation").getByRole("option").count(), 4193);
        await list("Workstation").getByRole("option").last().evaluate(n => { window.lastSingle = n; });
        await list("Workstation").press("End"); assert.equal(await active("Workstation"), "Synthetic workstation 4192");
        assert.equal(await summary("Workstation"), "Synthetic workstation 3500");
        await shot("large-desktop");
        await search("Workstation").focus(); started = Date.now(); await search("Workstation").fill("4192");
        assert.equal(await list("Workstation").getByRole("option").count(), 1); perf.search4192Ms = Date.now() - started;
        await search("Workstation").press("ArrowDown"); await list("Workstation").press("Space"); await inputsReady();
        assert.equal(await summary("Workstation"), "Synthetic workstation 4192");
        const cached = optionRequests.length; started = Date.now(); await open("Workstation"); perf.cachedOpen4192Ms = Date.now() - started;
        assert.equal(optionRequests.length, cached);
        await list("Workstation").getByRole("option").last().evaluate(n => { window.lastSingle = n; });
        await search("Workstation").fill("No workstation"); await search("Workstation").fill("");
        await search("Workstation").press("ArrowDown"); await list("Workstation").press("Home"); await list("Workstation").press("End");
        assert.equal(await list("Workstation").getByRole("option").last().evaluate(n => n === window.lastSingle), true);
        await page.keyboard.press("Escape");
        assert.ok(perf.search4192Ms < 1500 && perf.cachedOpen4192Ms < 2000, JSON.stringify(perf));
        await fs.writeFile("artifacts/browser/single-select-performance.json", JSON.stringify(perf, null, 2));

        // A late parameter response cannot restore an earlier selection or rapid edit.
        await page.evaluate(() => {
            window.singleSelectFetch = window.fetch;
            window.fetch = (url, options = {}) => window.singleSelectFetch(url, /\/parameters$/.test(url) ? { ...options, signal: undefined } : options);
        });
        let releaseForm, startedForm;
        const gate = new Promise(resolve => { releaseForm = resolve; }), starting = new Promise(resolve => { startedForm = resolve; });
        const handler = async route => {
            const body = route.request().postDataJSON();
            if (!body.changed.includes("branch") || body.changed.includes("note")) { await route.continue(); return; }
            const response = await route.fetch(); startedForm(); await gate; await route.fulfill({ response });
        };
        await page.route("**/parameters", handler);
        await open(); await search("branch").fill("CLC"); await list("branch").getByRole("option").click(); await starting;
        await page.getByLabel("Unrelated note", { exact: true }).fill("Rapid edit survives");
        await page.getByLabel("Unrelated note", { exact: true }).press("Tab"); await inputsReady();
        assert.ok(requests.at(-1).changed.includes("branch") && requests.at(-1).changed.includes("note"));
        const delivered = page.waitForResponse(r => /\/parameters$/.test(r.url()) && !r.request().postDataJSON().changed.includes("note"));
        releaseForm(); await (await delivered).finished(); await page.unroute("**/parameters", handler);
        assert.equal(await summary("branch"), "CLC Test branch");
        assert.equal(await page.getByLabel("Unrelated note", { exact: true }).inputValue(), "Rapid edit survives");
        await page.evaluate(() => { window.fetch = window.singleSelectFetch; delete window.singleSelectFetch; });

        // Explicit empty selection is distinct from an SSRS blank string and null.
        await selectSingle("Required choice", "Choice A");
        await selectSingle("branch", "Select branch…");
        assert.deepEqual(requests.at(-1).values.branch, []);
        assert.equal(await branch().getAttribute("aria-invalid"), "true");
        assert.equal(await page.locator("#run").isDisabled(), true);
        await page.getByLabel("Unrelated note", { exact: true }).fill("Clear stays cleared");
        await page.getByLabel("Unrelated note", { exact: true }).press("Tab"); await inputsReady();
        assert.equal(await summary("branch"), "Select branch…");
        await selectSingle("branch", "CLC Test branch");
        assert.equal(await summary("Nullable choice"), "(Blank choice)");
        await selectSingle("Nullable choice", "Named choice");
        const nullable = single("Nullable choice");
        await nullable.getByRole("checkbox").check(); await inputsReady();
        assert.deepEqual(requests.at(-1).values.nullable, [null]);
        assert.equal(await summary("Nullable choice"), "No value");
        assert.equal(await nullable.locator(".single-trigger").isDisabled(), true);
        await nullable.getByRole("checkbox").uncheck(); await inputsReady();
        assert.deepEqual(requests.at(-1).values.nullable, []);
        for (const [label, value] of [["Literal null", "null"], ["Literal None value", "None"], ["None", "none-labelled"], ["(Blank choice)", ""]]) {
            await selectSingle("Nullable choice", label);
            assert.deepEqual(requests.at(-1).values.nullable, [value]);
            assert.equal(await summary("Nullable choice"), label);
            assert.equal(await nullable.getByRole("checkbox").isChecked(), false);
        }

        // Viewport, touch, width and ellipsis checks with the same shared trigger CSS.
        for (const size of [{ width: 1440, height: 900 }, { width: 1024, height: 768 }, { width: 768, height: 1024 }, { width: 390, height: 844 }, { width: 320, height: 640 }]) {
            await page.setViewportSize(size); await open(); await bounds("branch");
            await scan("searchable single viewport " + size.width); await shot("open-" + size.width);
            await page.keyboard.press("Escape"); await shot("collapsed-" + size.width);
        }
        await page.emulateMedia({ media: "screen" });
        await page.setViewportSize({ width: 1440, height: 900 });
        assert.ok(await branch().evaluate(n => n.getBoundingClientRect().width >= 280 && n.getBoundingClientRect().width <= 400));
        const beforeLong = await branch().boundingBox();
        await branch().locator("span").first().evaluate(n => { n.textContent = "Very long selected SSRS label ".repeat(30); });
        const afterLong = await branch().boundingBox(); assert.deepEqual(afterLong, beforeLong);
        assert.equal(await branch().locator("span").first().evaluate(n => getComputedStyle(n).textOverflow === "ellipsis" && n.scrollWidth > n.clientWidth), true);
        assert.equal(await branch().locator("span").last().evaluate(n => n.getBoundingClientRect().width > 0), true);
        await shot("long-label-desktop");
        await branch().locator("span").first().evaluate(n => { n.textContent = "CLC Test branch"; });
        assert.equal(runs, 0);
        const run = page.waitForRequest(r => /\/run$/.test(r.url())); await page.locator("#run").click();
        const submitted = (await run).postDataJSON().values;
        assert.deepEqual(submitted.branch, ["21"]); assert.deepEqual(submitted.workstation, ["4192"]);
        assert.deepEqual(submitted.department, ["21-team"]); assert.deepEqual(submitted.required, ["a"]); assert.deepEqual(submitted.nullable, [""]);
        assert.ok(Object.values(submitted).every(value => value.length <= 1));
        await page.waitForFunction(() => Boolean(history.state?.ssrsReport?.token) && document.querySelector("#run").dataset.executing === "false");
        const unchanged = requests.length;
        await open(); await list("branch").getByRole("option", { selected: true }).click();
        assert.equal(requests.length, unchanged); assert.equal(await page.locator("#dirty").isVisible(), false);
        await selectSingle("branch", "Synthetic branch 022");
        assert.equal(await page.locator("#dirty").isVisible(), true); assert.equal(await page.locator("#viewer").isVisible(), true);
        await page.getByRole("button", { name: "Close", exact: true }).click(); await page.waitForURL(/\/\?folder=/);

        // Shared surfaces match beside each other, on several wrapping parameter rows.
        await page.goto("/Report?id=" + catalog.find(e => e.name === "Circulation By Workstation").id); await inputsReady();
        const styles = await page.locator(".choice-trigger").evaluateAll(nodes => nodes.map(n => {
            const s = getComputedStyle(n); return [s.height, s.border, s.borderRadius, s.fontSize, s.padding, s.backgroundColor];
        }));
        assert.ok(styles.length > 1); for (const style of styles) { assert.deepEqual(style, styles[0]); }
        await single("Library").locator(".single-trigger").click(); await list("Library").getByRole("option").last().waitFor();
        await shot("beside-multi-desktop"); await scan("single beside multi"); await page.keyboard.press("Escape");
        const touch = await context.browser().newContext({ baseURL, ignoreHTTPSErrors: true, storageState: await context.storageState(), hasTouch: true, viewport: { width: 390, height: 844 } });
        try {
            const mobile = await touch.newPage(); await mobile.goto(url);
            await mobile.waitForFunction(() => document.querySelector("#parameters").getAttribute("aria-busy") === "false");
            await mobile.locator(".single-trigger").first().tap(); await mobile.getByRole("option").last().waitFor();
            assert.equal(await mobile.evaluate(() => matchMedia("(pointer:coarse)").matches), true);
            assert.equal(await mobile.getByRole("option").evaluateAll(nodes => nodes.every(n => n.getBoundingClientRect().height >= 44)), true);
            await mobile.screenshot({ path: "artifacts/browser/single-touch-mobile.png", fullPage: true });
        } finally { await touch.close(); }

        // Loading can be canceled; errors retain the label and Retry works after reopen.
        let releaseLoad, startLoad;
        const loadingGate = new Promise(resolve => { releaseLoad = resolve; }), loadingStarted = new Promise(resolve => { startLoad = resolve; });
        const slow = async route => { const response = await route.fetch(); startLoad(); await loadingGate; await route.fulfill({ response }).catch(() => {}); };
        await page.route("**/options/branch", slow); await page.goto(url); await inputsReady();
        await branch().click(); await loadingStarted;
        assert.equal(await single("branch").getByText("Loading choices…", { exact: true }).isVisible(), true);
        await scan("single loading"); await shot("loading-desktop");
        await page.keyboard.press("Escape"); releaseLoad(); await page.unroute("**/options/branch", slow);
        assert.equal(await branch().getAttribute("aria-expanded"), "false");
        const errorHandler = route => route.fulfill({ status: 502, json: { message: "Choices unavailable. Try again." } });
        await page.route("**/options/branch", errorHandler); await branch().click();
        await single("branch").getByRole("button", { name: "Retry choices" }).waitFor();
        assert.equal(await summary("branch"), "Synthetic branch 152"); assert.equal(await branch().isEnabled(), true);
        await scan("single load failure"); await shot("error-desktop");
        await page.keyboard.press("Escape"); await branch().click();
        await single("branch").getByRole("button", { name: "Retry choices" }).waitFor();
        await page.unroute("**/options/branch", errorHandler);
        await single("branch").getByRole("button", { name: "Retry choices" }).click();
        await list("branch").getByRole("option").last().waitFor();
        assert.equal(await list("branch").getByRole("option").count(), 153); await page.keyboard.press("Escape");
        const changedHandler = async route => { const response = await route.fetch(); await route.fulfill({ response, json: { ...await response.json(), optionsKey: "changed-between-requests" } }); };
        await page.route("**/options/branch", changedHandler); await page.reload(); await inputsReady();
        const refreshStart = requests.length; await branch().click();
        await single("branch").getByRole("button", { name: "Refresh inputs" }).waitFor();
        assert.equal(requests.length, refreshStart, "Choice-key mismatch has bounded explicit recovery.");
        await page.unroute("**/options/branch", changedHandler);
        await single("branch").getByRole("button", { name: "Refresh inputs" }).click(); await inputsReady();
        await open(); assert.equal(await list("branch").getByRole("option").count(), 153); await page.keyboard.press("Escape");
        // Only the most recent opener waiting on a dependent refresh may survive.
        let releasePending, startPending;
        const pendingGate = new Promise(resolve => { releasePending = resolve; }), pendingStarted = new Promise(resolve => { startPending = resolve; });
        const pendingForm = async route => { const response = await route.fetch(); startPending(); await pendingGate; await route.fulfill({ response }); };
        await page.route("**/parameters", pendingForm);
        await open(); await search("branch").fill("CLC"); await list("branch").getByRole("option").click(); await pendingStarted;
        await page.evaluate(() => {
            document.querySelectorAll(".single-trigger")[0].click();
            document.querySelectorAll(".single-trigger")[2].click();
        });
        releasePending(); await inputsReady(); await list("Workstation").getByRole("option").last().waitFor();
        assert.equal(await page.locator(".choice-popup:visible").count(), 1);
        assert.equal(await branch().getAttribute("aria-expanded"), "false");
        await page.keyboard.press("Escape"); await page.unroute("**/parameters", pendingForm);

        // Failed parameter resolution recovers before using choices from the old form.
        const failedForm = route => route.fulfill({ status: 502, json: { message: "Synthetic form failure" } });
        await page.route("**/parameters", failedForm);
        await selectSingle("branch", "Synthetic branch 022");
        assert.equal(await page.locator("#run").isDisabled(), true);
        await page.unroute("**/parameters", failedForm);
        await open(); assert.equal(await summary("branch"), "Synthetic branch 022"); await page.keyboard.press("Escape");

        // A canceled dependent load must not populate the cache for the new parent.
        await page.goto(url); await inputsReady();
        await page.evaluate(() => {
            window.singleChoiceFetch = window.fetch;
            window.fetch = (url, options = {}) => window.singleChoiceFetch(url, /\/options\//.test(url) ? { ...options, signal: undefined } : options);
        });
        let releaseOld, startOld;
        const oldGate = new Promise(resolve => { releaseOld = resolve; }), oldStarted = new Promise(resolve => { startOld = resolve; });
        const oldChoices = async route => { const response = await route.fetch(); startOld(); await oldGate; await route.fulfill({ response }); };
        await page.route("**/options/department", oldChoices);
        await single("Department").locator(".single-trigger").click(); await oldStarted;
        await selectSingle("branch", "CLC Test branch");
        assert.equal(await single("Department").locator(".single-trigger").getAttribute("aria-expanded"), "false");
        const late = page.waitForResponse(r => /\/options\/department$/.test(r.url())); releaseOld(); await (await late).finished();
        await page.unroute("**/options/department", oldChoices);
        await open("Department");
        assert.equal(await list("Department").getByRole("option", { name: "Team for branch 21", exact: true }).count(), 1);
        assert.equal(await list("Department").getByRole("option", { name: "Team for branch 152", exact: true }).count(), 0);
        await page.keyboard.press("Escape");
        await page.evaluate(() => { window.fetch = window.singleChoiceFetch; delete window.singleChoiceFetch; });

        // No-op default provenance also survives a later unrelated parameter request.
        await page.goto(url); await inputsReady(); await open();
        const noOpRequests = requests.length;
        await list("branch").getByRole("option", { selected: true }).click();
        assert.equal(requests.length, noOpRequests);
        await page.getByLabel("Unrelated note", { exact: true }).fill("Probe default provenance");
        await page.getByLabel("Unrelated note", { exact: true }).press("Tab"); await inputsReady();
        assert.ok(requests.at(-1).defaulted.includes("branch"));

        // Internal redraw dismisses without committing and restores usable focus.
        await open("Department"); const departmentValue = requests.at(-1).values.department;
        await page.evaluate(() => {
            const note = [...document.querySelectorAll(".parameter-field>input")].find(n => n.type === "text");
            note.value = "Internal redraw probe"; note.dispatchEvent(new Event("change", { bubbles: true }));
        });
        await inputsReady();
        assert.deepEqual(requests.at(-1).values.department, departmentValue);
        assert.equal(requests.at(-1).changed.includes("department"), false);
        assert.equal(await single("Department").locator(".single-trigger").evaluate(n => n === document.activeElement), true);

        // Flip above a low trigger, then clamp while the trigger scrolls off-screen.
        await single("branch").evaluate(n => { n.style.marginTop = "600px"; });
        await branch().scrollIntoViewIfNeeded(); await open(); await bounds("branch");
        const lowTrigger = await branch().boundingBox(), abovePopup = await single("branch").locator(".single-popup").boundingBox();
        assert.ok(abovePopup.y + abovePopup.height < lowTrigger.y);
        await page.evaluate(() => { document.body.style.paddingBottom = "1200px"; window.scrollTo(0, document.body.scrollHeight); });
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(resolve))); await bounds("branch");
        await page.keyboard.press("Escape");
        await single("branch").evaluate(n => { n.style.marginTop = ""; });
        await page.evaluate(() => { document.body.style.paddingBottom = ""; window.scrollTo(0, 0); });

        return { searchableSingle152: true, searchableSingle4192: true, singleKeyboard: true, singleImmediateCommit: true,
            singleDefaultsAndNoOps: true, singleLocalSearchCache: true, singleCascadesClears: true, singleNullBlank: true,
            singleExecutionFreshness: true, singleDismissal: true, singleRetry: true, singleChoiceContextRecovery: true, singlePerformance: perf };
    } finally { page.off("request", observe); }
};
