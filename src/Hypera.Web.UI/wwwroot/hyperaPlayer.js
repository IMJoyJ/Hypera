(function () {
    "use strict";

    if (window.hyperaPlayer) {
        return;
    }

    const assetUrls = new Map();
    const registeredFonts = new Set();
    const scalers = new WeakMap();
    let fullscreenCleanup = null;

    // 10 SE channels + 1 BGM channel
    const seChannels = new Array(10).fill(null).map(() => ({
        audio: null,
        volume: 100,
        objectUrl: null,
        ownedUrl: false
    }));
    const bgmChannel = {
        audio: null,
        volume: 100,
        objectUrl: null,
        ownedUrl: false
    };
    let masterMuted = false;
    let masterVolume = 100;

    function effectiveGain(channelVolume) {
        if (masterMuted) {
            return 0;
        }
        const m = Math.max(0, Math.min(100, masterVolume)) / 100;
        const c = Math.max(0, Math.min(100, channelVolume)) / 100;
        return Math.max(0, Math.min(1, m * c));
    }

    function applyVolumes() {
        for (let i = 0; i < seChannels.length; i++) {
            const ch = seChannels[i];
            if (ch.audio) {
                ch.audio.volume = effectiveGain(ch.volume);
            }
        }
        if (bgmChannel.audio) {
            bgmChannel.audio.volume = effectiveGain(bgmChannel.volume);
        }
    }

    function stopChannelState(ch) {
        if (ch.audio) {
            try {
                ch.audio.pause();
                ch.audio.currentTime = 0;
                ch.audio.src = "";
            } catch (_) {
                // Ignore media teardown errors
            }
            ch.audio = null;
        }
        if (ch.ownedUrl && ch.objectUrl) {
            URL.revokeObjectURL(ch.objectUrl);
        }
        ch.objectUrl = null;
        ch.ownedUrl = false;
    }

    function playOnChannel(ch, url, ownedUrl, volume, loop) {
        stopChannelState(ch);
        ch.volume = typeof volume === "number" ? volume : 100;
        ch.objectUrl = url;
        ch.ownedUrl = ownedUrl;

        const audio = new Audio(url);
        audio.loop = !!loop;
        audio.volume = effectiveGain(ch.volume);
        ch.audio = audio;

        if (!loop && ownedUrl) {
            audio.addEventListener("ended", () => {
                if (ch.audio === audio) {
                    stopChannelState(ch);
                }
            }, { once: true });
        }

        const p = audio.play();
        if (p && typeof p.catch === "function") {
            p.catch(() => {
                // Browser autoplay policy may block until user gesture
            });
        }
    }

    async function streamRefToBlob(streamRef, mimeType) {
        const buffer = await streamRef.arrayBuffer();
        return new Blob([buffer], { type: mimeType || "application/octet-stream" });
    }

    function computeScale(wrapperEl, stageEl, logicalWidth, logicalHeight, paddingPx) {
        if (!wrapperEl || !stageEl || logicalWidth <= 0 || logicalHeight <= 0) {
            return 1;
        }
        const pad = typeof paddingPx === "number" && paddingPx >= 0 ? paddingPx : 4;
        const rect = wrapperEl.getBoundingClientRect();
        const availW = Math.max(1, rect.width - pad * 2);
        const availH = Math.max(1, rect.height - pad * 2);
        const scale = Math.min(availW / logicalWidth, availH / logicalHeight);
        const clamped = Math.max(0.05, scale);

        stageEl.style.width = `${logicalWidth}px`;
        stageEl.style.height = `${logicalHeight}px`;
        stageEl.style.transform = `scale(${clamped})`;
        stageEl.style.transformOrigin = "top left";
        stageEl.dataset.scale = String(clamped);

        const container = stageEl.parentElement;
        if (container && container.classList.contains("hypera-stage-box")) {
            container.style.width = `${Math.floor(logicalWidth * clamped)}px`;
            container.style.height = `${Math.floor(logicalHeight * clamped)}px`;
        }
        return clamped;
    }

    window.hyperaPlayer = {
        async downloadFromStream(fileName, contentType, streamRef) {
            const blob = await streamRefToBlob(streamRef, contentType || "application/octet-stream");
            const url = URL.createObjectURL(blob);
            try {
                const a = document.createElement("a");
                a.href = url;
                a.download = fileName || "download.bin";
                a.style.display = "none";
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
            } finally {
                setTimeout(() => URL.revokeObjectURL(url), 5000);
            }
        },

        async postJson(url, payload) {
            const resp = await fetch(url, {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    "Content-Type": "application/json",
                    "Accept": "application/json"
                },
                body: JSON.stringify(payload || {})
            });
            const text = await resp.text();
            return {
                ok: resp.ok,
                status: resp.status,
                text: text
            };
        },

        attachStageScaler(wrapperEl, stageEl, logicalWidth, logicalHeight, paddingPx) {
            if (!wrapperEl || !stageEl) {
                return;
            }
            this.detachStageScaler(wrapperEl);
            let rafId = 0;
            const runNow = () => {
                rafId = 0;
                computeScale(wrapperEl, stageEl, logicalWidth, logicalHeight, paddingPx);
            };
            const schedule = () => {
                if (!rafId) {
                    rafId = requestAnimationFrame(runNow);
                }
            };

            runNow();

            let ro = null;
            if (typeof ResizeObserver !== "undefined") {
                ro = new ResizeObserver(schedule);
                ro.observe(wrapperEl);
            }
            window.addEventListener("resize", schedule);
            if (window.visualViewport) {
                window.visualViewport.addEventListener("resize", schedule);
            }

            scalers.set(wrapperEl, {
                ro,
                schedule,
                cancelRaf: () => { if (rafId) { cancelAnimationFrame(rafId); rafId = 0; } }
            });
        },

        detachStageScaler(wrapperEl) {
            if (!wrapperEl) {
                return;
            }
            const entry = scalers.get(wrapperEl);
            if (!entry) {
                return;
            }
            entry.cancelRaf();
            if (entry.ro) {
                entry.ro.disconnect();
            }
            window.removeEventListener("resize", entry.schedule);
            if (window.visualViewport) {
                window.visualViewport.removeEventListener("resize", entry.schedule);
            }
            scalers.delete(wrapperEl);
        },

        isDocumentFullscreen() {
            return !!(document.fullscreenElement || document.webkitFullscreenElement);
        },

        async setDocumentFullscreen(enter) {
            try {
                if (enter) {
                    if (!this.isDocumentFullscreen()) {
                        const root = document.documentElement;
                        if (root.requestFullscreen) {
                            await root.requestFullscreen();
                        } else if (root.webkitRequestFullscreen) {
                            await root.webkitRequestFullscreen();
                        }
                    }
                    return true;
                } else {
                    if (this.isDocumentFullscreen()) {
                        if (document.exitFullscreen) {
                            await document.exitFullscreen();
                        } else if (document.webkitExitFullscreen) {
                            await document.webkitExitFullscreen();
                        }
                    }
                    return false;
                }
            } catch (_) {
                return !!enter;
            }
        },

        registerPlayKeyAndFullscreenHooks(dotNetRef) {
            this.unregisterPlayKeyAndFullscreenHooks();
            if (!dotNetRef) {
                return;
            }

            const onFullscreenChange = () => {
                const active = this.isDocumentFullscreen();
                dotNetRef.invokeMethodAsync("OnDocumentFullscreenChanged", active).catch(() => {});
            };

            const onKeyDown = (ev) => {
                if (ev.key === "F11") {
                    ev.preventDefault();
                    dotNetRef.invokeMethodAsync("OnFullscreenShortcutAsync").catch(() => {});
                } else if (ev.key === "Escape") {
                    dotNetRef.invokeMethodAsync("OnEscapeShortcutAsync").catch(() => {});
                }
            };

            document.addEventListener("fullscreenchange", onFullscreenChange);
            document.addEventListener("webkitfullscreenchange", onFullscreenChange);
            window.addEventListener("keydown", onKeyDown, true);

            fullscreenCleanup = () => {
                document.removeEventListener("fullscreenchange", onFullscreenChange);
                document.removeEventListener("webkitfullscreenchange", onFullscreenChange);
                window.removeEventListener("keydown", onKeyDown, true);
            };
        },

        unregisterPlayKeyAndFullscreenHooks() {
            if (fullscreenCleanup) {
                fullscreenCleanup();
                fullscreenCleanup = null;
            }
        },

        getLogicalCoords(stageEl, clientX, clientY, logicalWidth, logicalHeight) {
            const selection = window.getSelection ? window.getSelection().toString() : "";
            const hasSelection = selection && selection.length > 0;
            if (!stageEl || logicalWidth <= 0 || logicalHeight <= 0) {
                return { x: 0, y: 0, inside: false, hasSelection: !!hasSelection };
            }
            const rect = stageEl.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) {
                return { x: 0, y: 0, inside: false, hasSelection: !!hasSelection };
            }
            const lx = Math.floor(((clientX - rect.left) / rect.width) * logicalWidth);
            const ly = Math.floor(((clientY - rect.top) / rect.height) * logicalHeight);
            const inside = lx >= 0 && lx < logicalWidth && ly >= 0 && ly < logicalHeight;
            return {
                x: Math.max(0, Math.min(logicalWidth - 1, lx)),
                y: Math.max(0, Math.min(logicalHeight - 1, ly)),
                inside,
                hasSelection: !!hasSelection
            };
        },

        async registerFontFromStream(fontFamily, streamRef) {
            if (!fontFamily || registeredFonts.has(fontFamily)) {
                return true;
            }
            try {
                const buffer = await streamRef.arrayBuffer();
                const face = new FontFace(fontFamily, buffer);
                await face.load();
                document.fonts.add(face);
                registeredFonts.add(fontFamily);
                return true;
            } catch (_) {
                return false;
            }
        },

        async registerFontAliasesFromStream(fontFamilies, streamRef) {
            if (!Array.isArray(fontFamilies) || fontFamilies.length === 0) {
                return false;
            }
            const missing = fontFamilies.filter(f => f && !registeredFonts.has(f));
            if (missing.length === 0) {
                return true;
            }
            try {
                const buffer = await streamRef.arrayBuffer();
                for (const family of missing) {
                    const face = new FontFace(family, buffer.slice(0));
                    await face.load();
                    document.fonts.add(face);
                    registeredFonts.add(family);
                }
                return true;
            } catch (_) {
                return false;
            }
        },

        async registerAssetFromStream(assetKey, mimeType, streamRef) {
            if (!assetKey) {
                return null;
            }
            const existing = assetUrls.get(assetKey);
            const blob = await streamRefToBlob(streamRef, mimeType || "image/png");
            const url = URL.createObjectURL(blob);
            assetUrls.set(assetKey, url);
            // Delay revoking previous blob URL so in-flight <img> elements transition seamlessly without flicker.
            if (existing) {
                setTimeout(() => URL.revokeObjectURL(existing), 600);
            }
            return url;
        },

        getAssetUrl(assetKey) {
            return assetUrls.get(assetKey) || null;
        },

        revokeAsset(assetKey) {
            const url = assetUrls.get(assetKey);
            if (url) {
                URL.revokeObjectURL(url);
                assetUrls.delete(assetKey);
            }
        },

        revokeAllAssets() {
            for (const url of assetUrls.values()) {
                URL.revokeObjectURL(url);
            }
            assetUrls.clear();
        },

        async playSeFromStream(channelIndex, mimeType, streamRef, volume, loop) {
            if (channelIndex < 0 || channelIndex >= seChannels.length) {
                return;
            }
            const blob = await streamRefToBlob(streamRef, mimeType || "audio/wav");
            const url = URL.createObjectURL(blob);
            playOnChannel(seChannels[channelIndex], url, true, volume, loop);
        },

        playSeCached(channelIndex, assetKey, volume, loop) {
            if (channelIndex < 0 || channelIndex >= seChannels.length) {
                return;
            }
            const url = assetUrls.get(assetKey);
            if (!url) {
                return;
            }
            playOnChannel(seChannels[channelIndex], url, false, volume, loop);
        },

        stopSe(channelIndex) {
            if (channelIndex < 0 || channelIndex >= seChannels.length) {
                return;
            }
            stopChannelState(seChannels[channelIndex]);
        },

        stopAllSe() {
            for (let i = 0; i < seChannels.length; i++) {
                stopChannelState(seChannels[i]);
            }
        },

        setSeVolume(channelIndex, volume) {
            if (channelIndex < 0 || channelIndex >= seChannels.length) {
                return;
            }
            seChannels[channelIndex].volume = volume;
            applyVolumes();
        },

        async playBgmFromStream(mimeType, streamRef, volume, loop) {
            const blob = await streamRefToBlob(streamRef, mimeType || "audio/mpeg");
            const url = URL.createObjectURL(blob);
            playOnChannel(bgmChannel, url, true, volume, loop !== false);
        },

        playBgmCached(assetKey, volume, loop) {
            const url = assetUrls.get(assetKey);
            if (!url) {
                return;
            }
            playOnChannel(bgmChannel, url, false, volume, loop !== false);
        },

        stopBgm() {
            stopChannelState(bgmChannel);
        },

        setBgmVolume(volume) {
            bgmChannel.volume = volume;
            applyVolumes();
        },

        setMasterMuted(muted) {
            masterMuted = !!muted;
            applyVolumes();
        },

        setMasterVolume(volume) {
            masterVolume = typeof volume === "number" ? volume : 100;
            applyVolumes();
        },

        stopAllAudio() {
            this.stopAllSe();
            this.stopBgm();
        },

        focusElement(el) {
            if (el && typeof el.focus === "function") {
                try {
                    el.focus({ preventScroll: true });
                } catch (_) {
                    el.focus();
                }
            }
        },

        getLocalStorage(key) {
            try {
                return window.localStorage.getItem(key);
            } catch (_) {
                return null;
            }
        },

        setLocalStorage(key, value) {
            try {
                window.localStorage.setItem(key, value);
            } catch (_) {
                // Ignore storage quota / privacy restrictions
            }
        }
    };
})();
