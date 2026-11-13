"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");

module.exports = async ({ page, context, baseURL, scan, inputsReady }) => {
    const { single } = require("./single-choice-helpers.cjs")(page, inputsReady);
    const catalog = await (await context.request.get("/api/catalog")).json();
    const open = async name => { await page.goto("/Report?id=" + catalog.find(e => e.name === name).id); await inputsReady(); };
    const toggle = () => page.locator("#toggle-parameters");
    const settle = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const samples = [];
    const sizes = [[1440, 900], [1024, 768], [768, 1024], [390, 844], [320, 640]];
    const separate = (a, b) => a.right <= b.x + 1 || b.right <= a.x + 1 || a.bottom <= b.y + 1 || b.bottom <= a.y + 1;
    const geometry = () => page.evaluate(() => {
        const rect = node => { const r = node.getBoundingClientRect(); return { x: r.x, y: r.y, right: r.right, bottom: r.bottom, width: r.width, height: r.height }; };
        const get = selector => rect(document.querySelector(selector));
        const section = document.querySelector(".parameter-section"), button = document.querySelector("#toggle-parameters");
        const tab = getComputedStyle(button, "::before"), toolbar = document.querySelector(".viewer-toolbar");
        const rootSize = parseFloat(getComputedStyle(document.documentElement).fontSize);
        const fields = [...document.querySelectorAll(".parameter-field")].map(n => ({ ...rect(n), preferred: parseFloat(getComputedStyle(n).getPropertyValue("--parameter-width")) * rootSize }));
        // The wrappers use display:contents so actions can share the final field
        // row. Measure rendered field boxes rather than their boxless wrappers.
        const controls = fields.length ? { x: Math.min(...fields.map(r => r.x)), y: Math.min(...fields.map(r => r.y)),
            right: Math.max(...fields.map(r => r.right)), bottom: Math.max(...fields.map(r => r.bottom)) } : { x: 0, y: 0, right: 0, bottom: 0 };
        return { header: get(".shell-header"), section: rect(section), inset: parseFloat(getComputedStyle(section).paddingRight),
            fields, availableWidth: get("#parameters").width,
            labelsFit: [...document.querySelectorAll(".parameter-field label[id]")].every(n => n.scrollWidth <= n.clientWidth + 1 && n.scrollHeight <= n.clientHeight + 1),
            controls, expanded: !document.querySelector("#parameter-controls").hidden, run: get("#run"), divider: get(".parameter-divider"), button: rect(button),
            tab: { width: parseFloat(tab.width), height: parseFloat(tab.height) }, chevron: getComputedStyle(button.firstElementChild).transform,
            toolbar: rect(toolbar), toolbarTopBorder: parseFloat(getComputedStyle(toolbar).borderTopWidth), frame: get("#report-frame"),
            output: !document.querySelector("#viewer").hidden, overflow: document.documentElement.scrollWidth > innerWidth + 1,
            outsideHit: Boolean(document.elementFromPoint(button.getBoundingClientRect().left - 2, button.getBoundingClientRect().top + 3)?.closest("#toggle-parameters")),
            coarse: matchMedia("(pointer:coarse)").matches, width: innerWidth, scrollY };
    });
    const check = async fixture => {
        await settle();
        const g = await geometry();
        assert.equal(await page.locator(".parameter-command-row,.parameter-heading,#parameter-toggle-label,.parameter-section h2").count(), 0);
        assert.equal(await page.getByRole("heading", { name: "Parameters", exact: true }).count(), 0);
        assert.equal(await page.getByRole("button", { name: /^(Hide|Show)$/ }).count(), 0);
        assert.equal(await toggle().textContent(), "", "Only the CSS chevron is visible.");
        assert.equal(await page.locator("#run").isVisible(), g.expanded);
        assert.equal(await page.locator("#run").isEnabled(), true);
        assert.equal(await page.locator("#run").evaluate(n => n.form?.id), "parameters", "View report retains native form submission.");
        assert.equal(await page.getByRole("region", { name: "Report parameters", exact: true }).count(), 1);
        assert.equal(g.overflow, false, JSON.stringify({ fixture, g }));
        if (g.expanded) { assert.equal(g.labelsFit, true, "Long labels wrap without clipping."); }
        assert.ok(g.section.y - g.header.bottom >= 2 && g.section.y - g.header.bottom <= 4, "Inputs start 2–4px below navigation.");
        if (g.expanded && g.fields.length) { assert.ok(Math.abs(g.fields[0].y - g.section.y) <= 1, "There is no blank heading row."); }
        if (g.expanded) { assert.ok(Math.abs(g.run.right - (g.section.right - g.inset)) <= 1, "View report stays at the right inset."); }
        else { assert.equal(g.section.height, 0, "Collapse reclaims the entire idle interface, including the action slot."); }
        for (const [i, field] of (g.expanded ? g.fields : []).entries()) {
            assert.ok(separate(field, g.run), "View report never overlaps a field.");
            for (const next of g.fields.slice(i + 1)) { assert.ok(separate(field, next), "Wrapped fields never overlap."); }
            if (g.width > 600) { assert.ok(Math.abs(field.width - Math.min(field.preferred, g.availableWidth)) <= 1, "Actions cannot narrow a field's preferred width."); }
        }
        if ((fixture === "one" || fixture === "several") && g.width === 1440) {
            assert.ok(g.run.x >= g.controls.right - 1, "Desktop actions are beside the inputs.");
            assert.ok(Math.abs(g.run.bottom - g.controls.bottom) <= 1, "Desktop actions align with input bottoms.");
        }
        if (fixture === "long-nullable" && g.width === 1440) {
            assert.ok(new Set(g.fields.map(f => f.y)).size > 1, "Exercise wrapped desktop fields.");
            assert.ok(Math.abs(g.run.bottom - g.controls.bottom) <= 1, "Use room on the final wrapped row instead of adding an action row.");
        }
        assert.ok(Math.abs(g.button.x + g.button.width / 2 - (g.divider.x + g.divider.width / 2)) <= .5, "Collapse stays centered.");
        assert.equal(g.button.width, 44);
        assert.equal(g.tab.width, 28); assert.equal(g.tab.height, 13, "The visible tab must remain compact.");
        assert.equal(g.button.height, g.width <= 600 || g.coarse ? 44 : 24);
        assert.equal(g.divider.height, g.button.height, "The separator reserves only the actual hit area.");
        assert.ok(g.button.y >= g.divider.y - 1 && g.button.bottom <= g.divider.bottom, "The hit area cannot cover toolbar controls.");
        assert.equal(g.outsideHit, false, "The rest of the separator is not clickable.");
        assert.ok(Math.abs(g.divider.width - g.frame.width) <= 1 || !g.output, "Divider and output retain full width.");
        if (g.output) {
            assert.ok(Math.abs(g.toolbar.y - g.divider.bottom) <= 1, "Toolbar immediately follows the separator.");
            assert.equal(g.toolbarTopBorder, 0, "There is no doubled top border.");
            assert.ok(Math.abs(g.frame.y - g.toolbar.bottom) <= 1);
        }
        samples.push({ fixture, ...g });
        return g;
    };
    const state = async expanded => {
        assert.equal(await toggle().getAttribute("aria-controls"), "parameters");
        assert.equal(await toggle().getAttribute("aria-expanded"), String(expanded));
        assert.equal(await toggle().getAttribute("aria-label"), expanded ? "Hide parameters" : "Show parameters");
        assert.equal(await page.locator("#parameter-controls").evaluate(n => n.hidden), !expanded);
        if (await page.locator(".parameter-field").count()) { assert.equal(await page.locator("#parameter-controls").isHidden(), !expanded); }
        assert.equal(await page.locator("#run").isHidden(), !expanded);
        assert.equal(await page.getByRole("button", { name: "View report", exact: true }).count(), expanded ? 1 : 0);
        if (!expanded) {
            assert.equal(await page.locator("#cancel-run,#dirty").evaluateAll(nodes => nodes.every(n => !n.checkVisibility())), true);
            assert.equal(await page.locator("#parameters,.report-actions").evaluateAll(nodes => nodes.every(n => n.getBoundingClientRect().height === 0)), true);
        }
        const transform = await page.locator(".parameter-chevron").evaluate(n => getComputedStyle(n).transform);
        // The two CSS borders meet at the chevron's top-right corner: its
        // transformed y direction is negative for up and positive for down.
        const matrix = transform.match(/matrix\(([^)]+)\)/)[1].split(",").map(Number);
        assert.equal(matrix[1] < 0, expanded, "Chevron direction follows the actual expanded state.");
    };

    const definitions = Array.from({ length: 12 }, (_, i) => ({ name: "field" + i,
        prompt: i < 4 ? ["Library", "Ratio limit", "Minimum holds", "Types of Material"][i] : "A long parameter label that must wrap completely beside its No value checkbox " + i,
        type: i === 0 || i === 3 ? "String" : "Integer", width: i === 0 ? "short" : i === 3 ? "choice" : "number",
        nullable: i >= 4, allowBlank: false, multiValue: i === 3, hasOptions: i === 0 || i === 3, optionCount: 2, optionsKey: "compact-fixture", dependencies: [],
        selected: [i === 0 || i === 3 ? "a" : "2"], selectedOptions: [{ value: "a", label: "A selected material" }] }));

    // Change only the validation response: viewport, values, labels, help and
    // preferred widths remain identical. Document coordinates exclude focus scroll.
    const validationSamples = [], validationMovement = [];
    const validationGeometry = () => page.evaluate(() => {
        const rect = n => { const r = n.getBoundingClientRect(); return { x: r.x + scrollX, y: r.y + scrollY, width: r.width, height: r.height, right: r.right + scrollX, bottom: r.bottom + scrollY }; };
        const get = s => rect(document.querySelector(s));
        return { run: get("#run"), actions: get(".report-actions"), section: get(".parameter-section"), divider: get(".parameter-divider"),
            fields: [...document.querySelectorAll(".parameter-field")].map(rect),
            inputs: [...document.querySelectorAll(".parameter-field>input,.parameter-field>select,.parameter-field>textarea,.parameter-field>.choice-trigger")].map(rect),
            errors: [...document.querySelectorAll("#parameters .field-error:not(:empty)")].map(n => ({ ...rect(n), id: n.id, readable: n.checkVisibility() && n.clientWidth > 0 && n.clientHeight > 0 && n.scrollWidth <= n.clientWidth + 1 && n.scrollHeight <= n.clientHeight + 1 })),
            toolbar: document.querySelector("#viewer").hidden ? null : get(".viewer-toolbar"),
            frame: document.querySelector("#viewer").hidden ? null : get("#report-frame"),
            overflow: document.documentElement.scrollWidth > innerWidth + 1 };
    });
    const compareValidation = (before, after, label) => {
        for (const name of ["run", "actions"]) {
            for (const key of ["x", "y", "width", "height"]) {
                if (Math.abs(after[name][key] - before[name][key]) > 1) { validationMovement.push({ label, box: name, key, before: before[name][key], after: after[name][key] }); }
            }
        }
        assert.equal(after.inputs.length, before.inputs.length, label + " all controls remain mounted");
        after.inputs.forEach((input, i) => {
            for (const key of ["x", "y", "width", "height"]) {
                if (Math.abs(input[key] - before.inputs[i][key]) > 1) { validationMovement.push({ label, box: "input " + i, key, before: before.inputs[i][key], after: input[key] }); }
            }
            assert.ok(separate(input, after.actions), label + " actions cannot overlap inputs");
        });
        assert.equal(after.overflow, false, label + " horizontal overflow");
        for (const [i, error] of after.errors.entries()) {
            assert.equal(error.readable, true, label + " complete error text wraps without clipping");
            assert.ok(separate(error, after.actions), label + " errors cannot overlap actions");
            assert.ok(error.bottom <= after.divider.y + 1, label + " errors precede the divider");
            for (const input of after.inputs) { assert.ok(separate(error, input), label + " errors cannot overlap inputs"); }
            for (const other of after.errors.slice(i + 1)) { assert.ok(separate(error, other), label + " errors cannot overlap each other"); }
        }
        if (after.toolbar) {
            assert.ok(Math.abs(after.toolbar.y - after.divider.bottom) <= 1);
            assert.ok(Math.abs(after.frame.y - after.toolbar.bottom) <= 1);
            assert.ok(Math.abs(after.frame.width - before.frame.width) <= 1);
            assert.ok(Math.abs(after.divider.y - before.divider.y - after.section.height + before.section.height) <= 1, "Only validation feedback changes the output's document position.");
        }
        validationSamples.push({ label, ...after });
    };
    const enlargedValidationText = async route => {
        const response = await route.fetch(); await route.fulfill({ response, body: (await response.text()).replace("font-size:16px", "font-size:32px") });
    };
    for (const scale of [1, 2]) {
        if (scale === 2) { await page.route("**/css/site.css*", enlargedValidationText); }
        try {
            for (const fixture of ["one", "several", "wrapped", "mixed"]) {
                const fields = Array.from({ length: fixture === "several" ? 4 : 12 }, (_, i) => ({
                    name: "validation" + i, prompt: "Validation input " + i, type: "Integer", width: "number", nullable: false, allowBlank: false,
                    multiValue: false, hasOptions: false, optionCount: 0, optionsKey: "validation-fixture", dependencies: [], selected: ["2"], selectedOptions: []
                }));
                if (fixture === "mixed") {
                    Object.assign(fields[1], { prompt: "A long nullable date label that must remain readable", type: "DateTime", width: "date", nullable: true, selected: ["2026-10-30T09:15:23.123"] });
                    Object.assign(fields[2], { prompt: "Required single choice", type: "String", width: "choice", hasOptions: true, optionCount: 2, dependencies: [fields[0].name], selected: ["a"], selectedOptions: [{ value: "a", label: "Choice A" }] });
                    Object.assign(fields[3], { prompt: "Required multiple choices", type: "String", width: "choice", hasOptions: true, multiValue: true, optionCount: 2, dependencies: [fields[2].name], selected: ["a"], selectedOptions: [{ value: "a", label: "Choice A" }] });
                    Object.assign(fields[4], { prompt: "Values with help text", type: "String", width: "text", multiValue: true, selected: ["a"] });
                    Object.assign(fields[5], { prompt: "Precise date with help text", type: "DateTime", width: "date", nullable: true, selected: ["2026-10-30T09:15:23.1234567"] });
                }
                let errorIndexes = [], errorText = "Choose a required value.";
                const parameters = async route => {
                    const response = fixture === "one" ? await route.fetch() : null;
                    const json = response ? await response.json() : { fields: structuredClone(fields), values: Object.fromEntries(fields.map(f => [f.name, f.selected])), defaulted: fields.map(f => f.name) };
                    json.fields.forEach((f, i) => { f.error = errorIndexes.includes(i) ? errorText : null; });
                    json.canRun = errorIndexes.length === 0;
                    await route.fulfill({ ...(response ? { response } : {}), json });
                };
                await page.route("**/parameters", parameters);
                try {
                    for (const [width, height] of scale === 1 ? sizes : [sizes[0], sizes[3], sizes[4]]) {
                        errorIndexes = []; await page.setViewportSize({ width, height }); await open("One Input");
                        assert.equal(await page.locator("html").evaluate(n => getComputedStyle(n).fontSize), scale === 1 ? "16px" : "32px");
                        if (fixture === "one") {
                            await page.locator("#parameters").evaluate(n => n.requestSubmit());
                            await page.waitForFunction(() => Boolean(history.state?.ssrsReport?.token) && !document.querySelector("#run").disabled);
                        }
                        const label = `${fixture} ${width} text ${scale * 100}%`, before = await validationGeometry();
                        validationSamples.push({ label: label + " valid", ...before });
                        const capture = suffix => page.screenshot({ path: `artifacts/browser/compact-validation-${fixture}-${width}-${scale}-${suffix}.png`, fullPage: true });
                        await capture("normal");
                        const refreshValidation = async () => {
                            await page.locator("#param-0").dispatchEvent("change"); await inputsReady(); await settle();
                        };
                        const count = before.inputs.length;
                        for (const [state, indexes, message] of [
                            ["first", [0], "Choose a required value."],
                            ["middle", [Math.floor(count / 2)], "Choose a current authorized value before running this report. The complete validation message must wrap naturally, remain associated with its parameter and never move another input or the report actions. Reference: synthetic-validation."],
                            ["last", [count - 1], "Choose a required value."],
                            ["all", Array.from({ length: count }, (_, i) => i), "Choose a current authorized value. This longer validation message must remain readable at narrow widths and 200% text size. Reference: synthetic-validation."]
                        ]) {
                            errorIndexes = indexes; errorText = message;
                            await page.locator("#param-" + indexes[0]).focus(); await refreshValidation();
                            const invalid = await validationGeometry(); assert.equal(invalid.errors.length, indexes.length);
                            assert.ok(invalid.section.height > before.section.height, "Visible validation is allowed to grow the section.");
                            compareValidation(before, invalid, label + " " + state);
                            for (const i of indexes) {
                                const input = page.locator("#param-" + i);
                                assert.equal(await input.getAttribute("aria-invalid"), "true");
                                assert.ok((await input.getAttribute("aria-describedby")).split(" ").includes("param-" + i + "-error"));
                                assert.ok((await page.locator("#param-" + i + "-error").textContent()).includes(message));
                            }
                            assert.equal(await page.locator("#param-" + indexes[0]).evaluate(n => n === document.activeElement), true, "Refresh preserves focus on the invalid control.");
                            if (state === "all") {
                                await capture("invalid"); await scan(label + " validation");
                                const values = await page.locator("#fields input,#fields select,#fields textarea").evaluateAll(nodes => nodes.map(n => ({ id: n.id, value: n.value, checked: n.checked, disabled: n.disabled })));
                                await toggle().click();
                                assert.equal(await page.locator("#parameters").evaluate(n => n.getBoundingClientRect().height), 0, "Invalid collapse still removes the complete form.");
                                assert.equal(await page.locator("#parameters .field-error").evaluateAll(nodes => nodes.every(n => !n.checkVisibility())), true, "Collapsed errors stay inside the hidden parameter wrapper.");
                                await toggle().click();
                                compareValidation(before, await validationGeometry(), label + " invalid reexpanded");
                                assert.deepEqual(await page.locator("#fields input,#fields select,#fields textarea").evaluateAll(nodes => nodes.map(n => ({ id: n.id, value: n.value, checked: n.checked, disabled: n.disabled }))), values);
                            }
                            errorIndexes = []; await refreshValidation();
                            const corrected = await validationGeometry(); assert.equal(corrected.errors.length, 0);
                            compareValidation(before, corrected, label + " corrected " + state);
                            assert.ok(Math.abs(corrected.section.height - before.section.height) <= 1, "No empty validation area remains.");
                        }
                        await capture("corrected");
                        await toggle().click(); assert.equal((await validationGeometry()).section.height, 0); await capture("collapsed");
                        await toggle().click(); compareValidation(before, await validationGeometry(), label + " reexpanded");
                        if (fixture === "one") { await page.locator("#cancel-session").click(); await page.waitForURL(/\/\?folder=/); }
                    }
                } finally { await page.unroute("**/parameters", parameters); }
            }
        } finally { if (scale === 2) { await page.unroute("**/css/site.css*", enlargedValidationText); } }
    }
    await fs.writeFile("artifacts/browser/parameter-validation-verification.json", JSON.stringify({ tolerancePx: 1, validationSamples, validationMovement }, null, 2));
    assert.equal(validationMovement.length, 0, "Validation alone must preserve action and input geometry: " + JSON.stringify(validationMovement.slice(0, 12)));

    for (const fixture of ["one", "several", "many", "long-nullable", "none"]) {
        const parameters = route => {
            const fields = definitions.slice(0, fixture === "several" ? 4 : 12);
            return route.fulfill({ json: { fields, values: Object.fromEntries(fields.map(f => [f.name, f.selected])), defaulted: fields.map(f => f.name), canRun: true } });
        };
        const synthetic = ["several", "long-nullable"].includes(fixture);
        if (synthetic) { await page.route("**/parameters", parameters); }
        try {
            for (const [width, height] of sizes) {
                await page.setViewportSize({ width, height }); await open(fixture === "many" ? "Default Showcase" : fixture === "none" ? "Details" : "One Input");
                await state(true); const before = await check(fixture);
                await page.screenshot({ path: "artifacts/browser/compact-" + fixture + "-" + width + ".png", fullPage: true });
                await scan("compact parameters " + fixture + " " + width);
                const values = await page.locator("#fields input,#fields select").evaluateAll(nodes => nodes.map(n => ({ value: n.value, checked: n.checked, disabled: n.disabled })));
                await toggle().click(); await state(false); const collapsed = await geometry();
                assert.equal(collapsed.button.x, before.button.x, "Collapse never moves the tab horizontally.");
                assert.equal(collapsed.section.height, 0, "No empty action row or reserved Cancel slot remains.");
                assert.ok(Math.abs(before.divider.y + before.scrollY - collapsed.divider.y - collapsed.scrollY - before.section.height) <= 1, "The divider reclaims the complete parameter interface.");
                await toggle().press("Space"); await state(true);
                assert.deepEqual(await page.locator("#fields input,#fields select").evaluateAll(nodes => nodes.map(n => ({ value: n.value, checked: n.checked, disabled: n.disabled }))), values);
            }
        } finally { if (synthetic) { await page.unroute("**/parameters", parameters); } }
    }

    await page.setViewportSize({ width: 1440, height: 900 }); await open("One Input");
    const number = page.getByLabel("OrganizationID", { exact: true });
    await number.focus(); await number.press("Tab"); assert.equal(await page.locator("#run").evaluate(n => n === document.activeElement), true);
    await page.keyboard.press("Tab"); assert.equal(await toggle().evaluate(n => n === document.activeElement), true);
    assert.deepEqual(await toggle().evaluate(n => { const s = getComputedStyle(n); return [s.outlineStyle, s.outlineWidth, s.outlineOffset]; }), ["solid", "3px", "3px"]);
    await toggle().press("Shift+Tab"); assert.equal(await page.locator("#run").evaluate(n => n === document.activeElement), true);
    await page.keyboard.press("Tab");
    for (let i = 0; i < 3; i++) { await toggle().press("Enter"); await state(false); await toggle().press("Space"); await state(true); }
    await number.focus(); await toggle().evaluate(n => n.click()); await state(false);
    assert.equal(await toggle().evaluate(n => n === document.activeElement), true, "Collapsing focused fields transfers focus safely.");
    await toggle().press("Enter");
    await page.locator("#run").focus(); await toggle().evaluate(n => n.click()); await state(false);
    assert.equal(await toggle().evaluate(n => n === document.activeElement), true, "Collapsing focused View report transfers focus before it is hidden.");
    await page.keyboard.press("Tab");
    assert.equal(await page.locator("#parameters").evaluate(n => n.contains(document.activeElement)), false, "Tab skips every collapsed form control.");
    await toggle().focus(); await toggle().press("Enter");
    // Native Enter submission must still reach the form's actual submit button.
    await number.press("Enter");
    await page.waitForFunction(() => Boolean(history.state?.ssrsReport?.token) && !document.querySelector("#run").disabled);
    for (const [width, height] of sizes) {
        await page.setViewportSize({ width, height }); const expanded = await check("executed");
        await toggle().click(); await state(false); const collapsed = await check("executed-collapsed");
        const reclaimed = expanded.section.height - collapsed.section.height;
        assert.ok(reclaimed > 16);
        assert.ok(Math.abs(expanded.frame.y + expanded.scrollY - collapsed.frame.y - collapsed.scrollY - reclaimed) <= 1, "Output rises by exactly the reclaimed parameter height in document coordinates.");
        assert.equal(collapsed.frame.width, expanded.frame.width);
        await page.screenshot({ path: "artifacts/browser/compact-executed-collapsed-" + width + ".png", fullPage: true }); await scan("compact collapsed output " + width);
        await toggle().click(); await state(true);
    }
    await page.locator("#cancel-session").click(); await page.waitForURL(/\/\?folder=/);

    // Neither layering direction is sufficient: the popup and tab must occupy
    // separate rectangles so options and the entire hit target remain usable.
    for (const [width, height] of sizes) {
        await page.setViewportSize({ width, height }); await open("Expandable Groups");
        const behavior = single("Behavior"); await behavior.locator(".single-trigger").click();
        const option = behavior.getByRole("option", { name: "Lost toggle reply", exact: true }); await option.waitFor();
        assert.equal(await behavior.locator(".choice-popup").evaluate(n => {
            const a = n.getBoundingClientRect();
            return [...document.querySelectorAll("#toggle-parameters,#run,#cancel-run")].filter(b => b.checkVisibility()).every(node => {
                const b = node.getBoundingClientRect();
                return a.right <= b.left || b.right <= a.left || a.bottom <= b.top || b.bottom <= a.top;
            });
        }), true, "Choice popups cannot cover report actions or the collapse hit area.");
        await option.click(); await inputsReady();
        assert.equal(await behavior.locator(".single-trigger span").first().textContent(), "Lost toggle reply");
    }

    let resolutions = 0, runs = 0;
    const count = request => { if (/\/parameters$/.test(request.url())) { resolutions++; } if (/\/run$/.test(request.url())) { runs++; } };
    page.on("request", count);
    try {
        await page.setViewportSize({ width: 1440, height: 900 }); await open("Check Ins by Day of Week by Hour");
        const branches = page.locator(".multi-select").filter({ has: page.locator("label[id]").filter({ hasText: /^Branches$/ }) });
        const branchTrigger = branches.locator(".multi-trigger");
        await branchTrigger.click(); await branches.locator("input[data-option]").last().waitFor();
        assert.equal(await toggle().evaluate(n => { const r = n.getBoundingClientRect(); return n.contains(document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2)); }), true, "Open choices cannot cover the collapse target.");
        const unedited = resolutions;
        await toggle().click(); await state(false); assert.equal(await branches.locator(".choice-popup").isHidden(), true);
        assert.equal(resolutions, unedited, "An unedited dropdown closes without resolution or execution.");
        await toggle().click(); await branchTrigger.click(); await branches.locator("input[data-option]").last().waitFor();
        const all = branches.getByRole("checkbox", { name: "(Select All)", exact: true });
        const cascadeBefore = await validationGeometry();
        await all.check(); await all.uncheck();
        await toggle().click(); await inputsReady(); await state(true);
        assert.equal(await branchTrigger.getAttribute("aria-invalid"), "true", "A required empty selection reopens the panel for correction.");
        assert.equal(await branchTrigger.evaluate(n => n === document.activeElement), true, "Validation focuses the newly visible invalid field.");
        compareValidation(cascadeBefore, await validationGeometry(), "required multiselect cascade error");
        await branchTrigger.click(); await branches.locator("input[data-option]").last().waitFor();
        await branches.getByRole("checkbox", { name: "Synthetic branch 001", exact: true }).check();
        await toggle().click(); await inputsReady(); await state(false);
        assert.equal(resolutions, unedited + 2, "Empty and corrected selections each resolve exactly once when the toggle dismisses a dropdown.");
        assert.equal(runs, 0); await toggle().click();
        assert.equal(await branchTrigger.locator("span").first().textContent(), "Synthetic branch 001");
        compareValidation(cascadeBefore, await validationGeometry(), "successful cascade refresh clears error");
        const departmentTrigger = single("Department").locator(".single-trigger");
        assert.equal(await departmentTrigger.locator("span").first().textContent(), "Team for branch 1");
        await departmentTrigger.click();
        await page.getByRole("searchbox", { name: "Search Department", exact: true }).fill("Shared");
        const singleBefore = resolutions; await toggle().click(); await state(false);
        assert.equal(await page.getByRole("listbox", { name: "Department choices", exact: true }).isHidden(), true);
        assert.equal(resolutions, singleBefore, "Searching a single choice then collapsing cannot commit it.");
        assert.equal(await toggle().evaluate(n => n === document.activeElement), true);
    } finally { page.off("request", count); }

    // A desktop-sized touchscreen must also receive the coarse-pointer target.
    const touch = await context.browser().newContext({ baseURL, ignoreHTTPSErrors: true, hasTouch: true, storageState: await context.storageState() });
    try {
        const touchPage = await touch.newPage();
        for (const [width, height] of [[1440, 900], [390, 844], [320, 640]]) {
            await touchPage.setViewportSize({ width, height }); await touchPage.goto("/Report?id=" + catalog.find(e => e.name === "One Input").id);
            await touchPage.waitForFunction(() => document.querySelector("#parameters").getAttribute("aria-busy") === "false");
            const button = touchPage.getByRole("button", { name: "Hide parameters", exact: true }), box = await button.boundingBox();
            assert.ok(box.width >= 44 && box.height >= 44);
            await button.tap(); assert.equal(await touchPage.locator("#parameter-controls").isHidden(), true);
            await touchPage.getByRole("button", { name: "Show parameters", exact: true }).tap();
            assert.equal(await touchPage.locator("#parameter-controls").isVisible(), true);
        }
    } finally { await touch.close(); }
    await page.setViewportSize({ width: 1440, height: 900 });
    await fs.writeFile("artifacts/browser/parameter-validation-verification.json", JSON.stringify({ tolerancePx: 1, validationSamples, validationMovement }, null, 2));
    assert.equal(validationMovement.length, 0, "Real cascading validation preserves action and input geometry: " + JSON.stringify(validationMovement.slice(0, 12)));
    await fs.writeFile("artifacts/browser/compact-parameter-verification.json", JSON.stringify({ samples, keyboardCycles: 3, touchViewports: [1440, 390, 320], dropdownResolutionCount: 2 }, null, 2));
    return { compactParameterPanel: true, compactGeometryCases: samples.length, compactViewports: sizes.map(([width]) => width),
        compactKeyboardAndTouch: true, parameterEnterSubmission: true, collapsedOutputGeometry: true, separatorDropdownDismissal: true,
        validationGeometryChecks: validationSamples.length, validationTextScales: [100, 200] };
};
