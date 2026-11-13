"use strict";
const assert = require("node:assert/strict");

exports.waitForHeader = async page => {
    await page.waitForLoadState("load");
    await page.evaluate(async () => {
        await document.fonts.ready;
        await Promise.all([...document.querySelectorAll(".shell-header img")].map(image => image.decode().catch(() => {})));
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    });
};

exports.checkHeader = async page => {
    const metrics = await page.evaluate(() => {
        const rect = selector => document.querySelector(selector)?.getBoundingClientRect();
        const header = rect(".shell-header"), brand = rect(".brand"), account = rect(".account"), nav = rect(".breadcrumbs");
        const content = rect(".brand-logo:not([hidden])") || rect(".brand-fallback:not([hidden])") || brand;
        const geometry = Object.fromEntries(Object.entries({ header, brand, content, account, nav }).map(([name, box]) => [name, box?.toJSON()]));
        const intersects = (a, b) => !!b && a.left < b.right - 1 && a.right > b.left + 1 && a.top < b.bottom - 1 && a.bottom > b.top + 1;
        const inside = r => !r || (r.top >= header.top && r.bottom <= header.bottom + 1 && r.left >= 0 && r.right <= innerWidth + 1);
        const overflow = document.documentElement.scrollWidth > innerWidth + 1;
        const offenders = overflow ? [...document.querySelectorAll("body *")].filter(n => {
            const box = n.getBoundingClientRect(); return getComputedStyle(n).visibility === "visible" && box.width > 0 && (box.left < -1 || box.right > innerWidth + 1);
        }).slice(0, 12).map(n => ({ tag: n.tagName, id: n.id, classes: n.className, right: n.getBoundingClientRect().right })) : [];
        return { geometry, overflow, offenders, inside: [brand, content, account, nav].every(inside),
            overlap: Math.max(brand.right, content.right) > account.left + 1, breadcrumbOverlap: intersects(brand, nav),
            centered: Math.abs((brand.top + brand.bottom) / 2 - (account.top + account.bottom) / 2) < 2 };
    });
    assert.equal(metrics.overflow, false, "No horizontal page overflow: " + JSON.stringify(metrics.offenders));
    assert.equal(metrics.inside, true, "Header content is not clipped");
    assert.equal(metrics.overlap, false, "Logo and account controls do not overlap");
    assert.equal(metrics.breadcrumbOverlap, false, "Logo link and breadcrumbs do not overlap");
    assert.equal(metrics.centered, true, "Logo and account controls are vertically centered: " + JSON.stringify(metrics.geometry));
};

exports.checkLogo = async (page, partialAlpha = false) => {
    await exports.checkHeader(page);
    const bounds = await exports.checkLogoBounds(page);
    const style = await page.locator(".brand-logo").evaluate(image => {
        const css = getComputedStyle(image);
        return { background: css.backgroundColor, padding: css.padding, border: css.borderWidth, radius: css.borderRadius, opacity: css.opacity, filter: css.filter,
            insideLink: image.closest("a.brand")?.getAttribute("href") === "/" };
    });
    assert.deepEqual(style, { background: "rgba(0, 0, 0, 0)", padding: "0px", border: "0px", radius: "0px", opacity: "1", filter: "none", insideLink: true });
    if (!partialAlpha) { return bounds; }
    // Read actual browser screenshot pixels, including alpha compositing against
    // the live themed header. The source PNG is never rewritten or flattened.
    const png = await page.locator(".shell-header").screenshot();
    const samples = await page.evaluate(async bytes => {
        const header = document.querySelector(".shell-header").getBoundingClientRect();
        const image = document.querySelector(".brand-logo"), logo = image.getBoundingClientRect();
        const bitmap = await createImageBitmap(new Blob([Uint8Array.from(bytes)], { type: "image/png" }));
        const canvas = new OffscreenCanvas(bitmap.width, bitmap.height), ctx = canvas.getContext("2d"); ctx.drawImage(bitmap, 0, 0);
        const pixel = (x, y) => [...ctx.getImageData(Math.floor(logo.left - header.left + x * logo.width / image.naturalWidth), Math.floor(logo.top - header.top + y * logo.height / image.naturalHeight), 1, 1).data];
        const background = getComputedStyle(document.querySelector(".shell-header")).backgroundColor.match(/\d+/g).slice(0, 3).map(Number);
        return { transparent: pixel(4, 4), partial: pixel(850, 48), background };
    }, [...png]);
    assert.deepEqual(samples.transparent, [...samples.background, 255], "Transparent PNG pixels show the actual header");
    samples.partial.slice(0, 3).forEach((value, index) => assert.ok(Math.abs(value - (255 * 128 + samples.background[index] * 127) / 255) <= 2, "Partial PNG alpha is preserved"));
    return bounds;
};

exports.checkLogoBounds = async page => {
    const bounds = await page.locator(".shell-header .brand-logo").evaluate(image => {
        const brand = image.closest("a.brand"), link = brand.getBoundingClientRect(), logo = image.getBoundingClientRect();
        const outside = document.elementFromPoint(logo.right + 2, (logo.top + logo.bottom) / 2);
        return { link: { left: link.left, right: link.right, width: link.width, height: link.height },
            logo: { left: logo.left, right: logo.right, width: logo.width, height: logo.height },
            outsideHitsBrand: !!outside && brand.contains(outside),
            centerHitsBrand: brand.contains(document.elementFromPoint((logo.left + logo.right) / 2, (logo.top + logo.bottom) / 2)),
            mobile: innerWidth <= 600 || matchMedia("(pointer:coarse)").matches };
    });
    assert.ok(Math.abs(bounds.link.left - bounds.logo.left) <= 1, "Logo and link share their left boundary: " + JSON.stringify(bounds));
    assert.ok(Math.abs(bounds.link.right - bounds.logo.right) <= 1, "Logo link ends at the image: " + JSON.stringify(bounds));
    assert.equal(bounds.outsideHitsBrand, false, "A point just beyond the image must not activate its link.");
    assert.equal(bounds.centerHitsBrand, true, "The logo remains clickable.");
    if (bounds.mobile) { assert.ok(bounds.link.height >= 44, "Preserve the mobile logo touch-target height."); }
    return bounds;
};
