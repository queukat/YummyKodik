/* File: Web/seriesTranslation.js */
/* global ApiClient, Dashboard */

(function () {
    "use strict";

    const ROOT = "/YummyKodik";
    const WIDGET_ID = "ykTranslationWidget";

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
        return ApiClient.ajax({ type: "GET", url });
    }

    async function apiSetTranslation(seriesId, trId) {
        const url = ApiClient.getUrl("YummyKodik/setTranslation", { seriesId, tr: trId });
        return ApiClient.ajax({ type: "GET", url });
    }

    function buildWidget(model) {
        const wrapper = document.createElement("div");
        wrapper.id = WIDGET_ID;
        wrapper.className = "paperList";
        wrapper.style.marginTop = "0.75em";
        wrapper.style.padding = "0.75em";

        const title = document.createElement("div");
        title.style.fontSize = "1.1em";
        title.style.fontWeight = "600";
        title.textContent = "Озвучка";

        const row = document.createElement("div");
        row.style.display = "flex";
        row.style.flexWrap = "wrap";
        row.style.alignItems = "center";
        row.style.gap = "0.6em";
        row.style.marginTop = "0.6em";

        const select = document.createElement("select");
        select.className = "emby-select";
        select.style.minWidth = "260px";

        const hint = document.createElement("div");
        hint.style.opacity = "0.8";
        hint.style.fontSize = "0.85em";
        hint.style.marginTop = "0.45em";

        const btnClear = document.createElement("button");
        btnClear.type = "button";
        btnClear.className = "raised button-submit emby-button";
        btnClear.textContent = "Auto";

        const btnRefresh = document.createElement("button");
        btnRefresh.type = "button";
        btnRefresh.className = "raised emby-button";
        btnRefresh.textContent = "Обновить список";

        function normalizeTranslations(trs) {
            const list = Array.isArray(trs) ? trs : [];
            const voices = list.filter(t => String(t.type || "").toLowerCase() === "voice");
            return voices.length > 0 ? voices : list;
        }

        function fillSelect(translations, currentSavedId) {
            while (select.firstChild) select.removeChild(select.firstChild);

            const optAuto = document.createElement("option");
            optAuto.value = "";
            optAuto.textContent = "Auto (по фильтру и сохранённому выбору)";
            select.appendChild(optAuto);

            const norm = normalizeTranslations(translations);

            for (const t of norm) {
                const tid = String(t.id || "").trim();
                if (!tid || tid === "0") continue;

                const opt = document.createElement("option");
                opt.value = tid;

                const name = String(t.name || "").trim() || ("Translation " + tid);
                const type = String(t.type || "").trim();
                opt.textContent = type && type.toLowerCase() !== "voice" ? (name + " [" + type + "]") : name;

                select.appendChild(opt);
            }

            select.value = currentSavedId ? String(currentSavedId).trim() : "";
        }

        async function reload() {
            const data = await apiGetTranslations(model.seriesId);

            fillSelect(data.translations, data.savedTranslationId);

            const chosen = String(data.chosenTranslationId || "").trim();
            const reason = String(data.reason || "").trim();
            hint.textContent = "Сейчас будет выбрано: " + chosen + (reason ? (" (" + reason + ")") : "");
        }

        select.addEventListener("change", async () => {
            try {
                const chosen = (select.value || "").trim();
                await apiSetTranslation(model.seriesId, chosen);
                toast(chosen ? "Озвучка сохранена" : "Выбор сброшен на Auto");
                await reload();
            } catch (e) {
                console.error("[YummyKodik] setTranslation failed:", e);
                toast("Ошибка сохранения выбора");
            }
        });

        btnClear.addEventListener("click", async () => {
            try {
                select.value = "";
                await apiSetTranslation(model.seriesId, "");
                toast("Выбор сброшен на Auto");
                await reload();
            } catch (e) {
                console.error("[YummyKodik] clear translation failed:", e);
                toast("Ошибка сброса выбора");
            }
        });

        btnRefresh.addEventListener("click", async () => {
            try {
                await reload();
                toast("Список обновлён");
            } catch (e) {
                console.error("[YummyKodik] reload translations failed:", e);
                toast("Ошибка обновления списка");
            }
        });

        row.appendChild(select);
        row.appendChild(btnClear);
        row.appendChild(btnRefresh);

        wrapper.appendChild(title);
        wrapper.appendChild(row);
        wrapper.appendChild(hint);

        wrapper._reload = reload;
        return wrapper;
    }

    function findInjectHost() {
        return document.querySelector(".detailPagePrimaryContainer")
            || document.querySelector(".detailPageContent")
            || document.querySelector(".detailPageWrapper")
            || document.querySelector(".pageContainer")
            || document.body;
    }

    let lastSeriesId = "";

    async function injectIfNeeded() {
        if (!isDetailsPage()) return;

        const seriesId = parseItemIdFromHash();
        if (!seriesId) return;

        if (seriesId === lastSeriesId && document.getElementById(WIDGET_ID)) return;

        const old = document.getElementById(WIDGET_ID);
        if (old && old.parentElement) old.parentElement.removeChild(old);

        lastSeriesId = seriesId;

        try {
            const data = await apiGetTranslations(seriesId);

            const host = findInjectHost();
            const widget = buildWidget({ seriesId, data });

            host.insertBefore(widget, host.firstChild);

            await widget._reload();
        } catch (e) {
            console.debug("[YummyKodik] no translations widget for this item:", e);
        }
    }

    let timer = 0;
    function scheduleInject() {
        if (timer) window.clearTimeout(timer);
        timer = window.setTimeout(() => injectIfNeeded(), 250);
    }

    window.addEventListener("hashchange", scheduleInject);
    document.addEventListener("viewshow", scheduleInject);

    scheduleInject();
})();
