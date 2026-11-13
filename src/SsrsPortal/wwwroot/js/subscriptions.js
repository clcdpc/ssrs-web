"use strict";
(() => {
    const node = (tag, text, className) => { const element = document.createElement(tag); if (text != null) { element.textContent = text; } if (className) { element.className = className; } return element; };
    let csrf;
    const api = async (url, method = "GET", body, signal) => {
        if (method !== "GET" && !csrf) { csrf = (await api("/api/csrf")).token; }
        const response = await fetch(url, { method, credentials: "same-origin", cache: "no-store", signal,
            headers: method === "GET" ? {} : { "Content-Type": "application/json", RequestVerificationToken: csrf }, body: body === undefined ? undefined : JSON.stringify(body) });
        if (!response.ok) {
            let message = response.status === 401 || response.status === 403 ? "Your staff sign-in is unavailable. Sign in again." : "The request failed. Try again.";
            try { const problem = await response.json(); if (problem.message) { message = problem.message; } } catch { /* Retain a safe message. */ }
            const error = new Error(message); error.status = response.status; throw error;
        }
        return response.status === 204 ? null : response.json();
    };
    const date = value => value ? new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)) : "—";
    const days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    const scheduleText = schedule => {
        const time = schedule.startLocal.slice(11, 16), every = schedule.every || 1;
        const recurrence = schedule.kind === "once" ? "Once on " + schedule.startLocal.slice(0, 10)
            : schedule.kind === "interval" ? "Every " + every + " minutes (elapsed time)"
            : schedule.kind === "daily" ? (every === 1 ? "Daily" : "Every " + every + " days")
            : schedule.kind === "weekly" ? "Every " + every + " week" + (every === 1 ? "" : "s") + " on " + (schedule.weekdays || []).map(d => days[d]).join(", ")
            : schedule.kind === "monthDays" ? "Every " + every + " month" + (every === 1 ? "" : "s") + " on day " + (schedule.monthDays || []).join(", ")
            : schedule.kind === "monthWeekday" ? "Every " + every + " month" + (every === 1 ? "" : "s") + ": " + ({ 1: "first", 2: "second", 3: "third", 4: "fourth", "-1": "last" })[schedule.week] + " " + days[schedule.weekday]
            : "Cron " + schedule.cron;
        return recurrence + (schedule.kind === "cron" || schedule.kind === "interval" ? "" : " at " + time) + " · " + schedule.timeZone;
    };
    const reportUrl = path => "/report/" + path.slice(1).split("/").map(encodeURIComponent).join("/");
    const statusText = status => ({ Delivered: "Accepted by Postmark", Failed: "Failed before delivery", Unknown: "Delivery outcome unknown", Skipped: "Cancelled / skipped" })[status] || status || "Not run yet";
    const message = (target, text, error = false) => { target.textContent = text; target.classList.toggle("error", error); target.setAttribute("role", error ? "alert" : "status"); };
    const editor = document.getElementById("subscription-editor");
    if (editor) {
        const report = document.getElementById("report"), openButton = document.getElementById("subscribe");
        const form = document.getElementById("subscription-form"), status = document.getElementById("subscription-status");
        const save = document.getElementById("subscription-save"), host = document.getElementById("subscription-parameter-host");
        const input = name => document.getElementById("subscription-" + name);
        const field = name => document.getElementById("schedule-" + name);
        const parameterControls = document.getElementById("parameter-controls"), originalParent = parameterControls.parentNode, originalNext = parameterControls.nextSibling;
        let editing, saving = false, opening = false, closing = false, ready = false, scheduleDirty = false, editHandled = false, openGeneration = 0, openController, scheduleController, scheduleTimer;
        const retryInputs = document.getElementById("subscription-retry-inputs");
        const schedule = () => editing && !scheduleDirty ? structuredClone(editing.definition.schedule) : ({ kind: field("kind").value, startLocal: field("start").value + (field("start").value.length === 16 ? ":00" : ""),
            endLocal: field("end").value ? field("end").value + (field("end").value.length === 16 ? ":00" : "") : null,
            timeZone: field("zone").value, every: Number(field("every").value),
            weekdays: [...form.querySelectorAll("input[name=schedule-weekdays]:checked")].map(item => Number(item.value)),
            monthDays: field("month-days").value.split(",").map(value => Number(value.trim())), week: Number(field("week").value), weekday: Number(field("weekday").value), cron: field("cron").value });
        const scheduleFields = (reset = false) => {
            const kind = field("kind").value;
            const interval = kind === "interval", monthly = kind === "monthDays" || kind === "monthWeekday";
            field("every-field").hidden = !["interval", "daily", "weekly", "monthDays", "monthWeekday"].includes(kind);
            field("every").disabled = field("every-field").hidden;
            field("every").min = interval ? "5" : "1";
            field("every").max = interval ? "10080" : kind === "weekly" ? "52" : monthly ? "12" : "366";
            if (reset) { field("every").value = interval ? "60" : "1"; }
            field("unit").textContent = interval ? "minutes" : kind === "weekly" ? "weeks" : monthly ? "months" : "days";
            for (const [name, show] of [["weekdays", kind === "weekly"], ["month-days", kind === "monthDays"], ["week", kind === "monthWeekday"], ["weekday", kind === "monthWeekday"], ["cron", kind === "cron"]]) {
                field(name + "-field").hidden = !show;
                for (const control of field(name + "-field").querySelectorAll("input,select")) { control.disabled = !show; }
            }
            // Inactive controls never invalidate the form or leak an obsolete interval.
            if (field("every").disabled) { field("every").value = "1"; }
        };
        const preview = async () => {
            scheduleController?.abort(); const controller = new AbortController(); scheduleController = controller;
            const summary = field("summary");
            if (!editor.open || !field("start").value) { return; }
            const value = schedule();
            if (editing && !scheduleDirty) {
                const state = !editing.enabled ? "Paused. No delivery scheduled." : !editing.nextDueUtc ? "No remaining occurrences."
                    : "Next: " + (Date.parse(editing.nextDueUtc) < Date.now() ? "catch-up for " : "") + date(editing.nextDueUtc) + " (your local time)";
                message(summary, scheduleText(value) + " · " + state); return;
            }
            message(summary, scheduleText(value) + " · Calculating next execution…");
            try {
                const result = await api("/api/subscriptions/schedule", "POST", value, controller.signal);
                if (scheduleController !== controller || !editor.open) { return; }
                const prefix = editing && !editing.enabled ? "Preview if resumed now: " : "Next: ";
                message(summary, scheduleText(value) + " · " + prefix + (Date.parse(result.nextDueUtc) < Date.now() ? "catch-up for " : "") + date(result.nextDueUtc) + " (your local time)");
            } catch (error) { if (error.name !== "AbortError" && scheduleController === controller) { message(summary, error.message, true); } }
        };
        const deliveryState = () => { input("format").disabled = input("delivery").value === "link"; };
        const close = async (saved = false) => {
            if (saving || closing) { return; }
            closing = true; ++openGeneration; openController?.abort(); opening = false; ready = false;
            scheduleController?.abort(); clearTimeout(scheduleTimer);
            originalParent.insertBefore(parameterControls, originalNext);
            try { await window.portalSubscriptionInputs.end(); }
            finally { editor.close(); closing = false; openButton.focus(); }
            if (saved) { document.getElementById("link-feedback").textContent = "Subscription saved. Manage it in My subscriptions."; }
        };
        const open = async id => {
            if (opening || editor.open) { return; }
            const generation = ++openGeneration, controller = new AbortController(); openController = controller;
            const current = () => generation === openGeneration && !controller.signal.aborted && editor.open;
            opening = true; ready = false; scheduleDirty = false; retryInputs.hidden = true; openButton.disabled = true; save.disabled = true; editing = null; form.reset(); message(status, "Loading subscription…");
            input("name").value = (report.dataset.reportName + " schedule").slice(0, 150); input("to").value = editor.dataset.initialEmail || "";
            field("start").value = editor.dataset.initialStart; field("end").value = ""; field("zone").value = "America/New_York";
            document.getElementById("subscription-heading").textContent = id ? "Edit subscription" : "Subscribe to report";
            document.getElementById("subscription-report").textContent = report.dataset.reportName;
            editor.showModal();
            try {
                if (id) {
                    const loaded = await api("/api/subscriptions/" + encodeURIComponent(id), "GET", undefined, controller.signal);
                    if (!current()) { return; }
                    editing = loaded;
                    const definition = editing.definition;
                    if (definition.reportPath !== report.dataset.reportPath) { throw new Error("This subscription belongs to a different report."); }
                    input("name").value = definition.name;
                    for (const name of ["to", "cc", "bcc"]) { input(name).value = definition.recipients[name].join("; "); }
                    input("delivery").value = definition.delivery; input("format").value = definition.format;
                    const value = definition.schedule;
                    field("kind").value = value.kind; field("start").value = value.startLocal.slice(0, 16); field("end").value = value.endLocal?.slice(0, 16) || "";
                    if (![...field("zone").options].some(option => option.value === value.timeZone)) { const option = node("option", value.timeZone); option.value = value.timeZone; field("zone").append(option); }
                    field("zone").value = value.timeZone; field("every").value = value.every;
                    field("month-days").value = (value.monthDays || [1]).join(", "); field("week").value = value.week; field("weekday").value = value.weekday;
                    field("cron").value = value.cron || "0 8 * * 1-5";
                    for (const checkbox of form.querySelectorAll("input[name=schedule-weekdays]")) { checkbox.checked = (value.weekdays || [1]).includes(Number(checkbox.value)); }
                }
                host.append(parameterControls); scheduleFields(); deliveryState();
                await window.portalSubscriptionInputs.begin(editing?.definition.parameters, controller.signal);
                if (!current()) { return; }
                ready = true;
                message(status, ""); await preview(); if (current()) { input("name").focus(); }
            } catch (error) { if (current() && error.name !== "AbortError") { message(status, error.message, true); status.focus(); } }
            finally { if (current()) { opening = false; save.disabled = !ready; openButton.disabled = false; } }
        };
        openButton.addEventListener("click", () => open());
        for (const id of ["subscription-close", "subscription-cancel"]) { document.getElementById(id).addEventListener("click", () => close()); }
        editor.addEventListener("cancel", event => { event.preventDefault(); close(); });
        report.addEventListener("portal:inputs-state", event => {
            if (!editor.open) { openButton.disabled = event.detail.busy || !event.detail.loaded || event.detail.executing; }
            host.setAttribute("aria-busy", String(event.detail.busy)); save.disabled = !ready || saving || opening || event.detail.busy;
        });
        report.addEventListener("portal:inputs-error", event => { if (editor.open) { ready = false; retryInputs.hidden = false; message(status, event.detail, true); } });
        retryInputs.addEventListener("click", async () => {
            retryInputs.disabled = true;
            try { await window.portalSubscriptionInputs.retry(); ready = true; retryInputs.hidden = true; message(status, "Report inputs updated."); }
            catch (error) { message(status, error.message, true); }
            finally { retryInputs.disabled = false; save.disabled = !ready; }
        });
        const startEdit = () => {
            const state = window.portalSubscriptionInputs.state();
            if (editHandled || state.busy || !state.loaded || state.executing) { return; }
            const match = window.portalSubscriptionInputs.fragment.match(/^#subscription=([a-f0-9-]{36})$/i);
            if (match) { editHandled = true; open(match[1]); }
        };
        report.addEventListener("portal:inputs-ready", startEdit);
        const initialState = window.portalSubscriptionInputs.state();
        openButton.disabled = initialState.busy || !initialState.loaded || initialState.executing;
        startEdit();
        field("kind").addEventListener("change", () => { scheduleDirty = true; scheduleFields(true); preview(); });
        for (const control of form.querySelectorAll(".subscription-schedule input,.subscription-schedule select")) {
            if (control === field("kind")) { continue; }
            control.addEventListener("change", () => { scheduleDirty = true; clearTimeout(scheduleTimer); scheduleController?.abort(); message(field("summary"), "Updating schedule…"); scheduleTimer = setTimeout(preview, 150); });
        }
        input("delivery").addEventListener("change", deliveryState);
        form.addEventListener("submit", async event => {
            event.preventDefault(); if (saving || opening || closing || !ready || !form.reportValidity()) { return; }
            saving = true; save.disabled = true; message(status, "Saving subscription…");
            try {
                const parameters = await window.portalSubscriptionInputs.selection();
                const addresses = name => input(name).value.split(/[,;\r\n]+/).map(value => value.trim()).filter(Boolean);
                const definition = { name: input("name").value.trim(), reportPath: report.dataset.reportPath, recipients: { to: addresses("to"), cc: addresses("cc"), bcc: addresses("bcc") },
                    delivery: input("delivery").value, format: input("format").value, schedule: schedule(), parameters };
                const saved = await api("/api/subscriptions" + (editing ? "/" + editing.id : ""), editing ? "PUT" : "POST", { definition, revision: editing?.revision });
                const wasEdit = Boolean(editing); editing = saved; saving = false; await close(true);
                if (wasEdit) { window.location.assign("/subscriptions"); }
            } catch (error) {
                message(status, error.status || error.message.startsWith("Review ") ? error.message : "Saving could not be confirmed. Check My subscriptions before trying again.", true);
                status.focus();
            } finally { saving = false; save.disabled = !ready; }
        });
    }

    const management = document.getElementById("my-subscriptions");
    if (management) {
        const rows = document.getElementById("subscriptions-rows"), status = document.getElementById("subscriptions-status");
        const filter = document.getElementById("subscriptions-filter"), state = document.getElementById("subscriptions-state"), sort = document.getElementById("subscriptions-sort");
        const history = document.getElementById("subscription-history"); let subscriptions = [], loadPromise, historyController;
        const historyFor = async subscription => {
            historyController?.abort(); const controller = new AbortController(); historyController = controller;
            history.showModal(); const target = document.getElementById("history-rows"), notice = document.getElementById("history-status");
            target.replaceChildren(); message(notice, "Loading history for " + subscription.definition.name + "…");
            try {
                const items = await api("/api/subscriptions/" + subscription.id + "/history", "GET", undefined, controller.signal);
                if (historyController !== controller || !history.open) { return; }
                for (const item of items) {
                    const row = node("tr"); row.append(node("td", date(item.occurrenceUtc)), node("td", statusText(item.status)), node("td", item.attempts),
                        node("td", (item.diagnostic || "—") + (item.providerMessageId ? " · Message ID: " + item.providerMessageId : ""))); target.append(row);
                    row.setAttribute("role", "row");
                    [...row.children].forEach((cell, index) => { cell.setAttribute("role", "cell"); cell.dataset.label = ["Scheduled", "Outcome", "Attempts", "Details"][index]; });
                }
                message(notice, items.some(item => item.status === "Unknown") ? "Unknown delivery is never automatically resent. Ask an administrator to inspect provider activity." : items.length ? "Most recent " + items.length + " occurrences. Times are shown in your local time zone." : "No executions yet.");
            } catch (error) { if (historyController === controller && history.open && error.name !== "AbortError") { message(notice, error.message, true); } }
        };
        const render = () => {
            const query = filter.value.toLowerCase();
            const filtered = subscriptions.filter(item => (state.value === "all" || item.enabled === (state.value === "enabled")) &&
                [item.definition.name, item.definition.reportPath, ...Object.values(item.definition.recipients).flat()].some(value => value.toLowerCase().includes(query)));
            filtered.sort((a, b) => sort.value === "next" ? (a.nextDueUtc ? Date.parse(a.nextDueUtc) : Infinity) - (b.nextDueUtc ? Date.parse(b.nextDueUtc) : Infinity)
                : sort.value === "name" ? a.definition.name.localeCompare(b.definition.name) : sort.value === "report" ? a.definition.reportPath.localeCompare(b.definition.reportPath) : (a.lastStatus || "").localeCompare(b.lastStatus || ""));
            rows.replaceChildren();
            for (const item of filtered) {
                const definition = item.definition, row = node("tr"); row.dataset.id = item.id;
                const title = node("td"), report = node("a", definition.reportPath.split("/").pop()); report.href = reportUrl(definition.reportPath);
                title.append(node("strong", definition.name), report);
                const recurrence = node("td"); recurrence.append(node("span", scheduleText(definition.schedule)), node("small", "Next: " + date(item.nextDueUtc) + " (local)"));
                const delivery = node("td"); delivery.append(node("span", ({ link: "Link only", attachment: "Attachment only", both: "Link and attachment" })[definition.delivery] + (definition.delivery === "link" ? "" : " · " + definition.format.toUpperCase())));
                for (const name of ["to", "cc", "bcc"]) { if (definition.recipients[name].length) { delivery.append(node("small", name.toUpperCase() + ": " + definition.recipients[name].join(", "))); } }
                const outcome = node("td"); outcome.append(node("span", item.enabled ? "Enabled" : "Paused", item.enabled ? "subscription-enabled" : "subscription-paused"), node("small", statusText(item.lastStatus)));
                const historyButton = node("button", "History", "quiet"); historyButton.type = "button"; historyButton.setAttribute("aria-label", "History for " + definition.name);
                historyButton.addEventListener("click", () => historyFor(item)); outcome.append(historyButton);
                const actions = node("td"), group = node("div", null, "subscription-row-actions"), edit = node("a", "Edit");
                edit.href = reportUrl(definition.reportPath) + "?portal%3AnoExecute=true#subscription=" + item.id; edit.setAttribute("aria-label", "Edit " + definition.name);
                const pause = node("button", item.enabled ? "Pause" : "Resume", "quiet"), remove = node("button", "Delete", "quiet"); pause.type = remove.type = "button";
                for (const [button, label] of [[pause, item.enabled ? "Pause" : "Resume"], [remove, "Delete"]]) { button.setAttribute("aria-label", label + " " + definition.name); }
                const act = async deleting => {
                    if (deleting && !window.confirm('Delete subscription "' + definition.name + '"? Pending deliveries will be cancelled.')) { return; }
                    pause.disabled = remove.disabled = true;
                    try {
                        if (deleting) { await api("/api/subscriptions/" + item.id + "?revision=" + item.revision, "DELETE"); }
                        else { await api("/api/subscriptions/" + item.id + "/state", "POST", { enabled: !item.enabled, revision: item.revision }); }
                        await load(true); message(status, deleting ? "Subscription deleted." : item.enabled ? "Subscription paused." : "Subscription resumed.");
                        (rows.querySelector('tr[data-id="' + item.id + '"] .subscription-row-actions button') || filter).focus();
                    } catch (error) { message(status, error.message, true); status.focus(); }
                    finally { pause.disabled = remove.disabled = false; }
                };
                pause.addEventListener("click", () => act(false)); remove.addEventListener("click", () => act(true)); group.append(edit, pause, remove); actions.append(group);
                const cells = [title, recurrence, delivery, outcome, actions]; row.setAttribute("role", "row");
                cells.forEach((cell, index) => { cell.setAttribute("role", "cell"); cell.dataset.label = ["Subscription / report", "Schedule", "Delivery / recipients", "Status", "Actions"][index]; });
                row.append(...cells); rows.append(row);
            }
            message(status, filtered.length ? filtered.length + " subscription" + (filtered.length === 1 ? "" : "s") + ". Times below use your local time zone." : subscriptions.length ? "No subscriptions match this filter." : "No subscriptions yet. Open a report and select Subscribe.");
        };
        const load = (afterMutation = false) => {
            if (loadPromise) { return afterMutation ? loadPromise.catch(() => {}).then(() => load()) : loadPromise; }
            loadPromise = (async () => {
                management.setAttribute("aria-busy", "true");
                try { subscriptions = await api("/api/subscriptions"); render(); }
                catch (error) { message(status, error.message, true); throw error; }
                finally { loadPromise = null; management.setAttribute("aria-busy", "false"); }
            })();
            return loadPromise;
        };
        filter.addEventListener("input", render); state.addEventListener("change", render); sort.addEventListener("change", render);
        document.getElementById("subscriptions-reload").addEventListener("click", () => load().catch(() => {}));
        document.getElementById("history-close").addEventListener("click", () => { historyController?.abort(); history.close(); });
        history.addEventListener("cancel", () => historyController?.abort());
        load().catch(() => {});
    }
})();
