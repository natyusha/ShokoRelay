/**
 * @file animethemes.js
 * @description Dedicated logic for the AnimeThemes VFS and Theme.mp3 generation on the Shoko Relay dashboard.
 */
(() => {
  const { base, el, TOAST_MS, fetchJson, showToast, toastOperation, summarizeResult, withButtonAction, initToggle, setIfNotEmpty } = window._sr;

  // #region Helpers
  /**
   * Decode unicode escape sequences (e.g. \\u002C -> ,) in a string.
   * @param {string} s - The string to decode.
   * @returns {string} The decoded string.
   */
  const decodeUnicode = (s) => s.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)));

  /**
   * Generic helper to cycle through an array of modes.
   * @param {string} current - The current mode value.
   * @param {string[]} modes - Array of available modes.
   * @returns {string} The next mode in the sequence.
   */
  const getNextMode = (current, modes) => modes[(modes.indexOf(current) + 1) % modes.length];

  /**
   * Build URLSearchParams for AnimeThemes MP3 requests from the dashboard inputs and toggles.
   * @returns {URLSearchParams} The compiled search parameters.
   */
  const buildAtParams = () => {
    const ps = new URLSearchParams();
    ["path", "slug", "offset", "filter"].forEach((k) => setIfNotEmpty(ps, k, el(`at-${k}`)?.value));
    if (el("at-force")?.getAttribute("aria-pressed") === "true") ps.set("force", "true");
    if (el("at-batch")?.getAttribute("aria-pressed") === "true") ps.set("batch", "true");
    return ps;
  };

  /**
   * Build URLSearchParams for AnimeThemes VFS mapping requests from the filter input.
   * @returns {URLSearchParams} The compiled search parameters.
   */
  const buildAtMapParams = () => {
    const ps = new URLSearchParams();
    setIfNotEmpty(ps, "filter", el("at-filter-map")?.value);
    return ps;
  };
  // #endregion

  // #region VFS
  withButtonAction(el("at-import"), async () => {
    showToast("AnimeThemes: Importing Mapping...", "info", TOAST_MS);
    const res = await fetchJson(base + "/animethemes/vfs/import", { method: "POST" });
    toastOperation(res, "AnimeThemes Import", { summary: res.ok ? `mapping imported (${res.data?.count ?? 0} entries)` : undefined });
  });

  /**
   * Create a click handler for AnimeThemes map/build buttons with toast feedback.
   * @param {HTMLElement} btn - The button element to bind the action to.
   * @param {string} label - Display label for toast messages.
   * @param {string} endpoint - Server API endpoint to call.
   */
  function atBuildAction(btn, label, endpoint) {
    withButtonAction(btn, async () => {
      const startToast = showToast(`AnimeThemes: ${label}...`, "info", 0);
      const p = buildAtMapParams();
      const res = await fetchJson(`${base}${endpoint}${p.toString() ? "?" + p : ""}`);
      startToast?.remove();
      // Persistent toast if no filter is applied (unfiltered generation)
      const hideOnSucceed = p.get("filter") ? TOAST_MS : 0;
      toastOperation(res, `AnimeThemes ${label}`, { summary: summarizeResult(res) || "complete", hideOnSucceed });
    });
  }
  atBuildAction(el("at-mapping"), "Mapping", "/animethemes/vfs/map");
  atBuildAction(el("at-apply"), "Generation", "/animethemes/vfs/build");
  // #endregion

  // #region Theme MP3
  initToggle("at-force", false);
  initToggle("at-batch", false);
  let atAudio = null,
    nowPlayingToast = null,
    atCurrentFolder = null;

  /**
   * Dismiss the currently active 'Now Playing' toast notification if it exists.
   */
  function dismissNowPlaying() {
    if (nowPlayingToast) {
      nowPlayingToast.click();
      nowPlayingToast = null;
    }
  }

  /**
   * Fetches metadata for a Theme.mp3 and displays a 'Now Playing' toast.
   * @param {string} folderPath - The folder path to query for theme headers.
   * @returns {Promise<void>}
   */
  async function showNowPlaying(folderPath) {
    try {
      const res = await fetch(`${base}/animethemes/mp3/stream?path=${encodeURIComponent(folderPath)}`, { method: "HEAD" });
      const title = res.headers.get("X-Theme-Title");
      if (!title) return;
      const slug = (res.headers.get("X-Theme-Slug") || "").replace(/\bOpening\s?/gi, "OP").replace(/\bEnding\s?/gi, "ED");
      const artist = res.headers.get("X-Theme-Artist"),
        album = res.headers.get("X-Theme-Album");
      let html = `<span class="np-line">${title}</span>${artist ? `<span class="np-line">${artist}</span>` : ""}`;
      const meta = [album, slug].filter(Boolean).join(" \u2014 ");
      if (meta) html += `<small class="np-line">${meta}</small>`;
      dismissNowPlaying();
      nowPlayingToast = showToast(html, "info", 0);
    } catch {}
  }

  const atTrack = el("at-progress-track"),
    atFill = el("at-progress-fill");

  /**
   * Synchronizes the MP3 playback UI states (progress bar, play button state) with the audio object.
   */
  function syncPlaybackUI() {
    const isE = el("at-playback")?.getAttribute("aria-pressed") === "true",
      isA = isE && atAudio?.src && !atAudio.ended;
    atTrack?.classList.toggle("active", isA);
    atTrack?.classList.toggle("paused", atAudio?.paused);
    if (!isA && atFill) atFill.style.width = "0%";
    const pBtn = el("at-play");
    if (pBtn) pBtn.setAttribute("data-state", atAudio && !atAudio.paused && !atAudio.ended ? "playing" : "idle");
  }

  if (atTrack) {
    /** @param {MouseEvent} e */
    atTrack.onclick = (e) => atAudio && (atAudio.currentTime = ((e.clientX - atTrack.getBoundingClientRect().left) / atTrack.offsetWidth) * atAudio.duration);
    /** @param {MouseEvent} e */
    atTrack.onmousedown = (e) => {
      if (e.button === 1 && atAudio?.src) {
        e.preventDefault();
        atAudio.paused ? atAudio.play() : atAudio.pause();
      }
    };
  }

  /**
   * Stream and play a Theme.mp3 in the background using the streaming endpoint.
   * @param {string} folderPath - The folder containing the Theme.mp3 to stream.
   */
  function playThemeMp3(folderPath) {
    if (el("at-playback")?.getAttribute("aria-pressed") !== "true" || !folderPath) return;
    if (!atAudio) {
      atAudio = new Audio();
      atAudio.volume = 0.5;
      atAudio.ontimeupdate = () => atFill && (atFill.style.width = (atAudio.currentTime / atAudio.duration) * 100 + "%");
      ["play", "pause"].forEach((ev) => atAudio.addEventListener(ev, syncPlaybackUI));
      atAudio.onended = async () => {
        syncPlaybackUI();
        if (el("at-mode")?.getAttribute("data-mode") === "shuffle") {
          const d = await (await fetch(base + "/animethemes/mp3/random")).json();
          if (d?.path) playThemeMp3(d.path);
        } else dismissNowPlaying();
      };
    }
    atAudio.src = `${base}/animethemes/mp3/stream?path=${encodeURIComponent(folderPath)}`;
    atAudio.loop = el("at-mode")?.getAttribute("data-mode") === "loop";
    atAudio.play().catch(() => {});
    atCurrentFolder = folderPath;
    showNowPlaying(folderPath);
  }

  withButtonAction(el("at-single"), async () => {
    const startToast = showToast("AnimeThemes MP3: Generating...", "info", 0);
    const ps = buildAtParams();
    const isBatch = ps.get("batch") === "true";
    const res = await fetchJson(`${base}/animethemes/mp3?${ps}`);
    startToast?.remove();

    const status = res.data?.status || res.data?.Status;
    const hideOnSucceed = isBatch ? 0 : TOAST_MS;

    if (res.ok || status === "skipped") {
      toastOperation(res, "AnimeThemes MP3", { summary: summarizeResult(res) || status || "Complete", hideOnSucceed });
      if (!isBatch && (status === "ok" || status === "skipped")) {
        const folder = res.data?.folder || res.data?.Folder || ps.get("path");
        if (folder) playThemeMp3(folder);
      }
    } else {
      toastOperation(res, "AnimeThemes MP3", { hideOnSucceed: 0 });
    }
  });

  /**
   * Initialize AnimeThemes config bindings.
   * @param {Object} cfg - The current config object.
   * @param {Function} persist - Async function to save config changes.
   */
  window._sr.initAtConfig = (cfg, persist) => {
    const pb = el("at-playback"),
      mBtn = el("at-mode"),
      pBtn = el("at-play");

    const getVal = (path) => window._sr.getValueByPath(cfg, path);

    if (pb) {
      const sync = (v) => {
        if (pBtn) pBtn.hidden = !v;
        if (mBtn) mBtn.hidden = !v;
        syncPlaybackUI();
      };

      const isEnabled = !!getVal("Playback.AnimeThemesMp3Playback");
      pb.setAttribute("aria-pressed", isEnabled);
      sync(isEnabled);

      pb.onclick = async () => {
        const n = pb.getAttribute("aria-pressed") !== "true";
        pb.setAttribute("aria-pressed", n);
        sync(n);

        if (!n && atAudio) {
          atAudio.pause();
          atAudio.removeAttribute("src");
          atAudio.load();
          atCurrentFolder = null;
          dismissNowPlaying();
        }

        window._sr.setValueByPath(cfg, "Playback.AnimeThemesMp3Playback", n);
        await persist(cfg);
      };
    }

    if (mBtn) {
      mBtn.setAttribute("data-mode", getVal("Playback.AnimeThemesMp3Mode") || "loop");
      mBtn.onclick = async () => {
        const n = getNextMode(mBtn.getAttribute("data-mode"), ["loop", "shuffle", "off"]);
        mBtn.setAttribute("data-mode", n);
        if (atAudio) atAudio.loop = n === "loop";
        window._sr.setValueByPath(cfg, "Playback.AnimeThemesMp3Mode", n);
        await persist(cfg);
      };
    }

    if (pBtn) {
      pBtn.onclick = async () => {
        if (atAudio?.src && (atAudio.paused || atAudio.ended)) {
          if (atAudio.ended) atAudio.currentTime = 0;
          atAudio.play();
          if (atCurrentFolder) showNowPlaying(atCurrentFolder);
        } else {
          const d = await (await fetch(base + "/animethemes/mp3/random")).json();
          d?.path ? playThemeMp3(d.path) : showToast("No Theme.mp3 Files Found", "error", window._sr.TOAST_MS);
        }
      };
    }

    const webmModeEl = el("webm-mode");
    if (webmModeEl) {
      webmModeEl.setAttribute("data-mode", getVal("Playback.AnimeThemesWebmMode") || "loop");
      webmModeEl._persist = (m) => {
        window._sr.setValueByPath(cfg, "Playback.AnimeThemesWebmMode", m);
        persist(cfg);
      };
    }
  };
  // #endregion
})();
