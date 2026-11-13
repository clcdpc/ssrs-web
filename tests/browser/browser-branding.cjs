"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const { checkHeader, checkLogo, checkLogoBounds } = require("./branding-visual-assertions.cjs");

module.exports = async ({ page, context, scan, inputsReady }) => {
    const catalog = await (await context.request.get("/api/catalog")).json();
    const deep = catalog.find(e => e.name.startsWith("Check Ins by Day of Week by Hour with a very long"));
    const report = catalog.find(e => e.name === "Check Ins by Day of Week by Hour");
    const deepHtml = await (await context.request.get("/Report?id=" + deep.id)).text();
    const deepNavigation = deepHtml.match(/<nav class="breadcrumbs"[\s\S]*?<\/nav>/)[0];
    const wide = await fs.readFile("src/SsrsPortal.Mock/Branding/wide-logo.png");
    const compact = await fs.readFile("src/SsrsPortal.Mock/Branding/compact-logo.png");
    const ssrsWide = await fs.readFile("src/SsrsPortal.Mock/Branding/ssrs-wide-logo.png");
    const cases = [
        { name: "default" },
        { name: "dark-wide", logo: wide, background: "#183A53", foreground: "#FFFFFF" },
        { name: "light-compact", logo: compact, background: "#E9DDAF", foreground: "#213951" },
        { name: "light-wide", logo: wide, background: "#E9DDAF", foreground: "#213951" },
        { name: "dark-compact", logo: compact, background: "#183A53", foreground: "#FFFFFF" },
        { name: "colors-only", background: "#183A53", foreground: "#FFFFFF" },
        { name: "logo-only", logo: wide },
        { name: "missing" },
        { name: "transparent-white", logo: await fs.readFile("src/SsrsPortal.Mock/Branding/transparent-white.png"), background: "#183A53", foreground: "#FFFFFF" },
        { name: "transparent-black", logo: await fs.readFile("src/SsrsPortal.Mock/Branding/transparent-black.png"), background: "#E9DDAF", foreground: "#213951" },
        { name: "ssrs-wide-dark", logo: ssrsWide, background: "#183A53", foreground: "#FFFFFF" },
        { name: "ssrs-wide-light", logo: ssrsWide, background: "#E9DDAF", foreground: "#213951" },
        { name: "small", logo: await fs.readFile("src/SsrsPortal.Mock/Branding/small-logo.png"), background: "#183A53", foreground: "#FFFFFF" },
        { name: "broken-logo", logo: wide, broken: true, background: "#183A53", foreground: "#FFFFFF" }
    ];
    const viewports = [[1440, 900], [1024, 768], [768, 1024], [390, 844], [320, 640]];
    const snapshots = new Map(), measurements = [];
    let fixture = cases[0], captures = 0;
    const pattern = "**" + report.reportUrl + "*";
    // Replace only branding in real Razor responses with deterministic UI fixtures.
    // Actual REST/cache/validation/generated CSS/authorization are covered by .NET HTTP
    // integration tests. No mutable fixture endpoints are added to the application.
    const htmlRoute = async route => {
        const response = await route.fetch(); let html = await response.text();
        html = html.replace(/<nav class="breadcrumbs"[\s\S]*?<\/nav>/, deepNavigation);
        assert.match(response.headers()["content-security-policy"], /style-src 'self';/);
        if (fixture.name !== "default" && fixture.name !== "missing") {
            const version = cases.indexOf(fixture).toString(16).repeat(64); snapshots.set(version, fixture);
            const logo = fixture.logo ? '<img class="brand-logo" src="/branding/' + version + '/logo.png" alt="" />' : "";
            const fallback = '<span class="brand-fallback"' + (fixture.logo ? " hidden" : "") + '><span class="brand-mark" aria-hidden="true">R</span> Staff reports</span>';
            html = html.replace(/(<a class="brand"[^>]*>)[\s\S]*?<\/a>/, "$1" + logo + fallback + "</a>");
            if (fixture.background) { html = html.replace("</head>", '<link rel="stylesheet" href="/branding/' + version + '/theme.css" /></head>'); }
        }
        await route.fulfill({ response, body: html });
    };
    const resourceRoute = async route => {
        const match = new URL(route.request().url()).pathname.match(/^\/branding\/([0-9a-f]{64})\/(logo.png|theme.css)$/);
        const snapshot = match && snapshots.get(match[1]);
        if (!snapshot) { return route.continue(); }
        if (match[2] === "logo.png") {
            await route.fulfill({ status: snapshot.broken ? 404 : 200, contentType: "image/png", body: snapshot.broken ? Buffer.alloc(0) : snapshot.logo });
        } else {
            const bg = snapshot.background, fg = snapshot.foreground;
            await route.fulfill({ contentType: "text/css", body: `.shell-header{--ssrs-header-background:${bg};--ssrs-header-foreground:${fg};--ssrs-header-link:${fg};--ssrs-header-hover:${fg};--ssrs-header-focus:${fg};--ssrs-header-control-background:${bg};--ssrs-header-control-foreground:${fg};--ssrs-header-control-border:${fg};--ssrs-header-divider:${fg};}\n` });
        }
    };
    const settle = async () => {
        await inputsReady();
        await page.evaluate(async () => {
            await document.fonts.ready;
            await Promise.all([...document.images].map(image => image.decode().catch(() => {})));
            await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        });
    };
    const shot = async name => { captures++; await page.screenshot({ path: "artifacts/browser/branding-" + name + ".png", fullPage: true }); };
    const toRgb = color => "rgb(" + color.slice(1).match(/../g).map(v => parseInt(v, 16)).join(", ") + ")";
    await page.route(pattern, htmlRoute); await page.route("**/branding/**", resourceRoute);
    try {
        await page.goto(report.reportUrl); await inputsReady();
        await page.getByRole("button", { name: "View report", exact: true }).click();
        await page.waitForFunction(() => (history.state?.ssrsReport?.token ?? null) && !document.querySelector("#run").disabled);
        for (fixture of cases) {
            for (const [width, height] of viewports) {
                await page.setViewportSize({ width, height }); await page.reload(); await settle();
                await page.locator("#viewer").waitFor({ state: "visible" });
                await page.frameLocator("#report-frame").getByRole("heading", { name: "Page 1", exact: true }).waitFor();
                const brand = page.getByRole("link", { name: "Staff reports", exact: true });
                assert.equal(await brand.getAttribute("href"), "/");
                await page.locator(".skip").focus(); await page.keyboard.press("Tab");
                assert.equal(await brand.evaluate(n => n === document.activeElement), true, "Tab reaches the logo link.");
                assert.equal(await brand.evaluate(n => getComputedStyle(n).outlineStyle), "solid");
                assert.ok(await brand.evaluate(n => parseFloat(getComputedStyle(n).outlineWidth)) >= 2, "The logo keeps a visible keyboard focus indicator.");
                if (width <= 600) { assert.ok(await brand.evaluate(n => n.getBoundingClientRect().height) >= 44, "Logo and fallback keep their mobile touch target."); }
                const logoVisible = !!fixture.logo && !fixture.broken;
                assert.equal(await page.locator(".brand-fallback").isVisible(), !logoVisible);
                assert.equal(await page.locator(".brand-logo:visible").count(), logoVisible ? 1 : 0);
                assert.equal(await page.getByRole("button", { name: "Sign out", exact: true }).isVisible(), true);
                assert.equal(await page.locator(".account>span").isVisible(), width > 600);
                const metrics = await page.evaluate(() => {
                    const header = document.querySelector(".shell-header"), style = getComputedStyle(header);
                    return { height: header.getBoundingClientRect().height, background: style.backgroundColor, foreground: style.color,
                        current: getComputedStyle(document.querySelector(".breadcrumb-current")).color,
                        breadcrumbWidth: document.querySelector(".breadcrumbs").clientWidth,
                        overflow: document.documentElement.scrollWidth > innerWidth + 1,
                        button: getComputedStyle(document.querySelector(".account button")).color };
                });
                assert.equal(metrics.overflow, false); assert.ok(metrics.breadcrumbWidth >= 100);
                assert.ok(metrics.height <= (width > 1200 ? 60 : 100), fixture.name + " compact header at " + width);
                if (fixture.background) {
                    assert.equal(metrics.background, toRgb(fixture.background)); assert.equal(metrics.foreground, toRgb(fixture.foreground));
                    assert.equal(metrics.current, toRgb(fixture.foreground)); assert.equal(metrics.button, toRgb(fixture.foreground));
                } else { assert.equal(metrics.background, "rgb(255, 255, 255)"); }
                if (logoVisible) {
                    const dimensions = await page.locator(".brand-logo").evaluate(image => {
                        const r = image.getBoundingClientRect(), css = getComputedStyle(image);
                        return { width: r.width, height: r.height, naturalWidth: image.naturalWidth, naturalHeight: image.naturalHeight, natural: image.naturalWidth / image.naturalHeight,
                            ratio: (r.width - parseFloat(css.paddingLeft) - parseFloat(css.paddingRight)) / (r.height - parseFloat(css.paddingTop) - parseFloat(css.paddingBottom)), fit: css.objectFit };
                    });
                    assert.ok(dimensions.width <= 440.1 && dimensions.height <= 48.1);
                    assert.ok(dimensions.width <= dimensions.naturalWidth && dimensions.height <= dimensions.naturalHeight, "No enlargement of small logos");
                    assert.ok(Math.abs(dimensions.ratio - dimensions.natural) < .08, "Undistorted aspect ratio"); assert.equal(dimensions.fit, "contain");
                    const bounds = await checkLogo(page, fixture.name.startsWith("ssrs-wide"));
                    if (width === 1440 && dimensions.height >= 47.5) { assert.ok(metrics.height >= 54 && metrics.height <= 60); }
                    if (width === 1440 && fixture.name.startsWith("ssrs-wide")) { assert.ok(dimensions.width >= 439.5); }
                    assert.equal(await page.locator(".brand-fallback .brand-mark:visible").count(), 0);
                    measurements.push({ scenario: fixture.name, viewport: width, header: metrics.height, logo: dimensions, bounds });
                }
                else { await checkHeader(page); }
                await scan("branding " + fixture.name + " " + width); await shot(fixture.name + "-" + width);
                if (fixture.name === "dark-wide" || fixture.name === "light-wide") {
                    const summary = page.locator(".breadcrumbs summary");
                    if (await summary.isVisible()) {
                        await summary.press("ArrowDown"); const menu = page.locator(".breadcrumbs .breadcrumb-more details>ol");
                        assert.equal(await menu.isVisible(), true);
                        assert.equal(await menu.evaluate(n => getComputedStyle(n).backgroundColor), "rgb(255, 255, 255)");
                        assert.equal(await menu.locator("a").first().evaluate(n => getComputedStyle(n).color), "rgb(18, 79, 132)");
                        assert.equal(await menu.locator("a").first().evaluate(n => getComputedStyle(n).outlineStyle), "solid");
                        await scan("branding menu " + fixture.name + " " + width); await shot(fixture.name + "-menu-" + width);
                        await menu.locator("a").first().press("Escape"); assert.equal(await menu.isHidden(), true);
                    }
                    await page.keyboard.press("Tab"); await brand.focus();
                    assert.equal(await brand.evaluate(n => getComputedStyle(n).outlineStyle), "solid");
                    if (width === 1440) { await shot(fixture.name + "-focus-1440"); }
                }
                if (width === 320) {
                    if (logoVisible) { await page.locator(".brand-logo").click(); }
                    else { await brand.press("Enter"); }
                    assert.equal(new URL(page.url()).pathname, "/", "Logo and fallback still navigate to the catalog.");
                    await page.goBack(); await settle();
                }
            }
        }
        fixture = cases[1]; await page.setViewportSize({ width: 1440, height: 900 }); await page.reload(); await settle();
        const full = await page.locator(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder a").allTextContents();
        await page.setViewportSize({ width: 320, height: 640 }); await settle();
        assert.ok(await page.locator(".breadcrumbs .breadcrumb-more details>ol a").count() > 0);
        await page.setViewportSize({ width: 1440, height: 900 }); await settle();
        assert.deepEqual(await page.locator(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder a").allTextContents(), full);
        fixture = cases.find(c => c.name === "ssrs-wide-dark");
        await page.reload(); await settle();
        // Enlarge text without changing the viewport: the inert measuring list must
        // invalidate its cached widths and preserve usable links and controls.
        await page.evaluate(() => { document.documentElement.style.fontSize = "24px"; document.querySelector(".account>span").textContent = "Synthetic staff member with an unusually long account display name"; });
        await settle();
        await checkHeader(page); await checkLogoBounds(page);
        await scan("branding increased text and long account"); await shot("large-text-1440");
        await page.setViewportSize({ width: 320, height: 640 }); await settle();
        await checkHeader(page); await checkLogoBounds(page);
        await scan("branding increased text mobile"); await shot("large-text-320");
        fixture = cases.find(c => c.name === "colors-only");
        await page.reload(); await settle();
        await page.evaluate(() => { document.documentElement.style.fontSize = "24px"; }); await settle();
        await require("./branding-visual-assertions.cjs").checkHeader(page);
        await scan("branding fallback increased text mobile"); await shot("fallback-large-text-320");
        await page.evaluate(() => { document.documentElement.style.fontSize = ""; });
        await page.setViewportSize({ width: 1440, height: 900 }); await settle();
        // A delayed logo and a later image failure change actual grid allocation.
        fixture = cases.find(c => c.name === "ssrs-wide-dark");
        let releaseLogo;
        const logoGate = new Promise(resolve => { releaseLogo = resolve; });
        const delayedLogo = async route => { await logoGate; await route.fallback(); };
        await page.route("**/branding/*/logo.png", delayedLogo);
        await page.reload({ waitUntil: "domcontentloaded" });
        releaseLogo(); await settle(); await page.unroute("**/branding/*/logo.png", delayedLogo);
        await require("./branding-visual-assertions.cjs").checkHeader(page); await shot("delayed-logo-1440");
        await page.locator(".brand-logo").evaluate(image => { image.src = "/branding/" + "0".repeat(64) + "/logo.png"; });
        await page.locator(".brand-fallback").waitFor({ state: "visible" }); await settle();
        await require("./branding-visual-assertions.cjs").checkHeader(page); await shot("late-logo-failure-1440");
        await fs.writeFile("artifacts/browser/branding-measurements.json", JSON.stringify(measurements, null, 2));
        await page.getByRole("link", { name: "Staff reports", exact: true }).click(); assert.equal(new URL(page.url()).pathname, "/");
        await page.getByRole("button", { name: "Sign out", exact: true }).click(); await page.waitForURL(/\/SignedOut/);
        assert.equal((await context.request.get("/api/catalog")).status(), 401);
        return { brandingCases: cases.length, brandingViewports: viewports.length, brandingCaptures: captures, brandingFallback: true, brandingAspectRatio: true, brandingMenuAndFocus: true, brandingPixelTransparency: true, brandingLargeText: true, brandingLogoBounds: measurements.length, brandingOutsideLogoHit: true, brandingLogoNavigation: true };
    } finally { await page.unroute(pattern, htmlRoute); await page.unroute("**/branding/**", resourceRoute); }
};
