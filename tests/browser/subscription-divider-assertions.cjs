"use strict";
const assert = require("node:assert/strict");

module.exports = async ({ browser, context, baseURL, reportUrl }) => {
    const storageState = await context.storageState();
    for (const hasTouch of [false, true]) {
        const isolated = await browser.newContext({ baseURL, ignoreHTTPSErrors: true, storageState, hasTouch });
        try {
            const page = await isolated.newPage();
            await page.goto(reportUrl);
            await page.locator("#subscribe:not([disabled])").waitFor();
            assert.equal(await page.evaluate(() => matchMedia("(pointer:coarse)").matches), hasTouch);
            for (const width of [320, 390, 1280]) {
                await page.setViewportSize({ width, height: 900 });
                await page.locator(".parameter-divider").scrollIntoViewIfNeeded();
                const result = await page.evaluate(() => {
                    const ids = ["toggle-parameters", "subscribe", "copy-report-link"];
                    const controls = ids.map(id => {
                        const node = document.getElementById(id), box = node.getBoundingClientRect();
                        const hits = [box.left + 2, (box.left + box.right) / 2, box.right - 2].map(x =>
                            document.elementFromPoint(x, (box.top + box.bottom) / 2)?.closest("button")?.id);
                        return { id, left: box.left, right: box.right, width: box.width, hits };
                    });
                    return { controls, viewport: innerWidth };
                });
                const label = JSON.stringify({ width, hasTouch, ...result });
                for (const control of result.controls) {
                    assert.ok(control.width >= 24 && control.left >= 0 && control.right <= result.viewport, label);
                    assert.deepEqual(control.hits, [control.id, control.id, control.id], label);
                }
                for (let index = 1; index < result.controls.length; index++) {
                    assert.ok(result.controls[index - 1].right <= result.controls[index].left, label);
                }
                assert.equal(await page.getByRole("button", { name: "Subscribe", exact: true }).count(), 1);
            }
        } finally { await isolated.close(); }
    }
};
