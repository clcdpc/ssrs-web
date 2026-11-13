"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const readline = require("node:readline");
const { chromium } = require("playwright");
const AxeBuilder = require("@axe-core/playwright").default;
const { checkHeader, checkLogo, waitForHeader } = require("./branding-visual-assertions.cjs");

(async () => {
    const mode = process.argv[2], valid = mode === "valid";
    const input = readline.createInterface({ input: process.stdin });
    const lines = input[Symbol.asyncIterator]();
    const browser = await chromium.launch({ headless: true, ...(process.env.PORTAL_BROWSER_EXECUTABLE ? { executablePath: process.env.PORTAL_BROWSER_EXECUTABLE } : {}) });
    try {
        console.log("BROWSER_READY");
        const baseURL = (await lines.next()).value;
        const context = await browser.newContext({ baseURL, ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 } });
        const page = await context.newPage(), failures = [], requests = [];
        page.on("pageerror", error => failures.push(error.message));
        page.on("request", request => requests.push(request.url()));
        const shot = async name => page.screenshot({ path: "artifacts/browser/branding-startup-" + name + ".png", fullPage: true });
        const scan = async name => {
            const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
            assert.deepEqual(result.violations.map(v => ({ id: v.id, nodes: v.nodes.length })), [], name);
        };
        await fs.mkdir("artifacts/browser", { recursive: true });
        await page.goto("/TestLogin");
        await page.getByLabel("Test identity").selectOption("staff-one");
        // Keep first-response timing separate from browser resource readiness.
        // Hold the cold CSS/logo so geometry cannot accidentally pass on a fast cache.
        let releaseHeader, headerRequested, blockedResources = 0;
        const headerGate = new Promise(resolve => { releaseHeader = resolve; });
        const pendingHeader = new Promise(resolve => { headerRequested = resolve; });
        const delayedHeader = async route => { blockedResources++; headerRequested(); await headerGate; await route.continue(); };
        await page.route("**/css/site.css*", delayedHeader);
        await page.route("**/branding/*/logo.png", delayedHeader);
        const response = page.waitForResponse(r => new URL(r.url()).pathname === "/" && r.request().isNavigationRequest());
        const requested = request => {
            if (new URL(request.url()).pathname === "/" && request.isNavigationRequest()) { console.log("INITIAL_PAGE_REQUESTED"); page.off("request", requested); }
        };
        page.on("request", requested);
        const started = Date.now();
        await page.getByRole("button", { name: "Sign in to demonstration" }).click();
        const first = await response, elapsed = Date.now() - started;
        assert.equal(first.status(), 200); assert.ok(elapsed < 8000, "First page remains bounded by the shared five-second deadline");
        const html = await first.text();
        const logo = html.match(/src="(\/branding\/([a-f0-9]{64})\/logo.png)"/);
        const css = html.match(/href="(\/branding\/([a-f0-9]{64})\/theme.css)"/);
        assert.equal(!!logo, valid, "The first HTTP page contains the expected logo without refreshing");
        assert.equal(!!css, valid);
        await pendingHeader;
        let rendered = false;
        const ready = waitForHeader(page).then(() => { rendered = true; });
        assert.notEqual(await page.evaluate(() => document.readyState), "complete", "Cold header resources still block document load.");
        assert.ok(blockedResources > 0, "Cold header resources are deliberately pending.");
        assert.equal(rendered, false, "Geometry waits for the cold stylesheet and logo.");
        releaseHeader(); await ready;
        await page.unroute("**/css/site.css*", delayedHeader);
        await page.unroute("**/branding/*/logo.png", delayedHeader);
        await checkHeader(page);
        if (valid) {
            assert.equal(logo[2], css[2]);
            assert.deepEqual(await (await context.request.get(logo[1])).body(), await fs.readFile("src/SsrsPortal.Mock/Branding/ssrs-wide-logo.png"));
            assert.match(await (await context.request.get(css[1])).text(), /--ssrs-header-background:#183A53;/);
            await checkLogo(page, true);
            const size = await page.locator(".brand-logo").boundingBox(); assert.equal(size.width, 440); assert.equal(size.height, 48);
        } else { assert.equal(await page.locator(".brand-fallback").isVisible(), true); }
        await scan("cold " + mode); await shot("cold-" + mode + "-1440");
        await page.reload(); await waitForHeader(page); await checkHeader(page); await scan("warm " + mode); await shot("warm-" + mode + "-1440");
        if (valid) {
            assert.equal(await page.locator(".brand-logo").getAttribute("src"), logo[1]);
            const catalog = await (await context.request.get("/api/catalog")).json();
            const report = catalog.find(item => item.name.startsWith("Check Ins by Day of Week by Hour with a very long"));
            await page.goto("/Report?id=" + report.id);
            await page.waitForFunction(() => document.querySelector("#parameters").getAttribute("aria-busy") === "false");
            await waitForHeader(page); await checkLogo(page, true); await scan("warm real workspace"); await shot("workspace-1440");
            await page.setViewportSize({ width: 390, height: 844 }); await waitForHeader(page); await checkHeader(page); await scan("warm real mobile"); await shot("workspace-390");
            console.log("EXPIRE_FOR_OUTAGE"); assert.equal((await lines.next()).value, "OUTAGE_READY");
            await page.setViewportSize({ width: 1440, height: 900 });
            await page.goto("/"); assert.equal(await page.locator(".brand-logo").getAttribute("src"), logo[1]);
            await waitForHeader(page); await checkLogo(page, true); await scan("last good during outage"); await shot("last-known-good-1440");
        } else {
            for (let i = 0; i < 3; i++) { await page.reload(); assert.equal(await page.locator(".brand-logo").count(), 0); }
        }
        assert.deepEqual(failures, []);
        assert.ok(requests.every(url => url.startsWith(baseURL + "/")), "No browser-direct SSRS or external requests");
        const header = await page.locator(".shell-header").boundingBox();
        const result = { startup: mode, firstPageMilliseconds: elapsed, coldLogo: !!logo, snapshotPinned: valid, headerHeight: header.height, delayedHeaderResources: blockedResources, axeScans: valid ? 5 : 2, result: "PASS" };
        await fs.writeFile("artifacts/browser/branding-startup-" + mode + ".json", JSON.stringify(result, null, 2));
        console.log(JSON.stringify(result));
    } finally { input.close(); await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
