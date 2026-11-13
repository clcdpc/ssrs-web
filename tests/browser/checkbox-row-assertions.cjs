"use strict";
const assert = require("node:assert/strict");

// Start with the real, fully selected material collection. Use rendered text
// and empty row space so native label activation is exercised, not .check().
module.exports = async ({ page, group, touch = false }) => {
    const trigger = group.locator(".multi-trigger"), search = group.getByRole("searchbox");
    const option = name => group.getByRole("checkbox", { name, exact: true });
    const errors = [];
    const consoleError = message => { if (message.type() === "error") { errors.push(message.text()); } };
    page.on("console", consoleError);
    await group.evaluate(node => {
        node.rowChanges = [];
        node.addEventListener("change", event => {
            if (event.target.matches(".checkbox-choices input[type=checkbox]")) { node.rowChanges.push(event.target.id); }
        });
    });
    let changes = 0;
    const assertOpen = async () => assert.equal(await trigger.getAttribute("aria-expanded"), "true", "Row interaction keeps the popup open.");
    const activate = async (name, surface, checked, count) => {
        const checkbox = option(name), row = checkbox.locator("..");
        // Reproduce the actual user path: Search owns focus before the press.
        await search.focus();
        const target = surface === "text" ? row.locator("span") : surface === "checkbox" ? checkbox : row;
        await target.scrollIntoViewIfNeeded();
        const box = await target.boundingBox();
        const position = { x: surface === "padding" ? box.width - 2 : box.width / 2, y: box.height / 2 };
        if (surface === "padding") {
            assert.equal(await row.evaluate((node, point) => {
                const rect = node.getBoundingClientRect();
                return document.elementFromPoint(rect.x + point.x, rect.y + point.y) === node;
            }, position), true, "The pointer hits unused label space, outside the checkbox and text.");
        }
        if (touch) { await target.tap({ position }); }
        else {
            await page.mouse.move(box.x + position.x, box.y + position.y);
            await page.mouse.down();
            try {
                // No delay: allow the browser to process a real held press and
                // its focusout microtask before releasing it to produce click.
                await assertOpen();
                assert.equal(await group.evaluate(node => node.rowChanges.length), changes, "A press alone never changes selection.");
            } finally { await page.mouse.up(); }
        }
        changes++;
        await assertOpen();
        assert.equal(await checkbox.isChecked(), checked, name + " " + surface);
        assert.equal(await group.locator("input[data-option]:checked").count(), count);
        assert.equal(await group.evaluate(node => node.rowChanges.length), changes, "Each activation emits exactly one native change.");
        assert.equal(await checkbox.evaluate(node => node === document.activeElement), true, "Native label activation focuses its checkbox.");
        assert.match(await trigger.getAttribute("aria-label"), new RegExp(", " + count + " values selected\\."));
    };
    try {
        const row = option("4K Blu-ray").locator("..");
        const height = await row.evaluate(node => node.getBoundingClientRect().height);
        assert.ok(touch ? height >= 44 : height >= 32 && height <= 34, "Keep compact desktop rows and at least 44px touch targets.");
        if (!touch) {
            await row.hover();
            assert.equal(await row.evaluate(node => getComputedStyle(node).backgroundColor), "rgb(237, 243, 251)");
        }
        await activate("4K Blu-ray", "checkbox", false, 51);
        await activate("4K Blu-ray", "text", true, 52);
        await activate("4K Blu-ray", "text", false, 51);
        await activate("4K Blu-ray", "padding", true, 52);
        await activate("4K Blu-ray", "padding", false, 51);
        await activate("(Select All)", "text", true, 52);
        await activate("(Select All)", "text", false, 0);
        await activate("(Select All)", "padding", true, 52);
        await activate("(Select All)", "padding", false, 0);
        await activate("Atlas", "text", true, 1);
        await search.fill("aUdIo");
        assert.equal(await group.locator("input[data-option]:visible").count(), 5);
        await activate("Audio Books", "text", true, 2);
        await activate("Audio Books", "text", false, 1);
        await activate("Audio Books", "padding", true, 2);
        await activate("(Select All)", "text", true, 52);
        await activate("(Select All)", "text", false, 0);
        await activate("Audio Books", "text", true, 1);
        await search.fill("");
        await activate("Atlas", "padding", true, 2);
        await search.press("ArrowDown");
        assert.equal(await option("(Select All)").evaluate(node => node === document.activeElement), true);
        await page.keyboard.press("End");
        assert.equal(await option("Video Game").evaluate(node => node === document.activeElement), true);
        await page.keyboard.press("Home"); await page.keyboard.press("ArrowDown");
        await page.keyboard.press("Space"); await page.keyboard.press("Space");
        changes += 2;
        await page.keyboard.press("Enter"); await assertOpen();
        assert.equal(await group.evaluate(node => node.rowChanges.length), changes);
        assert.deepEqual(await group.locator("input[data-option]:checked").evaluateAll(nodes => nodes.map(node => node.optionValue)), ["material-3", "material-6"]);
        assert.deepEqual(await row.evaluate(node => [node, ...node.children].map(child => getComputedStyle(child).cursor)), ["pointer", "pointer", "pointer"], "The label, checkbox and text all present the row's pointer cursor.");
        assert.equal(await row.evaluate(node => getComputedStyle(node).backgroundColor), "rgb(237, 243, 251)", "Focus identifies the row.");
        assert.deepEqual(errors, [], "Row editing produces no console errors.");
    } finally { page.off("console", consoleError); }
};
