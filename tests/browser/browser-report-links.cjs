"use strict";
const assert = require("node:assert/strict");

module.exports = async ({ page, context, baseURL, inputsReady, scan }) => {
    const { openSingle } = require("./single-choice-helpers.cjs")(page, inputsReady);
    const catalog = await (await context.request.get("/api/catalog")).json();
    const url = name => catalog.find(e => e.name === name).reportUrl;
    const token = () => page.evaluate(() => history.state?.ssrsReport?.token ?? null);
    const ready = () => page.waitForFunction(() => Boolean(history.state?.ssrsReport?.token) &&
        document.querySelector("#parameters").getAttribute("aria-busy") === "false" && !document.querySelector("#viewer").hidden);
    const open = async (name, query = "") => { await page.goto(url(name) + query); await inputsReady(); };
    const close = async () => { if (await token()) { await page.locator("#cancel-session").click(); await page.waitForURL(/\?folder=/); } };
    let runs = 0;
    const count = request => { if (/\/run$/.test(request.url())) { runs++; } };
    const copy = async () => {
        await page.locator("#copy-report-link").press("Enter");
        await page.getByText("Report link copied.", { exact: true }).waitFor();
        return new URL(await page.evaluate(() => navigator.clipboard.readText()));
    };
    page.on("request", count);
    await context.grantPermissions(["clipboard-read", "clipboard-write"]);
    try {
        await open("One Input");
        assert.equal(runs, 0); assert.equal(await page.locator("#viewer").isHidden(), true);
        assert.equal(await page.locator("#print-report").getAttribute("href"), null);
        assert.equal(await page.locator("#print-report").getAttribute("aria-disabled"), "true");
        for (const [flag, suppressed] of [["true", true], ["1", true], ["false", false], ["0", false]]) {
            const before = runs;
            await open("One Input", "?OrganizationID=8&portal%3AnoExecute=" + flag);
            assert.equal(await page.getByLabel("OrganizationID", { exact: true }).inputValue(), "8");
            if (suppressed) {
                assert.equal(await page.locator("#viewer").isHidden(), true); assert.equal(runs, before);
                await page.locator("#run").press("Enter");
            }
            await ready(); assert.equal(runs, before + 1);
            await page.waitForTimeout(120); assert.equal(runs, before + 1, "URL initialization executes once.");
            await close();
        }
        const beforeControl = runs;
        await open("One Input", "?portal:noExecute=false");
        assert.equal(runs, beforeControl); assert.equal(await page.locator("#viewer").isHidden(), true);
        for (const query of ["?OrganizationID=8&OrganizationID=9", "?OrganizationID=invalid", "?organizationid=8",
            "?rs:Command=Render", "?OrganizationID=8&portal:noExecute=true&portal:noExecute=false", "?OrganizationID=%FF"]) {
            const before = runs; await open("One Input", query);
            assert.equal(runs, before); assert.equal(await page.locator("#viewer").isHidden(), true);
            assert.ok(await page.locator(".field-error,#status[role=alert]").count(), "Invalid links explain the input problem.");
        }
        await open("Single Choice Showcase", "?branch=2");
        assert.equal(await page.locator("#run").isDisabled(), true); assert.equal(await token(), null);
        await open("Default Showcase", "?branch=2&child=1-false");
        assert.equal(await token(), null); assert.equal(await page.locator("#run").isDisabled(), true);
        assert.match(await page.locator("#fields>.field-error").innerText(), /no longer valid/);
        await open("Default Showcase", "?branch=2&child=2-false"); await ready();
        const defaultInfo = await (await context.request.get("/api/executions/" + await token() + "/info")).json();
        assert.deepEqual(defaultInfo.input.values.child, ["2-false"]);
        assert.deepEqual(defaultInfo.input.values.blank, [""]); await close();
        await open("One Input", "?unknown=8");
        await page.locator("#run").click(); await ready();
        assert.equal((await copy()).searchParams.get("OrganizationID"), "21", "Manual review acknowledges link-only errors before sharing valid visible selections.");
        await close();

        // Actual browser clipboard round trips use edited values before rerunning.
        await open("Default Showcase", "?branch=2&flag=1&portal:noExecute=true");
        const precise = "2026-10-31T23:59:59.1234567+05:45";
        await page.getByLabel("Precise date", { exact: true }).fill(precise);
        await page.getByLabel("Multiple entries", { exact: true }).fill("First + entry\n日本語 & second");
        const copied = await copy();
        assert.equal(copied.pathname, url("Default Showcase"));
        assert.deepEqual(copied.searchParams.getAll("lines"), ["First + entry", "日本語 & second"]);
        assert.deepEqual(copied.searchParams.getAll("workstations"), ["3500", "4192"]);
        assert.equal(copied.searchParams.get("note:isnull"), "true");
        assert.equal(copied.searchParams.get("blank"), "");
        assert.equal(copied.searchParams.get("start"), precise);
        assert.equal(copied.searchParams.get("portal:noExecute"), "true");
        for (const key of ["internal", "internalMode", "token", "id", "__RequestVerificationToken"]) { assert.equal(copied.searchParams.has(key), false); }
        assert.match(copied.href, /Default%20Showcase/);
        await scan("copy link before execution");
        let release, entered;
        const gate = new Promise(resolve => { release = resolve; }), started = new Promise(resolve => { entered = resolve; });
        const delay = async route => { const response = await route.fetch(); entered(); await gate; await route.fulfill({ response }); };
        await page.getByLabel("Blank note", { exact: true }).fill("Draft & selection");
        await page.getByLabel("Blank note", { exact: true }).press("Tab"); await inputsReady();
        const branch = await openSingle("Branch");
        await page.route("**/parameters", delay);
        await branch.getByRole("option", { name: "Main library", exact: true }).click();
        await page.locator("#copy-report-link").click(); await started;
        assert.equal(await page.locator("#link-feedback").textContent(), "");
        release(); await page.getByText("Report link copied.", { exact: true }).waitFor();
        await page.unroute("**/parameters", delay);
        const draft = new URL(await page.evaluate(() => navigator.clipboard.readText()));
        assert.equal(draft.searchParams.get("blank"), "Draft & selection");
        assert.equal(draft.searchParams.get("branch"), "1");
        assert.equal(draft.searchParams.get("child"), "1-true", "Copy waits for the changed parent's dependent default.");
        await page.evaluate(() => { window.linkClipboardWrite = navigator.clipboard.writeText.bind(navigator.clipboard); navigator.clipboard.writeText = async () => { throw new Error("Clipboard permission denied."); }; });
        await page.locator("#copy-report-link").click(); await page.getByText("Clipboard permission denied.", { exact: true }).waitFor();
        await page.evaluate(() => { navigator.clipboard.writeText = window.linkClipboardWrite; });
        await page.getByLabel("Precise date", { exact: true }).fill("invalid date"); await page.getByLabel("Precise date", { exact: true }).press("Tab"); await inputsReady();
        await page.locator("#copy-report-link").click(); await page.getByText("Review the report inputs before copying a link.", { exact: true }).waitFor();
        assert.equal(await page.evaluate(() => navigator.clipboard.readText()), draft.href, "Invalid selections preserve the clipboard.");

        const freshContext = await context.browser().newContext({ baseURL, ignoreHTTPSErrors: true, storageState: await context.storageState() });
        try {
            const fresh = await freshContext.newPage(); let freshRuns = 0;
            fresh.on("request", request => { if (/\/run$/.test(request.url())) { freshRuns++; } });
            await fresh.goto(copied.href);
            await fresh.waitForFunction(() => document.querySelector("#parameters").getAttribute("aria-busy") === "false");
            assert.equal(freshRuns, 0); assert.equal(await fresh.getByLabel("Precise date", { exact: true }).inputValue(), precise);
            copied.searchParams.delete("portal:noExecute"); await fresh.goto(copied.href);
            await fresh.waitForFunction(() => Boolean(history.state?.ssrsReport?.token) && !document.querySelector("#run").disabled);
            assert.equal(freshRuns, 1); await fresh.locator("#cancel-session").click();
        } finally { await freshContext.close(); }

        // Repeated values can exceed Kestrel's ordinary 8 KiB request-line default.
        // Exercise a real copied link, navigation and server resolution at that size.
        const large = new URL(url("Default Showcase"), baseURL);
        for (let i = 0; i < 4000; i++) { large.searchParams.append("lines", "Synthetic entry " + i); }
        large.searchParams.set("portal:noExecute", "true");
        await page.goto(large.href); await inputsReady();
        const largeCopied = await copy();
        assert.ok(largeCopied.search.length > 8192);
        assert.equal(largeCopied.searchParams.getAll("lines").length, 4000);
        await page.goto(largeCopied.href); await inputsReady();
        assert.equal((await page.getByLabel("Multiple entries", { exact: true }).inputValue()).split("\n").length, 4000);

        const fail = route => route.fulfill({ status: 502, contentType: "application/json", json: { code: "timeout", message: "Synthetic execution failed." } });
        await page.route("**/run", fail); await open("One Input", "?OrganizationID=8");
        await page.getByText("Synthetic execution failed.", { exact: true }).waitFor();
        const afterFailure = runs; await page.reload(); await inputsReady(); assert.equal(runs, afterFailure);
        await page.unroute("**/run", fail); await page.locator("#run").click(); await ready();
        await page.getByLabel("OrganizationID", { exact: true }).fill("9"); await page.getByLabel("OrganizationID", { exact: true }).press("Tab"); await inputsReady();
        const active = await token(); const modified = await copy();
        assert.equal(modified.searchParams.get("OrganizationID"), "9");
        assert.equal(modified.searchParams.has("token"), false); assert.equal(await token(), active);
        await page.reload(); await inputsReady(); await ready(); assert.equal(await token(), active); assert.equal(runs, afterFailure + 1);
        assert.equal(new URL(page.url()).pathname, url("One Input"));
        assert.equal(new URL(page.url()).searchParams.has("token"), false);
        await page.goto("/"); await page.goBack(); await inputsReady(); await ready();
        assert.equal(await token(), active); assert.equal(runs, afterFailure + 1);
        await page.goForward(); await page.goBack(); await inputsReady(); await ready(); assert.equal(await token(), active);

        // Inline PDF opens in its own tab; PDF download remains an attachment.
        await page.locator("#next").click(); await page.waitForFunction(() => document.querySelector("#page-label").textContent === "Page 2 of 2");
        const beforePrint = await (await context.request.get("/api/executions/" + active + "/info")).json();
        const expectedPrint = await page.locator("#print-report").getAttribute("href");
        const pdfResponse = context.waitForEvent("response", { predicate: r => r.url() === baseURL + expectedPrint });
        const popupPromise = context.waitForEvent("page");
        await page.locator("#print-report").click();
        const popup = await popupPromise, response = await pdfResponse;
        assert.equal(response.status(), 200); assert.equal(response.headers()["content-type"], "application/pdf");
        assert.match(response.headers()["content-disposition"], /^inline;/);
        await popup.waitForURL(baseURL + expectedPrint); assert.equal(popup.url(), baseURL + expectedPrint);
        await popup.waitForLoadState("load");
        await popup.waitForTimeout(750); // The native PDF viewer paints after document load.
        await popup.screenshot({ path: "artifacts/browser/report-links-print.png" }); await popup.close();
        const attachment = await context.request.get("/api/executions/" + active + "/exports/pdf");
        assert.match(attachment.headers()["content-disposition"], /^attachment;/);
        assert.equal((await attachment.body()).subarray(0, 5).toString(), "%PDF-");
        assert.deepEqual(await (await context.request.get("/api/executions/" + active + "/info")).json(), beforePrint);
        assert.equal(await page.locator("#report-frame").getAttribute("sandbox"), "allow-same-origin");
        for (const width of [1440, 768, 390, 320]) {
            await page.setViewportSize({ width, height: 900 });
            const toolbar = await page.locator(".viewer-toolbar").boundingBox();
            await copy();
            assert.deepEqual(await page.locator(".viewer-toolbar").boundingBox(), toolbar, "Clipboard feedback never shifts the toolbar.");
            const feedback = await page.locator("#link-feedback").boundingBox();
            assert.ok(feedback.x >= 0 && feedback.x + feedback.width <= width && feedback.y >= toolbar.y + toolbar.height);
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
            await scan("report link and Print at " + width);
            await page.screenshot({ path: "artifacts/browser/report-links-" + width + ".png", fullPage: true });
        }
        const csrf = (await (await context.request.get("/api/csrf")).json()).token;
        await context.request.post("/api/executions/" + active + "/cancel", { data: {}, headers: { RequestVerificationToken: csrf } });
        const expiredRuns = runs; await page.reload(); await inputsReady();
        assert.equal(await token(), null); assert.equal(runs, expiredRuns); assert.equal(await page.locator("#print-report").getAttribute("href"), null);
        assert.equal(await page.getByLabel("OrganizationID", { exact: true }).inputValue(), "8");
        assert.equal(await page.locator("#run").isEnabled(), true);
        await open("Additional Report"); const plain = await copy();
        assert.equal(plain.pathname, url("Additional Report")); assert.equal(plain.search, "");
        await page.setViewportSize({ width: 1440, height: 900 });
        return { permanentReportLinks: true, automaticExecutionAndNoExecute: true, exactOnceAndRecovery: true,
            actualClipboard: true, draftAndCascadeCopy: true, freshContextLink: true, printNewTabInlinePdf: true, printDoesNotMutateExecution: true };
    } finally { page.off("request", count); }
};
