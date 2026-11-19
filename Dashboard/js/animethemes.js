/**
 * @file animethemes.js
 * @description Dedicated logic for AnimeThemes VFS and Theme.mp3 generation on the Shoko Relay dashboard.
 */
(() => {
  const { base, el, esc, TOAST_MS, fetchJson, showToast, toastOperation, initToggle, updatePlaybackTooltip, getData, summarizeResult } = window._sr;

  // #region Param Providers
  /**
   * Collects MP3 generation parameters for the Action Dispatcher.
   * @returns {URLSearchParams} The compiled parameters.
   */
  window._sr.getAtParams = () => {
    const params = new URLSearchParams();
    ["path", "slug", "offset"].forEach((key) => window._sr.setIfNotEmpty(params, key, el(`at-${key}`)?.value));
    ["seasonal", "force", "batch"].forEach((toggle) => el(`at-mp3-${toggle}`)?.getAttribute("aria-pressed") === "true" && params.set(toggle, "true"));
    return params;
  };

  /**
   * Collects VFS mapping parameters for the Action Dispatcher.
   * @returns {URLSearchParams} The compiled parameters.
   */
  window._sr.getAtMapParams = () => {
    const params = new URLSearchParams();
    window._sr.setIfNotEmpty(params, "filter", el("at-filter-map")?.value);
    return params;
  };

  /**
   * Dynamically calculates the current anime season parameters for WebM downloads.
   * @returns {URLSearchParams} The compiled parameters.
   */
  window._sr.getWebmSeasonParams = () => new URLSearchParams({ season: ["Winter", "Spring", "Summer", "Fall"][Math.floor(new Date().getMonth() / 3)] });
  // #endregion

  // #region Actions Registry
  Object.assign(window._sr.actions, {
    /**
     * Action for generating series Theme.mp3 files and triggering playback.
     * @returns {Promise<void>}
     */
    atMp3Build: async () => {
      const ps = window._sr.getAtParams();
      const isBatch = ps.get("batch") === "true";
      const label = "AnimeThemes MP3";

      showToast(`${label}: Generating...`, "info", TOAST_MS);
      const res = await fetchJson(`${base}/animethemes/mp3?${ps}`);
      const resData = getData(res);

      const status = resData?.Status || resData?.status;
      const message = resData?.Message || resData?.message;
      const folder = resData?.Folder || resData?.folder || ps.get("path");

      let summary = summarizeResult(res).text;
      let isNoTheme = false;

      // Handle specific skip scenarios for user-friendly feedback
      if (status === "skipped") {
        if (message === "Theme.mp3 already exists.") {
          summary = `Skipped: "${folder}" already contains a Theme.mp3`;
        } else if (message === "Entry not found." || (message && message.includes("No entry"))) {
          summary = "Skipped: No theme found for this series";
          isNoTheme = true;
        } else {
          summary = message || "Skipped";
        }
      } else if (status === "error") {
        summary = message || "Unknown error";
      }

      if (res.ok || status === "skipped") {
        toastOperation(res, label, {
          summary: summary || status || "Complete",
          hideOnSucceed: isBatch ? 0 : TOAST_MS,
          type: isNoTheme ? "warning" : undefined,
        });

        // Trigger playback side effect if a theme exists locally (or play first downloaded theme from a batch)
        if (!isBatch && (status === "ok" || (status === "skipped" && message === "Theme.mp3 already exists."))) {
          const playPath = resData?.Folder || resData?.folder || ps.get("path");
          if (playPath) playThemeMp3(playPath);
        } else if (isBatch) {
          const items = resData?.Items || resData?.items || [];
          const playItem =
            items.find((item) => (item.Status || item.status) === "ok") ||
            (items.length === 1 && (items[0].Status || items[0].status) === "skipped" && (items[0].Message || items[0].message) === "Theme.mp3 already exists." ? items[0] : null);
          const playPath = playItem?.Folder || playItem?.folder;
          if (playPath) playThemeMp3(playPath);
        }
      } else {
        toastOperation(res, label, { summary, hideOnSucceed: 0 });
      }
    },
  });
  // #endregion

  // #region Helpers
  /**
   * Generic helper to cycle through an array of modes.
   * @param {string} current - The current mode value.
   * @param {string[]} modes - Array of available modes.
   * @returns {string} The next mode in the sequence.
   */
  const getNextMode = (current, modes) => modes[(modes.indexOf(current) + 1) % modes.length];
  // #endregion

  // #region MP3 - State and UI
  initToggle("at-mp3-seasonal", false);
  initToggle("at-mp3-force", false);
  initToggle("at-mp3-batch", true);

  /** @type {HTMLAudioElement|null} Reference to the active HTML5 Audio element. */
  let atAudio = null;
  /** @type {HTMLElement|null} Reference to the active 'Now Playing' toast element. */
  let nowPlayingToast = null;
  /** @type {string|null} The folder path of the currently playing theme. */
  let atCurrentFolder = null;

  const atTrack = el("at-progress-track");
  const atFill = el("at-progress-fill");

  /**
   * Initializes real-time input sanitization event listeners for the Slug, Offset, and Filter fields.
   * @returns {void}
   */
  function initAtSanitizers() {
    const slugInput = el("at-slug");
    if (slugInput) {
      slugInput.oninput = () => {
        slugInput.value = slugInput.value.replace(/[^a-zA-Z0-9-]/g, "").toUpperCase();
      };
    }

    const offsetInput = el("at-offset");
    if (offsetInput) {
      offsetInput.oninput = () => {
        offsetInput.value = offsetInput.value.replace(/[^0-9]/g, "");
      };
    }

    const mapFilter = el("at-filter-map");
    if (mapFilter) {
      mapFilter.oninput = () => {
        mapFilter.value = mapFilter.value.replace(/[^aA0-9,\s]/g, "").toLowerCase();
      };
    }
  }

  /**
   * Dismiss the currently active 'Now Playing' toast notification if it exists.
   * @returns {void}
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
      const getHeader = (headerKey) => {
        const val = res.headers.get(headerKey);
        if (!val) return "";
        try {
          return decodeURIComponent(val);
        } catch {
          return val;
        }
      };
      const title = getHeader("X-Theme-Title");
      if (!title) return;
      const slug = getHeader("X-Theme-Slug")
        .replace(/\bOpening\s?/gi, "OP")
        .replace(/\bEnding\s?/gi, "ED");
      const artist = getHeader("X-Theme-Artist");
      const album = getHeader("X-Theme-Album");
      let html = `<span class="np-line title">${esc(title)}</span>${artist ? `<span class="np-line artist">${esc(artist)}</span>` : ""}`;
      const meta = [album, slug].filter(Boolean).map(esc).join(" \u2014 ");
      if (meta) html += `<small class="np-line">${meta}</small>`;
      dismissNowPlaying();
      nowPlayingToast = showToast(html, "info", 0);
    } catch {}
  }

  /**
   * Synchronizes the MP3 playback UI states with the audio object.
   * @returns {void}
   */
  function syncPlaybackUI() {
    const isEnabled = el("at-playback")?.getAttribute("aria-pressed") === "true";
    const isActive = isEnabled && atAudio?.src && !atAudio.ended;
    atTrack?.classList.toggle("active", isActive);
    atTrack?.classList.toggle("paused", atAudio?.paused);
    if (!isActive && atFill) atFill.style.width = "0%";
    const playBtn = el("at-play");
    if (playBtn) {
      const isPlaying = atAudio && !atAudio.paused && !atAudio.ended;
      playBtn.setAttribute("data-state", isPlaying ? "playing" : "idle");
      updatePlaybackTooltip(playBtn);
    }
  }

  if (atTrack) {
    atTrack.onclick = (ev) => {
      if (atAudio?.duration && isFinite(atAudio.duration)) atAudio.currentTime = ((ev.clientX - atTrack.getBoundingClientRect().left) / atTrack.offsetWidth) * atAudio.duration;
    };
    atTrack.onmousedown = (ev) => {
      if (ev.button === 1 && atAudio?.src) {
        ev.preventDefault();
        atAudio.paused ? atAudio.play() : atAudio.pause();
      }
    };
  }
  // #endregion

  // #region MP3 - Playback
  /**
   * Stream and play a Theme.mp3 in the background.
   * @param {string} folderPath - The folder containing the Theme.mp3 to stream.
   * @returns {Promise<void>}
   */
  async function playThemeMp3(folderPath) {
    if (el("at-playback")?.getAttribute("aria-pressed") !== "true" || !folderPath) return;
    if (!atAudio) {
      atAudio = new Audio();
      atAudio.volume = 0.5;
      atAudio.ontimeupdate = () => atFill && (atFill.style.width = (atAudio.currentTime / atAudio.duration) * 100 + "%");
      ["play", "pause"].forEach((ev) => atAudio.addEventListener(ev, syncPlaybackUI));
      atAudio.onended = async () => {
        syncPlaybackUI();
        if (el("at-mode")?.getAttribute("data-mode") === "shuffle") {
          const res = await fetchJson(base + "/animethemes/mp3/random");
          const randomData = getData(res);
          if (randomData?.path) playThemeMp3(randomData.path);
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

  // #region MP3 - Configuration
  /**
   * Initialize AnimeThemes config bindings.
   * @param {Object} cfg - The current config object.
   * @param {Function} persist - Async function to save config changes.
   * @returns {void}
   */
  window._sr.initAtConfig = (cfg, persist) => {
    const playback = el("at-playback");
    const modeButton = el("at-mode");
    const playButton = el("at-play");

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
        const nextPressed = playback.getAttribute("aria-pressed") !== "true";
        playback.setAttribute("aria-pressed", nextPressed);
        sync(nextPressed);

        if (!nextPressed && atAudio) {
          atAudio.pause();
          atAudio.removeAttribute("src");
          atAudio.load();
          atCurrentFolder = null;
          dismissNowPlaying();
        }

        window._sr.setValueByPath(cfg, "Playback.AnimeThemesMp3Playback", nextPressed);
        await persist(cfg);
      };
    }

    if (modeButton) {
      const currentMode = getVal("Playback.AnimeThemesMp3Mode") || "loop";
      modeButton.setAttribute("data-mode", currentMode);
      updatePlaybackTooltip(modeButton);

      modeButton.onclick = async () => {
        const nextMode = getNextMode(modeButton.getAttribute("data-mode"), ["loop", "shuffle", "off"]);
        modeButton.setAttribute("data-mode", nextMode);
        updatePlaybackTooltip(modeButton);
        if (atAudio) atAudio.loop = nextMode === "loop";
        window._sr.setValueByPath(cfg, "Playback.AnimeThemesMp3Mode", nextMode);
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
          const res = await fetchJson(base + "/animethemes/mp3/random");
          const randomData = getData(res);
          randomData?.path ? playThemeMp3(randomData.path) : showToast("No Theme.mp3 Files Found", "error", window._sr.TOAST_MS);
        }
      };
    }
  };
  // #endregion

  // #region Initialization
  initAtSanitizers(); // Initialize sanitizers immediately on script load
  // #endregion
})();
