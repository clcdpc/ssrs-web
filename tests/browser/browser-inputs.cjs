"use strict";
const assert = require("node:assert/strict");

module.exports = async ({ page, context, baseURL, scan, inputsReady, multi, single, selectSingle }) => {
    const catalog = await (await context.request.get("/api/catalog")).json();
    const report = catalog.find(e => e.name === "Check Ins by Day of Week by Hour");
    const reportUrl = report.reportUrl;
    const branches = multi("Branches");
    const trigger = branches.locator(".multi-trigger");
    const checkbox = n => branches.getByRole("checkbox", { name: "Synthetic branch " + String(n).padStart(3, "0"), exact: true });
    const search = branches.getByRole("searchbox", { name: "Search Branches", exact: true });
    const all = branches.getByRole("checkbox", { name: "(Select All)", exact: true });
    const opened = async () => {
        await trigger.click();
        await branches.locator("input[data-option]").last().waitFor();
    };
    const commit = async () => {
        await page.keyboard.press("Escape");
        await inputsReady();
    };
    let refreshes = 0, optionRequests = 0;
    const countRequest = request => {
        if (/\/parameters$/.test(request.url())) { refreshes++; }
        if (/\/options\/branch$/.test(request.url())) { optionRequests++; }
    };
    page.on("request", countRequest);
    try {
        await page.goto(reportUrl); await inputsReady();
        assert.equal(await page.locator(".breadcrumbs .breadcrumb-folder a").last().getAttribute("href"), "/?folder=" + report.parentId);
        await page.locator(".breadcrumbs .breadcrumb-folder a").last().click();
        assert.equal(await page.locator(".breadcrumbs [aria-current]").textContent(), "Reports");
        await page.goto("/?q=Check%20Ins");
        await page.getByRole("link", { name: "Report: " + report.name, exact: true }).click(); await inputsReady();
        await page.setViewportSize({ width: 1440, height: 900 });
        assert.equal(await page.locator("#status").getAttribute("role"), "status");
        assert.equal(await page.locator("#status").evaluate(n => n.classList.contains("visually-hidden")), true);
        assert.equal(await page.locator("#status.notice,#run-hint,select[multiple],.selection,.chip,.option-paging").count(), 0);
        assert.equal(await trigger.textContent(), "Synthetic branch 001, Synthetic branch 080, Synthetic branch 152⌄");
        assert.equal(await search.isVisible(), false);
        assert.equal(optionRequests, 0, "Closed multiselects serialize and render no choices.");
        assert.match(await page.getByLabel("beginDate", { exact: true }).inputValue(), /^2026-09-01T00:00(?::00)?$/);
        assert.equal(await page.getByLabel("endDate", { exact: true }).inputValue(), "2026-09-30T23:59:59");
        await scan("compact dates desktop");
        await page.screenshot({ path: "artifacts/browser/inputs-desktop.png", fullPage: true });
        await opened();
        const loaded = optionRequests;
        assert.equal(loaded, 1);
        assert.equal(await branches.locator(".checkbox-choices input").count(), 153);
        assert.equal(await branches.locator(".checkbox-choices input").first().getAttribute("id"), await all.getAttribute("id"));
        assert.equal(await page.getByRole("button", { name: /Earlier choices|More choices/ }).count(), 0);
        const rowHeight = await branches.locator(".checkbox-choice").first().evaluate(n => n.getBoundingClientRect().height);
        assert.ok(rowHeight >= 30 && rowHeight <= 34, "Desktop choices are dense and readable.");
        const before = refreshes;
        await checkbox(1).focus(); await page.keyboard.press("Tab"); await page.keyboard.press("Shift+Tab");
        assert.equal(await checkbox(1).evaluate(n => getComputedStyle(n).outlineStyle), "solid");
        await page.keyboard.press("Space"); await page.keyboard.press("ArrowDown");
        assert.equal(await checkbox(2).evaluate(n => n === document.activeElement), true);
        await page.keyboard.press("Space");
        await search.focus(); await search.press("Enter");
        assert.equal(page.url(), baseURL + reportUrl);
        await search.press("Escape");
        await inputsReady();
        assert.equal(await trigger.evaluate(n => n === document.activeElement), true);
        await opened();
        assert.equal(await checkbox(1).isChecked(), false);
        assert.equal(await checkbox(2).isChecked(), true);
        await checkbox(2).check(); await checkbox(152).scrollIntoViewIfNeeded();
        assert.equal(await checkbox(152).isChecked(), true);
        await search.fill("152");
        assert.equal(await branches.locator(".checkbox-choice:visible").count(), 2);
        assert.equal(await checkbox(152).isChecked(), true);
        await page.screenshot({ path: "artifacts/browser/checkbox-filtered-desktop.png", fullPage: true });
        await search.fill("NO MATCH");
        assert.equal(await branches.getByText("No matching choices.", { exact: true }).count(), 1);
        await search.fill("");
        assert.equal(await checkbox(2).isChecked(), true);
        assert.equal(optionRequests, loaded, "Search, reopening and scrolling reuse the complete loaded collection.");
        await commit();
        assert.equal(refreshes, before + 1, "Several edits resolve once on Escape.");
        const unchanged = refreshes;
        for (const closing of ["outside", "Escape", "trigger", "Tab"]) {
            await opened();
            if (closing === "outside") { await page.locator(".breadcrumbs [aria-current=page]").click(); }
            else if (closing === "trigger") { await trigger.click(); }
            else if (closing === "Tab") { await checkbox(152).focus(); await page.keyboard.press("Tab"); }
            else { await page.keyboard.press("Escape"); }
            assert.equal(await trigger.getAttribute("aria-expanded"), "false");
        }
        assert.equal(refreshes, unchanged, "Unedited dismissals make no requests.");
        await opened();
        assert.equal(await checkbox(2).isChecked(), true);
        await all.check(); await all.uncheck();
        await checkbox(3).check(); await checkbox(2).check(); await commit();
        assert.equal(refreshes, unchanged + 1);
        assert.equal(await trigger.textContent(), "Synthetic branch 002, Synthetic branch 003⌄");
        assert.equal(await single("Department").locator(".single-trigger span").first().textContent(), "Team for branch 2");
        assert.equal(optionRequests, loaded, "Own selection changes keep the same choice cache.");
        await selectSingle("Department", "Shared team");
        await opened(); await checkbox(2).uncheck(); await commit();
        assert.equal(await single("Department").locator(".single-trigger span").first().textContent(), "Shared team");

        // A deliberate empty selection stays empty until a parent or user changes it.
        await opened(); await all.check(); await all.uncheck(); await commit();
        assert.equal(await trigger.textContent(), "Select Branches⌄");
        assert.equal(await page.locator("#run").isDisabled(), true);
        await page.getByLabel("beginDate", { exact: true }).fill("2026-09-02T00:00");
        await page.getByLabel("beginDate", { exact: true }).press("Tab"); await inputsReady();
        assert.equal(await trigger.textContent(), "Select Branches⌄");
        await page.getByLabel("beginDate", { exact: true }).fill("2026-09-01T00:00");
        await page.getByLabel("beginDate", { exact: true }).press("Tab"); await inputsReady();
        await opened(); await search.fill("152");
        await all.check();
        assert.equal(await all.isChecked(), true);
        assert.match(await trigger.textContent(), /^Synthetic branch 001, Synthetic branch 002/);
        assert.equal(optionRequests, loaded, "Select all includes every choice without another request.");
        await commit();
        assert.match(await trigger.textContent(), /^Synthetic branch 001, Synthetic branch 002/);
        await opened();
        assert.equal(await branches.getByRole("checkbox", { checked: true }).count(), 153);
        await scan("dense full checkbox dropdown desktop");
        await page.screenshot({ path: "artifacts/browser/checkbox-desktop.png", fullPage: true });
        await page.keyboard.press("Escape");

        const nullable = multi("Nullable choices");
        await nullable.locator(".multi-trigger").click();
        await nullable.getByRole("checkbox", { name: "(Blank choice)", exact: true }).waitFor();
        assert.equal(await nullable.getByRole("checkbox", { name: "(Blank choice)", exact: true }).isChecked(), true);
        await nullable.getByRole("checkbox", { name: "Named choice", exact: true }).check();
        await page.keyboard.press("Escape"); await inputsReady();
        await page.setViewportSize({ width: 390, height: 844 });
        await scan("compact dates mobile");
        await page.screenshot({ path: "artifacts/browser/inputs-mobile.png", fullPage: true });
        await opened();
        const popup = await branches.locator(".checkbox-popup").boundingBox();
        assert.ok(popup.x >= 0 && popup.x + popup.width <= 391 && popup.y >= 0 && popup.y + popup.height <= 845);
        assert.ok(await branches.locator(".checkbox-choice").first().evaluate(n => n.getBoundingClientRect().height >= 44));
        await scan("touch checkbox dropdown mobile");
        await page.screenshot({ path: "artifacts/browser/checkbox-mobile.png", fullPage: true });
        await page.keyboard.press("Escape");
        await page.setViewportSize({ width: 320, height: 568 });
        await opened();
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
        const small = await branches.locator(".checkbox-popup").boundingBox();
        assert.ok(small.x >= 0 && small.x + small.width <= 321 && small.y >= 0 && small.y + small.height <= 569);
        await search.press("ArrowDown");
        assert.ok(await all.evaluate(n => { const r=n.getBoundingClientRect(); return r.top>=0 && r.bottom<=innerHeight; }));
        await page.keyboard.press("Escape");
        await page.setViewportSize({ width: 1440, height: 900 }); await opened(); await checkbox(1).uncheck();
        const runRequest = page.waitForRequest(r => /\/run$/.test(r.url()));
        await page.getByRole("button", { name: "View report", exact: true }).click();
        const submitted = (await runRequest).postDataJSON().values;
        assert.equal(submitted.branch.length, 151);
        assert.equal(submitted.branch.includes("1"), false, "View report commits pending edits and waits for dependencies.");
        assert.deepEqual(submitted.beginDate, ["2026-09-01T00:00:00"]);
        assert.deepEqual(submitted.endDate, ["2026-09-30T23:59:59"]);
        assert.deepEqual(submitted.blankChoices, ["", "named"]);
        await page.waitForFunction(() => Boolean(history.state?.ssrsReport?.token) && document.querySelector("#run").dataset.executing === "false");
        await page.getByRole("button", { name: "Close" }).click(); await page.waitForURL(/\/\?folder=/);

        // Oversized additions are rejected without replacing the valid local set.
        const limitHandler = async route => {
            const response = await route.fetch(), found = await response.json();
            found.fields.find(f => f.name === "branch").selectionLimit = 3;
            await route.fulfill({ response, json: found });
        };
        await page.route("**/parameters", limitHandler);
        await page.goto(reportUrl); await inputsReady(); await opened();
        await all.click();
        assert.equal(await branches.getByRole("checkbox", { checked: true }).count(), 3);
        assert.match(await branches.locator(".checkbox-popup .field-error").textContent(), /no more than 3/);
        await checkbox(1).uncheck(); await checkbox(80).uncheck(); await checkbox(152).uncheck();
        await checkbox(2).check(); await checkbox(3).check();
        assert.equal(await branches.locator(".checkbox-popup .field-error").textContent(), "");
        await commit();
        await page.unroute("**/parameters", limitHandler);

        // Closing cancels pending loading. Even a reply ignoring abort cannot repopulate a new popup.
        await page.goto(reportUrl); await inputsReady();
        await page.evaluate(() => {
            window.choiceFetch = window.fetch;
            window.fetch = (url, options = {}) => window.choiceFetch(url, /\/options\/branch$/.test(url) ? { ...options, signal: undefined } : options);
        });
        let releaseChoices, startedChoices;
        const gate = new Promise(resolve => { releaseChoices = resolve; });
        const started = new Promise(resolve => { startedChoices = resolve; });
        const delayed = async route => {
            const response = await route.fetch(); startedChoices(); await gate; await route.fulfill({ response });
        };
        await page.route("**/options/branch", delayed);
        await trigger.click(); await started; await page.keyboard.press("Escape");
        const delivered = page.waitForResponse(r => /\/options\/branch$/.test(r.url()));
        releaseChoices(); await (await delivered).finished(); await page.unroute("**/options/branch", delayed);
        await opened();
        assert.equal(await checkbox(2).isChecked(), false);
        await page.keyboard.press("Escape");
        await page.evaluate(() => { window.fetch = window.choiceFetch; delete window.choiceFetch; });
        // Actual browser abort on close, then successful reopen.
        await page.goto(reportUrl); await inputsReady();
        let releaseAbort, startedAbort;
        const abortGate = new Promise(resolve => { releaseAbort = resolve; });
        const abortStarted = new Promise(resolve => { startedAbort = resolve; });
        const abortHandler = async route => { startedAbort(); await abortGate; await route.abort().catch(() => {}); };
        await page.route("**/options/branch", abortHandler);
        await trigger.click(); await abortStarted;
        const aborted = page.waitForEvent("requestfailed", r => /\/options\/branch$/.test(r.url()));
        await page.keyboard.press("Escape"); await aborted; releaseAbort();
        await page.unroute("**/options/branch", abortHandler); await opened(); await page.keyboard.press("Escape");
        const errorHandler = route => route.fulfill({ status: 502, contentType: "application/json", body: JSON.stringify({ message: "Choices unavailable. Retry loading." }) });
        await page.goto(reportUrl); await inputsReady(); await page.route("**/options/branch", errorHandler);
        await trigger.click(); await branches.getByRole("alert").filter({ hasText: "Choices unavailable" }).waitFor();
        assert.equal(await branches.locator(".checkbox-choices input").count(), 0);
        await scan("choice loading failure");
        await page.unroute("**/options/branch", errorHandler);
        await branches.getByRole("button", { name: "Retry choices" }).click();
        await checkbox(152).waitFor();
        assert.equal(await branches.locator(".checkbox-popup .field-error").textContent(), "");
        await page.keyboard.press("Escape");

        // Actual Razor catalog rendering, including the linked report and report tilde case.
        const folder = catalog.find(e => e.name === "Sorting fixture");
        await page.setViewportSize({ width: 390, height: 844 });
        await page.goto("/?folder=" + folder.id);
        await page.evaluate(() => window.scrollTo(0, 0));
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
        const names = await page.locator(".catalog-name").allTextContents();
        assert.deepEqual(names.slice(0, 4), ["~Archives", "~Tech Services", "Administration", "Circulation"]);
        assert.deepEqual(names.slice(4), ["Alpha sample", "Linked sample", "~Report sample"].sort((a, b) => a.toUpperCase() < b.toUpperCase() ? -1 : 1));
        assert.equal(names.some(n => n.includes("Hidden")), false);
        await scan("tilde folder ordering");
        await page.screenshot({ path: "artifacts/browser/catalog-sorting-mobile.png", fullPage: true });
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.evaluate(() => window.scrollTo(0, 0));
        await page.screenshot({ path: "artifacts/browser/catalog-sorting-desktop.png", fullPage: true });
        await page.goto("/?q=sample");
        assert.equal(await page.locator(".catalog-list").getByText("~Archives", { exact: true }).count(), 0);
        assert.ok(await page.getByRole("link", { name: "Linked report: Linked sample", exact: true }).count());

        // Global Select all also works for the larger existing fixture without loading its pages.
        const bigReport = catalog.find(e => e.name === "Default Showcase");
        await page.goto("/Report?id=" + bigReport.id); await inputsReady();
        const big = multi("Default workstations");
        await big.locator(".multi-trigger").click();
        await big.getByRole("checkbox", { name: "(Select All)", exact: true }).check();
        assert.equal(await big.locator(".checkbox-choices input").count(), 4193);
        await page.keyboard.press("Escape"); await inputsReady();
        assert.match(await big.locator(".multi-trigger").textContent(), /^Synthetic workstation 0001, Synthetic workstation 0002/);

        // The button shows progress; running/cancellation announcements stay in
        // the live region without adding a navigation/parameter feedback row.
        let releaseRun, runStarted;
        const runGate = new Promise(resolve => { releaseRun = resolve; });
        const running = new Promise(resolve => { runStarted = resolve; });
        const runHandler = async route => {
            runStarted(); await runGate;
            await route.fulfill({ contentType: "application/json", body: JSON.stringify({ token: "cancelled-fixture" }) }).catch(() => {});
        };
        await page.route("**/run", runHandler);
        await page.getByRole("button", { name: "View report", exact: true }).click(); await running;
        assert.match(await page.locator(".workspace-feedback #status").textContent(), /Running report/);
        assert.equal(await page.locator("#status").evaluate(n => n.classList.contains("visually-hidden")), true);
        assert.equal(await page.getByRole("button", { name: "Running…", exact: true }).isDisabled(), true);
        await page.getByRole("button", { name: "Cancel request", exact: true }).click();
        await page.getByText("Request cancelled. Select View report to try again.", { exact: true }).waitFor();
        releaseRun(); await page.unroute("**/run", runHandler);
        assert.equal(await page.getByRole("button", { name: "View report", exact: true }).isEnabled(), true);
        assert.equal(await page.getByRole("button", { name: "Cancel request", exact: true }).isHidden(), true);
        return { checkboxAutomaticCommit: true, checkboxKeyboard: true, branchOptions: 152, selectAll4192: true, localSearch: true,
            closeAbortsChoices: true, staleChoicesIgnored: true, runCancellation: true, compactReport: true, containingFolder: true, tildeFolders: true, capturedDateDefaults: true };
    } finally {
        page.off("request", countRequest);
    }
};
