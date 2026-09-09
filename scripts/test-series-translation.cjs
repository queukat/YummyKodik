"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.resolve(__dirname, "../YummyKodik/Web/seriesTranslation.js"), "utf8");
const catalog = {
    seriesKey: "yummy:example",
    translations: [
        { Id: "voice-anistar", Name: "AniStar", Type: "voice" },
        { Id: "voice-animevost", Name: "AnimeVost", Type: "voice" }
    ]
};

function harness(serve = () => catalog) {
    const events = new Map();
    const requests = [];
    const toasts = [];
    let reloads = 0;
    const window = {
        location: { hash: "#/details?id=episode-5" },
        addEventListener() {}, setTimeout() { return 1; }, clearTimeout() {}
    };
    const document = {
        body: {},
        addEventListener(name, fn) { events.set(name, fn); },
        getElementById() { return { _reload: async () => { reloads++; } }; }
    };
    const ApiClient = {
        getUrl(endpoint, params) { return `http://test/${endpoint}?${new URLSearchParams(params)}`; },
        async ajax({ url }) {
            const parsed = new URL(url);
            const request = { endpoint: parsed.pathname, params: Object.fromEntries(parsed.searchParams) };
            requests.push(request);
            return serve(request);
        }
    };
    vm.runInNewContext(source, {
        window, document, ApiClient,
        Dashboard: {
            showToast({ text }) { toasts.push(text); },
            alert({ message }) { toasts.push(message); }
        },
        MutationObserver: class { observe() {} },
        console: { error() {}, debug() {} }
    });
    function choose(label = "AniStar", overrides = {}, eventOverrides = {}) {
        const select = {
            isConnected: true, value: "physical-version-id",
            selectedOptions: [{ textContent: label }],
            matches: selector => selector === "select.selectSource",
            getClientRects: () => [{}], closest: () => null,
            ...overrides
        };
        return events.get("change")({ target: select, isTrusted: true, ...eventOverrides });
    }
    return {
        choose, window, requests, toasts,
        saves: () => requests.filter(r => r.endpoint.endsWith("/setTranslation")),
        reloads: () => reloads
    };
}

const tests = [
    ["native voice saves the canonical series preference and refreshes the widget", async () => {
        const h = harness();
        await h.choose();
        assert.equal(h.saves().length, 1);
        assert.equal(h.saves()[0].params.seriesId, "episode-5");
        assert.equal(h.saves()[0].params.tr, "voice-anistar");
        assert.equal(h.reloads(), 1);
        assert.equal(h.toasts.length, 0, "successful native selection must not interrupt Play with a dialog");
    }],
    ["rendering, synthetic changes and unrelated selects cannot overwrite the preference", async () => {
        const h = harness();
        assert.equal(h.requests.length, 0);
        await h.choose("AnimeVost", {}, { isTrusted: false });
        await h.choose("AnimeVost", { matches: () => false });
        assert.equal(h.requests.length, 0);
    }],
    ["hidden cached pages and the video route cannot save a native default", async () => {
        const h = harness();
        await h.choose("AniStar", { getClientRects: () => [] });
        await h.choose("AniStar", { isConnected: false });
        await h.choose("AniStar", { closest: () => ({ classList: { contains: () => true } }) });
        h.window.location.hash = "#/video";
        await h.choose();
        assert.equal(h.requests.length, 0);
    }],
    ["ordinary media and ambiguous or absent voices never receive a preference write", async () => {
        for (const data of [
            { reason: "not-managed", translations: catalog.translations },
            { ...catalog, translations: [] },
            { ...catalog, translations: [...catalog.translations, { Id: "duplicate", Name: "AniStar", Type: "voice" }] }
        ]) {
            const h = harness(() => data);
            await h.choose();
            assert.equal(h.saves().length, 0);
        }
    }],
    ["canonical label matching accepts punctuation and casing without guessing an ID", async () => {
        const h = harness(() => ({ ...catalog, translations: [{ id: "lib", name: "AniLibria.TV", type: "voice" }] }));
        await h.choose("anilibria tv");
        assert.equal(h.saves()[0].params.tr, "lib");
    }],
    ["navigation during the catalog request saves the captured episode, without reloading another widget", async () => {
        let finish;
        const pending = new Promise(resolve => { finish = resolve; });
        const h = harness(request => request.endpoint.endsWith("/getTranslations") ? pending : {});
        const choice = h.choose();
        await Promise.resolve();
        h.window.location.hash = "#/details?id=another-series";
        finish(catalog);
        await choice;
        assert.equal(h.saves()[0].params.seriesId, "episode-5");
        assert.equal(h.reloads(), 0);
    }],
    ["rapid choices are saved in user order even when the first catalog response is slow", async () => {
        let finish;
        const pending = new Promise(resolve => { finish = resolve; });
        let reads = 0;
        const h = harness(request => {
            if (!request.endpoint.endsWith("/getTranslations")) return {};
            return ++reads === 1 ? pending : catalog;
        });
        const first = h.choose("AniStar");
        const second = h.choose("AnimeVost");
        await Promise.resolve();
        assert.equal(reads, 1);
        assert.equal(h.saves().length, 0);
        finish(catalog);
        await Promise.all([first, second]);
        assert.deepEqual(h.saves().map(r => r.params.tr), ["voice-anistar", "voice-animevost"]);
    }],
    ["a failed save is reported and does not poison later explicit choices", async () => {
        let writes = 0;
        const h = harness(request => {
            if (request.endpoint.endsWith("/getTranslations")) return catalog;
            if (++writes === 1) throw new Error("offline");
            return {};
        });
        await h.choose("AniStar");
        await h.choose("AnimeVost");
        assert.equal(writes, 2);
        assert.equal(h.toasts.length, 1);
        assert.match(h.toasts[0], /Не удалось/);
    }]
];

(async () => {
    for (const [name, run] of tests) {
        await run();
        console.log(`PASS ${name}`);
    }
    console.log(`${tests.length}/${tests.length} passed`);
})().catch(error => { console.error(error); process.exitCode = 1; });
