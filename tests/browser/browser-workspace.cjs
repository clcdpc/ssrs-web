"use strict";
const assert = require("node:assert/strict");

module.exports = async ({ page, context, baseURL, inputsReady, scan }) => {
    page.setDefaultTimeout(20000);
    const catalog = await (await context.request.get("/api/catalog")).json();
    const dates = catalog.find(e => e.name === "Check Ins by Day of Week by Hour");
    const url = "/Report?id=" + dates.id;
    const token = () => page.evaluate(() => history.state?.ssrsReport?.token ?? null);
    const ready = () => page.waitForFunction(() => !document.querySelector("#run").disabled && Boolean((history.state?.ssrsReport?.token ?? null)));
    const output = () => page.frameLocator("#report-frame");
    const change = async (name, value) => {
        await page.getByLabel(name, { exact: true }).fill(value.replace(/:00$/, "")); await page.getByLabel(name, { exact: true }).press("Tab");
        await page.waitForFunction(() => document.querySelector("#parameters").dataset.resolving === "false");
    };
    const run = async () => {
        const before = (await token()); await page.getByRole("button", { name: "View report", exact: true }).click();
        await page.waitForFunction(old => { const next = (history.state?.ssrsReport?.token ?? null); return next && next !== old && !document.querySelector("#run").disabled; }, before);
    };
    const api = async (current, path = "/info") => (await context.request.get("/api/executions/" + current + path)).status();
    const shot = async name => page.screenshot({ path: "artifacts/browser/workspace-" + name + ".png", fullPage: true });
    let runs = 0;
    const count = request => { if (/\/run$/.test(request.url())) { runs++; } };
    page.on("request", count);
    try {
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.goto(url); await inputsReady();
        assert.equal(runs, 0); assert.equal(await page.locator("#viewer").isHidden(), true);
        assert.equal(await page.locator("#report-frame").getAttribute("src"), null);
        assert.equal(await page.locator(".back,#report-name,.interaction-note,h1:not(.visually-hidden)").count(), 0);
        assert.equal(await page.getByRole("heading", { name: "Parameters", exact: true }).count(), 0);
        assert.equal(await page.getByRole("button", { name: "Hide parameters", exact: true }).getAttribute("aria-expanded"), "true");
        assert.equal(await page.locator(".parameter-divider").isVisible(), true, "Collapse is available before the viewer exists.");
        assert.equal(await page.locator("h1").textContent(), dates.name);
        assert.equal(await page.title(), dates.name + " · Staff reports");
        assert.deepEqual(await page.locator(".breadcrumbs>ol>li>a,.breadcrumbs>ol>li>[aria-current]").allTextContents(), ["Training", "Reports", dates.name]);
        assert.equal(await page.locator(".breadcrumbs [aria-current=page]").getAttribute("title"), dates.name);
        assert.equal(await page.locator(".brand").getAttribute("href"), "/");
        const inputs = await page.locator("#parameters").elementHandle();
        const initialHistory = await page.evaluate(() => history.length);
        const values = await page.locator("#fields input").evaluateAll(nodes => nodes.map(n => n.value));
        await shot("initial-desktop"); await scan("workspace before execution");
        await page.locator("#toggle-parameters").press("Enter");
        assert.equal(await page.locator("#parameters").isHidden(), true, "Before any execution the entire idle form collapses.");
        assert.equal(await page.locator(".parameter-section").evaluate(n => n.getBoundingClientRect().height), 0);
        assert.equal(await page.getByRole("button", { name: "View report", exact: true }).count(), 0);
        assert.equal(runs, 0, "Collapsing cannot execute a report.");
        await page.locator("#toggle-parameters").press("Space");
        assert.deepEqual(await page.locator("#fields input").evaluateAll(nodes => nodes.map(n => n.value)), values);
        assert.equal(await page.locator("#run").isVisible(), true);
        assert.equal(runs, 0, "Expanding cannot execute a report.");
        await page.locator("#parameters").evaluate(form => { form.requestSubmit(); form.requestSubmit(); });
        await ready(); await output().getByRole("heading", { name: "Page 1", exact: true }).waitFor();
        assert.equal(runs, 1);
        const first = (await token());
        assert.equal(new URL(page.url()).pathname, dates.reportUrl); assert.equal(await page.evaluate(() => history.length), initialHistory);
        assert.equal(await inputs.evaluate(n => n === document.getElementById("parameters")), true);
        assert.equal(await page.locator("#parameter-controls").isVisible(), true);
        assert.deepEqual(await page.locator("#fields input").evaluateAll(nodes => nodes.map(n => n.value)), values);
        assert.equal(await page.locator("#dirty").isHidden(), true);
        assert.equal(await page.locator("#status").evaluate(n => n.classList.contains("visually-hidden")), true);
        assert.equal(await page.locator("#pdf").getAttribute("href"), "/api/executions/" + first + "/exports/pdf");
        await shot("executed-desktop"); await scan("workspace executed with parameters");
        const top = await page.locator("#report-frame").evaluate(n => n.getBoundingClientRect().top + scrollY);
        const panelHeight = await page.locator(".parameter-section").evaluate(n => n.getBoundingClientRect().height);
        const fullWidth = await page.locator("#report-frame").evaluate(n => n.getBoundingClientRect().width);
        await page.getByRole("button", { name: "Hide parameters", exact: true }).click();
        assert.equal(await page.locator("#parameter-controls").isHidden(), true);
        assert.equal(await page.locator("#toggle-parameters").getAttribute("aria-expanded"), "false");
        const reclaimed = panelHeight - await page.locator(".parameter-section").evaluate(n => n.getBoundingClientRect().height);
        assert.equal(reclaimed, panelHeight, "Collapse reclaims the entire idle parameter section.");
        assert.equal(await page.locator("#run,#cancel-run,#dirty").evaluateAll(nodes => nodes.every(n => !n.checkVisibility())), true);
        assert.ok(Math.abs(top - await page.locator("#report-frame").evaluate(n => n.getBoundingClientRect().top + scrollY) - reclaimed) <= 1, "Output follows the exact reclaimed space in document coordinates.");
        assert.equal(await page.locator("#report-frame").evaluate(n => n.getBoundingClientRect().width), fullWidth);
        assert.ok(fullWidth >= 1400, "Collapsing must retain the full report width.");
        await shot("collapsed-desktop"); await scan("workspace collapsed");
        await page.getByRole("button", { name: "Show parameters", exact: true }).press("Enter");
        assert.deepEqual(await page.locator("#fields input").evaluateAll(nodes => nodes.map(n => n.value)), values);
        await change("beginDate", "2026-10-01T00:00:00"); await change("endDate", "2026-10-31T23:59:59");
        assert.equal(runs, 1, "Editing and dependency resolution never execute."); assert.equal((await token()), first);
        assert.equal(await page.locator("#dirty").isVisible(), true);
        const edited = await page.locator("#fields input").evaluateAll(nodes => nodes.map(n => n.value));
        await page.locator("#toggle-parameters").click();
        assert.equal(await page.locator("#dirty").isHidden(), true);
        assert.deepEqual(await page.locator("#fields input").evaluateAll(nodes => nodes.map(n => n.value)), edited);
        assert.equal(runs, 1, "Collapsing pending changes neither submits nor discards them.");
        await page.locator("#toggle-parameters").click();
        assert.equal(await page.locator("#dirty").isVisible(), true);
        await shot("edited-desktop");
        const fail = route => route.fulfill({ status: 502, contentType: "application/json", json: { code: "timeout", message: "The report request timed out. Try a smaller range or run it again.", trace: "synthetic-rerun" } });
        await page.route("**/run", fail);
        await page.getByRole("button", { name: "View report", exact: true }).click(); await page.locator("#status[role=alert]").waitFor();
        assert.equal((await token()), first); assert.equal(await api(first), 200);
        assert.equal(await page.locator("#pdf").getAttribute("href"), "/api/executions/" + first + "/exports/pdf");
        assert.equal(await page.getByLabel("endDate", { exact: true }).inputValue(), "2026-10-31T23:59:59");
        assert.equal(await page.locator("#next").isEnabled(), true);
        await shot("failed-rerun-desktop"); await scan("failed rerun retains report");
        await page.unroute("**/run", fail);
        // Successful SOAP execution is insufficient to retire the displayed session:
        // the replacement iframe must finish loading first.
        let releaseFrame, frameStarted;
        const frameGate = new Promise(resolve => { releaseFrame = resolve; });
        const staged = new Promise(resolve => { frameStarted = resolve; });
        const stageHandler = async route => { const response = await route.fetch(); frameStarted(); await frameGate; await route.fulfill({ response }).catch(() => {}); };
        await page.route("**/executions/*/viewer*", stageHandler);
        await page.getByRole("button", { name: "View report", exact: true }).click(); await staged;
        assert.equal((await token()), first); assert.equal(await api(first), 200);
        await output().getByRole("heading", { name: "Page 1", exact: true }).waitFor();
        releaseFrame(); await ready();
        await page.waitForFunction(old => (history.state?.ssrsReport?.token ?? null) !== old, first);
        await page.unroute("**/executions/*/viewer*", stageHandler);
        const second = (await token()); assert.equal(await api(first), 404); assert.equal(await api(second), 200);
        assert.equal(await page.locator("#dirty").isHidden(), true); assert.equal(await page.locator("#parameters").isVisible(), true);
        const info = await (await context.request.get("/api/executions/" + second + "/info")).json();
        assert.deepEqual(info.input.values.endDate, ["2026-10-31T23:59:59"]);
        assert.ok(info.input.defaulted.includes("branch"), "Default provenance survives execution.");
        await shot("rerun-desktop");
        const beforeRefresh = runs;
        await page.reload(); await inputsReady(); await ready();
        assert.equal((await token()), second); assert.equal(runs, beforeRefresh);
        assert.equal(await page.getByLabel("endDate", { exact: true }).inputValue(), "2026-10-31T23:59:59");
        await page.goto("/Viewer?token=" + second); await inputsReady(); await ready();
        assert.equal(new URL(page.url()).pathname, dates.reportUrl); assert.equal((await token()), second); assert.equal(runs, beforeRefresh);
        // Editing while Run is pending must not be overwritten by its result.
        let releaseRun, startedRun;
        const runGate = new Promise(resolve => { releaseRun = resolve; });
        const started = new Promise(resolve => { startedRun = resolve; });
        const delayedRun = async route => { const response = await route.fetch(); startedRun(); await runGate; await route.fulfill({ response }); };
        await page.route("**/run", delayedRun);
        await page.getByRole("button", { name: "View report", exact: true }).click(); await started;
        await change("endDate", "2026-10-30T23:59:59"); releaseRun();
        await ready(); await page.waitForFunction(old => (history.state?.ssrsReport?.token ?? null) !== old, second);
        await page.unroute("**/run", delayedRun);
        assert.equal(await page.getByLabel("endDate", { exact: true }).inputValue(), "2026-10-30T23:59:59");
        assert.equal(await page.locator("#dirty").isVisible(), true); await run(); assert.equal(await page.locator("#dirty").isHidden(), true);
        // Deliberately ignore AbortSignal to exercise revision guards on late replies.
        await page.evaluate(() => {
            const native = window.fetch;
            window.fetch = (url, init) => native(url, window.ignoreWorkspaceAbort && /\/run$|\/page\/\d+$/.test(String(url)) ? { ...init, signal: undefined } : init);
            window.ignoreWorkspaceAbort = true;
        });
        let releaseOld, oldStarted, oldToken, delayedCount = 0;
        const oldGate = new Promise(resolve => { releaseOld = resolve; });
        const oldReady = new Promise(resolve => { oldStarted = resolve; });
        const staleRun = async route => {
            if (++delayedCount > 1) { await route.continue(); return; }
            const response = await route.fetch(); oldToken = (await response.json()).token; oldStarted(); await oldGate; await route.fulfill({ response });
        };
        await page.route("**/run", staleRun);
        await page.getByRole("button", { name: "View report", exact: true }).click(); await oldReady;
        await page.getByRole("button", { name: "Cancel request", exact: true }).click(); await page.waitForFunction(() => !document.querySelector("#run").disabled);
        await run(); const current = (await token());
        const cancelledOld = page.waitForResponse(r => r.url().endsWith("/executions/" + oldToken + "/cancel")); releaseOld(); await cancelledOld;
        await page.unroute("**/run", staleRun);
        assert.equal((await token()), current); assert.equal(await api(oldToken), 404); assert.equal(await api(current), 200);
        let releasePage, pageStarted;
        const pageGate = new Promise(resolve => { releasePage = resolve; });
        const pageReady = new Promise(resolve => { pageStarted = resolve; });
        const stalePage = async route => { const response = await route.fetch(); pageStarted(); await pageGate; await route.fulfill({ response }); };
        await page.route("**/executions/*/page/*", stalePage);
        await page.getByRole("button", { name: "Next", exact: true }).click(); await pageReady;
        await run(); const newer = (await token());
        const latePage = page.waitForResponse(r => /\/page\/2$/.test(r.url())); releasePage(); await latePage;
        await page.unroute("**/executions/*/page/*", stalePage);
        assert.equal((await token()), newer); assert.equal(await page.locator("#page-label").textContent(), "Page 1 of 2");
        await output().getByRole("heading", { name: "Page 1", exact: true }).waitFor();
        await page.evaluate(() => { window.ignoreWorkspaceAbort = false; });
        for (const viewport of [{ width: 1440, height: 900 }, { width: 1024, height: 768 }, { width: 768, height: 1024 }, { width: 390, height: 844 }, { width: 320, height: 640 }]) {
            await page.setViewportSize(viewport);
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
            const geometry = await page.evaluate(() => {
                const crumb = document.querySelector(".breadcrumbs").getBoundingClientRect(), account = document.querySelector(".account").getBoundingClientRect();
                const toolbar = document.querySelector(".viewer-toolbar").getBoundingClientRect(), frame = document.querySelector("#report-frame").getBoundingClientRect();
                return { noOverlap: crumb.right <= account.left + 1 || crumb.top >= account.bottom - 1, gap: frame.top - toolbar.bottom };
            });
            assert.equal(geometry.noOverlap, true); assert.ok(Math.abs(geometry.gap) < 2);
            await shot("executed-" + viewport.width); await scan("workspace viewport " + viewport.width);
            await page.getByRole("button", { name: "Hide parameters", exact: true }).click();
            assert.ok(await page.locator("#report-frame").evaluate(n => n.getBoundingClientRect().width) >= viewport.width - 32, "Collapsed report keeps the available width.");
            await shot("collapsed-" + viewport.width); await scan("collapsed workspace " + viewport.width);
            await page.getByRole("button", { name: "Show parameters", exact: true }).click();
        }
        await page.setViewportSize({ width: 1440, height: 900 }); await change("endDate", "2026-10-29T23:59:59");
        for (const [name, label] of [["Last", "Page 2 of 2"], ["First", "Page 1 of 2"], ["Next", "Page 2 of 2"], ["Previous", "Page 1 of 2"]]) {
            await page.getByRole("button", { name, exact: true }).click();
            await page.waitForFunction(expected => document.querySelector("#page-label").textContent === expected && document.querySelector("#pdf").getAttribute("aria-disabled") === "false", label);
        }
        for (const [name, format] of [["PDF", "pdf"], ["Excel", "xlsx"], ["Word", "docx"], ["CSV", "csv"]]) {
            assert.equal(await page.getByRole("link", { name, exact: true }).getAttribute("href"), "/api/executions/" + newer + "/exports/" + format);
            const download = page.waitForEvent("download"); await page.getByRole("link", { name, exact: true }).click(); assert.equal(await (await download).failure(), null);
        }
        const closed = (await token()); await page.getByRole("button", { name: "Close", exact: true }).click();
        await page.waitForURL(baseURL + "/?folder=" + dates.parentId); assert.equal(await api(closed), 404);
        for (const [name, capture] of [["Default Showcase", "many-parameters"], ["One Input", "one-input"], [catalog.find(e => e.name.startsWith("Check Ins by Day of Week by Hour with a very long")).name, "deep-long-title"]]) {
            const entry = catalog.find(e => e.name === name); await page.goto("/Report?id=" + entry.id); await inputsReady();
            await shot(capture + "-desktop"); await scan(capture);
            if (capture === "deep-long-title") {
                const more = page.locator(".breadcrumbs summary");
                const parentCount = await page.locator(".breadcrumbs .breadcrumb-folder a").count();
                for (const width of [1024, 390, 320]) {
                    await page.setViewportSize({ width, height: width === 320 ? 640 : 844 });
                    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
                    if (await more.isVisible()) {
                        await more.focus();
                        if (!await page.locator(".breadcrumbs details").evaluate(n => n.open)) { await more.press("Enter"); }
                        assert.ok(await page.locator(".breadcrumbs details[open] a").count() > 0);
                        const box = await page.locator(".breadcrumbs .breadcrumb-more details>ol").boundingBox(); assert.ok(box.x >= 0 && box.x + box.width <= width);
                    } else {
                        // The wider second breadcrumb row can now fit the entire
                        // hierarchy at 1024px. Require every parent to remain visible.
                        assert.equal(await page.locator(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder a").count(), parentCount);
                        assert.equal(await page.locator(".breadcrumbs .breadcrumb-more details>ol a").count(), 0);
                    }
                    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
                    assert.equal(await page.locator(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder a").evaluateAll(nodes => nodes.every(n => n.getBoundingClientRect().width >= 24)), true, "Direct parent links must retain usable width beside a long report name.");
                    await shot("deep-menu-" + width); await scan("deep breadcrumb menu " + width);
                }
            } else { await page.setViewportSize({ width: 390, height: 844 }); await shot(capture + "-mobile"); }
            await page.setViewportSize({ width: 1440, height: 900 });
        }
        const beforeExpired = runs; await page.goto(url + "&token=" + closed); await inputsReady();
        assert.equal((await token()), null); assert.equal(runs, beforeExpired); assert.equal(await page.locator("#viewer").isHidden(), true);
        assert.match(await page.locator("#status[role=alert]").textContent(), /expired or is unavailable/); await run();
        // Output/cleanup failures after successful server rendering retain the old
        // report, then retry cleanup before allocating another replacement.
        const retained = (await token()); let rejectedToken;
        const brokenFrame = async route => {
            rejectedToken = /\/executions\/([^/]+)\/viewer/.exec(route.request().url())[1];
            await route.fulfill({ status: 502, contentType: "application/json", json: { code: "resource", message: "Synthetic output was unavailable. Try again." } });
        };
        let cleanupFailures = 0;
        const failedCleanup = async route => {
            if (++cleanupFailures === 1) { await route.fulfill({ status: 503, contentType: "application/json", json: { message: "Synthetic cleanup interruption." } }); }
            else { await route.continue(); }
        };
        await page.route("**/executions/*/viewer*", brokenFrame); await page.route("**/executions/*/cancel", failedCleanup);
        await page.getByRole("button", { name: "View report", exact: true }).click();
        await page.locator("#status[role=alert]").waitFor(); await page.waitForFunction(() => !document.querySelector("#run").disabled);
        assert.equal((await token()), retained); assert.equal(await api(retained), 200); assert.equal(await api(rejectedToken), 200);
        await page.unroute("**/executions/*/viewer*", brokenFrame);
        await run(); assert.equal(await api(rejectedToken), 404); await page.unroute("**/executions/*/cancel", failedCleanup);
        // A lost promotion reply must not invalidate a newly committed report.
        const priorCommit = (await token());
        const lostCommit = async route => { await route.fetch(); await route.abort(); };
        await page.route("**/executions/*/commit", lostCommit); await run();
        await page.unroute("**/executions/*/commit", lostCommit);
        assert.equal(await api(priorCommit), 404); assert.equal(await api((await token())), 200);
        // A lost Run reply is cleaned up through the still-authorized source.
        let lostToken;
        const lostRun = async route => { const response = await route.fetch(); lostToken = (await response.json()).token; await route.abort(); };
        await page.route("**/run", lostRun);
        const lostSource = (await token()); await page.getByRole("button", { name: "View report", exact: true }).click();
        await page.locator("#status[role=alert]").waitFor(); await page.waitForFunction(() => !document.querySelector("#run").disabled);
        assert.equal((await token()), lostSource); assert.equal(await api(lostSource), 200); assert.equal(await api(lostToken), 404);
        await page.unroute("**/run", lostRun);
        // Unknown and single-page counts drive the compact toolbar correctly.
        const unknown = async route => { const response = await route.fetch(); await route.fulfill({ response, json: { ...await response.json(), pages: null } }); };
        await page.route("**/run", unknown); await run(); await page.unroute("**/run", unknown);
        assert.match(await page.locator("#page-label").textContent(), /total not yet known/);
        assert.equal(await page.locator("#last").isDisabled(), true); assert.equal(await page.locator("#next").isEnabled(), true);
        const singlePage = async route => { const response = await route.fetch(); await route.fulfill({ response, json: { ...await response.json(), pages: 1 } }); };
        await page.route("**/run", singlePage); await run(); await page.unroute("**/run", singlePage);
        assert.equal(await page.locator("#page-label").textContent(), "Page 1 of 1");
        for (const name of ["first", "previous", "next", "last"]) { assert.equal(await page.locator("#" + name).isDisabled(), true); }
        await shot("single-page-desktop");
        const sessionToExpire = (await token()), csrf = (await (await context.request.get("/api/csrf")).json()).token;
        await context.request.post("/api/executions/" + sessionToExpire + "/cancel", { headers: { RequestVerificationToken: csrf }, data: {} });
        // Restore the real page count before triggering an expired-session action.
        await page.reload(); await inputsReady(); assert.equal((await token()), null); assert.equal(await page.locator("#viewer").isHidden(), true);
        await run();
        const historySession = (await token()), beforeHistory = runs;
        await page.goto("/?folder=" + dates.parentId);
        await page.getByRole("link", { name: "Report: " + dates.name, exact: true }).click(); await inputsReady();
        assert.equal((await token()), null); assert.equal(runs, beforeHistory);
        await page.goBack(); await page.waitForURL(baseURL + "/?folder=" + dates.parentId);
        await page.goBack(); await ready(); assert.equal((await token()), historySession); assert.equal(runs, beforeHistory);
        await page.getByRole("button", { name: "Close", exact: true }).click(); await page.waitForURL(/\/\?folder=/);
        const executionStatus = await require("./browser-execution-status.cjs")({ page, context, inputsReady, scan });
        return { ...executionStatus, unifiedWorkspace: true, workspaceRuns: runs, atomicFrameReplacement: true, failedRerunPreserved: true, boundedReplacementCleanup: true,
            staleExecutionIgnored: true, stalePageIgnored: true, refreshWithoutExecution: true, legacyViewerRedirect: true, parameterCollapse: true,
            parametersEditedDuringRun: true, cleanupRetry: true, lostRunReplyCleanup: true, lostCommitReplyRecovery: true, unknownAndSinglePage: true,
            browserHistoryRecovery: true, compactHeaderBreadcrumb: true, deepBreadcrumbMenu: true, workspaceViewports: [1440, 1024, 768, 390, 320] };
    } finally { page.off("request", count); }
};
