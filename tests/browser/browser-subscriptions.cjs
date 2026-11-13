"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");

module.exports = async ({ page, context, browser, baseURL, scan, inputsReady }) => {
    const catalog = await (await context.request.get("/api/catalog")).json();
    const reportUrl = name => catalog.find(item => item.name === name).reportUrl;
    const dialog = page.locator("#subscription-editor");
    const settled = async () => { await page.waitForFunction(() => document.querySelector("#subscription-parameter-host").getAttribute("aria-busy") === "false"); await page.locator("#subscription-save:not([disabled])").waitFor(); };
    const open = async () => { await page.locator("#subscribe").click(); await page.locator("#subscription-editor[open]").waitFor(); await settled(); };
    const group = prompt => dialog.locator(".parameter-field").filter({ has: page.getByLabel("Use default for " + prompt, { exact: true }) });
    const explicit = async prompt => { await dialog.getByLabel("Use default for " + prompt, { exact: true }).uncheck(); await settled(); };
    const localPast = () => {
        const value = new Date(Date.now() - 10 * 60 * 1000);
        const parts = Object.fromEntries(new Intl.DateTimeFormat("sv-SE", { timeZone: "America/New_York", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).formatToParts(value).map(part => [part.type, part.value]));
        return `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}`;
    };
    const save = async () => { await page.locator("#subscription-save").click(); await dialog.waitFor({ state: "hidden" }); await inputsReady(); };
    const dispatch = async outcome => {
        const csrf = await (await context.request.get("/api/csrf")).json();
        const response = await context.request.post("/api/subscriptions/synthetic-dispatch", { headers: { RequestVerificationToken: csrf.token }, data: { outcome: outcome || "Accepted" } });
        assert.equal(response.status(), 200, await response.text()); return response.json();
    };
    await require("./subscription-divider-assertions.cjs")({ browser, context, baseURL, reportUrl: reportUrl("One Input") });
    await fs.mkdir("artifacts/browser", { recursive: true });
    await page.goto(reportUrl("Check Ins by Day of Week by Hour")); await inputsReady();
    const originalDate = await page.getByLabel("beginDate", { exact: true }).inputValue();
    await open();
    assert.equal(await dialog.getByLabel("Attachment format", { exact: true }).inputValue(), "pdf");
    const defaults = dialog.locator(".subscription-default input"); assert.ok(await defaults.count() >= 5);
    assert.equal(await defaults.count(), await defaults.filter({ visible: true }).count());
    for (const checkbox of await defaults.all()) { assert.equal(await checkbox.isChecked(), true); }
    await scan("Subscription creation, default inputs");
    await page.locator("#subscription-name").fill("Branches delivered");
    await page.locator("#subscription-to").fill("one@example.invalid; two@example.invalid");
    await page.locator("#subscription-cc").fill("ONE@example.invalid; copy@example.invalid");
    await page.locator("#subscription-bcc").fill("blind@example.invalid");
    await page.locator("#subscription-delivery").selectOption("both");
    await page.locator("#schedule-start").fill(localPast()); await page.locator("#schedule-start").blur();
    await explicit("beginDate");
    await group("beginDate").locator("input[type=datetime-local]").fill("2026-11-01T00:00"); await page.locator("#subscription-name").click(); await settled();
    await dialog.getByLabel("Use default for beginDate", { exact: true }).check(); await settled();
    assert.equal(await group("beginDate").locator("input[type=datetime-local]").isDisabled(), true);
    await explicit("beginDate");
    assert.match(await group("beginDate").locator("input[type=datetime-local]").inputValue(), /^2026-11-01T00:00/);
    await explicit("Branches");
    await group("Branches").locator(".choice-trigger").click();
    const popup = group("Branches").locator(".choice-popup"); await popup.getByLabel("Synthetic branch 002", { exact: true }).check();
    await popup.getByLabel("Synthetic branch 080", { exact: true }).uncheck(); await popup.getByLabel("Synthetic branch 152", { exact: true }).uncheck();
    await page.locator("#subscription-name").click(); await settled();
    await page.locator("#schedule-kind").selectOption("weekly"); await page.locator("#schedule-every").fill("2"); await page.locator("#schedule-every").blur();
    await page.getByLabel("Wed", { exact: true }).check();
    await page.waitForFunction(() => document.querySelector("#schedule-summary").textContent.includes("Every 2 weeks on Mon, Wed") && document.querySelector("#schedule-summary").textContent.includes("Next:"));
    assert.match(await page.locator("#schedule-summary").innerText(), /Every 2 weeks on Mon, Wed/);
    await page.locator("#schedule-kind").selectOption("once");
    await page.waitForFunction(() => document.querySelector("#schedule-summary").textContent.includes("Next:"));
    await scan("Subscription explicit/cascading native widgets");
    await page.screenshot({ path: "artifacts/browser/subscription-editor-desktop.png", fullPage: true });
    // A known validation response must leave the editor usable and permit retry.
    await page.route("**/api/subscriptions", async route => {
        if (route.request().method() === "POST") { await route.fulfill({ status: 400, contentType: "application/json", body: JSON.stringify({ message: "Synthetic validation: review recipients." }) }); }
        else { await route.continue(); }
    });
    await page.locator("#subscription-save").click(); await page.getByText("Synthetic validation: review recipients.", { exact: true }).waitFor();
    await scan("Subscription validation recovery"); await page.unroute("**/api/subscriptions");
    await save();
    assert.equal(await page.getByLabel("beginDate", { exact: true }).inputValue(), originalDate, "Saving restores original report drafts");
    const definitions = await (await context.request.get("/api/subscriptions")).json(); assert.equal(definitions.length, 1);
    const created = definitions[0]; assert.equal(created.definition.delivery, "both");
    assert.deepEqual(created.definition.parameters.branch, { useDefault: false, values: ["1", "2"] });
    assert.equal(created.definition.parameters.endDate.useDefault, true); assert.deepEqual(created.definition.parameters.endDate.values, []);
    assert.equal(created.definition.recipients.cc.length, 1); assert.equal(created.definition.recipients.bcc[0], "blind@example.invalid");
    const sent = await dispatch(); assert.equal(sent.messages, 1); assert.ok(sent.lastLink.startsWith(baseURL + reportUrl("Check Ins by Day of Week by Hour") + "?"));
    assert.equal((await dispatch()).messages, 1, "Repeated dispatch cannot resend");
    const history = await (await context.request.get(`/api/subscriptions/${created.id}/history`)).json(); assert.equal(history[0].status, "Delivered"); assert.ok(history[0].providerMessageId);
    // A fresh authenticated browser follows the exact effective-parameter email link.
    const fresh = await browser.newContext({ baseURL, ignoreHTTPSErrors: true });
    try {
        const recipient = await fresh.newPage(); await recipient.goto(sent.lastLink); await recipient.waitForURL(/TestLogin/);
        await recipient.getByLabel("Test identity").selectOption("staff-two"); await recipient.getByRole("button", { name: "Sign in to demonstration" }).click();
        await recipient.goto(sent.lastLink); await recipient.locator("#viewer:not([hidden])").waitFor();
        assert.match(await recipient.getByLabel("beginDate", { exact: true }).inputValue(), /^2026-11-01T00:00/);
        const csrf = await (await fresh.request.get("/api/csrf")).json();
        assert.equal((await fresh.request.get(`/api/subscriptions/${created.id}`)).status(), 404);
        const other = await fresh.request.post("/api/subscriptions/synthetic-dispatch", { headers: { RequestVerificationToken: csrf.token }, data: { outcome: "Accepted" } });
        assert.equal((await other.json()).messages, 0, "Synthetic dispatch also scopes provider evidence to the current owner");
    } finally { await fresh.close(); }
    await page.getByRole("link", { name: "My subscriptions", exact: true }).first().click(); await page.waitForURL("**/subscriptions");
    const row = () => page.locator(`tr[data-id="${created.id}"]`);
    await row().getByRole("button", { name: "History for Branches delivered", exact: true }).click();
    await page.getByText("Accepted by Postmark", { exact: true }).last().waitFor(); await scan("Subscription execution history");
    await page.locator("#history-close").click();
    await row().getByRole("link", { name: "Edit Branches delivered", exact: true }).click(); await page.locator("#subscription-editor[open]").waitFor(); await settled();
    assert.match(await page.locator("#schedule-summary").innerText(), /No remaining occurrences/);
    assert.equal(await dialog.getByLabel("Use default for endDate", { exact: true }).isChecked(), true);
    assert.equal(await dialog.getByLabel("Use default for beginDate", { exact: true }).isChecked(), false);
    await page.locator("#subscription-name").fill("Branches edited"); await page.locator("#subscription-format").selectOption("xlsx");
    await page.locator("#subscription-to").fill("changed@example.invalid"); await page.locator("#schedule-kind").selectOption("monthWeekday");
    await page.locator("#schedule-week").selectOption("2"); await page.locator("#schedule-weekday").selectOption("2");
    await page.locator("#schedule-zone").selectOption(await page.locator('#schedule-zone option[value="America/Chicago"]').count() ? "America/Chicago" : "Central Standard Time");
    await page.waitForFunction(() => document.querySelector("#schedule-summary").textContent.includes("second Tue") && document.querySelector("#schedule-summary").textContent.includes("Next:"));
    await page.setViewportSize({ width: 390, height: 844 }); await scan("Subscription editor mobile");
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await page.screenshot({ path: "artifacts/browser/subscription-editor-mobile.png", fullPage: true });
    await page.locator("#subscription-save").scrollIntoViewIfNeeded(); await page.screenshot({ path: "artifacts/browser/subscription-editor-mobile-inputs.png", fullPage: true });
    await Promise.all([page.waitForURL("**/subscriptions"), page.locator("#subscription-save").click()]); await page.getByText("Branches edited", { exact: true }).waitFor();
    assert.equal(await row().locator("td").evaluateAll(cells => cells.every(cell => cell.getBoundingClientRect().width > 250)), true, "Mobile subscription cells use readable stacked widths");
    await scan("My subscriptions mobile"); await page.screenshot({ path: "artifacts/browser/subscriptions-mobile.png", fullPage: true });
    await row().getByRole("button", { name: "Pause Branches edited", exact: true }).click(); await row().getByText("Paused", { exact: true }).waitFor();
    await page.locator("#subscriptions-state").selectOption("enabled"); assert.equal(await row().count(), 0);
    await page.locator("#subscriptions-state").selectOption("paused"); await row().getByRole("button", { name: "Resume Branches edited", exact: true }).click();
    await page.locator("#subscriptions-state").selectOption("all"); await row().getByText("Enabled", { exact: true }).waitFor();
    await page.locator("#subscriptions-filter").fill("not present"); await page.getByText("No subscriptions match this filter.", { exact: true }).waitFor();
    await page.locator("#subscriptions-filter").fill("changed@example.invalid"); assert.equal(await row().count(), 1); await page.locator("#subscriptions-sort").selectOption("name");
    assert.equal((await (await context.request.get(`/api/subscriptions/${created.id}/history`)).json()).length, 1, "Editing and pause/resume do not create duplicate sends");
    await page.setViewportSize({ width: 1280, height: 900 }); await scan("My subscriptions sorting and actions");
    await page.screenshot({ path: "artifacts/browser/subscriptions-desktop.png", fullPage: true });
    page.once("dialog", confirmation => confirmation.accept()); await row().getByRole("button", { name: "Delete Branches edited", exact: true }).click();
    await page.getByText("Subscription deleted.", { exact: true }).waitFor(); assert.equal((await (await context.request.get("/api/subscriptions")).json()).length, 0);
    // Cancel restores drafts and the same widgets remain usable for manual execution.
    await page.goto(reportUrl("One Input")); await inputsReady(); const initial = await page.getByLabel("OrganizationID", { exact: true }).inputValue();
    await open(); await explicit("OrganizationID"); await group("OrganizationID").locator("input[type=number]").fill("33"); await page.locator("#subscription-name").click(); await settled();
    await page.locator("#subscription-cancel").click(); await dialog.waitFor({ state: "hidden" }); await inputsReady();
    assert.equal(await page.getByLabel("OrganizationID", { exact: true }).inputValue(), initial);
    await page.locator("#run").click(); await page.locator("#viewer:not([hidden])").waitFor(); await scan("Report execution after cancelled subscription");
    const recovery = await require("./browser-subscription-recovery.cjs")({ page, context, baseURL, scan, inputsReady, definition: created.definition, localPast });
    return { dividerLayouts: 6, subscriptionExecutions: 1, attachmentFormats: ["PDF", "XLSX edit"], browserCoverage: "create/defaults/explicit/cascade/link/fresh-recipient/history/edit/monthly/timezone/pause/resume/filter/delete/cancel", screenshots: 5, ...recovery };
};
