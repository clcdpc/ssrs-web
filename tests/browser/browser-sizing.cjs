"use strict";
const assert = require("node:assert/strict");

module.exports = async ({ page, context, scan, inputsReady }) => {
    const catalog = await (await context.request.get("/api/catalog")).json();
    const open = async name => { await page.goto("/Report?id=" + catalog.find(e => e.name === name).id); await inputsReady(); };
    const width = label => page.getByLabel(label, { exact: true }).evaluate(n => n.getBoundingClientRect().width);
    for (const viewport of [{ width: 1440, height: 900 }, { width: 1024, height: 768 }, { width: 768, height: 1024 }, { width: 390, height: 844 }, { width: 320, height: 568 }]) {
        await page.setViewportSize(viewport); await open("Compact Inputs");
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
        const groups = await page.locator(".parameter-field").evaluateAll(nodes => nodes.map(n => {
            const r = n.getBoundingClientRect(); return { x: r.x, y: r.y, right: r.right, bottom: r.bottom };
        }));
        for (const [i, a] of groups.entries()) {
            for (const b of groups.slice(i + 1)) { assert.ok(a.right <= b.x + 1 || b.right <= a.x + 1 || a.bottom <= b.y + 1 || b.bottom <= a.y + 1, "Fields and their labels/errors do not overlap."); }
        }
        assert.equal(await page.locator(".parameter-field>input,.parameter-field>select").evaluateAll(nodes => nodes.every(n => n.labels.length && n.getBoundingClientRect().width <= n.parentElement.getBoundingClientRect().width + 1)), true);
        if (viewport.width >= 768) {
            for (const label of ["OrganizationID", "Minimum copies", "Percentage"]) { assert.ok(await width(label) >= 140 && await width(label) <= 185); }
            assert.ok(await width("Include inactive") >= 120 && await width("Include inactive") <= 170);
            assert.ok(await width("Report year") <= 200 && await width("Material code") <= 200 && await width("Sort direction") <= 200);
            assert.ok(await width("Search expression") >= 350 && await width("Search expression") <= 450);
            assert.ok(await width("Format") >= 280 && await width("Format") <= 400);
            const firstRow = groups.filter(r => Math.abs(r.y - groups[0].y) < 2).length;
            assert.ok(firstRow >= (viewport.width === 1440 ? 6 : viewport.width === 1024 ? 4 : 3));
        }
        const format = page.getByLabel("Format", { exact: true });
        assert.equal(await format.getAttribute("title"), await format.locator("span").first().textContent());
        await format.click();
        const formatChoices = page.getByRole("listbox", { name: "Format choices", exact: true });
        await formatChoices.getByRole("option").last().waitFor();
        assert.equal(await formatChoices.getByRole("option").count(), 6, "Five complete choices plus the explicit empty selection.");
        await page.keyboard.press("Escape");
        await scan("content sized inputs " + viewport.width);
        await page.screenshot({ path: "artifacts/browser/sizing-" + viewport.width + ".png", fullPage: true });
        const originalWidth = await width("Material code");
        await page.getByLabel("Material code", { exact: true }).fill("A longer legitimate value remains editable");
        await page.getByLabel("Material code", { exact: true }).press("Tab"); await inputsReady();
        assert.ok(Math.abs(await width("Material code") - originalWidth) < 1, "Values do not resize the control.");
        await page.getByLabel("Percentage", { exact: true }).fill("-123.75");
        await page.getByLabel("Percentage", { exact: true }).press("Tab"); await inputsReady();
        assert.equal(await page.getByLabel("Percentage", { exact: true }).inputValue(), "-123.75");
        await open("One Input");
        if (viewport.width >= 768) { assert.ok(await width("OrganizationID") >= 140 && await width("OrganizationID") <= 185); }
        await page.screenshot({ path: "artifacts/browser/one-input-" + viewport.width + ".png", fullPage: true });
        await page.getByLabel("OrganizationID", { exact: true }).fill("");
        await page.getByLabel("OrganizationID", { exact: true }).press("Tab"); await inputsReady();
        const numeric = page.getByLabel("OrganizationID", { exact: true });
        assert.equal(await numeric.getAttribute("aria-invalid"), "true");
        assert.equal(await numeric.evaluate(n => {
            const error = document.getElementById(n.id + "-error");
            return n.getAttribute("aria-describedby").split(" ").includes(error.id) && n.form.contains(error)
                && error.checkVisibility() && error.textContent.startsWith(n.labels[0].textContent + ": ");
        }), true, "Visible, labeled validation remains associated with its input in the same form.");
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
        await open("Check Ins by Day of Week by Hour");
        for (const name of ["beginDate", "endDate"]) {
            const date = page.getByLabel(name, { exact: true });
            const w = await width(name);
            assert.ok(w >= (viewport.width < 390 ? 230 : 260) && w <= (viewport.width >= 768 ? 275 : viewport.width));
            assert.equal(await date.getAttribute("type"), "datetime-local");
            assert.equal(await date.getAttribute("step"), "any");
            assert.equal(await date.evaluate(n => getComputedStyle(n, "::-webkit-calendar-picker-indicator").display !== "none" && n.scrollWidth <= n.clientWidth + 1), true);
        }
        assert.equal(await page.getByLabel("endDate", { exact: true }).inputValue(), "2026-09-30T23:59:59");
        if (viewport.width >= 768) {
            const starts = await page.getByLabel("beginDate", { exact: true }).boundingBox(), ends = await page.getByLabel("endDate", { exact: true }).boundingBox();
            assert.ok(Math.abs(starts.y - ends.y) < 2, "Two native dates fit beside each other.");
        }
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
        await scan("compact date inputs " + viewport.width);
        await page.screenshot({ path: "artifacts/browser/dates-" + viewport.width + ".png", fullPage: true });
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await open("Default Showcase");
    for (const [name, expected] of [["Precise date", "2026-10-30T09:15:23.1234567"], ["Zoned date", "2026-10-30T09:15:23.123-04:00"]]) {
        assert.equal(await page.getByLabel(name, { exact: true }).getAttribute("type"), "text");
        assert.equal(await page.getByLabel(name, { exact: true }).inputValue(), expected);
        assert.ok(await width(name) >= 400 && await width(name) <= 450);
    }
    await scan("precise ISO date fields");
    await page.screenshot({ path: "artifacts/browser/precise-dates-desktop.png", fullPage: true });
    // This reaches the actual Kestrel request-size bound (TestServer does not implement it).
    const csrf = (await (await context.request.get("/api/csrf")).json()).token;
    const oversized = await context.request.post("/api/reports/" + catalog.find(e => e.name === "Single Choice Showcase").id + "/options/branch", {
        headers: { RequestVerificationToken: csrf }, data: { values: { branch: ["x".repeat(300000)] } }
    });
    assert.ok([400, 413].includes(oversized.status()), "The real listener rejects oversized JSON.");
    return { contentSizedControls: true, widthStableAfterEdits: true, oneInputCompact: true, nativeDatePickers: true, preciseDates: true, responsiveWidths: [1440, 1024, 768, 390, 320], requestBodyLimit: true };
};
