"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const sourcePath = path.resolve(__dirname, "../YummyKodik/Web/playbackBuffer.js");
const source = fs.readFileSync(sourcePath, "utf8");
const managedSource = "/YummyKodik/stream?type=shikimori&id=62957&ep=22&tr=704&format=hls";

class EventTargetMock {
    constructor() {
        this.listeners = new Map();
    }

    addEventListener(name, callback) {
        if (!this.listeners.has(name)) this.listeners.set(name, new Set());
        this.listeners.get(name).add(callback);
    }

    removeEventListener(name, callback) {
        this.listeners.get(name)?.delete(callback);
    }

    dispatch(name) {
        for (const callback of [...(this.listeners.get(name) || [])]) {
            callback({ type: name, target: this });
        }
    }

    listenerCount() {
        return [...this.listeners.values()].reduce((total, listeners) => total + listeners.size, 0);
    }
}

class ElementMock extends EventTargetMock {
    constructor(tagName) {
        super();
        this.tagName = tagName.toUpperCase();
        this.children = [];
        this.parentNode = null;
        this.style = { cssText: "" };
        this.attributes = new Map();
        this.textContent = "";
        this.id = "";
    }

    append(...elements) {
        for (const element of elements) this.appendChild(element);
    }

    appendChild(element) {
        element.remove();
        this.children.push(element);
        element.parentNode = this;
        return element;
    }

    remove() {
        if (!this.parentNode) return;
        const index = this.parentNode.children.indexOf(this);
        if (index >= 0) this.parentNode.children.splice(index, 1);
        this.parentNode = null;
    }

    setAttribute(name, value) {
        this.attributes.set(name, value);
    }
}

function createHarness({ eagerHls = false } = {}) {
    const mediaEvents = [];
    const intervals = new Map();
    let nextInterval = 1;

    class VideoMock extends ElementMock {
        constructor() {
            super("video");
            this.currentTime = 0;
            this.duration = 180;
            this.readyState = 3;
            this.seeking = false;
            this.paused = true;
            this.playCalls = 0;
            this.pauseCalls = 0;
            this.rejectNextPlay = false;
            this.setBuffer([]);
        }

        setBuffer(ranges) {
            this.buffered = {
                length: ranges.length,
                start: index => ranges[index][0],
                end: index => ranges[index][1]
            };
        }

        play() {
            this.playCalls++;
            if (this.rejectNextPlay) {
                this.rejectNextPlay = false;
                return Promise.reject(new Error("User gesture required"));
            }

            if (this.paused) {
                this.paused = false;
                // HTMLMediaElement queues these events; pause events must not fire synchronously.
                mediaEvents.push(() => this.dispatch("play"));
                mediaEvents.push(() => {
                    if (!this.paused && this.readyState >= 3) this.dispatch("playing");
                });
            }

            return Promise.resolve();
        }

        pause() {
            this.pauseCalls++;
            if (!this.paused) {
                this.paused = true;
                mediaEvents.push(() => this.dispatch("pause"));
            }
        }
    }

    class HlsMock {
        static Events = {
            MEDIA_ATTACHED: "hlsMediaAttached",
            MEDIA_DETACHING: "hlsMediaDetaching",
            BUFFER_APPENDED: "hlsBufferAppended",
            ERROR: "hlsError",
            DESTROYING: "hlsDestroying"
        };

        constructor() {
            this.config = { maxBufferLength: 30, maxMaxBufferLength: 90 };
            this.listeners = new Map();
            this.sources = [];
        }

        on(name, callback) {
            if (!this.listeners.has(name)) this.listeners.set(name, new Set());
            this.listeners.get(name).add(callback);
        }

        off(name, callback) {
            this.listeners.get(name)?.delete(callback);
        }

        emit(name, data = {}) {
            for (const callback of [...(this.listeners.get(name) || [])]) callback(name, data);
        }

        loadSource(value) {
            this.sources.push(value);
        }

        attachMedia(video) {
            this.media = video;
            this.emit(HlsMock.Events.MEDIA_ATTACHED, { media: video });
        }

        destroy() {
            this.emit(HlsMock.Events.DESTROYING);
        }
    }

    const body = new ElementMock("body");
    const document = { body, fullscreenElement: null, createElement: name => new ElementMock(name) };
    const window = {
        location: { href: "http://localhost:8096/web/index.html" },
        setInterval(callback) {
            const id = nextInterval++;
            intervals.set(id, callback);
            return id;
        },
        clearInterval: id => intervals.delete(id)
    };
    if (eagerHls) window.Hls = HlsMock;
    vm.runInNewContext(source, { window, document, URL, console }, { filename: sourcePath, timeout: 1000 });
    if (!eagerHls) window.Hls = HlsMock;
    const hls = new window.Hls();
    const video = new VideoMock();

    const harness = {
        hls,
        video,
        Hls: HlsMock,
        intervals,
        body,
        async flush() {
            for (let turn = 0; turn < 8; turn++) {
                let count = 0;
                while (mediaEvents.length) {
                    if (++count > 100) throw new Error("Media event feedback loop");
                    mediaEvents.shift()();
                }
                await Promise.resolve();
            }
            assert.equal(mediaEvents.length, 0, "Media event queue must settle");
        },
        async attach(url = managedSource) {
            hls.loadSource(url);
            hls.attachMedia(video);
            await harness.flush();
        },
        async progress(ranges) {
            video.setBuffer(ranges);
            video.dispatch("progress");
            hls.emit(HlsMock.Events.BUFFER_APPENDED);
            await harness.flush();
        },
        async tick() {
            for (const callback of [...intervals.values()]) callback();
            await harness.flush();
        },
        overlay() {
            return body.children.find(element => element.id === "ykPlaybackBuffer");
        },
        button() {
            const overlay = harness.overlay();
            assert.ok(overlay, "Buffering controls must be visible");
            return overlay.children.find(element => element.tagName === "BUTTON");
        }
    };
    return harness;
}

