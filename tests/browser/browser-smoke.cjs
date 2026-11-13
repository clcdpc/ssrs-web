"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const { chromium } = require("playwright");

(async () => {
    const executablePath = process.env.PORTAL_BROWSER_EXECUTABLE;
    // Full Chromium includes the native PDF viewer; the headless shell downloads PDFs.
    const browser = await chromium.launch({ headless: true, ...(executablePath ? { executablePath } : { channel: "chromium" }) });
    const baseURL = process.env.PORTAL_BASE_URL || "https://localhost:7443";
    const context = await browser.newContext({ baseURL, ignoreHTTPSErrors: true, acceptDownloads: true });
    const page = await context.newPage();
    const requests = [];
    const failures = [];
    const AxeBuilder = require("@axe-core/playwright").default;
    let axeScans = 0;
    const scan = async (name) => {
        axeScans++;
        // SSRS output deliberately forbids scripts. Scan the portal controls;
        // iframe isolation/title and sanitized report contents have separate checks.
        const result = await new AxeBuilder({ page }).exclude("#report-frame").withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
        assert.deepEqual(result.violations.map(v => ({ id: v.id, nodes: v.nodes.length })), [], name);
    };
    const inputsReady = () => page.waitForFunction(() => document.querySelector("#parameters").getAttribute("aria-busy") === "false");
    const { single, selectSingle } = require("./single-choice-helpers.cjs")(page, inputsReady);
    page.on("request", r => requests.push(r.url()));
    page.on("pageerror", e => failures.push(e.message));
    try {
        await page.goto("/TestLogin");
        await page.getByLabel("Test identity").selectOption("staff-one");
        await page.getByRole("button", { name: "Sign in to demonstration" }).click();
        await page.waitForURL(baseURL + "/");
        await fs.mkdir("artifacts/browser", { recursive: true });
        if (process.env.PORTAL_BROWSER_SUITE === "subscriptions") {
            const evidence = await require("./browser-subscriptions.cjs")({ page, context, browser, baseURL, scan, inputsReady });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "report-links") {
            const evidence = await require("./browser-report-links.cjs")({ page, context, baseURL, scan, inputsReady });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "breadcrumbs") {
            const evidence = await require("./browser-breadcrumbs.cjs")({ page, context, baseURL, scan, inputsReady });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "workspace") {
            const evidence = await require("./browser-workspace.cjs")({ page, context, baseURL, scan, inputsReady });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "execution-status") {
            const evidence = await require("./browser-execution-status.cjs")({ page, context, inputsReady, scan });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "report-layout") {
            const evidence = await require("./browser-report-layout.cjs")({ page, context, baseURL, inputsReady, scan, selectSingle });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "expansion") {
            const evidence = await require("./browser-expansion.cjs")({ page, context, baseURL, inputsReady, scan, selectSingle });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "dates") {
            const evidence = await require("./browser-dates.cjs")({ page, context, inputsReady, scan, selectSingle });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "single-select") {
            const evidence = await require("./browser-single-select.cjs")({ page, context, baseURL, scan, inputsReady, single, selectSingle });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "parameter-panel") {
            const evidence = await require("./browser-parameter-panel.cjs")({ page, context, baseURL, scan, inputsReady });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "workspace-layout") {
            const evidence = await require("./browser-workspace-layout.cjs")({ page, context, baseURL, scan, inputsReady });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "branding") {
            const evidence = await require("./browser-branding.cjs")({ page, context, baseURL, scan, inputsReady });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        if (process.env.PORTAL_BROWSER_SUITE === "multi-select") {
            const evidence = await require("./browser-multi-select.cjs")({ page, context, baseURL, scan, inputsReady });
            assert.deepEqual(failures, []);
            console.log(JSON.stringify({ result: "PASS", ...evidence, axeScans }, null, 2));
            return;
        }
        await page.setViewportSize({ width: 1440, height: 900 });
        assert.equal(await page.locator(".breadcrumbs [aria-current]").textContent(), "Training");
        assert.equal(await page.locator(".catalog-row").count(), 2, "The only root folder opens automatically.");
        await page.screenshot({ path: "artifacts/browser/catalog-default-desktop.png", fullPage: true });
        assert.equal(await page.locator(".intro,.card-grid,.catalog-card").count(), 0);
        const spacing = await page.evaluate(() => document.querySelector(".catalog-search").getBoundingClientRect().top - document.querySelector("header").getBoundingClientRect().bottom);
        assert.ok(spacing >= 0 && spacing <= 20, "Search sits immediately below the header.");
        assert.equal(await page.getByRole("link", { name: "All folders", exact: true }).count(), 0, "The sole top-level folder is the meaningful root.");
        await page.goto("/?root=true");
        assert.equal(await page.locator(".breadcrumbs [aria-current]").textContent(), "All folders");
        const rootFolder = page.getByRole("link", { name: "Folder: Training", exact: true });
        await rootFolder.focus();
        await page.keyboard.press("Tab");
        await page.keyboard.press("Shift+Tab");
        assert.equal(await rootFolder.evaluate(n => getComputedStyle(n).outlineStyle), "solid");
        await page.keyboard.press("Enter");
        await page.getByRole("link", { name: "Folder: Reports", exact: true }).click();
        assert.equal(await page.locator(".breadcrumbs [aria-current]").textContent(), "Reports");
        const rowHeights = await page.locator(".catalog-row").evaluateAll(nodes => nodes.map(n => n.getBoundingClientRect().height));
        assert.ok(rowHeights.every(height => height >= 36 && height <= 44));
        await scan("desktop catalog");
        await page.screenshot({ path: "artifacts/browser/catalog-desktop.png", fullPage: true });
        await page.setViewportSize({ width: 390, height: 844 });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true);
        await scan("mobile catalog");
        await page.screenshot({ path: "artifacts/browser/catalog-mobile.png", fullPage: true });
        // Search from Reports must also find the linked report in Shortcuts.
        await page.getByRole("searchbox", { name: "Search available reports" }).fill("Holds Link");
        await page.getByRole("button", { name: "Search", exact: true }).click();
        assert.equal(await page.getByRole("link", { name: "Linked report: Holds Link", exact: true }).count(), 1);
        for (const hidden of ["Hidden", "System"]) {
            await page.getByRole("searchbox", { name: "Search available reports" }).fill(hidden);
            await page.getByRole("button", { name: "Search", exact: true }).click();
            assert.equal(await page.locator(".catalog-row").count(), 0);
        }
        await page.getByRole("link", { name: "Clear search", exact: true }).click();
        assert.equal(await page.locator(".breadcrumbs [aria-current]").textContent(), "Training");
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.getByRole("link", { name: "Folder: Reports", exact: true }).click();
        await page.getByRole("link", { name: "Report: Circulation By Workstation", exact: true }).click();
        await page.waitForFunction(() => document.querySelector("#parameters").getAttribute("aria-busy") === "false");
        const multi = prompt => page.locator(".multi-select").filter({ has: page.locator("label[id]").filter({ hasText: new RegExp("^" + prompt + "$") }) });
        assert.equal(await single("Library").locator(".single-trigger span").first().textContent(), "Synthetic library 153", "The off-page label is displayed in the collapsed control.");
        assert.equal(await single("Library").locator("button.single-trigger[aria-haspopup=listbox]").count(), 1);
        assert.equal(await single("Library").locator("input[type=radio],.chip,.selection").count(), 0);
        const workstations = multi("Workstations");
        await page.evaluate(() => {
            window.choiceLongTasks = [];
            window.choiceObserver = new PerformanceObserver(list => window.choiceLongTasks.push(...list.getEntries().map(e => e.duration)));
            window.choiceObserver.observe({ type: "longtask" });
            window.choiceOpenStart = performance.now();
        });
        await workstations.locator(".multi-trigger").click();
        await workstations.locator(".checkbox-choices input[data-option]").last().waitFor();
        const choicePerformance = await page.evaluate(() => {
            window.choiceObserver.disconnect();
            return { choices: 4192, openMilliseconds: Math.round(performance.now() - window.choiceOpenStart), longestTaskMilliseconds: Math.round(Math.max(0, ...window.choiceLongTasks)) };
        });
        await fs.writeFile("artifacts/browser/choice-performance.json", JSON.stringify(choicePerformance, null, 2));
        assert.ok(choicePerformance.openMilliseconds < 5000 && choicePerformance.longestTaskMilliseconds < 2000, "Complete-list performance: " + JSON.stringify(choicePerformance));
        assert.equal(await workstations.locator(".checkbox-choices input[data-option]").count(), 4192);
        const search = page.getByRole("searchbox", { name: "Search Workstations", exact: true });
        await search.fill("4192");
        assert.equal(await workstations.locator(".checkbox-choice:visible").count(), 2);
        const lastChoice = workstations.getByRole("checkbox", { name: "Synthetic workstation 4192", exact: true });
        await lastChoice.focus();
        await page.keyboard.press("Space");
        assert.equal(await lastChoice.isChecked(), true);
        await page.keyboard.press("Escape");
        await inputsReady();
        assert.equal(await workstations.locator(".multi-trigger").textContent(), "Synthetic workstation 4192⌄");
        assert.equal(await workstations.locator(".multi-trigger").getAttribute("title"), "Synthetic workstation 4192");
        await workstations.locator(".multi-trigger").click();
        await workstations.locator(".checkbox-choices input").first().focus();
        await page.keyboard.press("End");
        assert.equal(await lastChoice.evaluate(n => n === document.activeElement), true, "Keyboard End reaches choice 4192 through the complete list.");
        assert.equal(await lastChoice.isChecked(), true);
        await page.keyboard.press("Escape");
        assert.equal(await workstations.locator(".multi-trigger").getAttribute("title"), "Synthetic workstation 4192", "Scrolling preserves the selection and label.");
        await page.getByRole("button", { name: "View report", exact: true }).click();
        await page.waitForFunction(() => Boolean(history.state?.ssrsReport?.token) && document.querySelector("#run").dataset.executing === "false");
        await page.waitForFunction(() => document.querySelector("#page-label").textContent === "Page 1 of 2");
        const frame = page.frameLocator("#report-frame");
        await frame.getByRole("heading", { name: "Page 1", exact: true }).waitFor();
        assert.equal(await frame.locator("img").evaluate(img => img.complete && img.naturalWidth > 0), true, "Authenticated chart must load.");
        assert.equal(await page.locator("body").getAttribute("data-pwned"), null);
        assert.equal(await frame.locator("script,iframe,form").count(), 0);
        assert.equal(await frame.getByText("Expand row").getAttribute("aria-disabled"), "true");
        assert.equal(await frame.locator("link[rel=stylesheet]").evaluate(link => Boolean(link.sheet)), true, "Authenticated CSS must load.");
        await page.getByRole("button", { name: "Next", exact: true }).focus();
        await page.keyboard.press("Enter");
        await page.waitForFunction(() => document.querySelector("#page-label").textContent === "Page 2 of 2");
        await frame.getByRole("heading", { name: "Page 2", exact: true }).waitFor();
        assert.equal(await page.getByRole("button", { name: "Next", exact: true }).isDisabled(), true);
        for (const name of ["PDF", "Excel", "Word", "CSV"]) {
            const download = page.waitForEvent("download");
            await page.getByRole("link", { name, exact: true }).click();
            const result = await download;
            assert.equal(await result.failure(), null);
            assert.match(result.suggestedFilename(), /\.(pdf|xlsx|docx|csv)$/);
            // Only synthetic browser downloads live in Playwright's temporary directory.
        }
        await page.setViewportSize({ width: 390, height: 844 });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true, "Portal shell must fit a mobile viewport.");
        const secondContext = await browser.newContext({ baseURL, ignoreHTTPSErrors: true });
        const second = await secondContext.newPage();
        await second.goto("/TestLogin");
        await second.getByLabel("Test identity").selectOption("staff-two");
        await second.getByRole("button", { name: "Sign in to demonstration" }).click();
        await second.waitForURL(baseURL + "/");
        const token = await page.evaluate(() => history.state?.ssrsReport?.token ?? null);
        const cross = await secondContext.request.get("/api/executions/" + token + "/viewer");
        assert.equal(cross.status(), 404);
        await secondContext.close();
        await page.getByRole("button", { name: "Close" }).click();
        await page.waitForURL(/\/\?folder=/);
        const expired = await context.request.get("/api/executions/" + token + "/viewer");
        assert.equal(expired.status(), 404);
        // Date/Boolean/cascade/nullable controls in the same browser.
        await page.getByRole("searchbox", { name: "Search available reports" }).fill("Holds Ratio");
        await page.getByRole("button", { name: "Search", exact: true }).click();
        await page.getByRole("link", { name: "Report: Holds Ratio", exact: true }).click();
        await page.waitForFunction(() => document.querySelector("#parameters").getAttribute("aria-busy") === "false");
        assert.equal(await page.getByRole("button", { name: "View report", exact: true }).isDisabled(), true, "A Boolean with no SSRS default stays unselected.");
        await page.getByLabel("Apply collection filter", { exact: true }).selectOption("false");
        await page.waitForFunction(() => !document.querySelector("#run").disabled);
        assert.equal(await page.getByLabel("Apply collection filter", { exact: true }).inputValue(), "false");
        await single("Library").locator(".single-trigger").click();
        await single("Library").getByRole("option").last().waitFor();
        assert.equal(await single("Library").getByRole("option").count(), 20, "All nineteen choices and the explicit empty selection are present.");
        await page.keyboard.press("Escape");
        assert.equal(await page.getByLabel("Optional note", { exact: true }).isDisabled(), true);
        assert.equal(await page.getByLabel("Start date", { exact: true }).getAttribute("type"), "datetime-local");
        assert.equal(await page.getByText("Internal", { exact: true }).count(), 0);
        // Every visible input has a programmatic label; each report input has help/error associations.
        const unlabeled = await page.locator("input:not([type=hidden]),select,textarea").evaluateAll(nodes => nodes.filter(n => !(n.labels?.length || n.getAttribute("aria-label"))).map(n => n.id));
        assert.deepEqual(unlabeled, []);
        assert.equal(await page.locator("[aria-describedby]").evaluateAll(nodes => nodes.every(n => n.getAttribute("aria-describedby").split(" ").every(id => document.getElementById(id)))), true);
        await page.getByLabel("Apply collection filter", { exact: true }).focus();
        assert.equal(await page.getByLabel("Apply collection filter", { exact: true }).evaluate(n => getComputedStyle(n).outlineStyle), "solid");
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true);
        await multi("Collections").locator(".multi-trigger").click();
        await multi("Collections").getByRole("checkbox", { name: "(Select All)", exact: true }).check();
        await multi("Collections").getByRole("checkbox", { name: "(Select All)", exact: true }).uncheck();
        await page.keyboard.press("Escape");
        await inputsReady();
        assert.equal(await page.getByRole("button", { name: "View report", exact: true }).isDisabled(), true, "Cleared query defaults stay cleared.");
        await page.getByLabel("Apply collection filter", { exact: true }).selectOption("true");
        await inputsReady();
        assert.equal(await page.getByRole("button", { name: "View report", exact: true }).isEnabled(), true, "A parent edit initializes its empty dependent default.");
        const noteGroup = page.getByLabel("Optional note", { exact: true }).locator("..");
        await noteGroup.getByRole("checkbox").uncheck();
        await inputsReady();
        await page.getByLabel("Optional note", { exact: true }).fill("Keep my note");
        await page.getByLabel("Optional note", { exact: true }).press("Tab");
        await inputsReady();
        await selectSingle("Library", "Synthetic library 2");
        assert.equal(await page.getByLabel("Optional note", { exact: true }).inputValue(), "Keep my note");
        await multi("Collections").locator(".multi-trigger").click();
        assert.equal(await multi("Collections").getByRole("checkbox", { name: "Synthetic collection 1", exact: true }).isChecked(), true);
        await page.keyboard.press("Escape");
        let axe = "not installed; label/focus/keyboard/mobile checks executed";
        try {
            const AxeBuilder = require("@axe-core/playwright").default;
            const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
            assert.deepEqual(result.violations.map(v => ({ id: v.id, nodes: v.nodes.length })), []);
            axeScans++;
            axe = "WCAG 2 A/AA and 2.1 AA scan passed (parameter page)";
        } catch (error) {
            if (error.code !== "MODULE_NOT_FOUND") {
                throw error;
            }
        }
        assert.deepEqual(failures, []);
        assert.equal(requests.some(url => new URL(url).origin !== new URL(baseURL).origin), false, "Browser must not contact SSRS or other origins.");
        await fs.mkdir("artifacts/browser", { recursive: true });
        await page.screenshot({ path: "artifacts/browser/parameters-mobile.png", fullPage: true });
        await page.goto("/?q=Default%20Showcase");
        await page.getByRole("link", { name: "Report: Default Showcase", exact: true }).click();
        await inputsReady();
        assert.equal(await page.getByRole("button", { name: "View report", exact: true }).isEnabled(), true, "SSRS defaults automatically make the report runnable.");
        assert.equal(await page.getByLabel("Include inactive", { exact: true }).inputValue(), "false");
        assert.equal(await page.getByLabel("Precise date", { exact: true }).inputValue(), "2026-10-30T09:15:23.1234567");
        assert.equal(await page.getByLabel("Zoned date", { exact: true }).inputValue(), "2026-10-30T09:15:23.123-04:00");
        assert.equal(await page.getByLabel("Multiple entries", { exact: true }).inputValue(), "First entry\nSecond entry");
        assert.equal(await page.getByLabel("Blank note", { exact: true }).inputValue(), "");
        assert.equal(await page.getByLabel("Nullable note", { exact: true }).isDisabled(), true);
        assert.equal(await multi("Default workstations").locator(".multi-trigger").textContent(), "Synthetic workstation 3500, Synthetic workstation 4192⌄");
        assert.equal(await multi("Default workstations").locator(".multi-trigger").getAttribute("title"), "Synthetic workstation 3500, Synthetic workstation 4192");
        assert.equal(await multi("Default workstations").locator(".checkbox-popup").isHidden(), true);
        assert.equal(await multi("Default workstations").locator(".checkbox-choices input").count(), 0, "Closed multiselects load no choice DOM.");
        await scan("default parameters on mobile");
        await page.screenshot({ path: "artifacts/browser/defaults-mobile.png", fullPage: true });
        await page.setViewportSize({ width: 1440, height: 900 });
        await scan("default parameters on desktop");
        await page.screenshot({ path: "artifacts/browser/defaults-desktop.png", fullPage: true });
        assert.equal(await page.getByLabel("Branch note", { exact: true }).inputValue(), "Branch 1");
        // Two synchronous edits deterministically cancel the first refresh before it
        // can return. The replacement request must carry both pending edits.
        await single("Branch").locator(".single-trigger").click();
        await single("Branch").getByRole("option", { name: "Other library", exact: true }).waitFor();
        const branchOptionId = await single("Branch").getByRole("option", { name: "Other library", exact: true }).getAttribute("id");
        const blankId = await page.getByLabel("Blank note", { exact: true }).getAttribute("id");
        const rapidResponse = page.waitForResponse(r => /\/parameters$/.test(r.url()) &&
            r.request().postDataJSON().changed.includes("branch") && r.request().postDataJSON().changed.includes("blank"));
        await page.evaluate(({ branchOptionId, blankId }) => {
            document.getElementById(branchOptionId).click();
            const blank = document.getElementById(blankId);
            blank.value = "Quick edit";
            blank.dispatchEvent(new Event("change", { bubbles: true }));
        }, { branchOptionId, blankId });
        await rapidResponse;
        await inputsReady();
        assert.equal(await page.getByLabel("Branch note", { exact: true }).inputValue(), "Branch 2", "A dependent default follows its parent even without a finite option list.");
        assert.equal(await page.getByLabel("Blank note", { exact: true }).inputValue(), "Quick edit");
        await page.getByLabel("Blank note", { exact: true }).fill("");
        await page.getByLabel("Blank note", { exact: true }).press("Tab");
        await inputsReady();
        await page.getByLabel("Branch note", { exact: true }).fill("Keep my branch note");
        await page.getByLabel("Branch note", { exact: true }).press("Tab");
        await inputsReady();
        await selectSingle("Branch", "Main library");
        assert.equal(await page.getByLabel("Branch note", { exact: true }).inputValue(), "Keep my branch note", "A valid manual child value survives parent edits.");
        const runRequest = page.waitForRequest(r => /\/api\/reports\/[^/]+\/run$/.test(r.url()));
        await page.getByRole("button", { name: "View report", exact: true }).click();
        const submitted = (await runRequest).postDataJSON().values;
        assert.deepEqual(submitted.workstations, ["3500", "4192"]);
        assert.deepEqual(submitted.lines, ["First entry", "Second entry"]);
        assert.deepEqual(submitted.start, ["2026-10-30T09:15:23.1234567"]);
        assert.deepEqual(submitted.zoned, ["2026-10-30T09:15:23.123-04:00"]);
        assert.deepEqual(submitted.note, [null]);
        assert.deepEqual(submitted.blank, [""]);
        assert.deepEqual(submitted.branchNote, ["Keep my branch note"]);
        await page.waitForFunction(() => Boolean(history.state?.ssrsReport?.token) && document.querySelector("#run").dataset.executing === "false");
        await page.getByRole("button", { name: "Close" }).click();
        await page.waitForURL(/\/\?folder=/);
        const dateEvidence = await require("./browser-dates.cjs")({ page, context, inputsReady, scan, selectSingle });
        const inputEvidence = await require("./browser-inputs.cjs")({ page, context, baseURL, scan, inputsReady, multi, single, selectSingle });
        const multiEvidence = await require("./browser-multi-select.cjs")({ page, context, baseURL, scan, inputsReady });
        const singleEvidence = await require("./browser-single-select.cjs")({ page, context, baseURL, scan, inputsReady, single, selectSingle });
        const sizingEvidence = await require("./browser-sizing.cjs")({ page, context, scan, inputsReady });
        const workspaceEvidence = await require("./browser-workspace.cjs")({ page, context, baseURL, scan, inputsReady });
        const layoutEvidence = await require("./browser-workspace-layout.cjs")({ page, context, baseURL, scan, inputsReady });
        await page.goto("/?q=Holds%20Ratio");
        await page.getByRole("link", { name: "Report: Holds Ratio", exact: true }).click();
        await inputsReady();
        await page.setViewportSize({ width: 390, height: 844 });
        // Simulate an eligible diagnostic API response to verify the UI's text-only
        // rendering. Server-side eligibility is separately covered by HTTP tests.
        const diagnostic = "Operation: GetItemParameters\nFault Message: <script>document.body.dataset.pwned='yes'</script>\nReference: browser-fixture";
        const longError = "The report choices could not be loaded because the reporting service did not complete the parameter query. Try the request again after checking the earlier inputs. If this continues, contact support with the reference below so they can inspect the authorized diagnostic details and the reporting service logs.";
        await page.route("**/api/reports/*/parameters", route => route.fulfill({
            status: 502, contentType: "application/json",
            body: JSON.stringify({ code: "service", message: longError, trace: "browser-fixture", diagnostics: diagnostic })
        }));
        await page.reload();
        const details = page.getByLabel("Diagnostic details", { exact: true });
        await details.waitFor();
        assert.equal(await details.textContent(), diagnostic);
        assert.equal(await details.locator("script").count(), 0);
        assert.equal(await page.locator("#status").textContent(), longError + " Reference: browser-fixture");
        assert.equal(await page.locator("#status").getAttribute("aria-live"), "assertive");
        assert.equal(await page.locator(".workspace-feedback #diagnostic-details").count(), 0, "Long diagnostics sit below the navigation row.");
        assert.equal(await page.locator("body").getAttribute("data-pwned"), null);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), true);
        axeScans++;
        const diagnosticScan = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
        assert.deepEqual(diagnosticScan.violations.map(v => ({ id: v.id, nodes: v.nodes.length })), []);
        await page.screenshot({ path: "artifacts/browser/diagnostics-mobile.png", fullPage: true });
        await page.setViewportSize({ width: 1440, height: 900 });
        await scan("diagnostic report desktop");
        await page.screenshot({ path: "artifacts/browser/diagnostics-desktop.png", fullPage: true });
        await page.unroute("**/api/reports/*/parameters");
        const breadcrumbEvidence = await require("./browser-breadcrumbs.cjs")({ page, context, baseURL, scan, inputsReady });
        const expansionEvidence = await require("./browser-expansion.cjs")({ page, context, baseURL, inputsReady, scan, selectSingle });
        const reportLayoutEvidence = await require("./browser-report-layout.cjs")({ page, context, baseURL, inputsReady, scan, selectSingle });
        const linksEvidence = await require("./browser-report-links.cjs")({ page, context, baseURL, scan, inputsReady });
        const brandingEvidence = await require("./browser-branding.cjs")({ page, context, baseURL, scan, inputsReady });
        assert.deepEqual(failures, []);
        console.log(JSON.stringify({ result: "PASS", ...dateEvidence, ...inputEvidence, ...multiEvidence, ...singleEvidence, ...sizingEvidence, ...workspaceEvidence, ...layoutEvidence, ...brandingEvidence, ...breadcrumbEvidence, ...expansionEvidence, ...reportLayoutEvidence, ...linksEvidence, axeScans, compactCatalog: true, automaticFolder: true, explicitRoot: true, globalSearch: true, defaultSelections: true, clearedDefaults: true, cascades: true, selectedLabels: true, displayedExecutionValues: true, catalogAndDefaultsAxe: true, keyboard4192: true, completeChoiceDOM: true, choicePerformance, pages: 2, resources: 2, exports: 4, crossUserDenied: true, cancelledSessionDenied: true, mobile: true, reportScriptExecution: false, diagnosticTextOnly: true, diagnosticAxe: true, remoteRequests: 0, axe }, null, 2));
    } catch (error) {
        await page.screenshot({ path: "artifacts/browser/failure.png", fullPage: true });
        throw error;
    } finally {
        await context.close();
        await browser.close();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
