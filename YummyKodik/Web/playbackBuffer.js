/* Wait for a playable minute on YummyKodik HLS streams before starting or recovering playback. */
(function () {
    "use strict";

    const TARGET_SECONDS = 60;
    // Jellyfin uses these CSS variables for all built-in and custom themes.
    // Keep them as CSS var() references so an in-session theme change updates
    // an already visible panel without re-creating it.
    const PANEL_STYLE = [
        "position:fixed", "z-index:100000", "left:50%", "top:36%",
        "transform:translate(-50%,-50%)", "width:min(360px,85vw)",
        "box-sizing:border-box", "padding:22px", "text-align:center",
        "background:var(--jf-palette-background-paper,Canvas)",
        "color:var(--jf-palette-text-primary,CanvasText)",
        "border:1px solid var(--jf-palette-divider,transparent)",
        "border-radius:var(--jf-card-borderRadius,0.75rem)",
        "font-family:inherit", "font-size:1rem", "line-height:1.5",
        "box-shadow:var(--jf-shadows-16,0 8px 36px rgb(0 0 0 / 0.24))"
    ].join(";");
    const BUTTON_STYLE = [
        "margin-top:14px", "padding:8px 14px",
        "border:1px solid var(--jf-palette-divider,currentColor)",
        "border-radius:var(--jf-card-borderRadius,0.5rem)",
        "color:var(--jf-palette-primary-main,inherit)",
        "background:transparent", "font:inherit", "cursor:pointer"
    ].join(";");
    const gates = new WeakMap();
    const installed = new WeakSet();

    function isManagedSource(source) {
        try {
            const path = new URL(source, window.location.href).pathname;
            return /\/YummyKodik\/(?:stream\/?|(?:kodik|alloha|cvh)-proxy(?:\/.*)?)$/i.test(path);
        } catch {
            return false;
        }
    }


    // A playback request starts before Jellyfin has an HLS URL. Query only the
    // installed plugin's own managed item membership, never inspect other libraries.
    const managedItems = new Map();
    const pendingChecks = new Map();
    const sourceRequests = new WeakMap();
    let requestedItemId = null;
    let preparation = null;

    function itemId(value) {
        const normalized = String(value || "").replace(/-/g, "").toLowerCase();
        return /^[0-9a-f]{32}$/.test(normalized) ? normalized : null;
    }

    function urlParts(value) {
        try {
            const result = new URL(value, window.location.href);
            return result.origin === new URL(window.location.href).origin ? result : null;
        } catch {
            return null;
        }
    }

    function playbackRequestId(method, url) {
        if (String(method).toUpperCase() !== "POST") return null;
        const parsed = urlParts(url);
        const match = parsed?.pathname.match(/\/Items\/([0-9a-f-]{32,36})\/PlaybackInfo\/?$/i);
        return match ? itemId(match[1]) : null;
    }

    function jellyfinHlsItemId(url) {
        const parsed = urlParts(url);
        const match = parsed?.pathname.match(/\/videos\/([0-9a-f-]{32,36})\/master\.m3u8$/i);
        return match ? itemId(match[1]) : null;
    }

    async function checkManagedItem(id) {
        if (managedItems.has(id)) return managedItems.get(id);
        if (pendingChecks.has(id)) return pendingChecks.get(id);
        const pending = (async () => {
            const api = window.ApiClient;
            if (!api || typeof api.getUrl !== "function" || typeof api.ajax !== "function") return false;
            const response = await api.ajax({
                type: "GET",
                url: api.getUrl("YummyKodik/playback-status/" + id)
            });
            let data = response;
            if (response && typeof response.json === "function") {
                if (response.ok === false) return false;
                data = await response.json();
            } else if (typeof response === "string") {
                data = JSON.parse(response);
            }
            const managed = data?.managed === true || data?.Managed === true;
            managedItems.set(id, managed);
            return managed;
        })();
        pendingChecks.set(id, pending);
        try {
            return await pending;
        } catch {
            return false; // An unavailable check must never block Jellyfin.
        } finally {
            pendingChecks.delete(id);
        }
    }

    function hidePreparation(id) {
        if (!preparation || (id && preparation.id !== id)) return;
        if (preparation.timeout) window.clearTimeout(preparation.timeout);
        preparation.element.remove();
        preparation = null;
    }

    function showPreparation(id, message) {
        if (!document.body) return;
        if (preparation && preparation.id !== id) hidePreparation();
        if (!preparation) {
            const panel = document.createElement("div");
            panel.id = "ykPlaybackBuffer";
            panel.style.cssText = PANEL_STYLE;
            const title = document.createElement("div");
            title.textContent = "Подготовка воспроизведения";
            title.style.cssText = "font-size:18px;font-weight:600;margin-bottom:8px";
            const status = document.createElement("div");
            status.setAttribute("role", "status");
            status.setAttribute("aria-live", "polite");
            const close = document.createElement("button");
            close.type = "button";
            close.textContent = "Скрыть";
            close.style.cssText = BUTTON_STYLE;
            close.addEventListener("click", () => hidePreparation(id));
            panel.append(title, status, close);
            (document.fullscreenElement || document.body).appendChild(panel);
            preparation = {
                id, element: panel, status,
                timeout: window.setTimeout(() => hidePreparation(id), 90000)
            };
        }
        preparation.status.textContent = message;
    }

    function observePlaybackRequest(method, url) {
        const id = playbackRequestId(method, url);
        if (!id) return;
        requestedItemId = id;
        if (preparation && preparation.id !== id) hidePreparation();
        // No influence on the request. Only an authenticated, positive local
        // membership check can display the preparation panel.
        void checkManagedItem(id).then(managed => {
            if (managed && requestedItemId === id) {
                showPreparation(id, "Получаю источник видео…");
            }
        });
    }

    function observePlaybackRequests() {
        if (typeof window.fetch === "function") {
            const originalFetch = window.fetch;
            window.fetch = function (request, options) {
                try {
                    observePlaybackRequest(
                        options?.method || request?.method || "GET",
                        typeof request === "string" ? request : request?.url
                    );
                } catch { /* Preserve the host's fetch behavior. */ }
                return originalFetch.apply(this, arguments);
            };
        }

        if (window.XMLHttpRequest?.prototype?.open) {
            const originalOpen = window.XMLHttpRequest.prototype.open;
            window.XMLHttpRequest.prototype.open = function (method, url) {
                try { observePlaybackRequest(method, url); }
                catch { /* Preserve the host's XHR behavior. */ }
                return originalOpen.apply(this, arguments);
            };
        }
    }

    function createGate(hls, Hls) {
        const originalBufferLength = hls.config.maxBufferLength;
        const originalMaxBufferLength = hls.config.maxMaxBufferLength;
        // Jellyfin's high-bitrate Chrome profile can cap both values at six seconds.
        hls.config.maxBufferLength = Math.max(120, originalBufferLength || 0);
        hls.config.maxMaxBufferLength = Math.max(120, originalMaxBufferLength || 0);
        let media = null;
        let filling = false;
        let resumeRequested = false;
        let ownPauseEvents = 0;
        let overlay = null;
        let status = null;
        let button = null;
        let disposed = false;
        let timer = 0;
        let resumePending = false;
        let waitingForSource = false;
        let lastBufferedSeconds = 0;

        function bufferedAhead() {
            if (!media) return 0;
            for (let index = 0; index < media.buffered.length; index++) {
                if (media.buffered.start(index) <= media.currentTime + 0.25 &&
                    media.buffered.end(index) > media.currentTime) {
                    return media.buffered.end(index) - media.currentTime;
                }
            }
            return 0;
        }

        function targetSeconds() {
            if (media && Number.isFinite(media.duration) && media.duration > 0) {
                return Math.min(TARGET_SECONDS, Math.max(0, media.duration - media.currentTime));
            }
            return TARGET_SECONDS;
        }

        function isReady() {
            const target = targetSeconds();
            const required = target < TARGET_SECONDS ? Math.max(0, target - 0.25) : target;
            return media && media.readyState >= 3 && bufferedAhead() >= required;
        }

        function hideOverlay() {
            if (overlay) overlay.remove();
            overlay = status = button = null;
        }

        function render() {
            if (!filling || !media || disposed || !document.body) return;
            if (!overlay) {
                overlay = document.createElement("div");
                overlay.id = "ykPlaybackBuffer";
                overlay.style.cssText = PANEL_STYLE;
                const title = document.createElement("div");
                title.textContent = "Подготовка воспроизведения";
                title.style.cssText = "font-size:18px;font-weight:600;margin-bottom:8px";
                status = document.createElement("div");
                status.setAttribute("role", "status");
                status.setAttribute("aria-live", "polite");
                button = document.createElement("button");
                button.type = "button";
                button.style.cssText = BUTTON_STYLE;
                button.addEventListener("click", () => {
                    resumeRequested = !resumeRequested;
                    tick();
                });
                overlay.append(title, status, button);
                (document.fullscreenElement || document.body).appendChild(overlay);
            }
            const loaded = Math.min(Math.floor(bufferedAhead()), Math.ceil(targetSeconds()));
            status.textContent = "Загружено " + loaded + " из " + Math.ceil(targetSeconds()) + " с видео. " +
                (waitingForSource ? "Ожидаю данные от источника. " : "") +
                (resumeRequested ? "Запустится автоматически." : "Автозапуск приостановлен.");
            button.textContent = resumeRequested ? "Приостановить автозапуск" : "Включить автозапуск";
        }

        function pauseForBuffer() {
            if (!media || disposed) return;
            filling = true;
            if (!media.paused) {
                ownPauseEvents++;
                media.pause();
            }
            render();
        }

        function onPlay() {
            if (disposed) return;
            resumeRequested = true;
            if (!isReady() && !resumePending) pauseForBuffer();
        }

        function onPause() {
            if (ownPauseEvents > 0) {
                ownPauseEvents--;
                return;
            }
            resumeRequested = false;
            if (filling) render();
        }

        function onWaiting() {
            if (media && !media.paused && !media.seeking && bufferedAhead() < 0.5) {
                resumeRequested = true;
                pauseForBuffer();
            }
        }

        function onSeeking() {
            if (media && (!media.paused || resumeRequested)) {
                resumeRequested = true;
                pauseForBuffer();
            }
        }

        function tick() {
            if (!filling || disposed || !media || resumePending) return;
            if (isReady() && resumeRequested && !media.seeking) {
                filling = false;
                resumePending = true;
                hideOverlay();
                Promise.resolve(media.play()).catch(() => {
                    // Autoplay may require a fresh user gesture. Keep the ready buffer available.
                    if (!disposed) {
                        filling = true;
                        resumeRequested = false;
                        render();
                    }
                }).finally(() => { resumePending = false; });
            } else {
                render();
            }
        }

        function onBufferProgress() {
            const buffered = bufferedAhead();
            if (buffered > lastBufferedSeconds + 0.01) waitingForSource = false;
            lastBufferedSeconds = buffered;
            tick();
        }

        const listeners = {
            play: onPlay,
            playing: onPlay,
            pause: onPause,
            waiting: onWaiting,
            seeking: onSeeking,
            seeked: tick,
            progress: onBufferProgress,
            loadeddata: onBufferProgress
        };

        function detach() {
            if (media) {
                for (const [event, listener] of Object.entries(listeners)) media.removeEventListener(event, listener);
            }
            if (timer) window.clearInterval(timer);
            timer = 0;
            media = null;
            filling = false;
            resumeRequested = false;
            ownPauseEvents = 0;
            waitingForSource = false;
            lastBufferedSeconds = 0;
            hideOverlay();
        }

        function attach(_, data) {
            detach();
            media = data.media;
            if (!media || media.tagName !== "VIDEO") {
                media = null;
                return;
            }
            for (const [event, listener] of Object.entries(listeners)) media.addEventListener(event, listener);
            timer = window.setInterval(tick, 500);
            filling = true;
            resumeRequested = true;
            render();
        }

        function onError(_, data) {
            // Jellyfin can recover even a fatal network error with startLoad(). The HLS
            // destroy/detach events own cleanup; hiding here silently abandoned the gate.
            if (data.type === "networkError") waitingForSource = true;
            if (filling) render();
        }

        function dispose() {
            if (disposed) return;
            disposed = true;
            detach();
            hls.config.maxBufferLength = originalBufferLength;
            hls.config.maxMaxBufferLength = originalMaxBufferLength;
            hls.off(Hls.Events.MEDIA_ATTACHED, attach);
            hls.off(Hls.Events.MEDIA_DETACHING, detach);
            hls.off(Hls.Events.BUFFER_APPENDED, onBufferProgress);
            hls.off(Hls.Events.ERROR, onError);
            hls.off(Hls.Events.DESTROYING, dispose);
        }

        hls.on(Hls.Events.MEDIA_ATTACHED, attach);
        hls.on(Hls.Events.MEDIA_DETACHING, detach);
        hls.on(Hls.Events.BUFFER_APPENDED, onBufferProgress);
        hls.on(Hls.Events.ERROR, onError);
        hls.on(Hls.Events.DESTROYING, dispose);
        return { dispose };
    }


    // Remuxed Jellyfin video is identified by the actual managed item ID.
    // Display buffering progress, but do not impose the 60-second gate used
    // for raw plugin HLS; this would slow ordinary episode transitions.
    function createRemuxIndicator(hls, Hls, id) {
        let media = null;
        let disposed = false;
        let waiting = true;

        function bufferedAhead() {
            if (!media) return 0;
            for (let i = 0; i < media.buffered.length; i++) {
                if (media.buffered.start(i) <= media.currentTime + 0.25 &&
                    media.buffered.end(i) > media.currentTime) {
                    return media.buffered.end(i) - media.currentTime;
                }
            }
            return 0;
        }

        function showProgress() {
            if (!media || disposed || !waiting) return;
            const seconds = Math.floor(bufferedAhead());
            showPreparation(id, seconds > 0
                ? "Загружено " + seconds + " с видео. Ожидаю начало воспроизведения…"
                : "Загружаю видео…");
        }

        function onPlaying() {
            waiting = false;
            hidePreparation(id);
        }

        function onWaiting() {
            if (!media || disposed || media.paused || media.seeking) return;
            waiting = true;
            showProgress();
        }

        function onSeeking() {
            if (!media || disposed || media.paused) return;
            waiting = true;
            showPreparation(id, "Перемотка видео…");
        }

        const events = {
            playing: onPlaying,
            waiting: onWaiting,
            seeking: onSeeking,
            progress: showProgress,
            loadeddata: showProgress,
            pause: () => hidePreparation(id),
            ended: () => hidePreparation(id)
        };

        function detach() {
            if (media) {
                for (const [event, callback] of Object.entries(events)) {
                    media.removeEventListener(event, callback);
                }
            }
            media = null;
            waiting = false;
            hidePreparation(id);
        }

        function attach(_, data) {
            detach();
            media = data?.media;
            if (!media || media.tagName !== "VIDEO") {
                media = null;
                return;
            }
            waiting = true;
            for (const [event, callback] of Object.entries(events)) {
                media.addEventListener(event, callback);
            }
            // A slow membership check may complete after playback has started.
            if (!media.paused && media.readyState >= 3) onPlaying();
            else showProgress();
        }

        function onBufferProgress() { showProgress(); }

        function onError(_, data) {
            if (data?.type === "networkError" && waiting) {
                showPreparation(id, "Ожидаю данные от источника…");
            }
        }

        function dispose() {
            if (disposed) return;
            disposed = true;
            detach();
            hls.off(Hls.Events.MEDIA_ATTACHED, attach);
            hls.off(Hls.Events.MEDIA_DETACHING, detach);
            hls.off(Hls.Events.BUFFER_APPENDED, onBufferProgress);
            hls.off(Hls.Events.ERROR, onError);
            hls.off(Hls.Events.DESTROYING, dispose);
        }

        hls.on(Hls.Events.MEDIA_ATTACHED, attach);
        hls.on(Hls.Events.MEDIA_DETACHING, detach);
        hls.on(Hls.Events.BUFFER_APPENDED, onBufferProgress);
        hls.on(Hls.Events.ERROR, onError);
        hls.on(Hls.Events.DESTROYING, dispose);
        return { dispose, attachMedia: element => attach(null, { media: element }) };
    }

    function install(Hls) {
        if (!Hls || !Hls.prototype || installed.has(Hls)) return;
        installed.add(Hls);
        const originalLoadSource = Hls.prototype.loadSource;
        Hls.prototype.loadSource = function (source) {
            const previous = gates.get(this);
            if (previous) previous.dispose();
            gates.delete(this);
            sourceRequests.set(this, source);

            if (isManagedSource(source)) {
                hidePreparation();
                gates.set(this, createGate(this, Hls));
            } else {
                const id = jellyfinHlsItemId(source);
                if (id && managedItems.get(id) === true) {
                    gates.set(this, createRemuxIndicator(this, Hls, id));
                } else if (id) {
                    // The membership request may complete after loadSource().
                    // Attach to the already-created video element in that case.
                    void checkManagedItem(id).then(managed => {
                        if (!managed || sourceRequests.get(this) !== source || gates.has(this)) return;
                        const gate = createRemuxIndicator(this, Hls, id);
                        gates.set(this, gate);
                        if (this.media) gate.attachMedia(this.media);
                    });
                } else {
                    hidePreparation();
                }
            }

            return originalLoadSource.call(this, source);
        };
    }

    observePlaybackRequests();

    // Jellyfin assigns the lazily imported constructor immediately before creating its player.
    let currentHls = window.Hls;
    const descriptor = Object.getOwnPropertyDescriptor(window, "Hls");
    if (!descriptor || descriptor.configurable) {
        Object.defineProperty(window, "Hls", {
            configurable: true,
            enumerable: true,
            get: () => currentHls,
            set: value => { currentHls = value; install(value); }
        });
    }
    install(currentHls);
})();
