"use strict";
const assert = require("node:assert/strict");

module.exports = async ({ page, context, scan, inputsReady, definition, localPast }) => {
    const csrf = (await (await context.request.get("/api/csrf")).json()).token;
    const create = async (name, schedule) => {
        const response = await context.request.post("/api/subscriptions", { headers: { RequestVerificationToken: csrf }, data: { definition: { ...definition, name, schedule: schedule || { kind: "once", startLocal: localPast() + ":00" } } } });
        assert.equal(response.status(), 200, await response.text()); return response.json();
    };
    const first = await create("Recovery A"), second = await create("Recovery B");
    const dispatch = await context.request.post("/api/subscriptions/synthetic-dispatch", { headers: { RequestVerificationToken: csrf }, data: { outcome: "Accepted" } }); assert.equal(dispatch.status(), 200);
    const row = item => page.locator(`tr[data-id="${item.id}"]`);
    const dialog = page.locator("#subscription-editor");
    const editUrl = item => "/report/" + item.definition.reportPath.slice(1).split("/").map(encodeURIComponent).join("/") + "?portal%3AnoExecute=true#subscription=" + item.id;
    const list = async () => { await page.goto("/subscriptions"); await row(first).waitFor(); };
    const deferred = () => {
        let resolve;
        const promise = new Promise((done, fail) => {
            const deadline = setTimeout(() => fail(new Error("Subscription recovery request gate timed out.")), 15000);
            resolve = value => { clearTimeout(deadline); done(value); };
        });
        promise.catch(() => {}); return { promise, resolve };
    };
    console.log("Subscription recovery: history isolation");
    await list();
    // A closed history request cannot inject its rows into another open history.
    const historyEntered = deferred(), historyRelease = deferred(), historyFinished = deferred();
    const historyPattern = `**/api/subscriptions/${first.id}/history`;
    await page.route(historyPattern, async route => {
        historyEntered.resolve(); await historyRelease.promise;
        try { await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify([{ occurrenceUtc: new Date().toISOString(), status: "Failed", attempts: 1, diagnostic: "stale-history-A" }]) }); }
        catch { /* Browser cancellation is expected. */ } finally { historyFinished.resolve(); }
    });
    await row(first).getByRole("button", { name: "History for Recovery A" }).click(); await historyEntered.promise;
    await page.locator("#history-close").click(); await row(second).getByRole("button", { name: "History for Recovery B" }).click();
    await page.locator("#history-rows").getByText("Accepted by Postmark", { exact: true }).waitFor();
    historyRelease.resolve(); await historyFinished.promise;
    assert.equal(await page.locator("#history-rows").getByText("stale-history-A", { exact: true }).count(), 0);
    await page.locator("#history-close").click(); await page.unroute(historyPattern);
    // A pre-mutation GET must not overwrite the final pause/delete result.
    console.log("Subscription recovery: stale list/mutation ordering");
    const staleList = async action => {
        const entered = deferred(), release = deferred(); let initial = true;
        await page.route("**/api/subscriptions", async route => {
            if (route.request().method() !== "GET" || !initial) { await route.continue(); return; }
            initial = false; const response = await route.fetch(); entered.resolve(); await release.promise; await route.fulfill({ response });
        });
        await page.locator("#subscriptions-reload").click(); await entered.promise; await action(); release.resolve();
    };
    await staleList(async () => { await row(first).getByRole("button", { name: "Pause Recovery A", exact: true }).click(); });
    await row(first).getByRole("button", { name: "Resume Recovery A", exact: true }).waitFor();
    assert.equal((await (await context.request.get(`/api/subscriptions/${first.id}`)).json()).enabled, false); await page.unroute("**/api/subscriptions");
    await staleList(async () => { page.once("dialog", prompt => prompt.accept()); await row(second).getByRole("button", { name: "Delete Recovery B", exact: true }).click(); });
    await page.getByText("Subscription deleted.", { exact: true }).waitFor(); assert.equal(await row(second).count(), 0); await page.unroute("**/api/subscriptions");
    // Closing during a delayed details GET is prompt; late results stay discarded.
    console.log("Subscription recovery: cancel details load");
    const detailEntered = deferred(), detailRelease = deferred(), detailFinished = deferred();
    const detailPattern = `**/api/subscriptions/${first.id}`;
    await page.route(detailPattern, async route => {
        const response = await route.fetch(); detailEntered.resolve(); await detailRelease.promise;
        try { await route.fulfill({ response }); } catch { /* Cancelled open. */ } finally { detailFinished.resolve(); }
    });
    await page.goto(editUrl(first)); await detailEntered.promise;
    assert.equal(await page.locator("#subscription-save").isDisabled(), true, "Save stays disabled while subscription details are loading.");
    await page.locator("#subscription-cancel").click();
    await dialog.waitFor({ state: "hidden", timeout: 1000 }); detailRelease.resolve(); await detailFinished.promise; await page.unroute(detailPattern);
    assert.equal(await dialog.isVisible(), false); assert.equal(await page.locator(".subscription-default").count(), 0);
    // Escape also cancels a slow parameter preview and preserves the report drafts.
    console.log("Subscription recovery: Escape cancels preview");
    const previewEntered = deferred(), previewRelease = deferred(), previewFinished = deferred(); let delayPreview = true;
    await page.route("**/api/subscriptions/parameters", async route => {
        if (!delayPreview) { await route.continue(); return; } delayPreview = false;
        const response = await route.fetch(); previewEntered.resolve(); await previewRelease.promise;
        try { await route.fulfill({ response }); } catch { /* Cancelled preview. */ } finally { previewFinished.resolve(); }
    });
    await page.locator("#subscribe").click(); await previewEntered.promise; await page.keyboard.press("Escape");
    await dialog.waitFor({ state: "hidden", timeout: 1000 }); previewRelease.resolve(); await previewFinished.promise; await page.unroute("**/api/subscriptions/parameters");
    assert.equal(await page.locator(".subscription-default").count(), 0); await inputsReady();
    // Deferred script loading must recover readiness and deep-linked editing.
    await list();
    console.log("Subscription recovery: delayed script initialization");
    const scriptEntered = deferred(), scriptRelease = deferred(), lateDetailEntered = deferred(), lateDetailRelease = deferred();
    await page.route("**/js/subscriptions.js?*", async route => { scriptEntered.resolve(); await scriptRelease.promise; await route.continue(); });
    await page.goto(editUrl(first), { waitUntil: "commit" }); await scriptEntered.promise;
    await page.waitForFunction(() => { const state = window.portalSubscriptionInputs?.state(); return state?.loaded && !state.busy; });
    await page.route(detailPattern, async route => { lateDetailEntered.resolve(); await lateDetailRelease.promise; await route.continue(); });
    scriptRelease.resolve(); await lateDetailEntered.promise;
    assert.equal(await page.locator("#subscription-save").isDisabled(), true, "Late script initialization also disables Save until edit readiness.");
    lateDetailRelease.resolve();
    await page.locator("#subscription-editor[open]").waitFor();
    await page.locator("#subscription-save:not([disabled])").waitFor(); assert.equal(await page.locator("#subscription-name").inputValue(), "Recovery A");
    assert.match(await page.locator("#schedule-summary").innerText(), /Paused\. No delivery scheduled/);
    await page.locator("#subscription-cancel").click(); await dialog.waitFor({ state: "hidden" }); await page.unroute("**/js/subscriptions.js?*"); await page.unroute(detailPattern);
    // A failed, superseded response body cannot disable a newer valid preview.
    await page.locator("#subscribe").click(); await page.locator("#subscription-save:not([disabled])").waitFor();
    console.log("Subscription recovery: superseded failure");
    await page.evaluate(() => {
        const original = window.fetch; window.subscriptionTestFetch = original;
        window.subscriptionTestFailNext = true; window.subscriptionTestBodyWaiting = false; window.subscriptionTestBodyFinished = false;
        window.fetch = async (...args) => {
            const response = await original(...args);
            if (String(args[0]).endsWith("/api/subscriptions/parameters") && window.subscriptionTestFailNext) {
                window.subscriptionTestFailNext = false;
                const json = response.json.bind(response);
                response.json = async () => { window.subscriptionTestBodyWaiting = true; await new Promise(resolve => { window.subscriptionTestReleaseBody = resolve; }); try { return await json(); } finally { window.subscriptionTestBodyFinished = true; } };
            }
            return response;
        };
    });
    let failNext = true;
    await page.route("**/api/subscriptions/parameters", async route => {
        if (failNext) { failNext = false; await route.fulfill({ status: 400, contentType: "application/json", body: JSON.stringify({ message: "Superseded preview failure" }) }); }
        else { await route.continue(); }
    });
    await page.getByLabel("Use default for beginDate", { exact: true }).uncheck(); await page.waitForFunction(() => window.subscriptionTestBodyWaiting);
    await page.getByLabel("Use default for beginDate", { exact: true }).check(); await page.locator("#subscription-save:not([disabled])").waitFor();
    await page.evaluate(() => window.subscriptionTestReleaseBody());
    await page.waitForFunction(() => window.subscriptionTestBodyFinished && !document.querySelector("#subscription-save").disabled && !document.querySelector("#subscription-status").textContent.includes("Superseded"));
    await page.evaluate(() => { window.fetch = window.subscriptionTestFetch; }); await page.unroute("**/api/subscriptions/parameters");
    await scan("Subscription cancellation and stale-response recovery"); await page.locator("#subscription-cancel").click(); await dialog.waitFor({ state: "hidden" });
    // An unrelated edit preserves the original minimal schedule exactly.
    const minimal = await create("Minimal daily", { kind: "daily", startLocal: "2020-01-01T08:00:00" });
    await list();
    await page.goto(editUrl(minimal)); await page.locator("#subscription-save:not([disabled])").waitFor();
    await page.locator("#subscription-name").fill("Minimal daily renamed");
    await Promise.all([page.waitForURL("**/subscriptions"), page.locator("#subscription-save").click()]);
    const after = await (await context.request.get(`/api/subscriptions/${minimal.id}`)).json(); assert.deepEqual(after.definition.schedule, minimal.definition.schedule); assert.equal(after.nextDueUtc, minimal.nextDueUtc);
    await scan("Minimal schedule preserved after name edit");
    // All compact schedule variants reach the shared server preview with their actual semantics.
    await page.goto("/report/Training/Reports/One%20Input"); await inputsReady(); await page.locator("#subscribe").click(); await page.locator("#subscription-save:not([disabled])").waitFor();
    const previewReady = async text => page.waitForFunction(expected => { const summary = document.querySelector("#schedule-summary"); return summary.textContent.includes(expected) && summary.textContent.includes("Next:") && !summary.classList.contains("error"); }, text);
    await page.locator("#schedule-kind").selectOption("interval"); await page.locator("#schedule-every").fill("720"); await page.locator("#schedule-every").blur(); await previewReady("Every 720 minutes");
    await page.locator("#schedule-kind").selectOption("monthDays"); await page.locator("#schedule-month-days").fill("1, 15, 31"); await page.locator("#schedule-month-days").blur(); await previewReady("day 1, 15, 31");
    await page.locator("#schedule-kind").selectOption("cron"); await page.locator("#schedule-cron").fill("0 8 * * 1-5"); await page.locator("#schedule-cron").blur(); await previewReady("Cron 0 8 * * 1-5");
    await page.locator("#schedule-kind").selectOption("daily"); await page.locator("#schedule-every").fill("3"); await page.locator("#schedule-every").blur(); await previewReady("Every 3 days");
    await page.locator("#schedule-end").fill("2000-01-01T00:00"); await page.locator("#schedule-end").blur(); await page.locator("#schedule-summary.error").waitFor();
    await page.locator("#schedule-end").fill(""); await page.locator("#schedule-end").blur(); await previewReady("Every 3 days");
    await scan("Subscription interval, monthly-day, cron and end validation controls");
    await page.locator("#subscription-cancel").click(); await dialog.waitFor({ state: "hidden" });
    // Unknown delivery has actionable history and is never automatically resent.
    const unknown = await create("Unknown delivery");
    await context.request.post("/api/subscriptions/synthetic-dispatch", { headers: { RequestVerificationToken: csrf }, data: { outcome: "Unknown" } });
    await context.request.post("/api/subscriptions/synthetic-dispatch", { headers: { RequestVerificationToken: csrf }, data: { outcome: "Accepted" } });
    await list(); await row(unknown).getByText("Delivery outcome unknown", { exact: true }).waitFor(); await row(unknown).getByRole("button", { name: "History for Unknown delivery", exact: true }).click();
    await page.getByText("Unknown delivery is never automatically resent. Ask an administrator to inspect provider activity.", { exact: true }).waitFor();
    const uncertainHistory = await (await context.request.get(`/api/subscriptions/${unknown.id}/history`)).json(); assert.equal(uncertainHistory.length, 1); assert.equal(uncertainHistory[0].attempts, 1);
    await scan("Uncertain delivery history and no automatic resend"); await page.locator("#history-close").click();
    // A hidden-input report gives clear feedback and retains usable attachment delivery.
    await page.goto("/report/Training/Reports/Holds%20Ratio"); await inputsReady(); await page.locator("#subscribe").click(); await page.locator("#subscription-save:not([disabled])").waitFor();
    await page.locator("#subscription-name").fill("Hidden inputs attachment"); await page.locator("#subscription-to").fill("one@example.invalid"); await page.locator("#subscription-delivery").selectOption("both");
    await page.locator("#schedule-start").fill(localPast()); await page.locator("#schedule-start").blur();
    await page.getByLabel("Use default for Apply collection filter", { exact: true }).uncheck(); await page.locator("#subscription-save:not([disabled])").waitFor();
    await page.getByLabel("Apply collection filter", { exact: true }).selectOption("false"); await page.locator("#subscription-save:not([disabled])").waitFor(); await page.locator("#subscription-save").click();
    await page.locator("#subscription-status").filter({ hasText: "internal inputs" }).waitFor(); await scan("Hidden-input link validation feedback");
    await page.locator("#subscription-delivery").selectOption("attachment"); await page.locator("#subscription-save").click(); await dialog.waitFor({ state: "hidden" });
    const hidden = (await (await context.request.get("/api/subscriptions")).json()).find(item => item.definition.name === "Hidden inputs attachment"); assert.ok(hidden);
    await context.request.post("/api/subscriptions/synthetic-dispatch", { headers: { RequestVerificationToken: csrf }, data: { outcome: "Accepted" } });
    assert.equal((await (await context.request.get(`/api/subscriptions/${hidden.id}/history`)).json())[0].status, "Delivered");
    return { recoveryChecks: 8, additionalScheduleVariants: 4, hiddenInputFeedback: true, ambiguousDeliveryHistory: true };
};
