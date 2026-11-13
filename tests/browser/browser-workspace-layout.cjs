"use strict";
const assert = require("node:assert/strict");

module.exports = async ({ page, context, baseURL, scan, inputsReady }) => {
    const { selectSingle } = require("./single-choice-helpers.cjs")(page, inputsReady);
    const catalog = await (await context.request.get("/api/catalog")).json();
    const deep = catalog.find(e => e.name.startsWith("Check Ins by Day of Week by Hour with a very long"));
    const open = async name => { await page.goto("/Report?id=" + catalog.find(e => e.name === name).id); await inputsReady(); };
    const settle = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const shot = name => page.screenshot({ path: "artifacts/browser/layout-" + name + ".png", fullPage: true });
    const viewport = async (width, height = 900) => { await page.setViewportSize({ width, height }); await settle(); };
    const noOverflow = async () => assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
    const folders = () => page.locator(".breadcrumbs>.breadcrumb-list>.breadcrumb-folder a");
    const summary = () => page.locator(".breadcrumbs summary");
    const menu = () => page.locator(".breadcrumbs details>ol");
    let names = ["Polaris", "Custom", "~Tech Services"], reportName = "Holds Ratio Report";
    const encode = value => value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll('"', "&quot;");
    // Only synthetic labels/ancestry change in the real Razor response. Links
    // keep authorized opaque IDs. Server tests separately cover real ancestry.
    const hierarchy = async route => {
        const response = await route.fetch(), html = await response.text();
        const originals = [...html.matchAll(/<li class="breadcrumb-folder">[\s\S]*?<\/li>/g)].map(m => m[0]);
        const separator = '<span class="breadcrumb-separator" aria-hidden="true"> &gt; </span>';
        const items = names.map((name, i) => originals[i].replace(/<span class="breadcrumb-separator"[^>]*>[\s\S]*?<\/span>/, "")
            .replace(/title="[^"]*"/, 'title="' + encode(name) + '"').replace(/>[^<]*<\/a>/, ">" + encode(name) + "</a>")
            .replace('<li class="breadcrumb-folder">', '<li class="breadcrumb-folder">' + (i ? separator : "")));
        const more = html.match(/<li class="breadcrumb-more" hidden>[\s\S]*?<\/li>/)[0];
        const list = '<ol class="breadcrumb-list">' + (items.length ? items[0] + (items.length > 2 ? more : "") + items.slice(1).join("") : "") + '<li class="breadcrumb-current">' + (items.length ? separator : "") + '<span aria-current="page" title="' + encode(reportName) + '">' + encode(reportName) + "</span></li></ol>";
        await route.fulfill({ response, body: html.replace(/(<nav class="breadcrumbs"[^>]*>)[\s\S]*?<\/nav>/, "$1" + list + "</nav>") });
    };
    const pattern = "**" + deep.reportUrl;
    await page.route(pattern, hierarchy);
    try {
        await viewport(1440);
        for (const labels of [["Polaris", "Custom", "~Tech Services"], ["Polaris", "Custom", "Circulation", "Monthly Reports"], ["Polaris", "Custom", "A", "B", "C", "~Tech Services"]]) {
            names = labels;
            await page.goto(deep.reportUrl); await inputsReady(); await settle();
            assert.deepEqual(await folders().allTextContents(), names);
            assert.equal(await summary().isHidden(), true, "Folder count alone never collapses ancestry.");
            assert.equal(await menu().locator("a").count(), 0); await scan("full " + names.length + " folder hierarchy");
        }
        names = []; await page.reload(); await inputsReady();
        assert.equal(await folders().count(), 0); assert.equal(await summary().count(), 0);
        assert.equal(await page.locator('.breadcrumbs [aria-current="page"]').textContent(), reportName);
        names = ["Polaris", "An exceptionally long intermediate folder " + "X".repeat(140), "~Tech Services"];
        reportName = "An unusually long report name " + "Y".repeat(140);
        await page.reload(); await inputsReady(); await settle();
        assert.deepEqual(await folders().allTextContents(), names); assert.equal(await summary().isHidden(), true, "Cap unusual labels before collapsing folders.");
        assert.equal(await folders().nth(1).evaluate(n => n.scrollWidth > n.clientWidth), true);
        await viewport(320, 640); await summary().press("ArrowDown");
        assert.equal(await menu().locator("a").textContent(), names[1]);
        assert.equal(await menu().locator("a").getAttribute("title"), names[1]);
        await noOverflow(); await shot("long-folder-menu-320"); await scan("long folder identity and bounded menu");
        await page.evaluate(() => { document.body.style.minHeight = "200vh"; window.scrollTo(0, 160); }); await settle();
        const scrolled = await menu().boundingBox(); assert.ok(scrolled.y >= 0 && scrolled.y + scrolled.height <= 640, "Scrolling cannot move an open menu outside the viewport.");
        await page.evaluate(() => { window.scrollTo(0, 0); document.body.style.minHeight = ""; }); await settle();
        await menu().locator("a").press("Tab"); assert.equal(await menu().isHidden(), true);
        names = ["Polaris", "Custom", "~Tech Services"];
        reportName = "Holds Ratio Report"; await viewport(1440);
        await page.reload(); await inputsReady(); await settle(); await shot("polaris-desktop");
        const hrefs = await folders().evaluateAll(nodes => nodes.map(n => n.getAttribute("href")));
        await viewport(320, 640);
        assert.deepEqual(await folders().allTextContents(), ["Polaris", "~Tech Services"]);
        await summary().focus(); await summary().press("ArrowDown"); await menu().locator("a").first().waitFor();
        assert.deepEqual(await menu().locator("a").allTextContents(), ["Custom"]);
        assert.equal(await menu().locator("a").first().evaluate(n => n === document.activeElement), true);
        assert.equal(await page.getByRole("link", { name: "Custom", exact: true }).count(), 1);
        assert.equal(await menu().locator("a").getAttribute("href"), hrefs[1]);
        assert.equal(await page.locator('[aria-current="page"]').getAttribute("title"), reportName);
        const bounds = await menu().boundingBox(); assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= 320 && bounds.y + bounds.height <= 640);
        await shot("polaris-menu-320"); await scan("adaptive menu accessible links");
        await menu().locator("a").press("Escape");
        assert.equal(await summary().evaluate(n => n === document.activeElement), true); assert.equal(await menu().isHidden(), true);
        await summary().press("Enter"); await page.getByRole("region", { name: "Report parameters", exact: true }).click({ position: { x: 2, y: 2 } }); assert.equal(await menu().isHidden(), true);
        await viewport(1440);
        assert.deepEqual(await folders().allTextContents(), names); assert.equal(await summary().isHidden(), true); await shot("polaris-restored-desktop");
        // Also change just the available header region, without viewport resize.
        await page.locator(".breadcrumbs").evaluate(n => { n.style.width = "250px"; }); await settle(); assert.equal(await summary().isVisible(), true);
        await page.locator(".breadcrumbs").evaluate(n => { n.style.width = ""; }); await settle(); assert.equal(await summary().isHidden(), true);
        await folders().nth(1).click(); await page.waitForURL(baseURL + hrefs[1]);
        names = ["Polaris", "Custom", "Administration", "Statistics", "Monthly Reports", "~Tech Services"];
        reportName = "A long report name for regional circulation review and historical comparisons";
        await page.goto(deep.reportUrl); await inputsReady(); await viewport(320, 640); await summary().press("ArrowDown");
        assert.deepEqual(await menu().locator("a").allTextContents(), names.slice(1, -1));
        assert.equal(await menu().locator("a").first().evaluate(n => n === document.activeElement), true, "Opening with ArrowDown focuses the first hidden folder exactly once.");
        await menu().locator("a").first().press("End"); assert.equal(await menu().locator("a").last().evaluate(n => n === document.activeElement), true);
        await menu().locator("a").last().press("Home"); assert.equal(await menu().locator("a").first().evaluate(n => n === document.activeElement), true);
        await menu().locator("a").first().press("ArrowDown"); assert.equal(await menu().locator("a").nth(1).evaluate(n => n === document.activeElement), true);
        await page.keyboard.press("Escape"); await summary().press("Enter"); await summary().press("Tab"); await viewport(1440);
        assert.equal(await page.evaluate(() => document.activeElement !== document.body && document.activeElement.getBoundingClientRect().width > 0), true);
        for (const size of [[1440, 900], [1024, 768], [768, 1024], [390, 844], [320, 640]]) {
            await viewport(...size); await noOverflow(); if (await summary().isVisible()) { await summary().press("ArrowDown"); }
            await scan("deep adaptive hierarchy " + size[0]); await shot("deep-" + size[0]);
            const samples = await page.evaluate(async () => {
                const results = [];
                for (let i = 0; i < 6; i++) { await new Promise(requestAnimationFrame); results.push(document.querySelector(".breadcrumbs>.breadcrumb-list").innerHTML); }
                return results;
            });
            assert.ok(samples.every(s => s === samples[0]), "Stable width does not churn breadcrumb layout.");
        }
        const fallback = await context.browser().newContext({ baseURL, ignoreHTTPSErrors: true, javaScriptEnabled: false, storageState: await context.storageState(), viewport: { width: 320, height: 640 } });
        try {
            await fallback.route(pattern, hierarchy); const noJS = await fallback.newPage(); await noJS.goto(deep.reportUrl);
            assert.deepEqual(await noJS.locator(".breadcrumb-folder a").allTextContents(), names);
            assert.equal(await noJS.locator(".breadcrumb-more").isHidden(), true);
            assert.equal(await noJS.locator(".breadcrumb-folder a").evaluateAll(nodes => nodes.every(n => n.getBoundingClientRect().width > 0)), true);
            await noJS.locator(".breadcrumb-folder a").first().click(); await noJS.waitForURL(/\/\?folder=/);
        } finally { await fallback.close(); }
    } finally { await page.unroute(pattern, hierarchy); }

    await viewport(1440);
    for (const [name, capture] of [["One Input", "one-input"], ["Check Ins by Day of Week by Hour", "one-row"], ["Default Showcase", "wrapped-fields"]]) {
        await open(name);
        assert.equal(await page.locator("#parameters>.report-actions #run").count(), 1);
        assert.equal(await page.locator("#parameter-controls #run,#parameter-controls #cancel-run,.workspace-feedback #toggle-parameters").count(), 0);
        const panel = await page.locator("#parameters").boundingBox(), run = await page.locator("#run").boundingBox();
        const fields = await page.locator(".parameter-field").evaluateAll(nodes => nodes.map(n => {
            const r = n.getBoundingClientRect(); return { x: r.x, y: r.y, right: r.right, bottom: r.bottom };
        }));
        assert.ok(Math.abs(run.x + run.width - panel.x - panel.width) <= 1);
        for (const field of fields) { assert.ok(run.x >= field.right - 1 || run.y >= field.bottom - 1, "Actions sit beside or below each field without overlap."); }
        assert.ok(Math.abs(run.y + run.height - panel.y - panel.height) <= 1, "Actions align with the bottom of the parameter area.");
        if (capture === "one-row") { assert.ok(Math.abs(run.y + run.height - Math.max(...fields.map(f => f.bottom))) <= 1, "Wrapped desktop inputs leave room for actions on their final row."); }
        assert.equal(await page.locator(".workspace-feedback").evaluate(n => n.getBoundingClientRect().height), 0);
        assert.equal(await page.locator("#cancel-run").isHidden(), true); await shot(capture + "-desktop");
    }
    let runs = 0, resolutions = 0;
    const count = request => { if (/\/run$/.test(request.url())) { runs++; } if (/\/parameters$/.test(request.url())) { resolutions++; } };
    page.on("request", count);
    try {
        await open("Check Ins by Day of Week by Hour");
        const original = await page.locator("#fields").elementHandle();
        const values = await page.locator("#fields input,#fields select").evaluateAll(nodes => nodes.map(n => n.value));
        for (const [width, height] of [[1440, 900], [1024, 768], [768, 1024], [390, 844], [320, 640]]) {
            await viewport(width, height);
            let release, started, finished;
            const gate = new Promise(resolve => { release = resolve; }), entered = new Promise(resolve => { started = resolve; });
            const handled = new Promise(resolve => { finished = resolve; });
            const delay = async route => { started(); await gate; await route.abort().catch(() => {}); finished(); };
            await page.route("**/run", delay);
            const fieldTop = await page.locator(".parameter-field").first().evaluate(n => n.getBoundingClientRect().top), before = runs;
            await page.locator("#parameters").evaluate(n => { n.requestSubmit(); n.requestSubmit(); }); await entered;
            assert.equal(runs, before + 1); assert.equal(await page.locator(".report-actions #cancel-run").isVisible(), true);
            assert.equal(await page.locator(".parameter-field").first().evaluate(n => n.getBoundingClientRect().top), fieldTop, "Cancel visibility never moves fields.");
            const run = await page.locator("#run").boundingBox(), cancel = await page.locator("#cancel-run").boundingBox(); assert.equal(run.y, cancel.y); assert.ok(run.x >= cancel.x + cancel.width);
            assert.match(await page.locator("#status[role=status]").textContent(), /Running/);
            await shot("running-" + width); await scan("parameter actions during execution " + width);
            const beforeCollapse = resolutions; await page.locator("#toggle-parameters").click();
            assert.equal(await page.locator("#run").isHidden(), true); assert.equal(await page.locator("#cancel-run").isVisible(), true);
            assert.equal(await page.locator("#parameter-controls").isHidden(), true); assert.equal(resolutions, beforeCollapse); assert.equal(runs, before + 1);
            await page.locator("#cancel-run").click(); release(); await handled; await page.unroute("**/run", delay); await inputsReady(); assert.equal(await page.locator("#cancel-run").isHidden(), true);
            await page.locator("#toggle-parameters").press("Enter");
            assert.equal(await original.evaluate(n => n === document.querySelector("#fields")), true);
            assert.deepEqual(await page.locator("#fields input,#fields select").evaluateAll(nodes => nodes.map(n => n.value)), values); await noOverflow();
        }
        await viewport(1440); const realRun = page.waitForResponse(response => /\/run$/.test(response.url()));
        await page.locator("#run").click(); assert.equal((await realRun).status(), 200);
        await page.waitForFunction(() => Boolean((history.state?.ssrsReport?.token ?? null)) && !document.querySelector("#run").disabled);
        for (const [width, height] of [[1440, 900], [390, 844], [320, 640]]) {
            await viewport(width, height); await shot("expanded-" + width);
            if (width < 600) {
                await page.evaluate(() => window.scrollTo(0, 220)); await settle();
                assert.ok(await page.evaluate(() => scrollY) > 0, "Exercise collapse after native focus changes the outer scroll position.");
            }
            // Native focus may scroll the outer page before/after hiding inputs.
            // Compare document positions to isolate actual reclaimed layout space.
            const top = await page.locator(".viewer-toolbar").evaluate(n => n.getBoundingClientRect().top + scrollY), before = [runs, resolutions];
            const beforeFocusScroll = await page.evaluate(() => scrollY);
            const sectionHeight = await page.locator(".parameter-section").evaluate(n => n.getBoundingClientRect().height);
            await page.getByLabel("beginDate", { exact: true }).focus();
            if (width < 600) { assert.notEqual(await page.evaluate(() => scrollY), beforeFocusScroll, "The regression must exercise native focus scrolling."); }
            await page.locator("#toggle-parameters").evaluate(n => n.click());
            assert.equal(await page.locator("#toggle-parameters").evaluate(n => n === document.activeElement), true);
            assert.equal(await page.locator("#toggle-parameters").getAttribute("aria-expanded"), "false"); assert.equal(await page.locator("#toggle-parameters").getAttribute("aria-controls"), "parameters");
            const reclaimed = sectionHeight - await page.locator(".parameter-section").evaluate(n => n.getBoundingClientRect().height);
            assert.ok(reclaimed > 16);
            const collapsedTop = await page.locator(".viewer-toolbar").evaluate(n => n.getBoundingClientRect().top + scrollY);
            assert.ok(Math.abs(top - collapsedTop - reclaimed) <= 1, JSON.stringify({ width, top, collapsedTop, reclaimed, scrollY: await page.evaluate(() => scrollY) }));
            assert.deepEqual([runs, resolutions], before);
            await shot("collapsed-" + width); await scan("compact collapsed workspace " + width); await page.locator("#toggle-parameters").press("Space");
        }
        await viewport(1440); await page.getByLabel("beginDate", { exact: true }).fill("2026-10-01T00:00"); await page.getByLabel("beginDate", { exact: true }).press("Tab"); await inputsReady();
        assert.equal(await page.locator("#parameters>#dirty").isVisible(), true); await shot("dirty-desktop");
        await viewport(320, 640); assert.ok((await page.locator("#dirty").boundingBox()).width > 0); await shot("dirty-mobile"); await viewport(1440);
        await page.locator("#toggle-parameters").click();
        assert.equal(await page.locator("#run,#dirty").evaluateAll(nodes => nodes.every(n => !n.checkVisibility())), true);
        await page.locator("#toggle-parameters").click();
        assert.equal(await page.locator("#dirty").isVisible(), true, "Expanding restores pending parameter feedback.");
        const beforeRun = runs; await page.locator("#run").click();
        await page.locator("#toggle-parameters").click(); await inputsReady(); assert.equal(runs, beforeRun + 1);
        assert.equal(await page.locator("#parameter-controls").isHidden(), true); assert.equal(await page.locator("#dirty").isHidden(), true);
        await page.locator("#cancel-session").click(); await page.waitForURL(/\/\?folder=/);
        await open("One Input"); await page.getByLabel("OrganizationID", { exact: true }).evaluate(n => n.setCustomValidity("Review this numeric value before running."));
        await page.locator("#toggle-parameters").click(); const beforeInvalid = runs; await page.locator("#parameters").evaluate(n => n.requestSubmit());
        assert.equal(await page.locator("#parameter-controls").isVisible(), true); assert.equal(await page.getByLabel("OrganizationID", { exact: true }).evaluate(n => n === document.activeElement), true); assert.equal(runs, beforeInvalid);
        await page.getByLabel("OrganizationID", { exact: true }).evaluate(n => n.setCustomValidity(""));
        const validationActions = () => page.locator(".report-actions,#run").evaluateAll(nodes => nodes.map(n => {
            const r = n.getBoundingClientRect(); return { x: r.x + scrollX, y: r.y + scrollY, width: r.width, height: r.height };
        }));
        const validationBefore = new Map();
        for (const width of [1440, 320]) { await viewport(width, width === 320 ? 640 : 900); validationBefore.set(width, await validationActions()); }
        const stableValidationActions = async width => {
            const after = await validationActions(), before = validationBefore.get(width);
            after.forEach((box, i) => { for (const key of ["x", "y", "width", "height"]) { assert.ok(Math.abs(box[key] - before[i][key]) <= 1, `Validation must not move actions at ${width}px: ${key}`); } });
        };
        await viewport(1440);
        let releaseValidation, validationStarted;
        const validationGate = new Promise(resolve => { releaseValidation = resolve; }), validating = new Promise(resolve => { validationStarted = resolve; });
        const longError = "Choose a current authorized organization before running the report. This deliberately long validation message must remain associated with its input and wrap naturally without moving the command actions or overlapping another control. Reference: synthetic-validation.";
        const validation = async route => { const response = await route.fetch(), json = await response.json(); json.fields[0].error = longError; json.canRun = false; validationStarted(); await validationGate; await route.fulfill({ response, json }); };
        await page.route("**/parameters", validation); await page.getByLabel("OrganizationID", { exact: true }).fill(""); await page.getByLabel("OrganizationID", { exact: true }).press("Tab"); await validating;
        await page.locator("#toggle-parameters").click(); releaseValidation(); await inputsReady();
        assert.equal(await page.locator("#parameter-controls").isVisible(), true); assert.equal(await page.locator("#run").isDisabled(), true); assert.equal(await page.locator("#status").isVisible(), true);
        assert.equal(await page.getByLabel("OrganizationID", { exact: true }).getAttribute("aria-describedby"), "param-0-error");
        const errorBox = await page.locator(".field-error").boundingBox(), runBox = await page.locator("#run").boundingBox();
        assert.ok(errorBox.y >= runBox.y + runBox.height - 1, "Labeled validation follows the stable control/action rows.");
        await stableValidationActions(1440);
        await shot("long-validation-desktop"); await scan("long validation with bottom actions"); await viewport(320, 640); await noOverflow(); await shot("long-validation-mobile"); await scan("long validation mobile");
        await stableValidationActions(320);
        await page.unroute("**/parameters", validation);
        await page.getByLabel("OrganizationID", { exact: true }).fill("21"); await page.getByLabel("OrganizationID", { exact: true }).press("Tab"); await inputsReady();
        assert.equal(await page.locator("#param-0-error").count(), 0);
        await stableValidationActions(320); await viewport(1440); await stableValidationActions(1440);
    } finally { page.off("request", count); }

    // Exercise the UI's typed null boundary independently of choice labels;
    // existing SOAP/default/cascade tests cover delivery to the server.
    const seeds = { exclusion: ["none"], number: ["12"], date: ["2026-10-31T09:15:23.123"], blank: [""] };
    const definitions = [["exclusion", "exclusion record set", "String", "choice"], ["number", "Nullable number", "Integer", "number"], ["date", "Nullable date", "DateTime", "date"], ["blank", "Blank text", "String", "short"]];
    const options = [{ value: "none", label: "None" }, { value: "", label: "(Blank)" }, { value: "null", label: "null" }, { value: "None", label: "Literal None value" }];
    const posted = []; let submitted;
    const parameters = route => {
        const input = route.request().postDataJSON(); posted.push(input); const values = input.initial ? seeds : input.values;
        const fields = definitions.map(([name, prompt, type, width]) => ({ name, prompt, type, width, nullable: name !== "blank", allowBlank: type === "String", multiValue: false,
            hasOptions: name === "exclusion", optionCount: name === "exclusion" ? options.length : 0, optionsKey: "null-fixture", dependencies: [], selected: values[name],
            selectedOptions: (values[name] || []).map(value => ({ value, label: options.find(o => o.value === value)?.label ?? String(value ?? "No value") })), error: values[name]?.length ? null : "Choose or enter a required value." }));
        return route.fulfill({ json: { values, fields, canRun: fields.every(f => !f.error), defaulted: input.initial ? Object.keys(seeds) : input.defaulted } });
    };
    const choices = route => route.fulfill({ json: { optionsKey: "null-fixture", options } });
    const captureRun = route => { submitted = route.request().postDataJSON(); return route.fulfill({ status: 502, json: { message: "Synthetic execution boundary", trace: "typed-null" } }); };
    await page.route("**/parameters", parameters); await page.route("**/options/*", choices); await page.route("**/run", captureRun);
    try {
        await viewport(1440); await open("One Input");
        await shot("inline-nullable-desktop");
        for (const prompt of ["exclusion record set", "Nullable number", "Nullable date"]) {
            const control = page.getByLabel(prompt, { exact: true }), checkbox = page.getByRole("checkbox", { name: prompt + ": No value", exact: true });
            assert.equal(await checkbox.locator("..").textContent(), " No value");
            assert.equal(await checkbox.evaluate(n => n.closest(".parameter-label-row").nextElementSibling === document.getElementById(n.id.replace(/-null$/, ""))), true);
            assert.equal(await control.evaluate(n => n.labels.length === 1 && n.labels[0].id === n.id + "-label"), true);
            const label = await control.locator("..").locator("label[id]").boundingBox(), check = await checkbox.boundingBox(); assert.ok(Math.abs(label.y - check.y) < 7, JSON.stringify({ prompt, label, check }));
        }
        assert.equal(await page.locator(".parameter-field").last().locator(".parameter-label-row").count(), 0);
        assert.equal(await page.getByLabel("exclusion record set", { exact: true }).locator("span").first().textContent(), "None"); await shot("inline-nullable-desktop"); await scan("inline nullable label associations");
        for (const [name, prompt, value] of [["exclusion", "exclusion record set", '"none"'], ["number", "Nullable number", "15"], ["date", "Nullable date", "2026-11-01T10:12:34.567"]]) {
            const checkbox = page.getByRole("checkbox", { name: prompt + ": No value", exact: true }), control = page.getByLabel(prompt, { exact: true });
            await checkbox.check(); await inputsReady(); assert.deepEqual(posted.at(-1).values[name], [null]); assert.equal(await control.isDisabled(), true);
            await page.locator("#run").click(); await inputsReady(); assert.deepEqual(submitted.values[name], [null]); assert.deepEqual(submitted.values.blank, [""]); assert.ok(!submitted.defaulted.includes(name)); assert.ok(submitted.defaulted.includes("blank"));
            await checkbox.uncheck(); await inputsReady(); assert.deepEqual(posted.at(-1).values[name], []); assert.equal(await control.isEnabled(), true); assert.equal(await page.locator("#run").isDisabled(), true);
            if (name === "exclusion") { await selectSingle(prompt, "None"); } else { await control.fill(value); await control.press("Tab"); } await inputsReady();
        }
        for (const [label, expected] of [["(Blank)", ""], ["null", "null"], ["Literal None value", "None"], ["None", "none"]]) {
            await selectSingle("exclusion record set", label); await inputsReady(); assert.deepEqual(posted.at(-1).values.exclusion, [expected]); assert.equal(await page.getByRole("checkbox", { name: "exclusion record set: No value", exact: true }).isChecked(), false);
        }
        await viewport(320, 640); await noOverflow(); await shot("inline-nullable-mobile"); await scan("nullable responsive wrap");
    } finally { await page.unroute("**/parameters", parameters); await page.unroute("**/options/*", choices); await page.unroute("**/run", captureRun); }
    await viewport(1440);
    const compact = await require("./browser-parameter-panel.cjs")({ page, context, baseURL, scan, inputsReady });
    return { ...compact, adaptiveBreadcrumbWidth: true, fullPolarisHierarchy: true, noJavaScriptHierarchy: true, breadcrumbResizeAndKeyboard: true,
        bottomParameterActions: true, hiddenInvalidInputRecovery: true, compactFeedback: true, inlineTypedNullControls: true, nullBlankNoneDistinct: true };
};
