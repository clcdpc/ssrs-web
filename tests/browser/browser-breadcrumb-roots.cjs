"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const { chromium } = require("playwright");
const AxeBuilder = require("@axe-core/playwright").default;
const { copyTrail, presentation } = require("./breadcrumb-assertions.cjs");
const { checkHeader } = require("./branding-visual-assertions.cjs");

(async () => {
    const { baseURL, reportId, fixtures } = JSON.parse(await fs.readFile(process.argv[2], "utf8"));
    const browser = await chromium.launch({ headless: true, ...(process.env.PORTAL_BROWSER_EXECUTABLE ? { executablePath: process.env.PORTAL_BROWSER_EXECUTABLE } : {}) });
    const context = await browser.newContext({ baseURL, ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 } });
    const page = await context.newPage(), failures = [], copies = [];
    page.on("pageerror", error => failures.push(error.message));
    let scans = 0;
    const scan = async name => {
        const result = await new AxeBuilder({ page }).exclude("#report-frame").withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
        assert.deepEqual(result.violations.map(v => ({ id: v.id, nodes: v.nodes.length })), [], name); scans++;
    };
    const settle = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const labels = () => page.locator(".breadcrumbs>.breadcrumb-list>li>a,.breadcrumbs>.breadcrumb-list>li>[aria-current]").allTextContents();
    const copy = async () => copies.push(await copyTrail(page, context, baseURL));
    try {
        await page.goto("/TestLogin"); await page.getByRole("button", { name: "Sign in to demonstration", exact: true }).click(); await page.waitForURL(baseURL + "/"); await settle();
        assert.deepEqual(await labels(), ["All folders"]); assert.equal(await page.locator(".catalog-row").count(), 2); await copy();
        await page.getByRole("link", { name: "Folder: Training", exact: true }).click(); await settle();
        assert.deepEqual(await labels(), ["All folders", "Training"]); await copy();
        await page.getByRole("link", { name: "Folder: Reports", exact: true }).click();
        await page.getByRole("link", { name: "Report: One Input", exact: true }).click(); await settle();
        assert.deepEqual(await labels(), ["All folders", "Training", "Reports", "One Input"]); await copy(); await scan("actual multiple-root report");
        await page.locator(".breadcrumbs a").first().click(); await settle(); assert.deepEqual(await labels(), ["All folders"]);
        await page.getByRole("searchbox", { name: "Search available reports", exact: true }).fill("Other report");
        await page.getByRole("button", { name: "Search", exact: true }).click(); await settle();
        assert.deepEqual(await labels(), ["All folders", "Search results"]); await copy(); await scan("actual multiple-root search");
        await page.getByRole("link", { name: "Linked report: Other report", exact: true }).click(); await settle();
        assert.deepEqual(await labels(), ["All folders", "Other", "Other report"]);
        await page.goBack(); await page.getByRole("link", { name: "Clear search", exact: true }).click(); await settle();
        assert.deepEqual(await labels(), ["All folders"]);
        // Reuse real Razor partials for valid root arrangements outside today's
        // folder-only AllowedRoots policy. No production policy or endpoint changes.
        let fixture;
        const pattern = "**/Report?id=" + reportId;
        await page.route(pattern, async route => {
            const response = await route.fetch();
            await route.fulfill({ response, body: (await response.text()).replace(/<nav class="breadcrumbs"[\s\S]*?<\/nav>/, fixture.html) });
        });
        for (fixture of fixtures) {
            for (const width of [1440, 320]) {
                await page.setViewportSize({ width, height: 900 }); await page.goto("/Report?id=" + reportId); await settle();
                assert.deepEqual(await labels(), fixture.labels, fixture.name);
                assert.equal((await presentation(page)).size, "15px"); await checkHeader(page); await copy(); await scan(fixture.name + " " + width);
            }
        }
        assert.deepEqual(failures, []);
        await fs.writeFile("artifacts/browser/breadcrumb-root-verification.json", JSON.stringify({ realMultiRoot: true, razorEdgeFixtures: fixtures.length, copies, axeScans: scans }, null, 2));
        console.log(JSON.stringify({ breadcrumbRootResult: "PASS", realMultiRoot: true, razorEdgeFixtures: fixtures.length, clipboardCopies: copies.length, axeScans: scans }));
    } finally { await context.close(); await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
