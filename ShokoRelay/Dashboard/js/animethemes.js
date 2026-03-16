/**
 * @file animethemes.js
 * @description Dedicated logic for AnimeThemes VFS and Theme.mp3 generation on the Shoko Relay dashboard.
 */
(() => {
  const { base, el, TOAST_MS, fetchJson, showToast, toastOperation, summarizeResult, withButtonAction, initToggle, setIfNotEmpty, updatePlaybackTooltip } = window._sr;

  // #region Helpers
  /**
   * Generic helper to cycle through an array of modes.
   * @param {string} current - The current mode value.
   * @param {string[]} modes - Array of available modes.
   * @returns {string} The next mode in the sequence.
   */
  const getNextMode = (current, modes) => modes[(modes.indexOf(current) + 1) % modes.length];

  /**
   * Build URLSearchParams for AnimeThemes MP3 requests from dashboard inputs.
   * @returns {URLSearchParams} The compiled search parameters.
   */
  const buildAtParams = () => {
    const ps = new URLSearchParams();
    ["path", "slug", "offset", "filter"].forEach((k) => setIfNotEmpty(ps, k, el(`at-${k}`)?.value));
    if (el("at-mp3-force")?.getAttribute("aria-pressed") === "true") ps.set("force", "true");
    if (el("at-mp3-batch")?.getAttribute("aria-pressed") === "true") ps.set("batch", "true");
    return ps;
  };

  /**
   * Build URLSearchParams for AnimeThemes VFS mapping requests.
   * @returns {URLSearchParams} The compiled search parameters.
   */
  const buildAtMapParams = () => {
    const ps = new URLSearchParams();
    setIfNotEmpty(ps, "filter", el("at-filter-map")?.value);
    return ps;
  };
  // #endregion

  // #region VFS Mapping
  withButtonAction(el("at-map-import"), async () => {
    showToast("AnimeThemes: Importing Mapping...", "info", TOAST_MS);
    const res = await fetchJson(base + "/animethemes/vfs/import", { method: "POST" });
    toastOperation(res, "AnimeThemes Import", { summary: res.ok ? `mapping imported (${res.data?.count ?? 0} entries)` : undefined });
  });

  /**
   * Create a click handler for AnimeThemes map/build buttons with toast feedback.
   * @param {HTMLElement} btn - The button element.
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
      toastOperation(res, `AnimeThemes ${label}`, { summary: summarizeResult(res).text || "complete", hideOnSucceed });
    });
  }
  atBuildAction(el("at-map-build"), "Mapping", "/animethemes/vfs/map");
  atBuildAction(el("at-vfs-build"), "Generation", "/animethemes/vfs/build");
  // #endregion

  // #region MP3 - State and UI
  initToggle("at-mp3-force", false);
  initToggle("at-mp3-batch", false);

  let atAudio = null,
    nowPlayingToast = null,
    atCurrentFolder = null;

  const atTrack = el("at-progress-track"),
    atFill = el("at-progress-fill");

  /** Dismiss the currently active 'Now Playing' toast notification if it exists. */
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

  /** Synchronizes the MP3 playback UI states with the audio object. */
  function syncPlaybackUI() {
    const isE = el("at-playback")?.getAttribute("aria-pressed") === "true",
      isA = isE && atAudio?.src && !atAudio.ended;
    atTrack?.classList.toggle("active", isA);
    atTrack?.classList.toggle("paused", atAudio?.paused);
    if (!isA && atFill) atFill.style.width = "0%";
    const pBtn = el("at-play");
    if (pBtn) {
      const isPlaying = atAudio && !atAudio.paused && !atAudio.ended;
      pBtn.setAttribute("data-state", isPlaying ? "playing" : "idle");
      updatePlaybackTooltip(pBtn);
    }
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
  // #endregion

  // #region MP3 - Playback
  /**
   * Stream and play a Theme.mp3 in the background.
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
  // #endregion

  // #region MP3 - Generation
  withButtonAction(el("at-mp3-build"), async () => {
    const startToast = showToast("AnimeThemes MP3: Generating...", "info", 0);
    const ps = buildAtParams();
    const isBatch = ps.get("batch") === "true";
    const res = await fetchJson(`${base}/animethemes/mp3?${ps}`);
    startToast?.remove();

    const d = res.data || {};
    const status = d.status || d.Status;
    const message = d.message || d.Message;
    const folder = d.folder || d.Folder || ps.get("path");
    const hideOnSucceed = isBatch ? 0 : TOAST_MS;

    // Custom summary mapping for friendly UI messages
    let summary = summarizeResult(res).text;

    if (status === "skipped") {
      if (message === "Theme.mp3 already exists.") {
        summary = `Skipped: "${folder}" already contains a Theme.mp3`;
      } else if (message === "Entry not found." || (message && message.includes("No entry for slug"))) {
        summary = `Skipped: No theme found for this series`;
      } else {
        summary = message || "Skipped";
      }
    } else if (status === "error") {
      if (message === "No video files found.") {
        summary = `A Shoko matched video was not found in "${folder}"`;
      } else if (message === "Folder not found.") {
        summary = `Folder not found: "${folder}"`;
      } else {
        summary = message || "Unknown error";
      }
    }

    if (res.ok || status === "skipped") {
      toastOperation(res, "AnimeThemes MP3", {
        summary: summary || status || "Complete",
        hideOnSucceed,
      });

      if (!isBatch && (status === "ok" || status === "skipped")) {
        const playPath = d.folder || d.Folder || ps.get("path");
        if (playPath) playThemeMp3(playPath);
      }
    } else {
      toastOperation(res, "AnimeThemes MP3", { summary, hideOnSucceed: 0 });
    }
  });
  // #endregion

  // #region MP3 - Configuration
  /**
   * Initialize AnimeThemes config bindings.
   * @param {Object} cfg - The current config object.
   * @param {Function} persist - Async function to save config changes.
   */
  window._sr.initAtConfig = (cfg, persist) => {
    const playback = el("at-playback"),
      modeButton = el("at-mode"),
      playButton = el("at-play");

    const getVal = (path) => window._sr.getValueByPath(cfg, path);

    if (playback) {
      const sync = (v) => {
        if (playButton) playButton.hidden = !v;
        if (modeButton) modeButton.hidden = !v;
        syncPlaybackUI();
      };

      const isEnabled = !!getVal("Playback.AnimeThemesMp3Playback");
      playback.setAttribute("aria-pressed", isEnabled);
      sync(isEnabled);

      playback.onclick = async () => {
        const n = playback.getAttribute("aria-pressed") !== "true";
        playback.setAttribute("aria-pressed", n);
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

    if (modeButton) {
      const currentMode = getVal("Playback.AnimeThemesMp3Mode") || "loop";
      modeButton.setAttribute("data-mode", currentMode);
      updatePlaybackTooltip(modeButton);

      modeButton.onclick = async () => {
        const n = getNextMode(modeButton.getAttribute("data-mode"), ["loop", "shuffle", "off"]);
        modeButton.setAttribute("data-mode", n);
        updatePlaybackTooltip(modeButton);
        if (atAudio) atAudio.loop = n === "loop";
        window._sr.setValueByPath(cfg, "Playback.AnimeThemesMp3Mode", n);
        await persist(cfg);
      };
    }

    if (playButton) {
      playButton.onclick = async () => {
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
  };
  // #endregion
})();
