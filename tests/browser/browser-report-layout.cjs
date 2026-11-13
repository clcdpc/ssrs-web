"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");

module.exports = async ({ page, context, baseURL, inputsReady, selectSingle, scan }) => {
    const near = (actual, expected, tolerance, label) => assert.ok(Math.abs(actual - expected) <= tolerance,
        `${label}: ${actual}, expected ${expected} ± ${tolerance}`);
    // Used unchanged for the independent raw-HTML reference and actual iframe.
    const measure = async () => {
        const iframe = document.querySelector("#report-frame");
        const doc = iframe ? iframe.contentDocument : document;
        const win = doc.defaultView;
        await doc.fonts.ready;
        const table = doc.querySelector("#holds-ratio");
        const headings = [...doc.querySelectorAll("#column-headings th")];
        const style = node => win.getComputedStyle(node);
        const header = style(headings[0]);
        const range = doc.createRange(); range.selectNodeContents(doc.querySelector("#report-values td:nth-child(2)"));
        return {
            tableWidth: table.getBoundingClientRect().width,
            columns: [...table.rows[0].cells].map(c => c.getBoundingClientRect().width),
            firstLeft: headings[0].getBoundingClientRect().left, lastRight: headings.at(-1).getBoundingClientRect().right,
            viewport: doc.documentElement.clientWidth, scrollWidth: doc.documentElement.scrollWidth, scrollX: win.scrollX,
            font: header.fontFamily, fontSize: parseFloat(header.fontSize), borderSpacing: style(table).borderSpacing,
            headerPadding: parseFloat(header.paddingLeft), headerBorder: header.borderBottomWidth,
            alignment: headings.slice(0, 4).map(c => style(c).textAlign), wrapping: style(doc.querySelector("#report-values td:nth-child(2)")).whiteSpace,
            wrappedLines: range.getClientRects().length,
            nonzeroSpacing: style(doc.querySelector("#spacing-sample")).borderSpacing,
            nonzeroPadding: style(doc.querySelector("#spacing-sample th")).paddingLeft,
            cssSpacing: style(doc.querySelector("#font-sample")).borderSpacing,
            cellPadding: style(doc.querySelector("#padded-cell")).padding, cellBorder: style(doc.querySelector("#padded-cell")).borderTopWidth,
            explicitFont: style(doc.querySelector("#explicit-font")).fontFamily, explicitFontSize: style(doc.querySelector("#explicit-font")).fontSize,
            explicitSizeFont: style(doc.querySelector("#explicit-size")).fontFamily, explicitSize: style(doc.querySelector("#explicit-size")).fontSize,
            wrapperSpacing: ["#ssrs-page", "#ssrs-section"].map(s => style(doc.querySelector(s)).borderSpacing),
            transform: style(table).transform, zoom: style(table).zoom,
            shellOverflow: iframe ? document.documentElement.scrollWidth - innerWidth : 0
        };
    };
    const raw = await fs.readFile("tests/SsrsPortal.Tests/Fixtures/holds-ratio-layout.html", "utf8");
    const referencePage = await context.newPage();
    const requests = [];
    const count = request => requests.push(request.url());
    context.on("request", count);
    try {
        await referencePage.setViewportSize({ width: 1886, height: 900 });
        await referencePage.setContent(raw);
        // Independently apply the operator's verified native defaults. Preserve
        // nonzero fixture variants; normalize only omitted wrapper attributes.
        await referencePage.evaluate(() => {
            document.documentElement.style.fontFamily = '"Times New Roman",serif';
            document.documentElement.style.fontSize = "10pt";
            document.body.style.margin = "0"; document.body.style.padding = "0";
            for (const table of document.querySelectorAll("table")) {
                for (const name of ["cellpadding", "cellspacing"]) {
                    if (!table.hasAttribute(name)) { table.setAttribute(name, "0"); }
                }
            }
        });
        const native = await referencePage.evaluate(measure);
        near(native.tableWidth, 1881.9, 0.15, "Native reference table");
        for (const [index, width] of [[0, 66], [1, 312], [2, 229], [3, 64]]) { near(native.columns[index], width, 0.05, "Native reference column " + index); }
        // Negative control retains native typography but strips the attributes
        // just as the old sanitizer did. Each sizing cell must grow by 2px.
        await referencePage.evaluate(() => {
            for (const table of document.querySelectorAll("table")) { table.removeAttribute("cellpadding"); table.removeAttribute("cellspacing"); }
        });
        const stripped = await referencePage.evaluate(measure);
        near(stripped.tableWidth - native.tableWidth, 36, 0.15, "Pre-fix width inflation");
        stripped.columns.forEach((width, index) => near(width - native.columns[index], 2, 0.05, "Pre-fix column inflation " + index));
        assert.ok(stripped.lastRight > stripped.viewport, "Negative control clips the last column at the measured viewport");

        const report = (await (await context.request.get("/api/catalog")).json()).find(r => r.name === "Expandable Groups");
        await page.setViewportSize({ width: 1886, height: 900 });
        await page.goto("/Report?id=" + report.id); await inputsReady();
        await selectSingle("Behavior", "Native report layout");
        await page.getByRole("button", { name: "View report", exact: true }).click();
        await page.waitForFunction(() => document.querySelector("#report-frame")?.contentDocument?.querySelector("#holds-ratio") &&
            document.querySelector("#viewer").getAttribute("aria-busy") === "false");
        const token = await page.evaluate(() => history.state?.ssrsReport?.token ?? null);
        const response = await context.request.get("/api/executions/" + token + "/viewer");
        const safe = await response.text();
        assert.equal(response.status(), 200);
        assert.match(response.headers()["content-security-policy"], /script-src 'none'/);
        assert.match(response.headers()["content-security-policy"], /sandbox allow-same-origin/);
        assert.equal(await page.locator("#report-frame").getAttribute("sandbox"), "allow-same-origin");
        assert.doesNotMatch(safe, /<script|onload=|onclick=|synthetic-execution|synthetic\.invalid/);

        const layouts = [];
        for (const width of [1886, 1440, 1024, 768, 390, 320]) {
            await page.setViewportSize({ width, height: 900 });
            await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(0, 0));
            const actual = await page.evaluate(measure);
            near(actual.tableWidth, native.tableWidth, 0.15, "Sanitized table at " + width);
            assert.equal(actual.columns.length, 18);
            actual.columns.forEach((w, i) => near(w, native.columns[i], 0.05, "Sanitized column " + i + " at " + width));
            assert.match(actual.font, /Times New Roman/); near(actual.fontSize, 40 / 3, 0.001, "Default 10pt");
            assert.equal(actual.borderSpacing, "0px"); near(actual.headerPadding, 8 / 3, 0.001, "Native 2pt header padding");
            assert.equal(actual.headerBorder, "1px"); assert.deepEqual(actual.alignment, ["center", "left", "left", "right"]);
            assert.equal(actual.wrapping, "normal"); assert.ok(actual.wrappedLines > 1, "Title still wraps inside its native column");
            assert.equal(actual.nonzeroSpacing, "4px"); assert.equal(actual.nonzeroPadding, "3px");
            assert.equal(actual.cssSpacing, "6px 8px", "Report CSS overrides presentation attributes");
            assert.equal(actual.cellPadding, "5px 7px"); assert.equal(actual.cellBorder, "2px");
            assert.equal(actual.explicitFont, "Arial"); assert.equal(actual.explicitFontSize, "16px");
            assert.match(actual.explicitSizeFont, /Courier New/); assert.equal(actual.explicitSize, "12px");
            assert.deepEqual(actual.wrapperSpacing, ["0px", "0px"]);
            assert.equal(actual.transform, "none"); assert.equal(actual.zoom, "1"); assert.ok(actual.shellOverflow <= 1);
            near(actual.firstLeft, 0, 0.05, "First column reaches the report edge");
            if (width === 1886) {
                assert.ok(actual.scrollWidth <= actual.viewport, "A native-width report fits without horizontal scrolling");
                assert.ok(actual.lastRight <= actual.viewport, "All eighteen columns are inside the viewport");
            } else {
                assert.ok(actual.scrollWidth > actual.viewport, "Genuinely wide reports retain horizontal scrolling");
                await page.locator("#report-frame").evaluate(f => f.contentWindow.scrollTo(10000, 0));
                const scrolled = await page.evaluate(measure);
                assert.ok(scrolled.scrollX > 0 && scrolled.lastRight > 0 && scrolled.lastRight <= scrolled.viewport + 1, "The last column is reachable by scrolling");
            }
            await scan("Native report width and typography at " + width);
            if (width === 1886 || width === 390) { await page.screenshot({ path: "artifacts/browser/expansion-layout-" + width + ".png", fullPage: true }); }
            layouts.push({ width, ...actual });
        }
        // Audit the exact sanitized copy; axe cannot inject into the production
        // script-disabled sandbox. The real frame retains its isolation.
        await referencePage.route("**/__native_layout_audit", route => route.fulfill({ contentType: "text/html", body: safe }));
        await referencePage.goto("/__native_layout_audit");
        const audit = await new (require("@axe-core/playwright").default)({ page: referencePage }).withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
        assert.deepEqual(audit.violations.map(v => ({ id: v.id, nodes: v.nodes.length })), []);
        assert.deepEqual([...new Set(requests.filter(url => new URL(url).origin !== new URL(baseURL).origin))], [], "No direct SSRS or external requests");
        const evidence = { nativeReportLayout: true, nativeReference: native, strippedTableWidth: stripped.tableWidth, nativeReportLayouts: layouts, layoutReportAxeScans: 1 };
        await fs.writeFile("artifacts/browser/report-layout-verification.json", JSON.stringify(evidence, null, 2));
        return evidence;
    } finally { context.off("request", count); await referencePage.close(); }
};
