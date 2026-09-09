/* Wait for a playable minute on YummyKodik HLS streams before starting or recovering playback. */
(function () {
    "use strict";

    const TARGET_SECONDS = 60;
    const gates = new WeakMap();
    const installed = new WeakSet();

    function isManagedSource(source) {
        try {
            const path = new URL(source, window.location.href).pathname;
            return /\/YummyKodik\/(?:stream\/?|kodik-proxy(?:\/.*)?)$/i.test(path);
        } catch {
            return false;
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
                overlay.style.cssText = "position:fixed;z-index:100000;left:50%;top:36%;transform:translate(-50%,-50%);width:min(360px,85vw);box-sizing:border-box;padding:22px;border-radius:14px;background:rgba(12,16,23,.94);color:#fff;text-align:center;font:16px/1.5 sans-serif;box-shadow:0 8px 36px #0008";
                const title = document.createElement("div");
                title.textContent = "Подготовка воспроизведения";
                title.style.cssText = "font-size:18px;font-weight:600;margin-bottom:8px";
                status = document.createElement("div");
                status.setAttribute("role", "status");
                status.setAttribute("aria-live", "polite");
                button = document.createElement("button");
                button.type = "button";
                button.style.cssText = "margin-top:14px;padding:8px 14px;border:1px solid #ffffff60;border-radius:8px;color:white;background:transparent;font:inherit;cursor:pointer";
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

    function install(Hls) {
        if (!Hls || !Hls.prototype || installed.has(Hls)) return;
        installed.add(Hls);
        const originalLoadSource = Hls.prototype.loadSource;
        Hls.prototype.loadSource = function (source) {
            const previous = gates.get(this);
            if (previous) previous.dispose();
            gates.delete(this);
            if (isManagedSource(source)) {
                gates.set(this, createGate(this, Hls));
            }
            return originalLoadSource.call(this, source);
        };
    }

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
