"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const { copyTrail, presentation, typography } = require("./breadcrumb-assertions.cjs");
const { checkHeader } = require("./branding-visual-assertions.cjs");

module.exports = async ({ page, context, baseURL, scan, inputsReady }) => {
    const catalog = await (await context.request.get("/api/catalog")).json();
    const folder = catalog.find(e => e.name === "Reports"), report = catalog.find(e => e.name === "One Input");
    const deep = catalog.find(e => e.name.startsWith("Check Ins by Day of Week by Hour with a very long"));
    const root = catalog.find(e => e.parentId === null);
    const settle = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const heights = { 1440: 900, 1024: 768, 768: 1024, 390: 844, 320: 640, 1920: 1080 };
    const viewport = async width => { await page.setViewportSize({ width, height: heights[width] }); await settle(); };
    const copies = [], layouts = [];
    const copy = async () => { copies.push(await copyTrail(page, context, baseURL)); };
    const labels = () => page.locator(".breadcrumbs>.breadcrumb-list>li>a,.breadcrumbs>.breadcrumb-list>li>[aria-current]").allTextContents();
    const openReport = async id => { await page.goto("/Report?id=" + id); await inputsReady(); await settle(); };
    await viewport(1440); await page.goto("/"); await settle();
    assert.deepEqual(await labels(), [root.name]); await presentation(page); await copy();
    assert.equal(await page.getByRole("link", { name: "All folders", exact: true }).count(), 0);
    await page.getByRole("link", { name: "Folder: Reports", exact: true }).click(); await settle();
    assert.deepEqual(await labels(), [root.name, folder.name]);
    const catalogStyle = await presentation(page); assert.equal(catalogStyle.size, "15px"); await copy();
    assert.equal(await page.locator("main .catalog-search").count(), 1);
    await page.getByRole("link", { name: "Report: One Input", exact: true }).click(); await inputsReady(); await settle();
    assert.deepEqual(await labels(), [root.name, folder.name, report.name]);
    assert.deepEqual(await presentation(page), catalogStyle, "Report and catalog share typography and separator styling.");
    await copy(); await scan("shared header report");
    for (const url of ["/?folder=" + folder.id, "/Report?id=" + report.id]) {
        for (const width of [1440, 1024, 768, 390, 320]) {
            await viewport(width); await page.goto(url);
            if (url.startsWith("/Report")) { await inputsReady(); }
            await settle(); await presentation(page); await checkHeader(page);
        }
    }
    await page.locator(".breadcrumbs .breadcrumb-folder a").last().click(); await settle();
    assert.equal(new URL(page.url()).searchParams.get("folder"), report.parentId);
    assert.deepEqual(await labels(), [root.name, folder.name]);
    await page.goBack(); await inputsReady(); await settle(); assert.deepEqual(await labels(), [root.name, folder.name, report.name]);
    await page.goForward(); await settle(); assert.deepEqual(await labels(), [root.name, folder.name]);
    // Global search has no subfolder ancestry, including an explicit folder query.
    await page.goto("/?folder=" + folder.id + "&q=Holds%20Link"); await settle();
    assert.deepEqual(await labels(), [root.name, "Search results"]); await copy();
    await page.getByRole("link", { name: "Linked report: Holds Link", exact: true }).click(); await inputsReady();
    assert.equal(await page.locator(".breadcrumbs [aria-current]").textContent(), "Holds Link");
    await page.goBack(); await page.getByRole("link", { name: "Clear search", exact: true }).click(); await settle();
    assert.deepEqual(await labels(), [root.name]);
    await page.goto("/?root=true"); await settle(); assert.deepEqual(await labels(), ["All folders"]); await copy();
    await page.getByRole("link", { name: "Folder: Training", exact: true }).click(); await settle();
    assert.deepEqual(await labels(), [root.name]);

    // Real authorized deep ancestry, with all widths measured at the new font size.
    await openReport(deep.id);
    const all = [];
    for (let item = catalog.find(e => e.id === deep.parentId); item; item = catalog.find(e => e.id === item.parentId)) { all.unshift(item); }
    for (const width of [1440, 1024, 768, 390, 320]) {
        await viewport(width); await checkHeader(page); await presentation(page); await copy();
        const state = await page.locator("header").evaluate(header => {
            const nav = header.querySelector(".breadcrumbs"), list = nav.querySelector(".breadcrumb-list");
            const links = [...nav.querySelectorAll(".breadcrumb-folder a")];
            return { width: innerWidth, headerHeight: header.getBoundingClientRect().height,
                fits: list.scrollWidth <= nav.clientWidth + 1, links: links.map(a => [a.textContent, a.getAttribute("href")]),
                current: nav.querySelector("[aria-current]").textContent };
        });
        assert.equal(state.fits, true, JSON.stringify(state));
        assert.ok(state.headerHeight <= (width > 1200 ? 64 : 100), "The header stays compact.");
        assert.deepEqual(state.links.map(a => a[0]).sort(), all.map(e => e.name).sort());
        assert.equal(state.current, deep.name);
        assert.equal(await page.locator(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder a").first().textContent(), root.name);
        assert.equal(await page.locator(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder a").last().textContent(), all.at(-1).name);
        assert.equal(await page.locator(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder a").evaluateAll(nodes => nodes.every(n => n.getBoundingClientRect().width >= 24)), true, "Ancestor labels retain usable space beside literal separators.");
        assert.equal(await page.getByRole("button", { name: "Sign out", exact: true }).isVisible(), true);
        await scan("shared breadcrumbs " + width); layouts.push(state);
        await page.screenshot({ path: "artifacts/browser/breadcrumb-" + width + ".png", fullPage: true });
    }
    // A wide system font must not consume the ancestor's usable label width.
    // Monospace spaces also reproduce the wider Linux separator metrics locally.
    await page.evaluate(() => { document.documentElement.style.fontFamily = "monospace"; }); await settle();
    const wideLabels = await page.locator(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder a").evaluateAll(nodes => nodes.map(n => ({ name: n.textContent, width: n.getBoundingClientRect().width })));
    assert.equal(wideLabels.every(n => n.width >= 24), true, "Wide-font ancestor labels: " + JSON.stringify(wideLabels));
    await checkHeader(page); await copy(); await scan("breadcrumbs with wide system font");
    await page.evaluate(() => { document.documentElement.style.fontFamily = ""; }); await settle();
    const summary = page.locator(".breadcrumbs summary"), menu = page.locator(".breadcrumbs details>ol");
    assert.equal(await summary.isVisible(), true); await summary.focus(); await summary.press("ArrowDown");
    const hidden = all.slice(1, -1);
    assert.deepEqual(await menu.locator("a").allTextContents(), hidden.map(e => e.name));
    assert.deepEqual(await menu.locator("a").evaluateAll(nodes => nodes.map(n => n.getAttribute("href"))), hidden.map(e => "/?folder=" + e.id));
    assert.equal(await menu.locator("a").first().evaluate(n => n === document.activeElement), true);
    assert.equal(await menu.locator("a").first().evaluate(n => getComputedStyle(n).outlineStyle), "solid");
    await typography(page); await scan("breadcrumb menu at 15px semibold");
    await page.keyboard.press("End"); assert.equal(await menu.locator("a").last().evaluate(n => n === document.activeElement), true);
    await page.keyboard.press("Home"); await page.keyboard.press("ArrowDown");
    assert.equal(await menu.locator("a").nth(1).evaluate(n => n === document.activeElement), true);
    // Give every capped label room, independently of platform font metrics.
    // The required 1440px case above may legitimately retain the ellipsis.
    await viewport(1920); assert.equal(await summary.isHidden(), true);
    assert.deepEqual(await page.locator(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder a").allTextContents(), all.map(e => e.name));
    const measurements = await page.evaluate(() => {
        const measure = document.querySelector(".breadcrumb-measure");
        return [...document.querySelectorAll(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder>a,.breadcrumbs .breadcrumb-current>span:not(.breadcrumb-separator)")].map(n => {
            const clone = [...measure.querySelectorAll(".breadcrumb-folder>a,.breadcrumb-current>span:not(.breadcrumb-separator)")].find(m => m.textContent === n.textContent);
            return { text: n.textContent, actual: n.getBoundingClientRect().width, measured: clone.getBoundingClientRect().width };
        });
    });
    assert.ok(measurements.every(m => Math.abs(m.actual - m.measured) <= 1), "Unshrunk labels match the adaptive measuring copy: " + JSON.stringify(measurements));
    assert.equal(await page.locator(".breadcrumbs a").filter({ hasText: hidden[1].name }).evaluate(n => n === document.activeElement), true);
    await checkHeader(page); await copy();
    await viewport(320); assert.equal(await summary.evaluate(n => n === document.activeElement), true);
    await summary.press("Space"); await page.keyboard.press("Escape"); assert.equal(await summary.evaluate(n => n === document.activeElement), true);
    await copy();
    await summary.press("ArrowDown"); await menu.locator("a").first().press("Enter"); await settle();
    assert.equal(new URL(page.url()).searchParams.get("folder"), hidden[0].id);
    assert.equal(await page.locator(".breadcrumbs [aria-current]").textContent(), hidden[0].name);
    await copy();

    await openReport(deep.id); await viewport(320);
    await summary.focus();
    for (const font of ["", "monospace", "Verdana, sans-serif"]) {
        await page.evaluate(family => { document.documentElement.style.fontSize = "32px"; document.documentElement.style.fontFamily = family; }, font); await settle();
        assert.equal(await summary.evaluate(n => n === document.activeElement), true, "Changing the collapsed set preserves summary focus.");
        await checkHeader(page); await copy(); await scan("breadcrumbs at 200 percent text size " + (font || "system font"));
        await typography(page, "30px", !font);
        assert.equal(await page.locator(".breadcrumb-list").first().evaluate(n => n.scrollWidth <= n.clientWidth + 1), true);
    }
    await page.evaluate(() => { document.documentElement.style.fontSize = ""; document.documentElement.style.fontFamily = ""; }); await settle();
    assert.equal(await summary.evaluate(n => n === document.activeElement), true);
    await viewport(1920);
    // A changing brand allocation must trigger remeasurement without a window resize.
    await page.locator(".breadcrumbs").evaluate(n => { n.style.width = "250px"; }); await settle();
    assert.equal(await summary.isVisible(), true); await copy();
    await page.locator(".breadcrumbs").evaluate(n => { n.style.width = ""; }); await settle();
    assert.equal(await summary.isHidden(), true);

    // One and two entries must stay valid at narrow widths as well.
    for (const url of ["/", "/?folder=" + folder.id]) {
        await page.goto(url); await viewport(320); await checkHeader(page); await copy(); await scan("short catalog breadcrumb " + url);
    }
    // Razor's complete hierarchy also works before JavaScript enhancement.
    const fallback = await context.browser().newContext({ baseURL, ignoreHTTPSErrors: true, javaScriptEnabled: false,
        storageState: await context.storageState(), viewport: { width: 320, height: 640 } });
    try {
        const noJS = await fallback.newPage(); await noJS.goto("/Report?id=" + deep.id);
        assert.deepEqual(await noJS.locator(".breadcrumbs .breadcrumb-folder a").allTextContents(), all.map(e => e.name));
        assert.equal(await noJS.locator(".breadcrumbs>.breadcrumb-list>li:not([hidden])>.breadcrumb-separator").count(), all.length);
        await checkHeader(noJS);
    } finally { await fallback.close(); }

    // A 1440x900 display at 200% browser zoom exposes a 720x450 CSS viewport.
    // Pair the reduced layout viewport with 2x pixel density to check that case.
    const zoomed = await context.browser().newContext({ baseURL, ignoreHTTPSErrors: true, deviceScaleFactor: 2,
        storageState: await context.storageState(), viewport: { width: 720, height: 450 } });
    try {
        const zoomPage = await zoomed.newPage(); await zoomPage.goto("/Report?id=" + deep.id);
        await zoomPage.waitForFunction(() => document.querySelector("#parameters").getAttribute("aria-busy") === "false");
        await zoomPage.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
        await presentation(zoomPage); await checkHeader(zoomPage);
        assert.equal(await zoomPage.evaluate(() => devicePixelRatio), 2);
        const zoomSummary = zoomPage.locator(".breadcrumbs summary"), zoomMenu = zoomPage.locator(".breadcrumbs details>ol");
        await zoomSummary.press("ArrowDown");
        assert.equal(await zoomMenu.locator("a").first().evaluate(n => n === document.activeElement && getComputedStyle(n).outlineStyle === "solid"), true);
        await zoomPage.keyboard.press("Escape");
        copies.push(await copyTrail(zoomPage, zoomed, baseURL));
        await zoomPage.screenshot({ path: "artifacts/browser/breadcrumb-zoom-200.png", fullPage: true });
    } finally { await zoomed.close(); }

    await viewport(1440); await openReport(report.id);
    const initial = await page.locator("header .breadcrumbs").evaluate(n => ({ html: n.outerHTML, top: n.getBoundingClientRect().top, height: n.getBoundingClientRect().height }));
    const stable = async () => assert.deepEqual(await page.locator("header .breadcrumbs").evaluate(n => ({ html: n.outerHTML, top: n.getBoundingClientRect().top, height: n.getBoundingClientRect().height })), initial);
    await page.getByLabel("OrganizationID", { exact: true }).fill("2"); await page.getByLabel("OrganizationID", { exact: true }).press("Tab"); await inputsReady(); await stable();
    await page.getByRole("button", { name: "View report", exact: true }).click();
    await page.waitForFunction(() => !!(history.state?.ssrsReport?.token ?? null) && !document.querySelector("#run").disabled); await stable();
    await page.getByRole("button", { name: "Next", exact: true }).click();
    await page.waitForFunction(() => document.querySelector("#page-label").textContent === "Page 2 of 2"); await stable();
    for (const name of ["PDF", "Excel", "Word", "CSV"]) {
        const download = page.waitForEvent("download"); await page.getByRole("link", { name, exact: true }).click(); assert.equal(await (await download).failure(), null); await stable();
    }
    await page.reload(); await inputsReady(); await stable();
    await page.getByRole("button", { name: "Close", exact: true }).click(); await page.waitForURL(/\/\?folder=/);
    assert.equal(await page.locator(".breadcrumbs [aria-current]").textContent(), folder.name);
    // Back restores the cancelled execution's history entry. Opening its plain
    // permanent URL afresh intentionally has no temporary execution to expire.
    await page.goBack(); await inputsReady(); await stable(); assert.match(await page.locator("#status").textContent(), /session expired|unavailable/);
    await page.getByRole("button", { name: "View report", exact: true }).click();
    await page.waitForFunction(() => !!(history.state?.ssrsReport?.token ?? null) && !document.querySelector("#run").disabled); await stable();
    await page.getByRole("button", { name: "Close", exact: true }).click(); await page.waitForURL(/\/\?folder=/);
    await fs.writeFile("artifacts/browser/breadcrumb-verification.json", JSON.stringify({ copies, layouts, measurements, wideLabels, typography: { size: "15px", ancestor: 600, current: 700, separator: 400 }, actualClipboard: true, increasedText: "200%", lifecycleStable: true }, null, 2));
    await viewport(1440);
    return { breadcrumbCopies: copies.length, breadcrumbViewports: layouts.length, breadcrumbClipboard: true, breadcrumbFocusAndHistory: true, breadcrumbLifecycleStable: true, breadcrumbZoomEquivalent: "200%" };
};
