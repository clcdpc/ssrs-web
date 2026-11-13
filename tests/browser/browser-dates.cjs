"use strict";
const assert = require("node:assert/strict");

module.exports = async ({ page, context, inputsReady, scan, selectSingle }) => {
    const catalog = await (await context.request.get("/api/catalog")).json();
    const report = catalog.find(entry => entry.name === "Check Ins by Day of Week by Hour");
    const url = "/Report?id=" + report.id;
    const begin = "2026-09-01T00:00:00", end = "2026-09-30T23:59:59";
    const edited = "2026-10-01T01:02:03";
    const parameterResponse = () => page.waitForResponse(response => response.url().endsWith("/parameters"));
    const dates = (values, expectedBegin = begin) => {
        assert.deepEqual(values.beginDate, [expectedBegin]);
        assert.deepEqual(values.endDate, [end]);
    };
    const provenance = (defaulted, manual = false) => {
        assert.equal(defaulted.includes("beginDate"), !manual);
        assert.ok(defaulted.includes("endDate"));
    };
    const controls = async (expectedBegin = begin) => {
        for (const [name, expected] of [["beginDate", expectedBegin], ["endDate", end]]) {
            const input = page.getByLabel(name, { exact: true });
            assert.equal(await input.getAttribute("type"), "datetime-local");
            assert.equal(await input.getAttribute("step"), "any");
            // HTML canonicalizes zero seconds away. valueAsNumber proves the
            // complete wall-clock value without using the browser's timezone.
            assert.equal(await input.evaluate(node => node.valueAsNumber), Date.parse(expected + "Z"));
            assert.equal(await input.evaluate(node => node.validity.valid), true);
        }
        assert.equal(await page.getByLabel("endDate", { exact: true }).inputValue(), end);
        assert.equal(await page.locator("#run").isEnabled(), true);
    };
    const token = () => page.evaluate(() => history.state?.ssrsReport?.token ?? null);
    const execute = async (expectedBegin = begin, manual = false) => {
        const previous = (await token());
        const request = page.waitForRequest(request => request.url().endsWith("/run"));
        await page.getByRole("button", { name: "View report", exact: true }).click();
        const submitted = (await request).postDataJSON();
        dates(submitted.values, expectedBegin); provenance(submitted.defaulted, manual);
        await page.waitForFunction(old => {
            const next = (history.state?.ssrsReport?.token ?? null);
            return next && next !== old && !document.querySelector("#run").disabled;
        }, previous);
        const response = await context.request.get("/api/executions/" + (await token()) + "/info");
        assert.equal(response.status(), 200);
        const saved = (await response.json()).input;
        dates(saved.values, expectedBegin); provenance(saved.defaulted, manual);
    };
    const reopen = async (expectedBegin = begin, manual = false) => {
        const savedToken = (await token()), before = runs;
        await page.reload(); await inputsReady();
        await page.frameLocator("#report-frame").getByRole("heading", { name: "Page 1", exact: true }).waitFor();
        assert.equal((await token()), savedToken); assert.equal(runs, before);
        await controls(expectedBegin);
        const info = await (await context.request.get("/api/executions/" + savedToken + "/info")).json();
        dates(info.input.values, expectedBegin); provenance(info.input.defaulted, manual);
    };
    let runs = 0, releaseOld;
    const countRun = request => { if (request.url().endsWith("/run")) { runs++; } };
    page.on("request", countRun);
    try {
        await page.setViewportSize({ width: 1440, height: 900 });
        const initialResponse = parameterResponse();
        await page.goto(url);
        const initial = await (await initialResponse).json(); await inputsReady();
        dates(initial.values); provenance(initial.defaulted); assert.equal(initial.canRun, true);
        for (const name of ["beginDate", "endDate"]) {
            assert.deepEqual(initial.fields.find(field => field.name === name).selected, initial.values[name]);
        }
        await controls(); await scan("observed DEV date defaults before edits");
        assert.equal(runs, 0);
        // Submit and reopen pristine defaults before any parameter is edited.
        await execute(); await reopen();
        let refreshed = parameterResponse();
        await selectSingle("Department", "Shared team");
        const unrelated = await (await refreshed).json();
        dates(unrelated.values); provenance(unrelated.defaulted); await controls();
        assert.deepEqual(unrelated.values.department, ["shared"]);
        await execute(); await reopen();

        // Use a real API response from the old date context. Ignore abort only
        // in this test to prove an obsolete successful response cannot win.
        await page.evaluate(() => {
            window.dateNativeFetch = window.fetch;
            window.fetch = (url, init) => window.dateNativeFetch(url,
                String(url).endsWith("/parameters") ? { ...init, signal: undefined } : init);
        });
        let startedOld, delayed = false;
        const started = new Promise(resolve => { startedOld = resolve; });
        const gate = new Promise(resolve => { releaseOld = resolve; });
        const oldResponse = async route => {
            if (delayed) { await route.continue(); return; }
            delayed = true;
            const response = await route.fetch();
            const old = await response.json(); dates(old.values); provenance(old.defaulted);
            startedOld(); await gate; await route.fulfill({ response });
        };
        await page.route("**/parameters", oldResponse);
        const changingDepartment = selectSingle("Department", "Team for branch 1");
        await started;
        refreshed = parameterResponse();
        await page.getByLabel("beginDate", { exact: true }).fill(edited);
        await page.getByLabel("beginDate", { exact: true }).press("Tab");
        const manualResponse = await refreshed, manual = await manualResponse.json();
        assert.ok(manualResponse.request().postDataJSON().changed.includes("beginDate"));
        assert.ok(manualResponse.request().postDataJSON().changed.includes("department"));
        await inputsReady(); await changingDepartment;
        dates(manual.values, edited); provenance(manual.defaulted, true);
        const delivered = parameterResponse(); releaseOld(); await (await delivered).finished();
        await page.unroute("**/parameters", oldResponse);
        await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        await controls(edited);
        await page.evaluate(() => { window.fetch = window.dateNativeFetch; delete window.dateNativeFetch; });

        refreshed = parameterResponse();
        await selectSingle("Department", "Shared team");
        const preserved = await (await refreshed).json();
        dates(preserved.values, edited); provenance(preserved.defaulted, true);
        await controls(edited); await scan("manual date and end-of-day seconds after unrelated refresh");
        await execute(edited, true); await reopen(edited, true);
        await scan("restored execution date values");
        await page.getByRole("button", { name: "Close", exact: true }).click();
        await page.waitForURL(/\/\?folder=/);
        assert.equal(runs, 3);
        return { observedDevDateDefaults: true, pristineDateExecution: true, dateProvenance: true,
            manualDatePreserved: true, staleDateResponseIgnored: true, savedDatesRestored: true, dateExecutions: runs };
    } finally {
        releaseOld?.();
        page.off("request", countRun);
    }
};
