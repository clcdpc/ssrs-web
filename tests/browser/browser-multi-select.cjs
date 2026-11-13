"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");

module.exports = async ({ page, context, baseURL, scan, inputsReady }) => {
    const catalog = await (await context.request.get("/api/catalog")).json();
    const url = "/Report?id=" + catalog.find(r => r.name === "Check Ins by Day of Week by Hour").id;
    const multi = prompt => page.locator(".multi-select").filter({ has: page.locator("label[id]").filter({ hasText: new RegExp("^" + prompt + "$") }) });
    const material = multi("Types of Material"), trigger = material.locator(".multi-trigger"), list = material.locator(".checkbox-choices");
    const all = material.getByRole("checkbox", { name: "(Select All)", exact: true });
    const option = name => material.getByRole("checkbox", { name, exact: true });
    const search = material.getByRole("searchbox");
    const open = async () => { await trigger.click(); await list.locator("input[data-option]").last().waitFor(); };
    const close = async () => { await page.keyboard.press("Escape"); await inputsReady(); };
    const fresh = async () => { await page.goto(url); await inputsReady(); };
    const shot = name => page.screenshot({ path: "artifacts/browser/multi-" + name + ".png", fullPage: true });
    const state = () => all.evaluate(n => ({ checked: n.checked, indeterminate: n.indeterminate }));
    const assertState = async (checked, indeterminate) => assert.deepEqual(await state(), { checked, indeterminate });
    const requests = [];
    const record = request => { if (/\/(parameters|run)$/.test(request.url())) { requests.push({ url: request.url(), body: request.postDataJSON() }); } };
    const refreshes = () => requests.filter(r => /\/parameters$/.test(r.url));
    page.on("request", record);
    try {
        await page.setViewportSize({ width: 1440, height: 900 }); await fresh(); await open();
        const rowStart = requests.length;
        await require("./checkbox-row-assertions.cjs")({ page, group: material });
        assert.equal(requests.length, rowStart, "Full-row editing stays local and never submits the form.");
        await scan("full-row mouse and keyboard selection");
        await page.locator(".breadcrumbs [aria-current=page]").click(); await inputsReady();
        assert.equal(await trigger.getAttribute("aria-expanded"), "false");
        assert.equal(requests.length, rowStart + 1, "Outside dismissal commits once.");
        assert.deepEqual(refreshes().at(-1).body.values.materials, ["material-3", "material-6"]);
        await open();
        assert.equal(await option("Atlas").isChecked(), true);
        assert.equal(await option("Audio Books").isChecked(), true);
        assert.equal(await list.locator("input[data-option]:checked").count(), 2); await close();

        const touchContext = await context.browser().newContext({ baseURL, ignoreHTTPSErrors: true, storageState: await context.storageState(),
            viewport: { width: 1024, height: 768 }, hasTouch: true, isMobile: true });
        const touchPage = await touchContext.newPage(), touchErrors = [];
        touchPage.on("pageerror", error => touchErrors.push(error.message));
        touchPage.on("request", record);
        try {
            await touchPage.goto(url);
            await touchPage.waitForFunction(() => document.querySelector("#parameters").getAttribute("aria-busy") === "false");
            assert.equal(await touchPage.evaluate(() => matchMedia("(pointer:coarse)").matches && innerWidth > 600), true, "Exercise coarse-pointer targets independently of the narrow-screen rule.");
            const touchMaterial = touchPage.locator(".multi-select").filter({ has: touchPage.locator("label[id]", { hasText: /^Types of Material$/ }) });
            await touchMaterial.locator(".multi-trigger").tap();
            await touchMaterial.locator("input[data-option]").last().waitFor();
            const touchStart = requests.length;
            await require("./checkbox-row-assertions.cjs")({ page: touchPage, group: touchMaterial, touch: true });
            assert.equal(requests.length, touchStart);
            const AxeBuilder = require("@axe-core/playwright").default;
            const touchScan = await new AxeBuilder({ page: touchPage }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
            assert.deepEqual(touchScan.violations.map(v => ({ id: v.id, nodes: v.nodes.length })), [], "Touch dropdown accessibility");
            await touchPage.locator(".breadcrumbs [aria-current=page]").tap();
            await touchPage.waitForFunction(() => document.querySelector("#parameters").getAttribute("aria-busy") === "false");
            assert.equal(await touchMaterial.locator(".multi-trigger").getAttribute("aria-expanded"), "false");
            assert.equal(requests.length, touchStart + 1);
            assert.deepEqual(refreshes().at(-1).body.values.materials, ["material-3", "material-6"]);
            assert.deepEqual(touchErrors, []);
        } finally { await touchContext.close(); }

        await page.setViewportSize({ width: 1440, height: 900 }); await fresh();
        const width = await trigger.evaluate(n => n.getBoundingClientRect().width);
        assert.match(await trigger.textContent(), /^4K Blu-ray, Abstract, Archival\/Mixed Materials, Atlas/);
        assert.doesNotMatch(await trigger.textContent(), /\d+ selected/);
        await scan("material labels collapsed"); await shot("all-1440"); await open();
        assert.equal(await list.locator("input").count(), 53);
        assert.equal(await list.locator("label").first().textContent(), "(Select All)");
        assert.equal(await list.locator("input").first().getAttribute("type"), "checkbox");
        assert.equal(await material.locator(".option-tools,.dropdown-actions,button:not(.multi-trigger):visible").count(), 0);
        assert.equal(await material.getByText(/52 choices|Apply to save/).count(), 0);
        await assertState(true, false); await scan("select all checked"); await shot("checked-1440");
        const before = refreshes().length;
        await list.locator("input[data-option]").first().evaluate(n => { window.retainedChoice = n; });
        await option("4K Blu-ray").uncheck(); await assertState(false, true);
        assert.equal(refreshes().length, before, "Checkbox clicks stay local.");
        assert.equal(await list.locator("input[data-option]").first().evaluate(n => n === window.retainedChoice), true, "A click does not rebuild the list.");
        assert.match(await trigger.textContent(), /^Abstract, Archival/);
        await scan("select all indeterminate"); await shot("indeterminate-1440");
        await option("4K Blu-ray").check(); await assertState(true, false);
        await all.uncheck(); await assertState(false, false);
        assert.equal(await trigger.textContent(), "Select Types of Material⌄");
        await scan("select all unchecked"); await shot("unchecked-1440");
        await option("Atlas").check(); assert.equal(await trigger.textContent(), "Atlas⌄");
        await option("Abstract").check(); assert.equal(await trigger.textContent(), "Abstract, Atlas⌄");
        await search.fill("aUdIo");
        assert.equal(await list.locator("input[data-option]:visible").count(), 5, "Filtering uses case-insensitive labels.");
        assert.equal(await all.isVisible(), true);
        await search.press("ArrowDown"); await page.keyboard.press("End");
        assert.equal(await option("eAudio").evaluate(n => n === document.activeElement), true, "End skips filtered-out rows.");
        await all.check(); await assertState(true, false);
        await search.fill(""); assert.equal(await material.getByRole("checkbox", { checked: true }).count(), 53);
        assert.equal(await list.locator("input[data-option]").first().evaluate(n => n === window.retainedChoice), true, "Search reuses the existing checkbox DOM.");
        await search.fill("Audio"); await all.uncheck(); await assertState(false, false);
        await option("Audio Books").check(); await search.fill("");
        assert.equal(await option("Audio Books").isChecked(), true);
        await option("Atlas").check();
        await search.fill("Audio"); await scan("search and full-set select all"); await shot("filtered-1440");
        await close(); assert.equal(refreshes().length, before + 1);
        assert.equal(await trigger.textContent(), "Atlas, Audio Books⌄");
        const committed = refreshes().at(-1).body;
        assert.deepEqual(committed.values.materials, ["material-3", "material-6"]);
        assert.equal(committed.values.materials.includes("(Select All)"), false);
        await shot("several-1440"); await open();
        await option("Audio Books").uncheck(); await close(); await shot("one-1440");
        await open(); await option("Atlas").uncheck(); await assertState(false, false); await close(); await shot("none-1440");
        assert.equal(await trigger.evaluate(n => n.getBoundingClientRect().width), width);
        await open(); await search.fill("Nothing matches this");
        await material.getByText("No matching choices.", { exact: true }).waitFor();
        await search.press("ArrowDown");
        assert.equal(await all.evaluate(n => document.activeElement === n), true);
        await page.keyboard.press("Space"); await assertState(true, false);
        await search.fill(""); await close();

        // Every user dismissal commits exactly once; no-op and edit/revert
        // dismissals retain SSRS defaults without requests or stale-report state.
        for (const method of ["trigger", "outside", "focus", "focusCommand", "Tab", "Escape", "another", "collapse"]) {
            await fresh(); await open(); const start = refreshes().length;
            await option("4K Blu-ray").uncheck();
            if (method === "trigger") { await trigger.click(); }
            if (method === "outside") { await page.locator(".breadcrumbs [aria-current=page]").click(); }
            if (method === "focus") { await page.getByLabel("beginDate", { exact: true }).focus(); }
            if (method === "focusCommand") { await multi("Branches").locator(".multi-trigger").focus(); }
            if (method === "Tab") { await option("Video Game").focus(); await page.keyboard.press("Tab"); }
            if (method === "Escape") { await page.keyboard.press("Escape"); }
            if (method === "another") {
                await multi("Branches").locator(".multi-trigger").click();
                await multi("Branches").getByRole("searchbox").waitFor();
                assert.equal(await multi("Branches").locator(".multi-trigger").getAttribute("aria-expanded"), "true");
                await page.keyboard.press("Escape");
            }
            if (method === "collapse") {
                await page.locator("#toggle-parameters").click();
                await inputsReady(); assert.equal(await page.locator("#parameter-controls").isHidden(), true);
                await page.locator("#toggle-parameters").click();
            }
            await inputsReady(); assert.equal(refreshes().length, start + 1, method + " resolves exactly once");
            await open(); assert.equal(await option("4K Blu-ray").isChecked(), false); await close();
            assert.equal(refreshes().length, start + 1, method + " reopen is unchanged");
        }
        await fresh(); await open(); const unchanged = refreshes().length;
        await option("Abstract").uncheck(); await option("Abstract").check(); await close();
        assert.equal(refreshes().length, unchanged, "Set equality ignores changed click order.");
        await open(); await search.press("ArrowDown");
        assert.equal(await all.evaluate(n => n === document.activeElement), true);
        await page.keyboard.press("End"); assert.equal(await option("Video Game").evaluate(n => n === document.activeElement), true);
        await page.keyboard.press("Home"); assert.equal(await all.evaluate(n => n === document.activeElement), true);
        assert.equal(await all.evaluate(n => getComputedStyle(n).outlineStyle), "solid");
        await page.keyboard.press("ArrowDown"); assert.equal(await option("4K Blu-ray").evaluate(n => n === document.activeElement), true);
        await page.keyboard.press("ArrowUp"); assert.equal(await all.evaluate(n => n === document.activeElement), true);
        await close(); assert.equal(await trigger.evaluate(n => n === document.activeElement), true);
        await trigger.press("Space"); await search.waitFor(); assert.equal(await search.evaluate(n => n === document.activeElement), true); await close();
        await trigger.press("Enter"); await search.waitFor(); await close();

        // Repeated events are harmless, and internal redraw cannot commit an
        // obsolete local set or recursively start another resolution.
        await fresh(); await open(); await option("4K Blu-ray").uncheck();
        let lifecycleStart = refreshes().length;
        await material.evaluate(group => {
            group.querySelector(".multi-trigger").click();
            group.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
            document.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
        });
        await inputsReady(); assert.equal(refreshes().length, lifecycleStart + 1);
        await fresh(); await open(); await option("4K Blu-ray").uncheck(); lifecycleStart = refreshes().length;
        await page.getByLabel("beginDate", { exact: true }).evaluate(input => {
            input.value = "2026-09-02T00:00";
            input.dispatchEvent(new Event("change", { bubbles: true }));
        });
        await inputsReady(); assert.equal(refreshes().length, lifecycleStart + 1);
        assert.deepEqual(refreshes().at(-1).body.changed, ["beginDate"]);
        assert.equal(refreshes().at(-1).body.values.materials.length, 52);
        await open(); await assertState(true, false); await close();

        // Pending empty selections cannot execute; correcting a required value
        // can be submitted directly from its still-open dropdown.
        await fresh(); await open(); await all.uncheck();
        const invalidRuns = requests.filter(r => /\/run$/.test(r.url)).length;
        await page.locator("#run").click(); await inputsReady();
        assert.equal(requests.filter(r => /\/run$/.test(r.url)).length, invalidRuns);
        assert.equal(await page.locator("#run").isDisabled(), true);
        await open(); await option("Atlas").check(); assert.equal(await page.locator("#run").isEnabled(), true);
        await page.locator("#run").click(); await page.waitForFunction(() => Boolean(history.state?.ssrsReport?.token) && document.querySelector("#run").dataset.executing === "false");
        assert.deepEqual(requests.filter(r => /\/run$/.test(r.url)).at(-1).body.values.materials, ["material-3"]);
        await page.getByRole("button", { name: "Close", exact: true }).click(); await page.waitForURL(/\/?folder=/);

        // Hold the dependent resolution in flight. Execution must not start
        // until the new branch selection and its defaulted Department agree.
        await fresh();
        const branches = multi("Branches"), branch = branches.getByRole("checkbox", { name: "Synthetic branch 001", exact: true });
        await branches.locator(".multi-trigger").click(); await branch.waitFor(); await branch.uncheck();
        let release, started;
        const gate = new Promise(resolve => { release = resolve; });
        const seen = new Promise(resolve => { started = resolve; });
        const delayed = async route => { started(); await gate; await route.continue(); };
        await page.route("**/parameters", delayed);
        const runCount = requests.filter(r => /\/run$/.test(r.url)).length;
        const start = refreshes().length;
        await page.locator("#run").click(); await seen;
        assert.equal(requests.filter(r => /\/run$/.test(r.url)).length, runCount);
        release();
        await page.waitForFunction(() => Boolean(history.state?.ssrsReport?.token) && document.querySelector("#run").dataset.executing === "false"); await page.unroute("**/parameters", delayed);
        assert.equal(refreshes().length, start + 1);
        const executed = requests.filter(r => /\/run$/.test(r.url)).at(-1).body;
        assert.deepEqual(executed.values.branch, ["80", "152"]);
        assert.deepEqual(executed.values.department, ["80-team"]);
        assert.equal(executed.defaulted.includes("branch"), false);
        assert.equal(executed.defaulted.includes("materials"), true, "Unedited material defaults keep their provenance.");
        await open(); await close(); assert.equal(await page.locator("#dirty").isHidden(), true);
        await open(); await option("4K Blu-ray").uncheck(); await close(); assert.equal(await page.locator("#dirty").isVisible(), true);
        await page.getByRole("button", { name: "Close", exact: true }).click(); await page.waitForURL(/\/?folder=/);

        // Typed values stay distinct through local selection and execution.
        await fresh(); const nullable = multi("Nullable choices");
        await nullable.locator(".multi-trigger").click();
        await nullable.getByRole("checkbox", { name: "(Select All)", exact: true }).check();
        await page.keyboard.press("Escape"); await inputsReady();
        assert.deepEqual(refreshes().at(-1).body.values.blankChoices, ["", "named", "null"]);
        assert.equal(await nullable.locator(".multi-trigger").textContent(), "(Blank choice), Named choice, Literal null⌄");
        await nullable.getByRole("checkbox", { name: "Nullable choices: No value", exact: true }).check(); await inputsReady();
        assert.deepEqual(refreshes().at(-1).body.values.blankChoices, [null]);

        // Rejection preserves the complete valid selection and offers recovery.
        const cap = async route => {
            const response = await route.fetch(), result = await response.json();
            result.fields.find(f => f.name === "materials").selectionLimit = 52;
            result.fields.find(f => f.name === "branch").selectionLimit = 3;
            await route.fulfill({ response, json: result });
        };
        await page.route("**/parameters", cap); await fresh();
        await branches.locator(".multi-trigger").click(); await branch.waitFor();
        await branches.getByText("(Select All)", { exact: true }).click();
        assert.equal(await branches.locator("input[data-option]:checked").count(), 3);
        await branches.getByRole("alert").getByText(/no more than 3/).waitFor();
        await branches.getByText("Synthetic branch 002", { exact: true }).click();
        assert.equal(await branches.getByRole("checkbox", { name: "Synthetic branch 002", exact: true }).isChecked(), false);
        assert.equal(await branches.locator(".multi-trigger").getAttribute("aria-expanded"), "true");
        await scan("actionable count rejection"); await shot("limit-1440");
        await branch.uncheck(); await branches.getByRole("checkbox", { name: "Synthetic branch 002", exact: true }).check();
        assert.equal(await branches.locator(".checkbox-popup .field-error").textContent(), "");
        await close(); assert.deepEqual(refreshes().at(-1).body.values.branch, ["2", "80", "152"]);
        await page.unroute("**/parameters", cap);

        // A synthetic oversized choice response exercises the browser byte bound
        // without weakening the independent server limit/serialization tests.
        const longValues = Array.from({ length: 152 }, (_, i) => ({ value: String(i).padEnd(2048, "é"), label: "Long choice " + i }));
        const oversized = async route => {
            const response = await route.fetch(), result = await response.json();
            result.values.branch = [longValues[0].value];
            const field = result.fields.find(f => f.name === "branch");
            field.selectedOptions = [longValues[0]]; field.type = "String";
            await route.fulfill({ response, json: result });
        };
        const longChoices = async route => { const response = await route.fetch(), result = await response.json(); result.options = longValues; await route.fulfill({ response, json: result }); };
        await page.route("**/parameters", oversized); await page.route("**/options/branch", longChoices); await fresh();
        await branches.locator(".multi-trigger").click(); await branches.getByRole("checkbox", { name: "Long choice 151", exact: true }).waitFor();
        const noRequests = refreshes().length;
        await branches.getByText("(Select All)", { exact: true }).click();
        assert.match(await branches.getByRole("alert").textContent(), /request size/);
        assert.equal(await branches.locator("input[data-option]:checked").count(), 1);
        let rejected;
        for (let i = 1; i < longValues.length; i++) {
            const checkbox = branches.getByRole("checkbox", { name: "Long choice " + i, exact: true });
            await checkbox.locator("..").locator("span").click();
            if (!await checkbox.isChecked()) { rejected = i; break; }
        }
        assert.ok(rejected > 1 && rejected < 152);
        assert.match(await branches.getByRole("alert").textContent(), /request size/);
        assert.equal(await branches.locator(".multi-trigger").getAttribute("aria-expanded"), "true");
        await branches.getByRole("checkbox", { name: "Long choice 0", exact: true }).uncheck();
        await branches.getByRole("checkbox", { name: "Long choice " + rejected, exact: true }).check();
        assert.equal(await branches.locator(".checkbox-popup .field-error").textContent(), "");
        assert.equal(refreshes().length, noRequests, "Limits never submit a partial or oversized selection.");
        // pagehide/teardown must not commit this obsolete synthetic collection.
        await page.goto("/"); await page.unroute("**/parameters", oversized); await page.unroute("**/options/branch", longChoices);

        // Visual width, arrow, one-line overflow, focus and viewport checks.
        for (const [w, h] of [[1440, 900], [1024, 768], [768, 1024], [390, 844], [320, 640]]) {
            await page.setViewportSize({ width: w, height: h }); await fresh();
            const geometry = await trigger.evaluate(n => {
                const text = n.firstElementChild, arrow = n.lastElementChild;
                const r = n.getBoundingClientRect(), a = arrow.getBoundingClientRect();
                return { width: r.width, height: r.height, wrap: getComputedStyle(text).whiteSpace, ellipsis: getComputedStyle(text).textOverflow,
                    truncated: text.scrollWidth > text.clientWidth, arrow: a.left >= r.left && a.right <= r.right && a.width > 0 };
            });
            assert.equal(geometry.wrap, "nowrap"); assert.equal(geometry.ellipsis, "ellipsis"); assert.equal(geometry.truncated, true); assert.equal(geometry.arrow, true);
            await scan("material collapsed " + w); await shot("collapsed-" + w); await open();
            const rect = await material.locator(".checkbox-popup").boundingBox();
            assert.ok(rect.x >= 0 && rect.x + rect.width <= w + 1 && rect.y >= 0 && rect.y + rect.height <= h + 1);
            assert.ok(rect.height < 340 || w <= 600, "Desktop popup shrank after removing action rows.");
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
            await scan("material expanded " + w); await shot("expanded-" + w);
            await all.uncheck(); await option("Abstract").check(); await close();
            const after = await trigger.boundingBox(); assert.equal(after.width, geometry.width); assert.equal(after.height, geometry.height);
        }

        // Large-list measurements retain the established 5s open / 2s long-task
        // ceilings. Bulk sync touches existing controls; ordinary clicks do not.
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.goto("/Report?id=" + catalog.find(r => r.name === "Default Showcase").id); await inputsReady();
        const big = multi("Default workstations"), bigAll = big.locator(".select-all input");
        const measurements = {};
        const measure = async (name, action) => {
            await page.evaluate(phase => { window.multiPhase = phase; }, name);
            const started = Date.now(); await action(); measurements[name + "Milliseconds"] = Date.now() - started;
        };
        await page.evaluate(() => {
            window.multiTasks = [];
            window.multiObserver = new PerformanceObserver(list => window.multiTasks.push(...list.getEntries().map(e => ({ phase: window.multiPhase, milliseconds: e.duration }))));
            window.multiObserver.observe({ type: "longtask" });
        });
        await measure("open", async () => { await big.locator(".multi-trigger").click(); await big.locator("input[data-option]").last().waitFor(); });
        const beforeBig = refreshes().length;
        await page.evaluate(() => {
            window.multiCapture = event => {
                if (event.target.type !== "checkbox" || !event.target.closest(".multi-select")) { return; }
                const timing = window.multiEvent = { start: performance.now() };
                requestAnimationFrame(() => requestAnimationFrame(() => { timing.paint = performance.now() - timing.start; }));
            };
            window.multiBubble = event => {
                if (event.target.type === "checkbox" && window.multiEvent) { window.multiEvent.handler = performance.now() - window.multiEvent.start; }
            };
            document.addEventListener("change", window.multiCapture, true);
            document.addEventListener("change", window.multiBubble);
        });
        const checkboxTiming = async name => {
            await page.waitForFunction(() => window.multiEvent?.paint !== undefined);
            const timing = await page.evaluate(() => ({ handler: window.multiEvent.handler, paint: window.multiEvent.paint }));
            measurements[name + "AutomationMilliseconds"] = measurements[name + "Milliseconds"];
            measurements[name + "Milliseconds"] = Math.round(timing.paint);
            measurements[name + "HandlerMilliseconds"] = Math.round(timing.handler);
        };
        await measure("selectAll", () => bigAll.check());
        await checkboxTiming("selectAll");
        assert.equal(await big.locator("input[data-option]:checked").count(), 4192);
        assert.ok((await big.locator(".multi-trigger").textContent()).length < 2000);
        assert.ok((await big.locator(".multi-trigger").getAttribute("aria-label")).length < 300);
        assert.ok((await big.locator(".multi-trigger").getAttribute("title")).length <= 320);
        await big.locator("input[data-option]").first().evaluate(n => { window.retainedBigChoice = n; });
        await measure("deselectOne", () => big.locator("input[data-option]").first().uncheck());
        await checkboxTiming("deselectOne");
        await page.evaluate(() => {
            document.removeEventListener("change", window.multiCapture, true);
            document.removeEventListener("change", window.multiBubble);
        });
        assert.equal(await big.locator("input[data-option]").first().evaluate(n => n === window.retainedBigChoice), true);
        assert.equal(refreshes().length, beforeBig);
        await big.locator('input[type="search"]').press("ArrowDown"); await page.keyboard.press("End");
        assert.equal(await big.locator("input[data-option]").last().evaluate(n => n === document.activeElement && n.checked), true);
        await measure("closeCommit", close); assert.equal(refreshes().length, beforeBig + 1);
        await measure("reopen", async () => { await big.locator(".multi-trigger").click(); await big.locator("input[data-option]").last().waitFor(); });
        assert.equal(await big.locator("input[data-option]:checked").count(), 4191);
        await measure("search", () => big.locator('input[type="search"]').fill("4192"));
        assert.equal(await big.locator("input[data-option]:visible").count(), 1);
        await big.locator('input[type="search"]').fill("");
        await big.locator('input[type="search"]').press("ArrowDown"); await page.keyboard.press("End");
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        measurements.longestTaskMilliseconds = await page.evaluate(() => { window.multiObserver.disconnect(); return Math.round(Math.max(0, ...window.multiTasks.map(t => t.milliseconds))); });
        measurements.longTasks = await page.evaluate(() => window.multiTasks);
        // Keep expensive test analysis after every timed interaction. Accessible
        // name lookup traverses all 4,192 controls; axe also allocates a large
        // analysis tree. Neither belongs inside the UI performance interval.
        assert.equal(await big.getByRole("checkbox", { name: "Synthetic workstation 4192", exact: true }).evaluate(n => n === document.activeElement && n.checked), true);
        await scan("4192 native choices"); await shot("4192-1440"); await close();
        await fs.writeFile("artifacts/browser/multi-select-performance.json", JSON.stringify(measurements, null, 2));
        assert.ok(measurements.openMilliseconds < 5000 && measurements.longestTaskMilliseconds < 2000, JSON.stringify(measurements));
        assert.ok(measurements.reopenMilliseconds < 5000 && measurements.closeCommitMilliseconds < 5000, JSON.stringify(measurements));
        assert.ok(measurements.selectAllMilliseconds < 2000 && measurements.deselectOneMilliseconds < 2000 && measurements.searchMilliseconds < 2000, JSON.stringify(measurements));
        return { nativeMultiSelect: true, fullRowMouse: true, fullRowTouch: true, touchAxeScans: 1, fullSetSearch: true, closeMethods: 9, unchangedDefaults: true, synchronizedExecution: true,
            typedMultiValues: true, countAndByteLimits: true, materialChoices: 52, multiViewports: 5, multiPerformance: measurements };
    } finally { page.off("request", record); }
};
