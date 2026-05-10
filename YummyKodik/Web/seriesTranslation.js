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
        const url = ApiClient.getUrl("YummyKodik/getTranslations", { seriesId });
        return requestJson(url);
    }

    async function apiSetTranslation(seriesId, trId) {
        const url = ApiClient.getUrl("YummyKodik/setTranslation", { seriesId, tr: trId });
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
        const voices = list.filter(t => String(t.type || "").toLowerCase() === "voice");
        return (voices.length > 0 ? voices : list)
            .map(t => {
                const id = String(t.id || "").trim();
                if (!id || id === "0") {
                    return null;
                }

                const name = String(t.name || "").trim() || ("Translation " + id);
                const type = String(t.type || "").trim();
                return {
                    id,
                    label: type && type.toLowerCase() !== "voice" ? (name + " [" + type + "]") : name
                };
            })
            .filter(Boolean);
    }

    function hasVisibleTranslations(data) {
        return normalizeTranslations(data && data.translations).length > 0
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
                button.textContent = item.label;
                button.title = item.title;
                button.setAttribute("aria-pressed", item.active ? "true" : "false");
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
        const detailsGroup = document.querySelector(".itemDetailsGroup");
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

    async function injectIfNeeded() {
        if (!isDetailsPage()) {
            lastSeriesId = "";
            pendingSeriesId = "";
            injectRequestId++;
            removeWidget();
            return;
        }

        const seriesId = parseItemIdFromHash();
        if (!seriesId) {
            pendingSeriesId = "";
            injectRequestId++;
            removeWidget();
            return;
        }

        const host = findInjectHost();
        if (!host) {
            scheduleInject(250);
            return;
        }

        const existingWidgets = document.querySelectorAll(".ykTranslationGroup");
        if (seriesId === lastSeriesId && existingWidgets.length === 1) {
            return;
        }

        removeWidget();

        lastSeriesId = seriesId;
        pendingSeriesId = seriesId;
        const requestId = ++injectRequestId;

        try {
            const data = await apiGetTranslations(seriesId);
            if (requestId !== injectRequestId || pendingSeriesId !== seriesId || parseItemIdFromHash() !== seriesId) {
                return;
            }

            if (!hasVisibleTranslations(data)) {
                return;
            }

            removeWidget();
            const widget = buildWidget({ seriesId }, data);

            if (host.before && host.before.parentElement === host.container) {
                host.container.insertBefore(widget, host.before);
            } else {
                host.container.appendChild(widget);
            }
            pendingSeriesId = "";
        } catch (e) {
            if (requestId === injectRequestId) {
                pendingSeriesId = "";
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
