"use strict";
(() => {
    const brandLogo = document.querySelector(".brand-logo");
    if (brandLogo) {
        const restoreBrand = () => { brandLogo.hidden = true; document.querySelector(".brand-fallback").hidden = false; };
        brandLogo.addEventListener("error", restoreBrand);
        if (brandLogo.complete && brandLogo.naturalWidth === 0) { restoreBrand(); }
    }
    let csrf = "";
    const status = document.getElementById("status");
    const announce = (message, error = false, visible = true) => {
        if (status) {
            status.classList.toggle("error", error);
            status.setAttribute("role", error ? "alert" : "status");
            status.setAttribute("aria-live", error ? "assertive" : "polite");
            status.classList.toggle("visually-hidden", !visible);
            status.textContent = message;
        }
    };
    const el = (tag, text, attrs = {}) => {
        const node = document.createElement(tag);
        if (text !== null) {
            node.textContent = text;
        }
        for (const [key, value] of Object.entries(attrs)) {
            node.setAttribute(key, value);
        }
        return node;
    };
    const request = async (url, body, signal) => {
        const response = await fetch(url, {
            method: body === undefined ? "GET" : "POST",
            headers: body === undefined ? {} : { "Content-Type": "application/json", "RequestVerificationToken": csrf },
            body: body === undefined ? undefined : JSON.stringify(body),
            cache: "no-store", credentials: "same-origin", signal
        });
        if (!response.ok) {
            let message = response.status === 401 || response.status === 403
                ? "Your staff sign-in is unavailable. Sign in again."
                : "The request failed. Try again or contact support.";
            let diagnostics;
            let code;
            try {
                const problem = await response.json();
                if (problem.message) {
                    message = problem.message + (problem.trace ? " Reference: " + problem.trace : "");
                }
                diagnostics = problem.diagnostics;
                code = problem.code;
            } catch { /* A safe default covers HTML errors and interrupted responses. */ }
            const error = new Error(message);
            error.diagnostics = diagnostics;
            error.code = code;
            error.status = response.status;
            throw error;
        }
        return response.status === 204 ? null : response.json();
    };
    const initialize = async () => { csrf = (await request("/api/csrf")).token; };
    const breadcrumbs = document.querySelector(".breadcrumbs");
    if (breadcrumbs) {
        const list = breadcrumbs.querySelector(".breadcrumb-list");
        const folders = [...list.querySelectorAll(":scope > .breadcrumb-folder")];
        const current = list.querySelector(".breadcrumb-current");
        const more = list.querySelector(".breadcrumb-more");
        const details = more?.querySelector("details"), summary = more?.querySelector("summary"), menu = more?.querySelector("ol");
        // Separators belong to adjacent visible entries, never to a folder moved
        // into the popup. Keep real, selectable text in the rendered trail.
        const separate = items => {
            [...items].forEach((item, index) => {
                item.querySelector(":scope > .breadcrumb-separator")?.remove();
                if (index > 0) { item.prepend(el("span", " > ", { class: "breadcrumb-separator", "aria-hidden": "true" })); }
            });
        };
        // Measure capped, unshrunk labels in one invisible, inaccessible copy.
        // Reads never depend on which folders were moved into the visible menu.
        const measure = list.cloneNode(true);
        measure.classList.add("breadcrumb-measure");
        measure.setAttribute("aria-hidden", "true");
        measure.inert = true;
        measure.querySelector("[aria-current]")?.removeAttribute("aria-current");
        for (const link of measure.querySelectorAll("a")) { link.removeAttribute("href"); }
        measure.querySelector(".breadcrumb-more")?.removeAttribute("hidden");
        separate(measure.children);
        document.body.append(measure);
        let frame, lastWidth = -1, hiddenCount = -1;
        const positionMenu = () => {
            if (!details?.open) { return; }
            const anchor = summary.getBoundingClientRect(), width = menu.getBoundingClientRect().width;
            const below = innerHeight - anchor.bottom - 8, above = anchor.top - 8;
            const down = below >= Math.min(240, innerHeight / 2) || below >= above;
            menu.style.left = Math.max(8, Math.min(anchor.left, innerWidth - width - 8)) + "px";
            menu.style.top = down ? Math.max(8, anchor.bottom + 4) + "px" : "auto";
            menu.style.bottom = down ? "auto" : Math.max(8, innerHeight - anchor.top + 4) + "px";
            menu.style.maxHeight = Math.max(44, Math.min(innerHeight / 2, down ? below : above)) + "px";
        };
        const closeMenu = focus => {
            if (details?.open) {
                details.open = false;
                if (focus) { summary.focus(); }
            }
        };
        const fit = () => {
            frame = undefined;
            const available = breadcrumbs.clientWidth;
            if (Math.abs(available - lastWidth) < .5) { positionMenu(); return; }
            lastWidth = available;
            const sizes = [...measure.querySelectorAll(":scope > .breadcrumb-folder")].map(n => n.getBoundingClientRect().width);
            const gap = parseFloat(getComputedStyle(measure).columnGap);
            let width = sizes.reduce((total, size) => total + size, 0) + measure.querySelector(".breadcrumb-current").getBoundingClientRect().width + gap * folders.length;
            const moreWidth = measure.querySelector(".breadcrumb-more")?.getBoundingClientRect().width ?? 0;
            let count = 0;
            // Keep the first folder, immediate parent and report. Only collapse
            // intermediates if capped labels still cannot fit the available area.
            for (let index = 1; index < folders.length - 1 && width > available + .5; index++) {
                width -= sizes[index] + gap;
                if (++count === 1) { width += moreWidth + gap; }
            }
            // At increased text size the first, popup, parent and current can
            // exceed even their minimum widths. Move the parent only then.
            const measuredFolders = [...measure.querySelectorAll(":scope > .breadcrumb-folder")];
            if (count > 0 && count < folders.length - 1) {
                const retained = [...measuredFolders.filter((_, i) => i === 0 || i > count), measure.querySelector(".breadcrumb-current")];
                // Read used minima rather than parsing CSS min()/rem expressions.
                for (const node of retained) { node.style.width = "0"; }
                const minimumWidth = retained.reduce((total, node) => total + node.getBoundingClientRect().width, moreWidth);
                for (const node of retained) { node.style.width = ""; }
                if (minimumWidth > available + .5) { count = folders.length - 1; }
            }
            if (count !== hiddenCount) {
                const focused = document.activeElement;
                closeMenu(false);
                const visible = document.createDocumentFragment();
                folders.forEach((folder, index) => {
                    if (index === 1 && more) { visible.append(more); }
                    if (index > 0 && index <= count) {
                        folder.querySelector(":scope > .breadcrumb-separator")?.remove();
                        menu.append(folder);
                    }
                    else { visible.append(folder); }
                });
                visible.append(current);
                list.replaceChildren(visible);
                if (more) { more.hidden = count === 0; }
                // Hidden popup controls must not introduce a prefix before the
                // first visible item or contribute text to a copied trail.
                const shown = [...list.children].filter(item => !item.hidden);
                separate(shown);
                if (focused === summary) {
                    if (count) { summary.focus(); }
                    else { folders[1]?.querySelector("a").focus(); }
                }
                else if (menu?.contains(focused)) { summary.focus(); }
                else if (folders.some(folder => folder.contains(focused))) { focused.focus(); }
                hiddenCount = count;
            }
            breadcrumbs.dataset.adaptive = "true";
            positionMenu();
        };
        const schedule = () => { if (!frame) { frame = requestAnimationFrame(fit); } };
        const observer = typeof ResizeObserver === "function" ? new ResizeObserver(entries => {
            // Text zoom can change label widths without changing the nav's width.
            if (entries.some(entry => entry.target === measure)) { lastWidth = -1; }
            schedule();
        }) : null;
        observer?.observe(breadcrumbs);
        observer?.observe(measure);
        window.addEventListener("resize", schedule);
        window.addEventListener("scroll", positionMenu, true);
        document.fonts?.ready.then(() => { lastWidth = -1; schedule(); });
        details?.addEventListener("toggle", positionMenu);
        summary?.addEventListener("keydown", event => {
            if (event.key === "ArrowDown") { event.preventDefault(); event.stopPropagation(); details.open = true; positionMenu(); menu.querySelector("a")?.focus(); }
        });
        details?.addEventListener("keydown", event => {
            if (event.key === "Escape") { event.preventDefault(); closeMenu(true); }
            const links = [...menu.querySelectorAll("a")], index = links.indexOf(document.activeElement);
            if (index < 0) { return; }
            const next = event.key === "ArrowDown" ? Math.min(index + 1, links.length - 1)
                : event.key === "ArrowUp" ? Math.max(index - 1, 0) : event.key === "Home" ? 0 : event.key === "End" ? links.length - 1 : undefined;
            if (next !== undefined) { event.preventDefault(); links[next].focus(); }
        });
        details?.addEventListener("focusout", event => { if (event.relatedTarget && !details.contains(event.relatedTarget)) { closeMenu(false); } });
        document.addEventListener("pointerdown", event => { if (!details?.contains(event.target)) { closeMenu(menu?.contains(document.activeElement)); } });
        window.addEventListener("pageshow", () => { lastWidth = -1; schedule(); });
        window.addEventListener("pagehide", event => {
            if (!event.persisted) { observer?.disconnect(); cancelAnimationFrame(frame); measure.remove(); }
        });
        fit();
    }
    const report = document.getElementById("report");
    const viewer = document.getElementById("viewer");
    const showFailure = error => {
        document.getElementById("diagnostic-details")?.remove();
        announce(error.message, true);
        if (error.diagnostics && status) {
            let details = document.getElementById("diagnostic-details");
            if (!details) {
                details = el("pre", null, { id: "diagnostic-details", "aria-label": "Diagnostic details" });
                (status.closest(".workspace-feedback") || status).after(details);
            }
            details.textContent = error.diagnostics;
        }
    };
    if (report) {
        const id = encodeURIComponent(report.dataset.id);
        const form = document.getElementById("parameters");
        const run = document.getElementById("run");
        const cancel = document.getElementById("cancel-run");
        const toggle = document.getElementById("toggle-parameters");
        const copyLink = document.getElementById("copy-report-link");
        const linkFeedback = document.getElementById("link-feedback");
        const printLink = document.getElementById("print-report");
        const parameterControls = document.getElementById("parameter-controls");
        const parametersExpanded = () => toggle.getAttribute("aria-expanded") === "true";
        let values = {}, defaulted = [], controller, version = 0, activeDropdown, dropdownIntent = 0;
        let subscriptionInputs = null;
        const subscriptionFragment = window.location.hash;
        let parameterResolution = Promise.resolve();
        const canonicalUrl = report.dataset.canonicalUrl;
        const entryQuery = report.dataset.linkQuery || "";
        let fields = [], inputsLoaded = false, linkErrors = [], linkInitialization = false;
        let noExecute = new URLSearchParams(entryQuery).getAll("portal:noExecute").some(v => v === "true" || v === "1");
        const dropdownOpeners = new Map();
        let parameterBusy = true, canRun = false, executing = false, paging = false, closing = false, committing = false, toggling = false;
        let activeSession = null, displayedValues = null, executionRevision = 0, viewRevision = 0, executionController, pageController, pendingToken;
        let interactionBridge;
        const pendingSessions = new Set();
        const buttons = ["first", "previous", "next", "last"].map(name => document.getElementById(name));
        const exports = ["pdf", "xlsx", "docx", "csv"].map(name => document.getElementById(name));
        const closeButton = document.getElementById("cancel-session");
        const synchronizeButton = document.getElementById("resynchronize");
        const signature = input => JSON.stringify(Object.keys(input).sort().map(key => [key, input[key]]));
        const dirty = () => { document.getElementById("dirty").hidden = !parametersExpanded() || !activeSession || displayedValues === signature(values); };
        const presentParameters = () => {
            const expanded = parametersExpanded(), cancellable = executing && !committing;
            // Move focus before hiding any part of the form, including Run.
            // The one existing Cancel button is the collapsed execution exception.
            if (!expanded && form.contains(document.activeElement) && !(document.activeElement === cancel && cancellable)) {
                toggle.focus({ preventScroll: true });
            }
            parameterControls.hidden = !expanded && !subscriptionInputs;
            run.hidden = !expanded;
            form.hidden = !expanded && !cancellable;
            dirty();
        };
        const controls = () => {
            const unavailable = !activeSession?.valid || executing || paging || toggling || closing || activeSession.needsSynchronization;
            const page = activeSession?.page ?? 1, pages = activeSession?.pages ?? null;
            run.disabled = ((parameterBusy || !canRun) && !activeDropdown?.hasValidEdits()) || executing || toggling || closing;
            run.dataset.executing = String(executing);
            run.querySelector(".run-label").setAttribute("aria-hidden", String(executing));
            run.querySelector(".run-pending").setAttribute("aria-hidden", String(!executing));
            form.setAttribute("aria-busy", String(parameterBusy || executing));
            buttons[0].disabled = buttons[1].disabled = unavailable || page === 1;
            buttons[2].disabled = unavailable || (pages !== null && page >= pages) || page >= 10000;
            buttons[3].disabled = unavailable || pages === null || page >= pages;
            document.getElementById("page-label").textContent = activeSession ? "Page " + page + (pages === null ? " · total not yet known" : " of " + pages) : "";
            for (const link of exports) {
                link.setAttribute("aria-disabled", String(unavailable));
                if (activeSession?.valid) { link.href = "/api/executions/" + encodeURIComponent(activeSession.token) + "/exports/" + link.id; }
                else { link.removeAttribute("href"); }
            }
            printLink.setAttribute("aria-disabled", String(Boolean(unavailable)));
            if (!unavailable) { printLink.href = "/api/executions/" + encodeURIComponent(activeSession.token) + "/print?revision=" + activeSession.revision; }
            else { printLink.removeAttribute("href"); }
            copyLink.disabled = !inputsLoaded || executing || toggling || closing;
            closeButton.disabled = closing || committing;
            if ((!executing || committing) && document.activeElement === cancel) {
                (parametersExpanded() && !run.disabled ? run : toggle).focus({ preventScroll: true });
            }
            cancel.setAttribute("aria-hidden", String(!executing));
            cancel.disabled = !executing || committing;
            synchronizeButton.hidden = !activeSession?.valid || !activeSession.needsSynchronization;
            synchronizeButton.disabled = executing || paging || toggling || closing;
            viewer.setAttribute("aria-busy", String(paging || toggling));
            const currentFrame = document.getElementById("report-frame");
            for (const control of currentFrame?.contentDocument?.querySelectorAll("button[data-portal-toggle]") || []) {
                control.setAttribute("aria-disabled", String(Boolean(unavailable)));
            }
            presentParameters();
            const subscribe = document.getElementById("subscribe");
            if (subscribe) { subscribe.disabled = !inputsLoaded || parameterBusy || executing || toggling || closing; }
            report.dispatchEvent(new CustomEvent("portal:inputs-state", { detail: { busy: parameterBusy, canRun, loaded: inputsLoaded, executing: executing || toggling || closing } }));
        };
        for (const link of [...exports, printLink]) {
            link.addEventListener("click", event => { if (link.getAttribute("aria-disabled") === "true") { event.preventDefault(); } });
        }
        const remember = token => {
            const state = { ...window.history.state, ssrsReport: { path: canonicalUrl, token, attempted: true } };
            window.history.replaceState(state, "", canonicalUrl + entryQuery);
        };
        const release = async token => {
            if (!token) { return; }
            try { await request("/api/executions/" + encodeURIComponent(token) + "/cancel", {}); }
            catch (error) { if (error.status !== 404) { throw error; } }
            pendingSessions.delete(token);
        };
        const establish = async token => {
            try { await request("/api/executions/" + encodeURIComponent(token) + "/commit", {}); }
            catch (error) {
                let info;
                try { info = await request("/api/executions/" + encodeURIComponent(token) + "/info"); }
                catch (lookupError) {
                    if (lookupError.code === "session" || lookupError.status === 401 || lookupError.status === 403) { throw lookupError; }
                    return false; // Outcome unknown: retain the ready new report.
                }
                if (info.pending) { throw error; }
            }
            return true;
        };
        // Load a second isolated frame without touching the displayed document. A
        // server-owned marker distinguishes sanitized output from error documents.
        const prepareFrame = (url, revision, signal, expectedRevision) => new Promise((resolve, reject) => {
            const frame = el("iframe", null, { title: "Isolated report content", sandbox: "allow-same-origin", referrerpolicy: "no-referrer", class: "frame-staging", "aria-hidden": "true", tabindex: "-1" });
            let timer;
            const finish = (error) => {
                clearTimeout(timer);
                signal.removeEventListener("abort", aborted);
                frame.onload = frame.onerror = null;
                if (error) { frame.remove(); reject(error); } else { resolve(frame); }
            };
            const aborted = () => finish(new DOMException("Cancelled", "AbortError"));
            frame.onload = () => {
                if (revision !== viewRevision || signal.aborted) { aborted(); return; }
                try {
                    const doc = frame.contentDocument;
                    if (!doc?.querySelector('meta[name="ssrs-portal-report"][content="ready"]')) {
                        let problem;
                        try { problem = JSON.parse(doc?.body.textContent); } catch { /* Use the safe fallback. */ }
                        const error = new Error(problem?.message ? problem.message + (problem.trace ? " Reference: " + problem.trace : "") : "The report output could not be loaded. Select View report to try again.");
                        error.code = problem?.code;
                        error.diagnostics = problem?.diagnostics;
                        finish(error);
                    } else if (expectedRevision !== undefined && doc.querySelector('meta[name="ssrs-portal-revision"]')?.content !== String(expectedRevision)) {
                        const error = new Error("This report view changed while it was loading. Reload report output to synchronize it.");
                        error.code = "view-stale";
                        finish(error);
                    } else { finish(); }
                } catch { finish(new Error("The report output could not be loaded. Select View report to try again.")); }
            };
            frame.onerror = () => finish(new Error("The report output could not be loaded. Select View report to try again."));
            signal.addEventListener("abort", aborted, { once: true });
            timer = setTimeout(() => finish(new Error("Loading report output timed out. Select View report to try again.")), 125000);
            if (signal.aborted) { aborted(); return; }
            frame.src = url;
            viewer.append(frame);
        });
        const display = (frame, state) => {
            interactionBridge?.remove();
            // Moving an iframe reloads its browsing context in Chromium. Keep the
            // staged frame in place so the verified document and resources survive.
            document.getElementById("report-frame").remove();
            frame.id = "report-frame";
            frame.classList.remove("frame-staging");
            frame.removeAttribute("aria-hidden");
            frame.removeAttribute("tabindex");
            activeSession = { ...state, valid: true, needsSynchronization: false };
            interactionBridge = installInteractionBridge(frame, state.token);
            viewer.hidden = false;
            remember(state.token);
            controls();
        };
        const installInteractionBridge = (frame, token) => {
            const doc = frame.contentDocument;
            let pointerTarget, sessionEnded = false;
            const clearPointerFocus = () => {
                for (const button of doc.querySelectorAll("button[data-portal-pointer-focus]")) { button.removeAttribute("data-portal-pointer-focus"); }
            };
            const pointercancel = () => {
                if (pointerTarget && doc.activeElement !== pointerTarget) { pointerTarget.removeAttribute("data-portal-pointer-focus"); }
                pointerTarget = undefined;
            };
            const pointerdown = event => {
                if (!event.isTrusted || event.button !== 0 || !event.isPrimary) { return; }
                pointercancel();
                pointerTarget = event.target?.closest?.("button[data-portal-toggle]");
                // Suppress an existing keyboard ring before the pointer's default
                // focus action. Unrelated pointer/scroll input must not change the
                // modality of the control that still owns focus.
                if (pointerTarget) { pointerTarget.dataset.portalPointerFocus = "true"; }
            };
            const focusout = event => {
                event.target?.removeAttribute?.("data-portal-pointer-focus");
                if (event.target === pointerTarget) { pointerTarget = undefined; }
            };
            const activate = event => {
                const button = event.target?.closest?.("button[data-portal-toggle]");
                // A click alone is not pointer evidence (keyboard, AT and script
                // can all produce it). Consume only a matching real pointerdown.
                const pointer = event.type === "click" && event.isTrusted && pointerTarget === button &&
                    (event.detail > 0 || Boolean(event.pointerType));
                if (event.type === "click") { pointercancel(); }
                if (!button) { return; }
                if (event.type === "keydown" && event.key !== "Enter" && event.key !== " ") { return; }
                event.preventDefault();
                if (event.repeat || frame !== document.getElementById("report-frame") || activeSession?.token !== token ||
                    !activeSession.valid || activeSession.needsSynchronization || paging || toggling || executing || closing) { return; }
                if (!/^[0-9a-f]{64}$/.test(button.dataset.portalToggle)) { return; }
                if (pointer) { button.dataset.portalPointerFocus = "true"; }
                else { button.removeAttribute("data-portal-pointer-focus"); }
                void changeReport(button.dataset.portalToggle, button.dataset.portalFocus);
            };
            const keydown = event => {
                pointerTarget = undefined;
                clearPointerFocus();
                if (!sessionEnded) { activate(event); }
            };
            // The callback belongs to the parent realm. Chromium permits these
            // listeners while the child still forbids all report-owned scripts.
            doc.addEventListener("click", activate);
            doc.addEventListener("keydown", keydown, true);
            doc.addEventListener("pointerdown", pointerdown, true);
            doc.addEventListener("pointercancel", pointercancel, true);
            doc.addEventListener("focusout", focusout, true);
            frame.dataset.toggleBridge = "installed";
            return { endSession: () => {
                sessionEnded = true;
                pointercancel();
                doc.removeEventListener("click", activate);
            }, remove: () => {
                doc.removeEventListener("click", activate);
                doc.removeEventListener("keydown", keydown, true);
                doc.removeEventListener("pointerdown", pointerdown, true);
                doc.removeEventListener("pointercancel", pointercancel, true);
                doc.removeEventListener("focusout", focusout, true);
                pointercancel();
                clearPointerFocus();
            } };
        };
        const positionInReport = focus => {
            const frame = document.getElementById("report-frame");
            const win = frame.contentWindow;
            const divs = [...frame.contentDocument.querySelectorAll("div")];
            return { focus: frame.contentDocument.activeElement?.dataset?.portalFocus || focus,
                hadFocusedToggle: Boolean(frame.contentDocument.activeElement?.dataset?.portalFocus),
                pointerFocus: frame.contentDocument.activeElement?.dataset?.portalPointerFocus === "true",
                restoreFocus: document.activeElement === frame || document.activeElement === synchronizeButton,
                x: win.scrollX, y: win.scrollY, outerX: window.scrollX, outerY: window.scrollY,
                scrolls: divs.map((node, index) => ({ index, top: node.scrollTop, left: node.scrollLeft })).filter(s => s.top || s.left).slice(0, 64) };
        };
        const restoreReportPosition = position => {
            const frame = document.getElementById("report-frame"), doc = frame.contentDocument;
            const divs = doc.querySelectorAll("div");
            for (const scroll of position.scrolls) {
                if (divs[scroll.index]) { divs[scroll.index].scrollTo(scroll.left, scroll.top); }
            }
            const target = position.focus && [...doc.querySelectorAll("button[data-portal-focus]")].find(n => n.dataset.portalFocus === position.focus);
            if (position.restoreFocus) {
                // Set before focus so the replacement never paints a pointer ring.
                if (target && position.pointerFocus) { target.dataset.portalPointerFocus = "true"; }
                (target || frame).focus({ preventScroll: true });
            }
            frame.contentWindow.scrollTo(position.x, position.y);
            window.scrollTo(position.outerX, position.outerY);
        };
        const outOfDate = error => {
            if (activeSession?.valid && error.code !== "session" && error.status !== 401 && error.status !== 403) {
                activeSession.needsSynchronization = true;
                const frame = document.getElementById("report-frame");
                frame.dataset.stale = "true";
                frame.title = "Previous report output — reload required";
                if (!["synchronize", "view-stale", "toggle-missing"].includes(error.code)) {
                    error.message += " The displayed output may be out of date. Reload report output before continuing.";
                }
            }
            expire(error);
        };
        const changeReport = async (action, focus) => {
            if (!activeSession?.valid || paging || executing || toggling || closing) { return; }
            const session = activeSession, position = positionInReport(focus);
            const revision = ++viewRevision;
            pageController?.abort();
            pageController = new AbortController();
            const signal = pageController.signal;
            toggling = true;
            controls();
            document.getElementById("diagnostic-details")?.remove();
            announce(action ? "Updating report group…" : "Reloading report output…", false, false);
            let frame;
            try {
                const state = await request("/api/executions/" + encodeURIComponent(session.token) +
                    (action ? "/toggle/" + encodeURIComponent(action) : "/synchronize"), {}, signal);
                if (revision !== viewRevision || signal.aborted) { return; }
                frame = await prepareFrame(state.viewerUrl, revision, signal, state.revision);
                if (revision !== viewRevision || signal.aborted) { frame.remove(); return; }
                // Preserve navigation performed while the old output was still
                // visible, including that control's current input modality. Its
                // own marker carries the activation intent across the request;
                // later keyboard/focus changes supersede it without global state.
                // An explicit departure to the report body should restore frame
                // focus, rather than reviving the original expander. Retain the
                // fallback for activations that began without a focused toggle.
                const latestPosition = positionInReport(position.hadFocusedToggle ? undefined : position.focus);
                display(frame, { ...session, ...state });
                restoreReportPosition(latestPosition);
                announce(action ? "Report group updated." : "Report output synchronized.", false, false);
            } catch (error) {
                frame?.remove();
                if (revision === viewRevision) { outOfDate(error); }
            } finally {
                if (revision === viewRevision) { toggling = false; controls(); }
            }
        };
        synchronizeButton.addEventListener("click", () => changeReport(null));
        window.addEventListener("pagehide", () => { interactionBridge?.remove(); pageController?.abort(); });
        const expire = error => {
            if (error.code === "session" && activeSession) {
                // End activation immediately. The retained expired output still
                // needs visual focus handling until its view is replaced/closed.
                interactionBridge?.endSession();
                activeSession.valid = false;
                remember(null);
            }
            showFailure(error);
        };
        const go = async target => {
            if (!activeSession?.valid || activeSession.needsSynchronization || paging || toggling || executing || closing) { return; }
            const session = activeSession;
            const revision = ++viewRevision;
            pageController?.abort();
            pageController = new AbortController();
            paging = true;
            controls();
            document.getElementById("diagnostic-details")?.remove();
            announce("Loading page " + target + "…", false, false);
            try {
                const state = await request("/api/executions/" + encodeURIComponent(session.token) + "/page/" + target, {}, pageController.signal);
                if (revision !== viewRevision) { return; }
                const frame = await prepareFrame(state.viewerUrl, revision, pageController.signal, state.revision);
                display(frame, { ...session, ...state });
                announce("Page " + state.page + " loaded.", false, false);
            } catch (error) {
                if (revision === viewRevision && error.name !== "AbortError") { outOfDate(error); }
            } finally {
                if (revision === viewRevision) { paging = false; controls(); }
            }
        };
        buttons[0].addEventListener("click", () => go(1));
        buttons[1].addEventListener("click", () => go(activeSession.page - 1));
        buttons[2].addEventListener("click", () => go(activeSession.page + 1));
        buttons[3].addEventListener("click", () => { if (activeSession.pages !== null) { go(activeSession.pages); } });
        closeButton.addEventListener("click", async () => {
            if (closing || committing) { return; }
            closing = true;
            ++executionRevision; ++viewRevision;
            executionController?.abort(); pageController?.abort();
            controls();
            announce("Closing report…", false, false);
            try {
                for (const token of pendingSessions) { await release(token); }
                await release(activeSession?.token);
                await release(activeSession?.sourceToken);
                window.location.assign(report.dataset.folderUrl);
            } catch (error) { closing = false; executing = paging = toggling = false; outOfDate(error); controls(); }
        });
        const setParametersExpanded = expanded => {
            if (!expanded) { activeDropdown?.close(false); if (activeDropdown) { return; } }
            toggle.setAttribute("aria-expanded", String(expanded));
            toggle.setAttribute("aria-label", expanded ? "Hide parameters" : "Show parameters");
            presentParameters();
        };
        toggle.addEventListener("click", () => setParametersExpanded(!parametersExpanded()));
        // Native validation runs before submit and must never focus a hidden input.
        form.addEventListener("invalid", () => {
            setParametersExpanded(true);
            announce("Review the highlighted inputs before selecting View report.", true);
        }, true);
        const revealInvalidParameters = () => {
            if (!parametersExpanded()) {
                setParametersExpanded(true);
                parameterControls.querySelector('[aria-invalid="true"]:not(:disabled)')?.focus();
            }
        };
        const pendingChanges = new Set();
        const choicesCache = new Map();
        const cleanups = new Set();
        const disposeChoices = () => {
            // Teardown is deliberately different from a user dismissal: never
            // recommit an obsolete selection during refresh, draw or pagehide.
            activeDropdown?.dispose();
            for (const cleanup of cleanups) { cleanup(); }
            cleanups.clear();
        };
        const handlesDropdownClose = target => target?.closest?.(".choice-trigger,#run,#toggle-parameters,#copy-report-link");
        let dropdownPointerTarget;
        document.addEventListener("pointerdown", event => {
            dropdownPointerTarget = handlesDropdownClose(event.target);
            // These commands close explicitly, then wait for resolution. Closing
            // on their pointerdown would disable/remove the click target first.
            if (!activeDropdown?.group.contains(event.target) && !handlesDropdownClose(event.target)) {
                ++dropdownIntent;
                activeDropdown?.close(false);
            }
        });
        document.addEventListener("pointerup", () => { dropdownPointerTarget = null; });
        document.addEventListener("pointercancel", () => { dropdownPointerTarget = null; });
        window.addEventListener("pagehide", () => {
            ++version; ++executionRevision; ++viewRevision;
            disposeChoices(); controller?.abort(); executionController?.abort(); pageController?.abort();
        });
        window.addEventListener("pageshow", event => { if (event.persisted) { window.location.reload(); } });
        const busy = on => {
            parameterBusy = on;
            form.dataset.resolving = String(on);
            controls();
        };
        const refresh = (initial = false) => parameterResolution = resolveParameters(initial);
        const settleParameters = async () => {
            let current;
            do { current = parameterResolution; await current; } while (current !== parameterResolution);
        };
        const resolveParameters = async initial => {
            const revision = ++version;
            const subscriptionContext = subscriptionInputs;
            if (subscriptionInputs) { subscriptionInputs.failure = null; }
            canRun = false;
            disposeChoices();
            controller?.abort();
            controller = new AbortController();
            busy(true);
            document.getElementById("diagnostic-details")?.remove();
            announce(initial ? "Loading report inputs…" : "Updating report inputs…", false, false);
            try {
                let result;
                if (linkInitialization) {
                    linkInitialization = false;
                    const link = await request("/api/reports/" + id + "/link-parameters", { query: entryQuery }, controller.signal);
                    result = link.form;
                    noExecute = link.noExecute;
                    autoExecute = link.hasParameters && !link.noExecute && !result.linkErrors?.length;
                } else if (subscriptionInputs) {
                    result = await request("/api/subscriptions/parameters", {
                        name: "Input preview", reportPath: report.dataset.reportPath, recipients: { to: [], cc: [], bcc: [] }, delivery: "attachment", format: "pdf",
                        schedule: { kind: "once", startLocal: "2026-01-01T00:00:00" }, parameters: subscriptionSelection()
                    }, controller.signal);
                } else {
                    result = await request("/api/reports/" + id + "/parameters", { values, initial, changed: [...pendingChanges], defaulted }, controller.signal);
                }
                if (revision !== version) { return; }
                fields = result.fields;
                inputsLoaded = true;
                linkErrors = result.linkErrors || [];
                values = result.values;
                defaulted = result.defaulted;
                pendingChanges.clear();
                await draw(result);
                if (revision !== version) { return; }
                // Keep a useful live announcement without a routine visible banner.
                if (!result.canRun && !subscriptionInputs) { revealInvalidParameters(); }
                if (!executing && !paging) { announce(result.canRun ? "Report inputs updated." : "Review the highlighted inputs before selecting View report.", false, !result.canRun); }
                canRun = result.canRun;
                if (linkErrors.length) { announce(linkErrors.join(" "), true); }
            } catch (error) {
                if (revision === version && error.name !== "AbortError") { showFailure(error); }
                if (revision === version && subscriptionInputs && subscriptionInputs === subscriptionContext && error.name !== "AbortError") {
                    subscriptionInputs.failure = error.message; report.dispatchEvent(new CustomEvent("portal:inputs-error", { detail: error.message }));
                }
            } finally {
                if (revision === version) { busy(false); }
            }
        };
        copyLink.addEventListener("click", async () => {
            activeDropdown?.close(false);
            ++dropdownIntent;
            if (activeDropdown) { return; }
            linkFeedback.textContent = "";
            try {
                await settleParameters();
                if (parameterBusy || !canRun || linkErrors.length || !form.reportValidity()) {
                    throw new Error("Review the report inputs before copying a link.");
                }
                const url = new URL(canonicalUrl, window.location.origin);
                for (const field of fields) {
                    const selected = values[field.name];
                    if (!selected?.length || selected.some(v => v === null) && (selected.length !== 1 || !field.nullable) ||
                        field.name.includes(":") || field.name.startsWith("portal:")) {
                        throw new Error("These input selections cannot be represented in a report link.");
                    }
                    if (selected[0] === null) { url.searchParams.append(field.name + ":isnull", "true"); }
                    else { for (const value of selected) { url.searchParams.append(field.name, value); } }
                }
                if (fields.length && noExecute) { url.searchParams.append("portal:noExecute", "true"); }
                if (new TextEncoder().encode(JSON.stringify({ query: url.search })).length > 224 * 1024) {
                    throw new Error("These selections exceed the report link size limit. Select fewer values before copying.");
                }
                if (!navigator.clipboard?.writeText) { throw new Error("Clipboard access is unavailable. Allow clipboard access in your browser and try again."); }
                await navigator.clipboard.writeText(url.href);
                linkFeedback.textContent = "Report link copied.";
            } catch (error) {
                linkFeedback.textContent = error.message || "The report link could not be copied. Allow clipboard access and try again.";
            }
        });
        const getChoices = async (field, signal) => {
            const cached = choicesCache.get(field.name);
            if (cached?.key === field.optionsKey) { return cached.options; }
            const found = await request("/api/reports/" + id + "/options/" + encodeURIComponent(field.name), { values, defaulted }, signal);
            if (signal.aborted) { throw new DOMException("Cancelled", "AbortError"); }
            if (found.optionsKey !== field.optionsKey) {
                // Do not loop if a query keeps changing between requests, or mix
                // a newly returned collection with an older parent/default context.
                const error = new Error("The available choices changed. Refresh the report inputs and try again.");
                error.choicesChanged = true;
                throw error;
            }
            choicesCache.set(field.name, { key: field.optionsKey, options: found.options });
            return found.options;
        };
        // Both controls use the same overlay geometry without changing field widths.
        const watchChoicePopup = (control, popup, isOpen) => {
            const position = () => {
                if (!isOpen()) { return; }
                const rect = control.getBoundingClientRect(), viewport = window.visualViewport;
                const top = viewport?.offsetTop || 0, left = viewport?.offsetLeft || 0;
                const height = viewport?.height || innerHeight, width = viewport?.width || innerWidth;
                const anchorTop = Math.max(top + 8, Math.min(rect.top, top + height - 8));
                const anchorBottom = Math.max(top + 8, Math.min(rect.bottom, top + height - 8));
                const below = top + height - anchorBottom - 8, above = anchorTop - top - 8;
                const popupWidth = Math.min(Math.max(rect.width, 360), width - 16);
                const popupLeft = Math.max(left + 8, Math.min(rect.left, left + width - popupWidth - 8));
                let down = below >= Math.min(400, height * .7) || below >= above;
                let popupTop = anchorBottom + 4, available = down ? below : above;
                const tab = toggle.getBoundingClientRect();
                // Keep both the choices and the separator target unobstructed.
                // Reuse the existing overlay positioning; field layout is unchanged.
                if (down && popupLeft < tab.right && popupLeft + popupWidth > tab.left
                    && popupTop < tab.bottom && popupTop + Math.min(440, height * .75, below) > tab.top) {
                    const afterTab = top + height - tab.bottom - 12;
                    if (afterTab >= above) { popupTop = tab.bottom + 4; available = afterTab; }
                    else { down = false; available = above; }
                }
                popup.style.width = popupWidth + "px";
                popup.style.left = popupLeft + "px";
                popup.style.top = down ? popupTop + "px" : "auto";
                popup.style.bottom = down ? "auto" : innerHeight - anchorTop + 4 + "px";
                popup.style.maxHeight = Math.max(0, Math.min(440, height * .75, available)) + "px";
            };
            window.addEventListener("resize", position);
            window.addEventListener("scroll", position, true);
            window.visualViewport?.addEventListener("resize", position);
            window.visualViewport?.addEventListener("scroll", position);
            cleanups.add(() => {
                window.removeEventListener("resize", position);
                window.removeEventListener("scroll", position, true);
                window.visualViewport?.removeEventListener("resize", position);
                window.visualViewport?.removeEventListener("scroll", position);
            });
            return position;
        };
        const dismissOnFocusOut = (group, isOpen, close) => {
            group.addEventListener("focusout", event => {
                const check = target => {
                    if (isOpen() && !group.contains(target) && target !== dropdownPointerTarget) { close(false); }
                };
                if (event.relatedTarget) { check(event.relatedTarget); }
                else { queueMicrotask(() => check(document.activeElement)); }
            });
        };
        const draw = async result => {
            for (const [name, cached] of choicesCache) {
                if (!result.fields.some(f => f.name === name && f.optionsKey === cached.key)) { choicesCache.delete(name); }
            }
            const drawVersion = version;
            const active = document.activeElement?.id;
            disposeChoices();
            dropdownOpeners.clear();
            const root = document.getElementById("fields");
            root.replaceChildren();
            for (const [index, field] of result.fields.entries()) {
                const prompt = field.prompt || field.name;
                const controlId = "param-" + index;
                const helpId = controlId + "-help";
                const errorId = controlId + "-error";
                const group = el("div", null, { class: "parameter-field" });
                const label = el("label", prompt, { for: controlId, id: controlId + "-label" });
                group.append(label);
                const help = el("p", "", { id: helpId, class: "field-help" });
                const selected = () => values[field.name] || [];
                const update = nextValues => {
                    values[field.name] = nextValues;
                    defaulted = defaulted.filter(name => name !== field.name);
                    pendingChanges.add(field.name);
                    dirty();
                    refresh();
                };
                let control;
                group.classList.add("parameter-" + field.width);
                if (field.hasOptions && !field.multiValue) {
                    group.classList.add("option-select", "single-select");
                    const summary = el("span", selected().length
                        ? field.selectedOptions.find(o => o.value === selected()[0])?.label
                            ?? (selected()[0] === null ? "No value" : String(selected()[0]))
                        : "Select " + prompt + "…", { id: controlId + "-summary" });
                    control = el("button", null, { id: controlId, type: "button", class: "choice-trigger single-trigger",
                        "aria-haspopup": "listbox", "aria-expanded": "false", "aria-controls": controlId + "-choices",
                        "aria-labelledby": label.id + " " + summary.id });
                    control.title = summary.textContent;
                    control.append(summary, el("span", "⌄", { "aria-hidden": "true" }));
                    const popup = el("div", null, { class: "choice-popup single-popup" });
                    popup.hidden = true;
                    // Search is a sibling of the listbox: only options belong inside it.
                    const search = el("input", null, { type: "search", maxlength: "100", placeholder: "Search " + prompt + "…", "aria-label": "Search " + prompt });
                    const list = el("div", null, { id: controlId + "-choices", class: "choice-list single-choices",
                        role: "listbox", tabindex: "0", "aria-label": prompt + " choices" });
                    list.hidden = true;
                    const loading = el("p", "", { class: "field-help", role: "status", "aria-live": "polite", "aria-atomic": "true" });
                    const retry = el("button", "Retry choices", { type: "button", class: "quiet" });
                    retry.hidden = true;
                    popup.append(search, list, loading, retry);
                    group.append(control, popup);
                    let opened = false, items, rows, visible = [], activeRow, optionController, revision = 0, choicesChanged = false;
                    const position = watchChoicePopup(control, popup, () => opened);
                    const sameValue = next => selected().length === next.length && selected()[0] === next[0];
                    const activate = (entry, scroll = true) => {
                        activeRow?.row.classList.remove("is-active");
                        activeRow = entry;
                        if (entry) {
                            entry.row.classList.add("is-active");
                            list.setAttribute("aria-activedescendant", entry.row.id);
                            if (scroll) { entry.row.scrollIntoView({ block: "nearest" }); }
                        } else { list.removeAttribute("aria-activedescendant"); }
                    };
                    const close = focus => {
                        if (!opened) { return; }
                        opened = false;
                        ++dropdownIntent;
                        ++revision;
                        optionController?.abort();
                        popup.hidden = true;
                        control.setAttribute("aria-expanded", "false");
                        if (activeDropdown?.group === group) { activeDropdown = null; }
                        // Outside pointer/focus dismissal never takes focus from its target.
                        if (focus && control.isConnected) { control.focus(); }
                    };
                    cleanups.add(() => close(false));
                    const choose = entry => {
                        if (!opened || !items || parameterBusy || drawVersion !== version || !entry || entry.row.hidden) { return; }
                        const changed = !sameValue(entry.next);
                        if (changed) { summary.textContent = entry.label; control.title = entry.label; }
                        close(true);
                        if (changed) { update(entry.next); }
                    };
                    const filter = () => {
                        const query = search.value.toLowerCase();
                        visible = [];
                        for (const entry of rows || []) {
                            const match = entry.searchLabel.includes(query);
                            if (entry.row.hidden === match) { entry.row.hidden = !match; }
                            if (match) { visible.push(entry); }
                        }
                        activate(visible.find(entry => sameValue(entry.next)) || visible[0], false);
                        if (!visible.length && document.activeElement === list) { search.focus(); }
                        list.hidden = !visible.length;
                        list.scrollTop = 0;
                        loading.textContent = visible.length ? "" : "No matching choices.";
                    };
                    const showOptions = () => {
                        if (!rows) {
                            const fragment = document.createDocumentFragment();
                            // Preserve the existing explicit empty selection contract ([]),
                            // separately from an SSRS blank string or true null choice.
                            rows = [{ label: "Select " + prompt + "…", next: [] }, ...items.map(item => ({ label: item.label, next: [item.value] }))]
                                .map((entry, index) => {
                                    const row = el("div", entry.label, { id: controlId + "-option-" + index, class: "single-choice",
                                        role: "option", "aria-selected": String(sameValue(entry.next)) });
                                    const found = { ...entry, row, searchLabel: entry.label.toLowerCase() };
                                    row.addEventListener("click", () => choose(found));
                                    fragment.append(row);
                                    return found;
                                });
                            list.replaceChildren(fragment);
                        }
                        filter();
                        activate(activeRow);
                    };
                    const load = async focusChoices => {
                        const current = ++revision;
                        optionController?.abort();
                        optionController = new AbortController();
                        list.setAttribute("aria-busy", "true");
                        loading.textContent = "Loading choices…";
                        if (document.activeElement === retry) { search.focus(); }
                        retry.hidden = true;
                        try {
                            const found = await getChoices(field, optionController.signal);
                            if (!opened || !group.isConnected || current !== revision) { return; }
                            items = found;
                            showOptions();
                            if (focusChoices && document.activeElement === search) { list.focus(); }
                        } catch (error) {
                            if (opened && group.isConnected && current === revision && error.name !== "AbortError") {
                                loading.textContent = "Unable to load choices. Retry.";
                                choicesChanged = Boolean(error.choicesChanged);
                                retry.textContent = choicesChanged ? "Refresh inputs" : "Retry choices";
                                retry.hidden = false;
                                showFailure(error);
                            }
                        } finally { if (opened && current === revision) { list.setAttribute("aria-busy", "false"); } }
                    };
                    const open = async focusChoices => {
                        activeDropdown?.close(false);
                        if (activeDropdown) { return; }
                        const intent = ++dropdownIntent;
                        await settleParameters();
                        if (intent !== dropdownIntent) { return; }
                        if (!control.isConnected) { dropdownOpeners.get(field.name)?.(focusChoices); return; }
                        if (parameterBusy || executing || closing || control.disabled) { return; }
                        if (drawVersion !== version) {
                            // A failed form refresh leaves old controls visible. Recover
                            // their context before allowing a choice from that old form.
                            await refresh();
                            if (intent === dropdownIntent && !control.isConnected) { dropdownOpeners.get(field.name)?.(focusChoices); }
                            return;
                        }
                        opened = true;
                        search.value = "";
                        popup.hidden = false;
                        control.setAttribute("aria-expanded", "true");
                        activeDropdown = { group, close, dispose: () => close(popup.contains(document.activeElement)), hasValidEdits: () => false };
                        position();
                        search.focus();
                        load(focusChoices);
                    };
                    dropdownOpeners.set(field.name, open);
                    control.addEventListener("click", () => opened ? close(true) : open(false));
                    control.addEventListener("keydown", event => {
                        if (event.key === "ArrowDown") { event.preventDefault(); if (!opened) { open(true); } else { list.focus(); } }
                    });
                    group.addEventListener("keydown", event => {
                        if (event.key === "Escape" && opened) { event.preventDefault(); event.stopPropagation(); close(true); }
                        if (event.key === "Enter" && opened && event.target.tagName !== "BUTTON") { event.preventDefault(); }
                    });
                    dismissOnFocusOut(group, () => opened, close);
                    search.addEventListener("input", () => { if (items) { filter(); } });
                    search.addEventListener("keydown", event => {
                        if (event.key === "ArrowDown") { event.preventDefault(); if (visible.length) { list.focus(); activate(activeRow); } }
                    });
                    list.addEventListener("keydown", event => {
                        const index = visible.indexOf(activeRow);
                        const target = event.key === "ArrowDown" ? Math.min(index + 1, visible.length - 1)
                            : event.key === "ArrowUp" ? Math.max(index - 1, 0) : event.key === "Home" ? 0 : event.key === "End" ? visible.length - 1 : undefined;
                        if (target !== undefined) { event.preventDefault(); activate(visible[target]); }
                        if (event.key === "Enter" || event.key === " ") { event.preventDefault(); choose(activeRow); }
                    });
                    retry.addEventListener("click", () => {
                        if (choicesChanged) { close(true); refresh(); }
                        else { load(false); }
                    });
                } else if (field.hasOptions) {
                    group.classList.add("option-select", "multi-select");
                    const summary = el("span", "", { id: controlId + "-summary", "aria-hidden": "true" });
                    control = el("button", null, {
                        id: controlId, type: "button", class: "choice-trigger multi-trigger", "aria-expanded": "false",
                        "aria-controls": controlId + "-popup"
                    });
                    control.append(summary, el("span", "⌄", { "aria-hidden": "true" }));
                    const popup = el("div", null, { id: controlId + "-popup", class: "choice-popup checkbox-popup", role: "group", "aria-labelledby": label.id });
                    popup.hidden = true;
                    const search = el("input", null, { type: "search", maxlength: "100", placeholder: "Search " + prompt + "…", "aria-label": "Search " + prompt });
                    const all = el("input", null, { type: "checkbox", id: controlId + "-all" });
                    const allRow = el("label", null, { for: all.id, class: "checkbox-choice select-all" });
                    allRow.append(all, el("span", "(Select All)"));
                    const retry = el("button", "Retry choices", { type: "button", class: "quiet" });
                    retry.hidden = true;
                    const list = el("div", null, { class: "choice-list checkbox-choices" });
                    const loading = el("p", "", { class: "field-help", role: "status", "aria-live": "polite", "aria-atomic": "true" });
                    const feedback = el("p", "", { class: "field-error", role: "alert", "aria-atomic": "true" });
                    popup.append(search, list, loading, feedback, retry);
                    group.append(control, popup);
                    let draft = new Set(selected()), original, opened = false, items, itemValues, optionController, revision = 0, choicesChanged = false;
                    let choiceRows, visibleChoices = [all];
                    let draftBytes = 0, baseBytes = 0, localChanged = false;
                    const encoder = new TextEncoder(), byteCosts = new Map();
                    const valueBytes = value => {
                        if (!byteCosts.has(value)) { byteCosts.set(value, encoder.encode(JSON.stringify(value)).length + 1); }
                        return byteCosts.get(value);
                    };
                    const sumBytes = selection => { let bytes = 0; for (const value of selection) { bytes += valueBytes(value); } return bytes; };
                    const requestBody = selection => ({ values: { ...values, [field.name]: selection }, initial: false,
                        changed: [...new Set([...pendingChanges, field.name])], defaulted: defaulted.filter(name => name !== field.name) });
                    const selectionError = (size = draft.size, bytes = draftBytes) => size > field.selectionLimit
                        ? "Choose no more than " + field.selectionLimit + " values. Deselect choices to continue."
                        : baseBytes + bytes - (size ? 1 : 0) > 224 * 1024
                            ? "The selection exceeds the supported request size. Select fewer choices to continue." : "";
                    function* displayOptions() {
                        yield* (items || field.selectedOptions);
                        // SSRS permits a typed null that is not a listed choice.
                        if (items) { for (const item of field.selectedOptions) { if (!itemValues.has(item.value)) { yield item; } } }
                    }
                    const syncSummary = () => {
                        // Keep only enough ordered text to overflow the actual control.
                        // CSS handles pixel truncation; resize can expose more labels.
                        const budget = Math.max(256, Math.ceil(control.clientWidth * 2));
                        let text = "", count = 0;
                        for (const item of displayOptions()) {
                            if (!draft.has(item.value)) { continue; }
                            const labelText = item.label;
                            const remaining = budget - text.length;
                            text += (text ? ", " : "") + (draft.size === 1 ? labelText : labelText.slice(0, Math.max(0, remaining)));
                            count++;
                            if (text.length >= budget && count < draft.size) { text += "…"; break; }
                        }
                        // A nullable value can exist outside the valid-value list.
                        if (!text && draft.size) { text = field.selectedOptions.find(o => draft.has(o.value))?.label || "No value"; }
                        summary.textContent = text || "Select " + prompt;
                        control.title = text.slice(0, 320);
                        control.setAttribute("aria-label", prompt + ", multi-select, " + draft.size + " values selected. Open to review or change choices.");
                    };
                    const summaryObserver = new ResizeObserver(syncSummary);
                    summaryObserver.observe(control);
                    syncSummary();
                    const position = watchChoicePopup(control, popup, () => opened);
                    const close = (focus, commit = true) => {
                        if (!opened) { return; }
                        const changed = commit && items && (draft.size !== original.size || [...draft].some(value => !original.has(value)));
                        let committed;
                        if (changed) {
                            // Validate the current context once more at commit;
                            // individual clicks only adjust cached byte costs.
                            baseBytes = encoder.encode(JSON.stringify(requestBody([]))).length;
                            if (selectionError()) { feedback.textContent = selectionError(); return; }
                            committed = [...new Set(items.filter(item => draft.has(item.value)).map(item => item.value))];
                            const available = new Set(committed);
                            for (const value of draft) { if (!available.has(value)) { committed.push(value); } }
                        }
                        const hadFocus = popup.contains(document.activeElement);
                        revision++;
                        optionController?.abort();
                        opened = false;
                        ++dropdownIntent;
                        popup.hidden = true;
                        control.setAttribute("aria-expanded", "false");
                        if (activeDropdown?.group === group) { activeDropdown = null; }
                        if ((focus || hadFocus) && control.isConnected) { control.focus(); }
                        if (changed) {
                            // Preserve SSRS collection order and typed values. The
                            // pseudo-option has no optionValue and never enters draft.
                            update(committed);
                        } else { controls(); }
                    };
                    cleanups.add(() => {
                        close(false, false);
                        summaryObserver.disconnect();
                        if (dropdownOpeners.get(field.name) === open) { dropdownOpeners.delete(field.name); }
                    });
                    const sync = (bulk = false) => {
                        if (bulk) { for (const choice of list.querySelectorAll("input[data-option]")) { choice.checked = draft.has(choice.optionValue); } }
                        all.disabled = !items;
                        let selectedCount = 0;
                        if (items) { for (const item of items) { if (draft.has(item.value)) { selectedCount++; } } }
                        all.checked = Boolean(items?.length) && selectedCount === items.length;
                        all.indeterminate = selectedCount > 0 && selectedCount < items.length;
                        syncSummary();
                        controls();
                    };
                    const showOptions = () => {
                        if (!items) { return; }
                        if (!choiceRows) {
                            const fragment = document.createDocumentFragment();
                            fragment.append(allRow);
                            choiceRows = items.map((item, optionIndex) => {
                                const choice = el("input", null, { type: "checkbox", id: controlId + "-choice-" + optionIndex, "data-option": "" });
                                choice.optionValue = item.value;
                                choice.checked = draft.has(item.value);
                                choice.addEventListener("change", () => {
                                    feedback.textContent = "";
                                    const bytes = valueBytes(item.value);
                                    if (choice.checked) {
                                        const error = selectionError(draft.size + 1, draftBytes + bytes);
                                        if (error || item.value?.length > 2048) {
                                            choice.checked = false;
                                            feedback.textContent = error || "This choice exceeds the supported value size. Choose another value.";
                                            return;
                                        }
                                        draft.add(item.value); draftBytes += bytes;
                                    } else { draft.delete(item.value); draftBytes -= bytes; }
                                    localChanged = true;
                                    sync();
                                });
                                const row = el("label", null, { for: choice.id, class: "checkbox-choice" });
                                row.append(choice, el("span", item.label));
                                fragment.append(row);
                                return { row, choice, label: item.label.toLowerCase() };
                            });
                            list.replaceChildren(fragment);
                        }
                        // Retain native controls/listeners across searches. Hiding
                        // rows avoids rebuilding thousands of checked inputs and
                        // keeps their identity and selection state intact.
                        const query = search.value.toLowerCase();
                        visibleChoices = [all];
                        for (const item of choiceRows) {
                            const matching = item.label.includes(query);
                            if (!matching && item.row.contains(document.activeElement)) { search.focus(); }
                            if (item.row.hidden === matching) { item.row.hidden = !matching; }
                            if (matching) { visibleChoices.push(item.choice); }
                        }
                        list.scrollTop = 0;
                        loading.textContent = visibleChoices.length === 1 ? "No matching choices." : "";
                        sync();
                    };
                    const load = async focusChoices => {
                        const current = ++revision;
                        optionController?.abort();
                        optionController = new AbortController();
                        list.setAttribute("aria-busy", "true");
                        loading.textContent = "Loading choices…";
                        feedback.textContent = "";
                        // Hiding a focused Retry button otherwise emits a null
                        // focusout and closes/aborts its own replacement load.
                        if (document.activeElement === retry) { search.focus(); }
                        retry.hidden = true;
                        sync();
                        try {
                            const found = await getChoices(field, optionController.signal);
                            if (!opened || !group.isConnected || current !== revision) { return; }
                            itemValues = new Set();
                            items = found.filter(item => {
                                if (itemValues.has(item.value)) { return false; }
                                itemValues.add(item.value); return true;
                            });
                            showOptions();
                            sync(true);
                            if (focusChoices && document.activeElement === search) { all.focus(); }
                        } catch (error) {
                            if (opened && group.isConnected && current === revision && error.name !== "AbortError") {
                                feedback.textContent = error.message;
                                choicesChanged = Boolean(error.choicesChanged);
                                retry.textContent = choicesChanged ? "Refresh inputs" : "Retry choices";
                                loading.textContent = "";
                                retry.hidden = false;
                                showFailure(error);
                            }
                        } finally {
                            if (opened && current === revision) { list.setAttribute("aria-busy", "false"); sync(); }
                        }
                    };
                    const open = async focusChoices => {
                        activeDropdown?.close(false);
                        if (activeDropdown) { return; }
                        const intent = ++dropdownIntent;
                        await settleParameters();
                        if (intent !== dropdownIntent) { return; }
                        if (!control.isConnected) { dropdownOpeners.get(field.name)?.(focusChoices); return; }
                        if (parameterBusy || executing || closing) { return; }
                        opened = true;
                        draft = new Set(selected());
                        original = new Set(draft);
                        draftBytes = sumBytes(draft);
                        baseBytes = encoder.encode(JSON.stringify(requestBody([]))).length;
                        localChanged = false;
                        search.value = "";
                        feedback.textContent = "";
                        popup.hidden = false;
                        control.setAttribute("aria-expanded", "true");
                        activeDropdown = { group, close, dispose: () => close(false, false),
                            hasValidEdits: () => localChanged && !selectionError() };
                        position();
                        search.focus();
                        load(focusChoices);
                    };
                    dropdownOpeners.set(field.name, open);
                    control.addEventListener("click", () => opened ? close(true) : open(false));
                    control.addEventListener("keydown", event => {
                        if (event.key === "ArrowDown") { event.preventDefault(); if (!opened) { open(true); } else { list.querySelector("input")?.focus(); } }
                    });
                    group.addEventListener("keydown", event => {
                        if (event.key === "Escape" && opened) { event.preventDefault(); event.stopPropagation(); close(true); }
                        if (event.key === "Enter" && opened && event.target.tagName !== "BUTTON") { event.preventDefault(); }
                    });
                    dismissOnFocusOut(group, () => opened, close);
                    list.addEventListener("mousedown", event => {
                        const row = event.target.closest(".checkbox-choice");
                        // Keep focus inside until native label click activation.
                        // Otherwise mousedown on text/padding blurs to the body,
                        // and focusout dismisses the popup before click can run.
                        if (event.button === 0 && row && event.target !== row.control) { event.preventDefault(); }
                    });
                    list.addEventListener("keydown", event => {
                        const choices = visibleChoices;
                        const index = choices.indexOf(document.activeElement);
                        if (index < 0) { return; }
                        const target = event.key === "ArrowDown" ? Math.min(index + 1, choices.length - 1)
                            : event.key === "ArrowUp" ? Math.max(index - 1, 0) : event.key === "Home" ? 0 : event.key === "End" ? choices.length - 1 : undefined;
                        if (target !== undefined) { event.preventDefault(); choices[target]?.focus(); }
                    });
                    search.addEventListener("keydown", event => {
                        if (event.key === "ArrowDown") { event.preventDefault(); if (!all.disabled) { all.focus(); } }
                    });
                    search.addEventListener("input", showOptions);
                    all.addEventListener("change", () => {
                        if (!items) { return; }
                        feedback.textContent = "";
                        const next = all.checked ? new Set(items.map(o => o.value)) : new Set();
                        const bytes = sumBytes(next), error = selectionError(next.size, bytes);
                        if (error || [...next].some(value => value?.length > 2048)) {
                            feedback.textContent = error || "A choice exceeds the supported value size. Select individual choices instead.";
                            sync(); return;
                        }
                        draft = next; draftBytes = bytes; localChanged = true;
                        sync(true);
                    });
                    retry.addEventListener("click", () => {
                        if (choicesChanged) { close(true); refresh(); }
                        else { load(false); }
                    });
                } else if (field.multiValue) {
                    control = el("textarea", null, { id: controlId, rows: "4" });
                    control.value = selected().map(value => value ?? "").join("\n");
                    help.textContent = "Enter one value per line.";
                    control.addEventListener("change", () => update(control.value === "" && !(field.type === "String" && field.allowBlank) ? [] : control.value.split(/\r?\n/)));
                    group.append(control);
                } else if (field.type === "Boolean") {
                    control = el("select", null, { id: controlId });
                    for (const [value, labelText] of [["", "Choose…"], ["true", "Yes"], ["false", "No"]]) {
                        const option = el("option", labelText, { value });
                        option.selected = selected()[0] === value;
                        control.append(option);
                    }
                    control.addEventListener("change", () => update(control.value ? [control.value] : []));
                    group.append(control);
                } else {
                    const dateValue = selected()[0] ?? "";
                    // Native datetime-local cannot represent offsets or sub-millisecond precision.
                    const preciseDate = field.type === "DateTime" && (/(Z|[+-]\d{2}:\d{2})$/.test(dateValue) || /\.\d{4,}/.test(dateValue));
                    control = el("input", null, { id: controlId, type: field.type === "DateTime" && !preciseDate ? "datetime-local" : field.type === "Integer" || field.type === "Float" ? "number" : "text", maxlength: "2048" });
                    if (field.type === "Float" || field.type === "DateTime") { control.step = "any"; }
                    control.value = selected()[0] ?? "";
                    if (preciseDate) { group.classList.replace("parameter-date", "parameter-date-precise"); help.textContent = "Enter an ISO date and time, preserving the precision and timezone shown."; }
                    control.addEventListener("change", () => update(control.value === "" && !(field.type === "String" && field.allowBlank) ? [] : [control.value]));
                    group.append(control);
                }
                if (control.tagName === "TEXTAREA" || (control.tagName === "INPUT" && control.type !== "checkbox")) {
                    // Preserve in-progress typing if an earlier dependency refresh
                    // returns before this field's change/blur event is committed.
                    control.addEventListener("input", () => {
                        values[field.name] = field.multiValue ? (control.value === "" && !field.allowBlank ? [] : control.value.split(/\r?\n/))
                            : control.value === "" && !(field.type === "String" && field.allowBlank) ? [] : [control.value];
                        defaulted = defaulted.filter(name => name !== field.name);
                        pendingChanges.add(field.name);
                        ++version;
                        controller?.abort();
                        busy(true);
                    });
                }
                const descriptions = [];
                if (help.textContent) { group.append(help); descriptions.push(helpId); }
                if (field.error) { descriptions.push(errorId); }
                if (descriptions.length) { control.setAttribute("aria-describedby", descriptions.join(" ")); }
                else { control.removeAttribute("aria-describedby"); }
                control.setAttribute("aria-invalid", String(Boolean(field.error)));
                if (field.nullable) {
                    const nullable = el("input", null, { type: "checkbox", id: controlId + "-null", "aria-label": prompt + ": No value" });
                    nullable.checked = selected().length === 1 && selected()[0] === null;
                    control.disabled = nullable.checked || control.getAttribute("aria-busy") === "true";
                    nullable.addEventListener("change", () => update(nullable.checked ? [null] : []));
                    if (descriptions.length) { nullable.setAttribute("aria-describedby", descriptions.join(" ")); }
                    const nullLabel = el("label", " No value", { for: nullable.id, class: "null-label" });
                    nullLabel.prepend(nullable);
                    const labelRow = el("div", null, { class: "parameter-label-row" });
                    label.before(labelRow);
                    labelRow.append(label, nullLabel);
                }
                if (subscriptionInputs) {
                    const useDefault = el("input", null, { id: controlId + "-default", type: "checkbox", "aria-label": "Use default for " + prompt });
                    useDefault.checked = subscriptionInputs.defaults.has(field.name);
                    if (useDefault.checked) { for (const editor of group.querySelectorAll("input,select,textarea,button")) { editor.disabled = true; } }
                    const defaultLabel = el("label", " Use default", { for: useDefault.id, class: "null-label subscription-default" });
                    defaultLabel.prepend(useDefault);
                    let row = group.querySelector(".parameter-label-row");
                    if (!row) { row = el("div", null, { class: "parameter-label-row" }); label.before(row); row.append(label); }
                    row.append(defaultLabel);
                    useDefault.addEventListener("change", () => {
                        if (useDefault.checked) { subscriptionInputs.explicit[field.name] = structuredClone(values[field.name] || []); subscriptionInputs.defaults.add(field.name); }
                        else { subscriptionInputs.defaults.delete(field.name); if (field.name in subscriptionInputs.explicit) { values[field.name] = structuredClone(subscriptionInputs.explicit[field.name]); } }
                        refresh();
                    });
                }
                root.append(group);
                if (field.error) {
                    // Let CSS flow validation after all controls/actions. Inline
                    // errors would also push later flex rows and their actions.
                    const error = el("p", null, { id: errorId, class: "field-error" });
                    error.append(el("span", prompt + ": ", { class: "parameter-error-label" }), document.createTextNode(field.error));
                    root.append(error);
                }
            }
            if (drawVersion === version) { document.getElementById(active)?.focus(); }
        };
        const subscriptionSelection = () => Object.fromEntries(fields.map(field => [field.name,
            subscriptionInputs.defaults.has(field.name) ? { useDefault: true, values: [] } : { useDefault: false, values: values[field.name] || [] }]));
        window.portalSubscriptionInputs = {
            fragment: subscriptionFragment,
            state: () => ({ busy: parameterBusy, loaded: inputsLoaded, executing: executing || toggling || closing }),
            async begin(saved, signal) {
                activeDropdown?.close(false); await settleParameters();
                signal?.throwIfAborted();
                if (executing || toggling || closing || !inputsLoaded) { throw new Error("Wait for the report inputs to finish loading."); }
                if (saved && Object.keys(saved).some(name => !fields.some(field => field.name === name))) { throw new Error("This report's inputs changed. Recreate the subscription to remove unavailable settings."); }
                const snapshot = { values: structuredClone(values), defaulted: [...defaulted], fields: structuredClone(fields), canRun, linkErrors: [...linkErrors] };
                subscriptionInputs = { defaults: new Set(), explicit: Object.create(null), snapshot };
                for (const field of fields) {
                    const selection = saved?.[field.name];
                    if (!selection || selection.useDefault) { subscriptionInputs.defaults.add(field.name); }
                    if (selection && !selection.useDefault) { values[field.name] = structuredClone(selection.values); }
                    subscriptionInputs.explicit[field.name] = structuredClone(values[field.name] || []);
                }
                const context = subscriptionInputs;
                await refresh();
                if (subscriptionInputs !== context || signal?.aborted) { return; }
                if (context.failure) { throw new Error(context.failure); }
            },
            async selection() {
                activeDropdown?.close(false); await settleParameters();
                if (parameterBusy || !canRun || !subscriptionInputs) { throw new Error("Review the highlighted report inputs before saving."); }
                return subscriptionSelection();
            },
            async end() {
                activeDropdown?.close(false); ++version; controller?.abort();
                const snapshot = subscriptionInputs?.snapshot; subscriptionInputs = null;
                if (snapshot) {
                    values = snapshot.values; defaulted = snapshot.defaulted; fields = snapshot.fields; canRun = snapshot.canRun; linkErrors = snapshot.linkErrors;
                    pendingChanges.clear(); await draw(snapshot);
                }
                busy(false);
            },
            async retry() {
                await refresh();
                if (subscriptionInputs?.failure) { throw new Error(subscriptionInputs.failure); }
            }
        };
        form.addEventListener("submit", async event => {
            event.preventDefault();
            if (executing || toggling || closing) { return; }
            activeDropdown?.close(false);
            ++dropdownIntent;
            if (activeDropdown) { return; }
            await settleParameters();
            if (parameterBusy || !canRun || executing || toggling || closing || !form.reportValidity()) { revealInvalidParameters(); return; }
            // An explicit View report acknowledges link-only errors (for example
            // an unknown query key) after reviewing the valid visible selections.
            linkErrors = [];
            const revision = ++executionRevision;
            const outputRevision = ++viewRevision;
            if (paging && activeSession?.valid) {
                activeSession.needsSynchronization = true;
                const previousFrame = document.getElementById("report-frame");
                previousFrame.dataset.stale = "true";
                previousFrame.title = "Previous report output — reload required";
            }
            pageController?.abort();
            paging = false;
            const source = activeSession?.valid ? activeSession.token : null;
            const input = { values: structuredClone(values), defaulted: [...defaulted], replaceToken: source };
            const submitted = signature(input.values);
            executionController = new AbortController();
            const signal = executionController.signal;
            executing = true;
            controls();
            document.getElementById("diagnostic-details")?.remove();
            announce("Running report…", false, false);
            let result, frame;
            try {
                // Retry a previously interrupted cleanup before reserving again.
                for (const token of pendingSessions) { await release(token); }
                if (activeSession?.needsCommit) {
                    if (!await establish(activeSession.token)) { throw new Error("Session cleanup could not be confirmed. Select View report to try again."); }
                    activeSession.needsCommit = false;
                }
                result = await request("/api/reports/" + id + "/run", input, signal);
                pendingSessions.add(result.token);
                if (revision !== executionRevision || signal.aborted) { await release(result.token); return; }
                pendingToken = result.token;
                announce("Loading report output…", false, false);
                frame = await prepareFrame("/api/executions/" + encodeURIComponent(result.token) + "/viewer?revision=" + result.revision, outputRevision, signal, result.revision);
                if (revision !== executionRevision || signal.aborted) { frame.remove(); await release(result.token); return; }
                // Once commit starts, finish adoption even if its HTTP reply is
                // lost. Info confirms whether the idempotent promotion succeeded.
                committing = true;
                controls();
                const confirmed = await establish(result.token);
                displayedValues = submitted;
                display(frame, { ...result, needsCommit: !confirmed, sourceToken: source });
                pendingSessions.delete(result.token);
                pendingToken = null;
                if (confirmed) { announce("Report loaded.", false, false); }
                else { announce("Report loaded; session cleanup could not be confirmed. Select View report to retry.", true); }
            } catch (error) {
                frame?.remove();
                // Failure leaves the previous frame, page and downloads intact.
                if (result?.token && result.token !== activeSession?.token) {
                    try { await release(result.token); } catch { /* Expiry is the server-side fallback. */ }
                }
                if (revision === executionRevision) {
                    if (!result && source && !error.status) {
                        // A response lost after successful server rendering still
                        // leaves a bounded pending session reachable by its source.
                        committing = true;
                        controls();
                        try { await request("/api/executions/" + encodeURIComponent(source) + "/replacement/cancel", {}); }
                        catch { /* The reaper remains the final cleanup guarantee. */ }
                    }
                    pendingToken = null;
                    if (error.name === "AbortError") { announce("Request cancelled. Select View report to try again.", false, false); }
                    else if (activeSession?.needsSynchronization) { outOfDate(error); }
                    else { expire(error); }
                }
            } finally {
                if (revision === executionRevision) { executing = committing = false; controls(); }
            }
        });
        cancel.addEventListener("click", async () => {
            if (!executing || committing) { return; }
            const revision = ++executionRevision;
            ++viewRevision;
            executionController?.abort();
            try {
                await release(pendingToken);
                if (activeSession?.valid) { await request("/api/executions/" + encodeURIComponent(activeSession.token) + "/replacement/cancel", {}); }
                if (revision === executionRevision) { announce("Request cancelled. Select View report to try again.", false, false); }
            } catch (error) { if (revision === executionRevision) { showFailure(error); } }
            finally { if (revision === executionRevision) { pendingToken = null; executing = false; controls(); } }
        });
        controls();
        let autoExecute = false;
        initialize().then(async () => {
            let recoveryError;
            const saved = window.history.state?.ssrsReport;
            const recoveryToken = report.dataset.token || (saved?.path === canonicalUrl ? saved.token : null);
            const recoveryAttempted = Boolean(recoveryToken || report.dataset.sessionMessage || saved?.path === canonicalUrl && saved.attempted);
            // Replace legacy addresses only after the server has admitted the report.
            window.history.replaceState(window.history.state, "", canonicalUrl + entryQuery);
            if (recoveryToken) {
                let recoveryFrame;
                try {
                    let info = await request("/api/executions/" + encodeURIComponent(recoveryToken) + "/info");
                    if (info.reportId !== report.dataset.id) { throw new Error("This report session is unavailable. Select View report to run it again."); }
                    values = info.input.values || {};
                    defaulted = info.input.defaulted || [];
                    displayedValues = signature(values);
                    pageController = new AbortController();
                    if (info.needsSynchronization) {
                        info = { ...info, ...await request("/api/executions/" + encodeURIComponent(info.token) + "/synchronize", {}, pageController.signal) };
                    }
                    recoveryFrame = await prepareFrame("/api/executions/" + encodeURIComponent(info.token) + "/viewer?revision=" + info.revision, ++viewRevision, pageController.signal, info.revision);
                    let confirmed = !info.pending;
                    if (info.pending) {
                        try { confirmed = await establish(info.token); }
                        catch (error) {
                            if (error.code === "session" || error.status === 401 || error.status === 403) { throw error; }
                        }
                    }
                    display(recoveryFrame, { ...info, needsCommit: !confirmed });
                    recoveryFrame = null;
                    if (!confirmed) { recoveryError = new Error("Report loaded; session cleanup could not be confirmed. Select View report to retry."); }
                } catch (error) { recoveryFrame?.remove(); recoveryError = error; remember(null); }
            }
            linkInitialization = !activeSession && Boolean(entryQuery);
            await refresh(!activeSession);
            if (recoveryError) { showFailure(recoveryError); }
            else if (report.dataset.sessionMessage) {
                remember(null);
                showFailure(new Error(report.dataset.sessionMessage));
            }
            if (!recoveryAttempted && autoExecute && canRun && !parameterBusy && !activeSession) {
                remember(null); // Refresh/back after a failed attempt must not replay execution.
                form.requestSubmit();
            }
            report.dispatchEvent(new CustomEvent("portal:inputs-ready"));
        }).catch(showFailure);
    }
})();
