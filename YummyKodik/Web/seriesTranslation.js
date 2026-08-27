/* File: Web/seriesTranslation.js */
/* global ApiClient, Dashboard */

(function () {
    "use strict";

    const WIDGET_ID = "ykTranslationWidget";
    const WIDGET_CLASS = "detailsGroupItem ykTranslationGroup";

    function parseItemIdFromHash() {
        const h = (window.location.hash || "");
        const m = h.match(/[?&]id=([^&]+)/i);
        if (!m || !m[1]) return "";
        return decodeURIComponent(m[1]);
    }

    function isDetailsPage() {
        const h = (window.location.hash || "").toLowerCase();
        return h.includes("details") || h.includes("item");
    }

    function toast(text) {
        try {
            if (Dashboard && Dashboard.showToast) {
                Dashboard.showToast({ text });
                return;
            }
        } catch { }
        try {
            if (Dashboard && Dashboard.alert) {
                Dashboard.alert({ message: text });
            }
        } catch { }
    }

    async function apiGetTranslations(seriesId) {
        const url = ApiClient.getUrl("YummyKodik/getTranslations", {
            seriesId,
            ykRequest: Date.now().toString()
        });
        return requestJson(url);
    }

    async function apiSetTranslation(seriesId, trId) {
        const url = ApiClient.getUrl("YummyKodik/setTranslation", {
            seriesId,
            tr: trId,
            ykRequest: Date.now().toString()
        });
        return requestJson(url);
    }

    async function requestJson(url) {
        const response = await ApiClient.ajax({ type: "GET", url });

        if (response && typeof response.json === "function") {
            if (typeof response.ok === "boolean" && !response.ok) {
                const errorText = typeof response.text === "function" ? await response.text() : "";
                throw new Error(errorText || ("HTTP " + response.status));
            }

            const text = typeof response.text === "function" ? await response.text() : "";
            return text ? JSON.parse(text) : {};
        }

        if (typeof response === "string") {
            return response ? JSON.parse(response) : {};
        }

        return response || {};
    }

    function normalizeTranslations(trs) {
        const list = Array.isArray(trs) ? trs : [];
        const readValue = (item, camelName, pascalName) => {
            if (!item) {
                return "";
            }

            const value = item[camelName] !== undefined && item[camelName] !== null
                ? item[camelName]
                : item[pascalName];
            return String(value === undefined || value === null ? "" : value).trim();
        };
        const voices = list.filter(t => readValue(t, "type", "Type").toLowerCase() === "voice");
        return (voices.length > 0 ? voices : list)
            .map(t => {
                const id = readValue(t, "id", "Id");
                if (!id || id === "0") {
                    return null;
                }

                const name = readValue(t, "name", "Name") || ("Translation " + id);
                const type = readValue(t, "type", "Type");
                return {
                    id,
                    label: type && type.toLowerCase() !== "voice" ? (name + " [" + type + "]") : name
                };
            })
            .filter(Boolean);
    }

    function hasVisibleTranslations(data) {
        return normalizeTranslations(data && data.translations).length > 0;
    }

    function isManagedPartialResponse(data) {
        const reason = String(data && data.reason || "").trim().toLowerCase();
        if (reason === "not-managed") {
            return false;
        }

        return !!String(data && data.seriesKey || "").trim()
            || !!String(data && data.savedTranslationId || "").trim()
            || !!String(data && data.chosenTranslationId || "").trim();
    }

    function clearNode(node) {
        while (node.firstChild) {
            node.removeChild(node.firstChild);
        }
    }

    function setBusy(content, isBusy) {
        const buttons = content.querySelectorAll("button");
        buttons.forEach(button => {
            button.disabled = !!isBusy;
            button.style.opacity = isBusy ? "0.7" : "";
        });
    }

    function buildWidget(model, initialData) {
        const wrapper = document.createElement("div");
        wrapper.id = WIDGET_ID;
        wrapper.className = WIDGET_CLASS;

        const label = document.createElement("div");
        label.className = "label";
        label.textContent = "Озвучка";

        const content = document.createElement("div");
        content.className = "content focuscontainer-x";

        async function reload() {
            const data = await apiGetTranslations(model.seriesId);
            if (!hasVisibleTranslations(data)) {
                scheduleCatalogRetry(model.seriesId);
                return;
            }

            render(data);
        }

        async function saveTranslation(translationId) {
            try {
                setBusy(content, true);
                await apiSetTranslation(model.seriesId, translationId);
                await reload();
                toast(translationId ? "Озвучка сохранена" : "Автовыбор включён");
            } catch (e) {
                console.error("[YummyKodik] setTranslation failed:", e);
                toast("Ошибка сохранения выбора");
            } finally {
                setBusy(content, false);
            }
        }

        function render(data) {
            clearNode(content);

            const savedTranslationId = String(data && data.savedTranslationId || "").trim();
            const chosenTranslationId = String(data && data.chosenTranslationId || "").trim();
            const translations = normalizeTranslations(data && data.translations);
            const items = [{
                id: "",
                label: "Авто",
                active: !savedTranslationId,
                title: !savedTranslationId && chosenTranslationId
                    ? ("Сейчас автоматически выберется: " + chosenTranslationId)
                    : "Автоматический выбор по фильтру и сохранённым настройкам"
            }].concat(translations.map(item => ({
                id: item.id,
                label: item.label,
                active: savedTranslationId === item.id,
                title: savedTranslationId === item.id ? "Сохранённый выбор" : item.label
            })));

            items.forEach((item, index) => {
                const button = document.createElement("button");
                button.type = "button";
                button.className = "button-link emby-button";
                button.style.color = "inherit";
                button.style.fontWeight = item.active ? "600" : "400";
                button.style.textDecoration = item.active ? "underline" : "none";
                button.style.textUnderlineOffset = item.active ? "0.15em" : "";
                button.style.backgroundColor = item.active ? "rgba(0, 164, 220, 0.24)" : "transparent";
                button.style.borderRadius = "0.3em";
                button.style.padding = "0.1em 0.3em";
                button.textContent = item.active ? ("✓ " + item.label) : item.label;
                button.title = item.title;
                button.setAttribute("aria-pressed", item.active ? "true" : "false");
                if (item.active) {
                    button.setAttribute("aria-current", "true");
                }
                button.addEventListener("click", () => saveTranslation(item.id));

                content.appendChild(button);
                if (index < items.length - 1) {
                    content.appendChild(document.createTextNode(", "));
                }
            });
        }

        wrapper.appendChild(label);
        wrapper.appendChild(content);

        render(initialData || {});
        wrapper._reload = reload;
        return wrapper;
    }

    function findInjectHost() {
        const candidates = Array.from(document.querySelectorAll(".itemDetailsGroup"));
        const detailsGroup = candidates.find(candidate => {
            if (!candidate || !candidate.isConnected) {
                return false;
            }

            const page = candidate.closest(".page");
            if (page && (page.classList.contains("hide") || page.getAttribute("aria-hidden") === "true")) {
                return false;
            }

            return candidate.getClientRects().length > 0;
        });
        if (!detailsGroup) {
            return null;
        }

        const studiosGroup = detailsGroup.querySelector(".studiosGroup");
        return {
            container: detailsGroup,
            before: studiosGroup && studiosGroup.parentElement === detailsGroup ? studiosGroup : null
        };
    }

    function removeWidget() {
        const existing = Array.from(document.querySelectorAll(".ykTranslationGroup"));
        existing.forEach(node => {
            if (node && node.parentElement) {
                node.parentElement.removeChild(node);
            }
        });
    }

    let lastSeriesId = "";
    let injectRequestId = 0;
    let pendingSeriesId = "";
    let retrySeriesId = "";
    let retryCount = 0;

    function scheduleCatalogRetry(seriesId) {
        if (retrySeriesId !== seriesId) {
            retrySeriesId = seriesId;
            retryCount = 0;
        }

        if (retryCount >= 4) {
            return;
        }

        retryCount++;
        scheduleInject(Math.min(1000 * Math.pow(2, retryCount - 1), 8000));
    }

    async function injectIfNeeded() {
        if (!isDetailsPage()) {
            lastSeriesId = "";
            pendingSeriesId = "";
            retrySeriesId = "";
            retryCount = 0;
            injectRequestId++;
            removeWidget();
            return;
        }

        const seriesId = parseItemIdFromHash();
        if (!seriesId) {
            pendingSeriesId = "";
            retrySeriesId = "";
            retryCount = 0;
            injectRequestId++;
            removeWidget();
            return;
        }

        const host = findInjectHost();
        if (!host) {
            scheduleInject(250);
            return;
        }

        const currentWidget = Array.from(host.container.children)
            .find(node => node.classList && node.classList.contains("ykTranslationGroup"));
        if (seriesId === lastSeriesId && currentWidget) {
            return;
        }

        pendingSeriesId = seriesId;
        const requestId = ++injectRequestId;

        try {
            const data = await apiGetTranslations(seriesId);
            if (requestId !== injectRequestId || pendingSeriesId !== seriesId || parseItemIdFromHash() !== seriesId) {
                return;
            }

            if (!hasVisibleTranslations(data)) {
                lastSeriesId = "";
                pendingSeriesId = "";
                removeWidget();
                if (isManagedPartialResponse(data)) {
                    scheduleCatalogRetry(seriesId);
                } else {
                    retrySeriesId = "";
                    retryCount = 0;
                }
                return;
            }

            retrySeriesId = "";
            retryCount = 0;
            const currentHost = findInjectHost();
            if (!currentHost) {
                pendingSeriesId = "";
                scheduleInject(250);
                return;
            }

            removeWidget();
            const widget = buildWidget({ seriesId }, data);

            if (currentHost.before && currentHost.before.parentElement === currentHost.container) {
                currentHost.container.insertBefore(widget, currentHost.before);
            } else {
                currentHost.container.appendChild(widget);
            }
            lastSeriesId = seriesId;
            pendingSeriesId = "";
        } catch (e) {
            if (requestId === injectRequestId) {
                lastSeriesId = "";
                pendingSeriesId = "";
                scheduleInject(1500);
            }
            console.debug("[YummyKodik] no translations widget for this item:", e);
        }
    }

    let timer = 0;
    function scheduleInject(delay) {
        if (timer) window.clearTimeout(timer);
        timer = window.setTimeout(() => injectIfNeeded(), typeof delay === "number" ? delay : 150);
    }

    window.addEventListener("hashchange", scheduleInject);
    document.addEventListener("viewshow", scheduleInject);
    window.addEventListener("pageshow", scheduleInject);

    const observer = new MutationObserver(() => {
        if (isDetailsPage()) {
            scheduleInject(150);
        }
    });

    if (document.body) {
        observer.observe(document.body, { childList: true, subtree: true });
    } else {
        window.addEventListener("DOMContentLoaded", () => {
            observer.observe(document.body, { childList: true, subtree: true });
        }, { once: true });
    }

    scheduleInject();
})();