const tests = [
    ["network recovery keeps progress and pending automatic startup", async () => {
        const h = createHarness();
        await h.attach();
        await h.video.play();
        await h.flush();
        await h.progress([[0, 24]]);
        h.hls.emit(h.Hls.Events.ERROR, { type: "networkError", details: "fragLoadTimeOut", fatal: true });
        await h.tick();
        assert.ok(h.overlay(), "Recoverable fatal errors must not hide the buffering indicator");
        assert.match(h.overlay().children[1].textContent, /24 из 60.*Ожидаю данные/);
        assert.equal(h.video.paused, true);
        await h.progress([[0, 48]]);
        assert.doesNotMatch(h.overlay().children[1].textContent, /Ожидаю данные/);
        await h.progress([[0, 60]]);
        assert.equal(h.video.paused, false, "Jellyfin recovery must preserve the user's pending start");
        assert.equal(h.overlay(), undefined);
    }],
    ["network errors do not override a canceled automatic startup", async () => {
        const h = createHarness();
        await h.attach();
        h.button().dispatch("click");
        h.hls.emit(h.Hls.Events.ERROR, { type: "networkError", fatal: true });
        await h.progress([[0, 80]]);
        assert.equal(h.video.paused, true);
        assert.ok(h.overlay());
        h.hls.destroy();
        assert.equal(h.overlay(), undefined, "Unrecoverable errors still clean up on actual player destruction");
    }],
    ["waits below 60 seconds and starts once at 60", async () => {
        const h = createHarness();
        await h.attach();
        assert.equal(h.hls.config.maxBufferLength, 120);
        assert.equal(h.hls.config.maxMaxBufferLength, 120);
        await h.video.play();
        await h.flush();
        assert.equal(h.video.paused, true, "An attempted early start must be paused");
        const attempts = h.video.playCalls;
        await h.progress([[0, 12]]);
        await h.progress([[0, 59.9]]);
        assert.equal(h.video.paused, true, "59.9 seconds must not satisfy a 60-second startup requirement");
        assert.equal(h.video.playCalls, attempts, "No premature automatic play call is allowed");
        await h.progress([[0, 60]]);
        assert.equal(h.video.paused, false);
        assert.equal(h.video.playCalls, attempts + 1, "Readiness should cause exactly one automatic start");
        assert.equal(h.overlay(), undefined);
        await h.tick();
        await h.progress([[0, 70]]);
        assert.equal(h.video.playCalls, attempts + 1, "Repeated ready notifications must not replay play()");
    }],
    ["counts contiguous buffer at the current position", async () => {
        const h = createHarness();
        h.video.currentTime = 20;
        await h.attach();
        await h.progress([[0, 30], [40, 150]]);
        assert.equal(h.video.paused, true, "A later disjoint range is not playable buffer ahead");
        await h.progress([[20, 80]]);
        assert.equal(h.video.paused, false);
    }],
    ["starts a shorter remaining tail when it is buffered", async () => {
        const h = createHarness({ eagerHls: true });
        h.video.duration = 100;
        h.video.currentTime = 90;
        await h.attach();
        await h.progress([[90, 98]]);
        assert.equal(h.video.paused, true);
        await h.progress([[90, 100]]);
        assert.equal(h.video.paused, false, "The last ten seconds must not wait for an impossible minute");
    }],
    ["autostart control cancels and restores a pending start", async () => {
        const h = createHarness();
        await h.attach();
        await h.video.play();
        await h.flush();
        h.button().dispatch("click");
        await h.flush();
        const attempts = h.video.playCalls;
        await h.progress([[0, 80]]);
        await h.tick();
        assert.equal(h.video.paused, true);
        assert.equal(h.video.playCalls, attempts, "A canceled auto-start must remain canceled when buffering finishes");
        h.button().dispatch("click");
        await h.flush();
        assert.equal(h.video.paused, false);
        assert.equal(h.video.playCalls, attempts + 1);
    }],
    ["manual pause stays paused despite progress and waiting events", async () => {
        const h = createHarness();
        await h.attach();
        await h.progress([[0, 80]]);
        h.video.pause();
        await h.flush();
        const attempts = h.video.playCalls;
        h.video.currentTime = 80;
        h.video.setBuffer([[0, 80]]);
        h.video.dispatch("waiting");
        await h.progress([[80, 160]]);
        await h.tick();
        assert.equal(h.video.paused, true, "Buffer completion must not undo an explicit manual pause");
        assert.equal(h.video.playCalls, attempts);
    }],
    ["repeated depletion pauses and refills before resuming", async () => {
        const h = createHarness();
        await h.attach();
        await h.progress([[0, 60]]);
        h.video.currentTime = 60;
        h.video.setBuffer([[0, 60.1]]);
        h.video.dispatch("waiting");
        await h.flush();
        assert.equal(h.video.paused, true);
        assert.ok(h.overlay());
        const attempts = h.video.playCalls;
        await h.progress([[0, 110]]);
        assert.equal(h.video.paused, true, "Recovery must refill the target buffer");
        await h.progress([[0, 120]]);
        assert.equal(h.video.paused, false);
        assert.equal(h.video.playCalls, attempts + 1);
    }],
    ["seek waits for the new position and does not start while seeking", async () => {
        const h = createHarness();
        h.video.duration = 300;
        await h.attach();
        await h.progress([[0, 80]]);
        h.video.currentTime = 150;
        h.video.seeking = true;
        h.video.dispatch("seeking");
        await h.flush();
        assert.equal(h.video.paused, true);
        await h.progress([[0, 80], [150, 210]]);
        assert.equal(h.video.paused, true, "Even a ready buffer must wait for seek completion");
        h.video.seeking = false;
        h.video.dispatch("seeked");
        await h.flush();
        assert.equal(h.video.paused, false);
    }],
    ["destroy removes listeners, timer, overlay and automatic actions", async () => {
        const h = createHarness();
        await h.attach();
        assert.ok(h.video.listenerCount() > 0);
        h.hls.destroy();
        assert.equal(h.video.listenerCount(), 0);
        assert.equal(h.intervals.size, 0);
        assert.equal(h.overlay(), undefined);
        await h.progress([[0, 90]]);
        await h.tick();
        assert.equal(h.video.playCalls, 0);
        assert.equal(h.video.pauseCalls, 0);
    }],
    ["ordinary sources are untouched and changing source detaches the gate", async () => {
        const ordinary = createHarness();
        await ordinary.attach("https://example.test/movie/playlist.m3u8");
        assert.deepEqual(ordinary.hls.config, { maxBufferLength: 30, maxMaxBufferLength: 90 });
        assert.equal(ordinary.video.listenerCount(), 0);
        assert.equal(ordinary.intervals.size, 0);
        await ordinary.video.play();
        await ordinary.flush();
        assert.equal(ordinary.video.paused, false);

        const switched = createHarness();
        await switched.attach("/base/YummyKodik/kodik-proxy/segment.ts?sessionId=opaque&resource=opaque");
        assert.ok(switched.overlay());
        switched.hls.loadSource("https://example.test/other.m3u8");
        assert.deepEqual(switched.hls.config, { maxBufferLength: 30, maxMaxBufferLength: 90 },
            "Reusing an Hls instance for another stream must restore its original buffer configuration");
        assert.equal(switched.video.listenerCount(), 0);
        assert.equal(switched.intervals.size, 0);
        assert.equal(switched.overlay(), undefined);
        await switched.video.play();
        await switched.flush();
        assert.equal(switched.video.paused, false, "The removed gate must not pause an unrelated new stream");
    }],
    ["rejected autoplay waits for explicit user action", async () => {
        const h = createHarness();
        await h.attach();
        h.video.rejectNextPlay = true;
        await h.progress([[0, 80]]);
        assert.equal(h.video.paused, true);
        assert.ok(h.overlay());
        const attempts = h.video.playCalls;
        await h.tick();
        assert.equal(h.video.playCalls, attempts, "Blocked autoplay must not create a retry loop");
        h.button().dispatch("click");
        await h.flush();
        assert.equal(h.video.paused, false);
    }]
];

(async () => {
    let failures = 0;
    for (const [name, test] of tests) {
        try {
            await test();
            process.stdout.write(`PASS ${name}\n`);
        } catch (error) {
            failures++;
            process.stderr.write(`FAIL ${name}\n${error.stack}\n`);
        }
    }
    process.stdout.write(`${tests.length - failures}/${tests.length} playback buffer regressions passed\n`);
    process.exitCode = failures ? 1 : 0;
})().catch(error => {
    process.stderr.write(`${error.stack}\n`);
    process.exitCode = 1;
});
