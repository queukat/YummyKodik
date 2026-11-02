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

function harness(serve) {
    const events = new Map(), timers = new Map(), requests = [], toasts = [];
    let serial = 0, saved = "voice-anistar";
    function node(tag = "div") {
        return {
            tag, children: [], style: {}, attributes: {}, events: {}, isConnected: true,
            get firstChild() { return this.children[0]; },
            get classList() { return { contains: name => (this.className || "").split(" ").includes(name) }; },
            closest() { return null; }, getClientRects() { return [{}]; },
            appendChild(child) { child.parentElement = this; this.children.push(child); },
            removeChild(child) { this.children.splice(this.children.indexOf(child), 1); child.parentElement = null; },
            setAttribute(name, value) { this.attributes[name] = value; },
            addEventListener(name, fn) { this.events[name] = fn; },
            querySelector() { return null; },
            querySelectorAll(selector) {
                return this.children.flatMap(child => [
                    ...(selector === "button" && child.tag === "button" ? [child] : []),
                    ...(child.querySelectorAll ? child.querySelectorAll(selector) : [])
                ]);
            }
        };
    }
    const host = node();
    const window = {
        location: { hash: "#/details?id=episode-5" }, addEventListener() {},
        setTimeout(fn) { timers.set(++serial, fn); return serial; }, clearTimeout(id) { timers.delete(id); }
    };
    const document = {
        body: {}, addEventListener(name, fn) { events.set(name, fn); },
        createElement: node, createTextNode: text => ({ textContent: text }),
        querySelectorAll(selector) { return selector === ".itemDetailsGroup" ? [host] : host.children; },
        getElementById(id) { return host.children.find(child => child.id === id); }
    };
    vm.runInNewContext(source, {
        window, document,
        ApiClient: {
            getUrl(endpoint, params) { return `http://test/${endpoint}?${new URLSearchParams(params)}`; },
            async ajax({ url }) {
                const parsed = new URL(url);
                const request = { endpoint: parsed.pathname, params: Object.fromEntries(parsed.searchParams) };
                requests.push(request);
                if (serve) {
                    const response = await serve(request);
                    if (response !== undefined) return response;
                }
                if (request.endpoint.endsWith("/setTranslation")) { saved = request.params.tr; return {}; }
                return { ...catalog, savedTranslationId: saved };
            }
        },
        Dashboard: { showToast({ text }) { toasts.push(text); } },
        MutationObserver: class { observe() {} }, console: { error() {}, debug() {} }
    });
    return {
        requests, toasts, window,
        async inject() { const [id, fn] = timers.entries().next().value; timers.delete(id); await fn(); },
        buttons: () => host.querySelectorAll("button"),
        click(label) { return this.buttons().find(button => button.textContent.replace(/^✓ /, "") === label).events.click(); },
        native(label = "AnimeVost") {
            const select = { isConnected: true, value: "physical-version-2", selectedOptions: [{ textContent: label }],
                matches: value => value === "select.selectSource", getClientRects: () => [{}], closest: () => null };
            const listener = events.get("change");
            return Promise.resolve(listener && listener({ target: select, isTrusted: true })).then(() => select);
        },
        saves: () => requests.filter(request => request.endpoint.endsWith("/setTranslation"))
    };
}

