"use strict";
const assert = require("node:assert/strict");

const normalize = text => text.replace(/\s+/g, " ").trim();
exports.typography = async (page, size = "15px", segoe = true) => {
    const state = await page.evaluate(() => {
        const nav = document.querySelector(".breadcrumbs"), measure = document.querySelector(".breadcrumb-measure");
        const family = getComputedStyle(nav).fontFamily;
        const read = root => root && [root, ...root.querySelectorAll(".breadcrumb-folder>a,.breadcrumb-current>span:not(.breadcrumb-separator),.breadcrumb-separator,.breadcrumb-more summary")].map(n => {
            const css = getComputedStyle(n);
            return { text: n === root ? "container" : n.textContent, size: css.fontSize, family: css.fontFamily, weight: css.fontWeight,
                expectedWeight: n.matches(".breadcrumb-folder>a,.breadcrumb-more summary") ? "600"
                    : n.matches(".breadcrumb-current>span:not(.breadcrumb-separator)") ? "700" : n.matches(".breadcrumb-separator") ? "400" : null };
        });
        return { family, rootFamily: getComputedStyle(document.documentElement).fontFamily, visible: read(nav), measure: read(measure) };
    });
    assert.equal(state.family, state.rootFamily, "Breadcrumbs inherit the application font stack.");
    if (segoe) { assert.equal(state.family, '"Segoe UI", system-ui, sans-serif'); }
    for (const item of [...state.visible, ...(state.measure || [])]) {
        assert.equal(item.size, size, "Breadcrumb font size: " + item.text);
        assert.equal(item.family, state.family, "Visible, popup and measuring text share a font family.");
        if (item.expectedWeight) { assert.equal(item.weight, item.expectedWeight, "Breadcrumb font weight: " + item.text); }
    }
    return state;
};

exports.copyTrail = async (page, context, baseURL) => {
    await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: baseURL });
    const state = await page.locator("header .breadcrumbs").evaluate(nav => {
        const list = nav.querySelector(".breadcrumb-list"), items = [...list.children].filter(n => !n.hidden);
        const labels = items.map(n => n.querySelector(":scope>a,:scope>[aria-current],:scope>details>summary").textContent.trim());
        const separators = items.flatMap(n => [...n.querySelectorAll(":scope>.breadcrumb-separator")]);
        const selection = getSelection(), range = document.createRange();
        selection.removeAllRanges(); range.selectNodeContents(list); selection.addRange(range);
        return {
            selected: selection.toString(), expected: labels.join(" > "),
            realText: separators.every(n => n.childNodes.length === 1 && n.firstChild.nodeType === Node.TEXT_NODE && n.textContent === " > " && n.getAttribute("aria-hidden") === "true"),
            separators: separators.length, entries: items.length,
            firstHasSeparator: !!items[0].querySelector(".breadcrumb-separator"),
            menuSeparators: nav.querySelectorAll(".breadcrumb-more ol .breadcrumb-separator").length,
            generated: [nav, list, ...items, ...separators].some(n => ["::before", "::after"].some(p => !["none", "normal", '""'].includes(getComputedStyle(n, p).content))),
            selectable: getComputedStyle(nav).userSelect !== "none"
        };
    });
    assert.equal(state.realText, true, "Every visible separator is an ordinary DOM text node.");
    assert.equal(state.selectable, true); assert.equal(state.generated, false);
    assert.equal(state.firstHasSeparator, false); assert.equal(state.menuSeparators, 0);
    assert.equal(state.separators, state.entries - 1);
    assert.equal(normalize(state.selected), state.expected, "Range selection preserves exactly the visible separators.");
    await page.evaluate(() => navigator.clipboard.writeText("breadcrumb clipboard sentinel"));
    await page.keyboard.press("Control+c");
    const copied = await page.evaluate(() => navigator.clipboard.readText());
    assert.equal(normalize(copied), state.expected, "Actual Chromium keyboard copying preserves literal separators.");
    await page.evaluate(() => getSelection().removeAllRanges());
    return state.expected;
};

exports.presentation = async page => {
    await exports.typography(page);
    assert.equal(await page.getByRole("navigation", { name: "Report location", exact: true }).count(), 1);
    assert.equal(await page.locator("header .breadcrumbs>.breadcrumb-list").count(), 1);
    assert.equal(await page.locator("main nav,.catalog-breadcrumbs").count(), 0);
    assert.equal(await page.locator(".breadcrumbs [aria-current=page]").count(), 1);
    assert.equal(await page.locator(".breadcrumbs [aria-current=page]").evaluate(n => n.tagName), "SPAN");
    assert.equal(await page.locator(".breadcrumbs [aria-current=page] a").count(), 0);
    return page.locator(".breadcrumbs").evaluate(nav => {
        const css = getComputedStyle(nav), separator = nav.querySelector(".breadcrumb-separator");
        return { size: css.fontSize, separatorColor: separator ? getComputedStyle(separator).color : null,
            separatorWeight: separator ? getComputedStyle(separator).fontWeight : null };
    });
};