const tests = [
    ["DOM churn neither postpones injection nor duplicates pending catalog requests", async () => {
        const timers = new Map();
        const windowEvents = new Map();
        let serial = 0, mutation, requests = 0, finish;
        const pending = new Promise(resolve => { finish = resolve; });
        const host = { isConnected: true, closest: () => null, getClientRects: () => [{}], querySelector: () => null, children: [] };
        const window = {
            location: { hash: "#/details?id=one" },
            addEventListener: (name, callback) => windowEvents.set(name, callback),
            setTimeout: callback => { timers.set(++serial, callback); return serial; },
            clearTimeout: id => timers.delete(id)
        };
        vm.runInNewContext(source, {
            window,
            document: { body: {}, addEventListener() {}, querySelectorAll: selector => selector === ".itemDetailsGroup" ? [host] : [] },
            ApiClient: { getUrl: () => "http://test/catalog", ajax: () => { requests++; return pending; } },
            MutationObserver: class { constructor(callback) { mutation = callback; } observe() {} }, console
        });
        const firstTimer = [...timers.keys()][0];
        for (let i = 0; i < 100; i++) mutation();
        assert.deepEqual([...timers.keys()], [firstTimer], "mutations must not reset the first injection deadline");
        const runTimer = () => { const [id, callback] = timers.entries().next().value; timers.delete(id); return callback(); };
        const firstRequest = runTimer();
        assert.equal(requests, 1);
        mutation();
        await runTimer();
        assert.equal(requests, 1, "pending request must retain ownership during page rendering");
        window.location.hash = "#/details?id=two";
        windowEvents.get("hashchange")();
        const secondRequest = runTimer();
        assert.equal(requests, 2, "navigation must not wait for the previous item's request");
        finish({ reason: "not-managed", translations: [] });
        await Promise.all([firstRequest, secondRequest]);
        mutation();
        await runTimer();
        assert.equal(requests, 2, "a known empty result must not refetch on every DOM mutation");
    }],
    ["native Version changes current selection without writing or changing series priority", async () => {
        const h = harness();
        await h.inject();
        const before = h.requests.length;
        const select = await h.native();
        assert.equal(select.value, "physical-version-2");
        assert.equal(h.requests.length, before, "native choice must not fetch or save priority");
        assert.equal(h.saves().length, 0);
        assert.equal(h.buttons().find(button => button.attributes["aria-pressed"] === "true").textContent, "✓ AniStar");
        assert.equal(h.toasts.length, 0);
    }],
    ["priority buttons still persist and highlight priority; Auto clears it", async () => {
        const h = harness();
        await h.inject();
        await h.click("AnimeVost");
        assert.equal(h.saves()[0].params.tr, "voice-animevost");
        assert.equal(h.saves()[0].params.seriesId, "episode-5");
        assert.equal(h.buttons().find(button => button.attributes["aria-pressed"] === "true").textContent, "✓ AnimeVost");
        await h.click("Авто");
        assert.equal(h.saves()[1].params.tr, "");
        assert.equal(h.buttons()[0].attributes["aria-pressed"], "true");
    }],
    ["native choice during a pending priority save never overwrites priority", async () => {
        let finish;
        const pending = new Promise(resolve => { finish = resolve; });
        const h = harness(async request => {
            if (request.endpoint.endsWith("/setTranslation")) await pending;
        });
        await h.inject();
        const priority = h.click("AnimeVost");
        await Promise.resolve();
        await h.native("AniStar");
        finish();
        await priority;
        assert.deepEqual(h.saves().map(request => request.params.tr), ["voice-animevost"]);
    }],
    ["priority saves retain click order after slow responses", async () => {
        let finish, writes = 0;
        const pending = new Promise(resolve => { finish = resolve; });
        const h = harness(async request => {
            if (request.endpoint.endsWith("/setTranslation") && ++writes === 1) await pending;
        });
        await h.inject();
        const first = h.click("AnimeVost"), second = h.click("AniStar");
        await Promise.resolve();
        assert.equal(writes, 1);
        finish();
        await Promise.all([first, second]);
        assert.deepEqual(h.saves().map(request => request.params.tr), ["voice-animevost", "voice-anistar"]);
        assert.equal(h.buttons().find(button => button.attributes["aria-pressed"] === "true").textContent, "✓ AniStar");
    }],
    ["failed priority save allows a later retry and does not involve native choices", async () => {
        let writes = 0;
        const h = harness(request => {
            if (request.endpoint.endsWith("/setTranslation") && ++writes === 1) throw new Error("offline");
        });
        await h.inject();
        await h.click("AnimeVost");
        assert.match(h.toasts[0], /Ошибка/);
        await h.native();
        assert.equal(writes, 1);
        await h.click("AnimeVost");
        assert.equal(writes, 2);
        assert.equal(h.buttons().find(button => button.attributes["aria-pressed"] === "true").textContent, "✓ AnimeVost");
    }]
];

(async () => {
    for (const [name, run] of tests) { await run(); console.log(`PASS ${name}`); }
    console.log(`${tests.length}/${tests.length} passed`);
})().catch(error => { console.error(error); process.exitCode = 1; });
