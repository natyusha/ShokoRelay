(() => {
  const {
    base,
    el,
    TOAST_MS,
    fetchJson,
    showToast,
    toastOperation,
    summarizeResult,
    makeLogLink,
    withButtonAction,
    initToggle,
    setIfNotEmpty,
    setValueByPath,
    getErrorCount,
    initDetailsAnimation,
    attachModalCloseHandlers,
  } = window._sr;

  // #region Helpers

  /** Build URLSearchParams for AnimeThemes MP3 requests from the dashboard inputs and toggles. @returns {URLSearchParams} */
  const buildAtParams = () => {
    const ps = new URLSearchParams();
    setIfNotEmpty(ps, "path", el("at-path")?.value);
    setIfNotEmpty(ps, "slug", el("at-slug")?.value);
    setIfNotEmpty(ps, "offset", el("at-offset")?.value);
    setIfNotEmpty(ps, "filter", el("at-filter")?.value);
    if (el("at-force")?.getAttribute("aria-pressed") === "true") ps.set("force", "true");
    if (el("at-batch")?.getAttribute("aria-pressed") === "true") ps.set("batch", "true");
    return ps;
  };

  /** Build URLSearchParams for AnimeThemes VFS mapping requests from the filter input. @returns {URLSearchParams} */
  const buildAtMapParams = () => {
    const ps = new URLSearchParams();
    setIfNotEmpty(ps, "filter", el("at-filter-map")?.value);
    return ps;
  };

  // #endregion

  // #region VFS
  const atImportBtn = el("at-import");
  if (atImportBtn) {
    withButtonAction(atImportBtn, async function () {
      showToast("AnimeThemes: Importing Mapping...", "info", TOAST_MS);
      const res = await fetchJson(base + "/animethemes/vfs/import", { method: "POST" });
      if (res.ok) toastOperation(res, "AnimeThemes Import", { summary: `mapping imported (${res.data?.count ?? 0} entries)` });
      else toastOperation(res, "AnimeThemes Import");
    });
  }

  /**
   * Create a click handler for AnimeThemes map/build buttons with toast feedback.
   * @param {HTMLElement} btn - The button element to bind the action to.
   * @param {string} label - Display label for toast messages.
   * @param {string} endpoint - Server API endpoint to call.
   */
  function atBuildAction(btn, label, endpoint) {
    withButtonAction(btn, async function () {
      const startToast = showToast(`AnimeThemes: ${label}...`, "info", 0);
      const p = buildAtMapParams();
      const res = await fetchJson(base + endpoint + (p.toString() ? "?" + p.toString() : ""));
      if (startToast?.parentElement) startToast.remove();
      if (res.ok) {
        const summary = summarizeResult(res) || "complete";
        toastOperation(res, `AnimeThemes ${label}`, { summary });
      } else {
        toastOperation(res, `AnimeThemes ${label}`);
      }
    });
  }
  const atMappingBtn = el("at-mapping");
  if (atMappingBtn) atBuildAction(atMappingBtn, "Mapping", "/animethemes/vfs/map");
  const atApplyBtn = el("at-apply");
  if (atApplyBtn) atBuildAction(atApplyBtn, "Generation", "/animethemes/vfs/build");
  // #endregion

  // #region Video Player
  {
    const webmModal = el("webm-modal");
    const webmOpenBtn = el("at-webm-open");
    const webmCancelBtn = el("at-webm-cancel");
    const webmVideo = /** @type {HTMLVideoElement} */ (el("webm-video"));
    const webmTree = el("webm-tree");
    const webmFilter = /** @type {HTMLInputElement} */ (el("webm-filter"));
    const webmNextBtn = el("webm-next");
    const webmModeBtn = el("webm-mode");
    const webmTitle = el("webm-title");
    const webmAnime = el("webm-anime");
    const webmTreeDetails = el("webm-tree-details");
    let webmTreeData = null; // cached API response

    /** Update the cancel button visibility based on playback state. */
    function syncCancelButtonState() {
      if (!webmCancelBtn) return;
      const isPlaying = !!webmVideo?.src && !webmVideo.paused;
      webmCancelBtn.hidden = !isPlaying;
    }

    /** Stop all video playback and hide the cancel button. */
    function cancelVideoPlayback() {
      if (!webmVideo) return;
      webmVideo.pause();
      webmVideo.removeAttribute("src");
      webmVideo.load();
      updateNowPlaying("");
      syncCancelButtonState();
    }

    // Animate the tree collapsible section
    if (webmTreeDetails) {
      const treeContent = webmTreeDetails.querySelector(".details-content");
      if (treeContent) initDetailsAnimation(webmTreeDetails, treeContent);
    }

    /** Decode unicode escape sequences (e.g. \\u002C -> ,) in a string. */
    const decodeUnicodeEscapes = (s) => s.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)));

    /**
     * Build an alphabetically sorted tree from the API items and render it into the tree container.
     * Uses a `<ul>/<li>/<details>` structure with CSS connecting lines.
     * @param {Array<{group:string, series:string, file:string, path:string}>} items
     * @param {string} filterText - Exact-match filter string (case-insensitive).
     */
    function renderWebmTree(items, filterText) {
      if (!webmTree) return;
      webmTree.innerHTML = "";

      const ft = (filterText || "").toLowerCase();
      const filtered = ft ? items.filter((i) => i.group.toLowerCase().includes(ft) || i.series.toLowerCase().includes(ft) || i.file.toLowerCase().includes(ft)) : items;

      if (filtered.length === 0) {
        const p = document.createElement("div");
        p.className = "placeholder";
        p.textContent = ft ? "No matches" : "Generate AnimeThemes VFS to populate";
        webmTree.appendChild(p);
        return;
      }

      // Group -> Series -> Files
      const groups = new Map();
      for (const item of filtered) {
        if (!groups.has(item.group)) groups.set(item.group, new Map());
        const seriesMap = groups.get(item.group);
        if (!seriesMap.has(item.series)) seriesMap.set(item.series, []);
        seriesMap.get(item.series).push(item);
      }

      const sortedGroupKeys = [...groups.keys()].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: "base" }));

      /** Create a leaf <li> for a file item and return it. */
      function makeLeaf(f) {
        const li = document.createElement("li");
        const leaf = document.createElement("div");
        leaf.className = "webm-leaf";
        leaf.textContent = decodeUnicodeEscapes(f.file);
        leaf.dataset.path = f.path;
        leaf.addEventListener("click", () => {
          webmTree.querySelectorAll(".webm-leaf.active").forEach((el) => el.classList.remove("active"));
          leaf.classList.add("active");
          playWebmFile(f.path);
        });
        li.appendChild(leaf);
        return li;
      }

      /** Render a series <details>/<ul> node with its file leaves inside a <li>. */
      function makeSeries(seriesName, files, defaultOpen) {
        const li = document.createElement("li");
        const details = document.createElement("details");
        details.open = defaultOpen;
        const summary = document.createElement("summary");
        summary.innerHTML = '<span class="tree-icon expand"></span><span class="tree-icon collapse"></span>';
        summary.append(seriesName);
        details.appendChild(summary);
        const ul = document.createElement("ul");
        files.sort((a, b) => a.file.localeCompare(b.file, undefined, { sensitivity: "base" }));
        for (const f of files) ul.appendChild(makeLeaf(f));
        details.appendChild(ul);
        li.appendChild(details);
        return li;
      }

      const rootUl = document.createElement("ul");
      rootUl.className = "tree";

      for (const groupName of sortedGroupKeys) {
        const seriesMap = groups.get(groupName);
        const sortedSeriesKeys = [...seriesMap.keys()].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: "base" }));

        // Flatten: if a group has only 1 series, render the series directly at root level
        if (sortedSeriesKeys.length === 1) {
          rootUl.appendChild(makeSeries(sortedSeriesKeys[0], seriesMap.get(sortedSeriesKeys[0]), sortedGroupKeys.length <= 5 || !!ft));
          continue;
        }

        const groupLi = document.createElement("li");
        const groupDetails = document.createElement("details");
        groupDetails.open = sortedGroupKeys.length <= 5 || !!ft;
        const groupSummary = document.createElement("summary");
        groupSummary.innerHTML = '<span class="tree-icon expand"></span><span class="tree-icon collapse"></span>';
        groupSummary.append(groupName);
        groupDetails.appendChild(groupSummary);
        const groupUl = document.createElement("ul");

        for (const seriesName of sortedSeriesKeys) {
          groupUl.appendChild(makeSeries(seriesName, seriesMap.get(seriesName), sortedSeriesKeys.length <= 3 || !!ft));
        }

        groupDetails.appendChild(groupUl);
        groupLi.appendChild(groupDetails);
        rootUl.appendChild(groupLi);
      }

      webmTree.appendChild(rootUl);
    }

    /** Collect all file items currently in the tree (flattened). */
    function getAllWebmItems() {
      return webmTreeData || [];
    }

    /** Update the now-playing label with the filename (without extension). */
    const [npTitle, npAnime] = ["AnimeThemes: VFS Video Player", "Select a theme to begin..."];
    function updateNowPlaying(path) {
      if (!webmTitle) return;
      if (!path) {
        webmTitle.textContent = npTitle;
        if (webmAnime) webmAnime.textContent = npAnime;
        return;
      }
      const name = decodeUnicodeEscapes(path.split("/").pop().split("\\").pop() || "");
      const txt = name.replace(/\.[^.]+$/, "");
      webmTitle.textContent = txt || npTitle;
      if (webmAnime) {
        const item = (webmTreeData || []).find((i) => i.path === path);
        webmAnime.textContent = item?.series || npAnime;
      }
    }

    /** Play a webm file by setting the video source. */
    function playWebmFile(path) {
      if (!webmVideo) return;
      webmVideo.src = base + "/animethemes/vfs/webm/stream?path=" + encodeURIComponent(path);
      webmVideo.play().catch(() => {});
      syncCancelButtonState();
      updateNowPlaying(path);
    }

    /** Pick a random item from the webm tree data and play it. */
    function playRandomWebm() {
      const items = getAllWebmItems();
      if (!items.length) return;
      const pick = items[Math.floor(Math.random() * items.length)];
      highlightWebmLeaf(pick.path);
      playWebmFile(pick.path);
    }

    /** Play the next item alphabetically from the webm tree data. */
    function playNextWebm() {
      const items = getAllWebmItems();
      if (!items.length) return;
      const sorted = [...items].sort((a, b) => a.path.localeCompare(b.path, undefined, { sensitivity: "base" }));
      const currentSrc = webmVideo?.src || "";
      let idx = sorted.findIndex((i) => currentSrc.includes(encodeURIComponent(i.path)));
      idx = idx < 0 ? 0 : (idx + 1) % sorted.length;
      const pick = sorted[idx];
      highlightWebmLeaf(pick.path);
      playWebmFile(pick.path);
    }

    /** Highlight a webm leaf in the tree by path. */
    function highlightWebmLeaf(path) {
      if (!webmTree) return;
      webmTree.querySelectorAll(".webm-leaf.active").forEach((el) => el.classList.remove("active"));
      webmTree.querySelectorAll(".webm-leaf").forEach((el) => {
        if (el.dataset.path === path) el.classList.add("active");
      });
    }

    /** Get the current webm mode from the button. */
    const getWebmMode = () => (webmModeBtn ? webmModeBtn.getAttribute("data-mode") : "off") || "off";

    // Wire up video events for mode button behavior and cancel button state
    if (webmVideo) {
      webmVideo.addEventListener("play", syncCancelButtonState);
      webmVideo.addEventListener("pause", syncCancelButtonState);
      webmVideo.addEventListener("ended", () => {
        const mode = getWebmMode();
        if (mode === "loop") {
          webmVideo.currentTime = 0;
          webmVideo.play().catch(() => {});
        } else if (mode === "shuffle") {
          playRandomWebm();
        } else if (mode === "next") {
          playNextWebm();
        } else {
          syncCancelButtonState();
        }
      });
    }

    // Cancel button: stop all playback and hide the button
    if (webmCancelBtn) {
      webmCancelBtn.onclick = cancelVideoPlayback;
    }

    // Mode button: cycle loop -> shuffle -> next -> off
    if (webmModeBtn) {
      const modes = ["loop", "shuffle", "next", "off"];
      webmModeBtn.onclick = () => {
        const idx = modes.indexOf(getWebmMode());
        const next = modes[(idx + 1) % modes.length];
        webmModeBtn.setAttribute("data-mode", next);
        if (webmModeBtn._persist) webmModeBtn._persist(next);
      };
    }

    // Next button: respects current mode (shuffle = random, otherwise alphabetical)
    if (webmNextBtn) {
      webmNextBtn.onclick = () => {
        if (getWebmMode() === "shuffle") playRandomWebm();
        else playNextWebm();
      };
    }

    /** Load webm tree data from backend and render into the modal. */
    async function loadWebmTree() {
      if (!webmTree) return;
      const res = await fetchJson(base + "/animethemes/vfs/webm/tree");
      if (res.ok && res.data?.status === "ok" && Array.isArray(res.data.items)) {
        webmTreeData = res.data.items;
      } else {
        webmTreeData = [];
      }
      renderWebmTree(webmTreeData, webmFilter?.value || "");
    }

    let _closeWebmOverlay = null;

    function setTreeHeight() {
      if (!webmTreeDetails || !webmTree) return;
      // Force reflow to get latest height
      webmTree.style.height = "auto";
      // 62px is the summary header, 48px is the filter bar, 16px is padding in landscape
      const isLandscape = window.innerWidth / window.innerHeight > 4 / 3;
      const offset = isLandscape ? 126 : 62;
      webmTree.style.height = webmTreeDetails.offsetHeight - offset + "px";
    }

    async function openWebmModal() {
      if (!webmModal) return;
      webmModal.classList.add("open");
      webmModal.setAttribute("aria-hidden", "false");
      await loadWebmTree();
      // Defer height calculation to next paint cycle to ensure layout is finalized
      requestAnimationFrame(() => {
        requestAnimationFrame(setTreeHeight);
      });
      let resizeTimeout = null;
      function debouncedSetTreeHeight() {
        if (resizeTimeout) clearTimeout(resizeTimeout);
        resizeTimeout = setTimeout(setTreeHeight, 50);
      }
      window.addEventListener("resize", debouncedSetTreeHeight);
      openWebmModal._resizeHandler = debouncedSetTreeHeight;
      _closeWebmOverlay = attachModalCloseHandlers(webmModal);
    }

    if (webmOpenBtn) {
      webmOpenBtn.onclick = () => {
        openWebmModal();
        syncLandscapeTree();
      };
    }

    if (webmFilter) {
      webmFilter.addEventListener("input", () => {
        if (webmTreeData) renderWebmTree(webmTreeData, webmFilter.value);
      });
    }

    // ensure tree pane is forced open in landscape mode
    function syncLandscapeTree() {
      if (!webmTreeDetails) return;
      if (window.innerWidth / window.innerHeight > 4 / 3) {
        webmTreeDetails.open = true;
      }
    }
    window.addEventListener("resize", syncLandscapeTree);
  }
  // #endregion

  // #region Theme MP3
  initToggle("at-force", false);
  initToggle("at-batch", false);

  /** Shared Audio element for background MP3 playback; reused across plays to avoid stacking. */
  let atAudio = null;
  /** Currently visible "Now Playing" toast element, dismissed when playback stops. */
  let nowPlayingToast = null;
  /** Folder path of the currently playing theme, used to re-show the toast on resume. */
  let atCurrentFolder = null;

  /** Dismiss the current "Now Playing" toast if one is showing. */
  function dismissNowPlaying() {
    if (nowPlayingToast) {
      nowPlayingToast.click();
      nowPlayingToast = null;
    }
  }

  /** Fetch ID3 tags from the stream response headers and show a persistent "Now Playing" toast. */
  async function showNowPlaying(folderPath) {
    try {
      const streamUrl = base + "/animethemes/mp3/stream?path=" + encodeURIComponent(folderPath);
      const res = await fetch(streamUrl, { method: "HEAD" });
      const title = res.headers.get("X-Theme-Title");
      if (!title) return;
      let slug = res.headers.get("X-Theme-Slug") || "";
      slug = slug.replace(/\bOpening\s?/gi, "OP").replace(/\bEnding\s?/gi, "ED");
      const artist = res.headers.get("X-Theme-Artist");
      const album = res.headers.get("X-Theme-Album");
      let html = `<span class="np-line">${title}</span>`;
      if (artist) html += `<span class="np-line">${artist}</span>`;
      const line3Parts = [];
      if (album) line3Parts.push(album);
      if (slug) line3Parts.push(slug);
      if (line3Parts.length) html += `<small class="np-line">${line3Parts.join(" \u2014 ")}</small>`;
      dismissNowPlaying();
      nowPlayingToast = showToast(html, "info", 0);
    } catch {
      /* ignore */
    }
  }

  /** Progress bar elements. */
  const atProgressTrack = el("at-progress-track");
  const atProgressFill = el("at-progress-fill");

  /** Update the progress bar fill width from the current audio position. */
  function updateProgress() {
    if (!atAudio || !atProgressFill || !atAudio.duration) return;
    atProgressFill.style.width = (atAudio.currentTime / atAudio.duration) * 100 + "%";
  }

  /** Show or hide the progress bar and sync paused state. */
  function syncProgressState() {
    if (!atProgressTrack) return;
    if (atAudio && atAudio.src) {
      atProgressTrack.classList.add("active");
      atProgressTrack.classList.toggle("paused", atAudio.paused);
    } else {
      atProgressTrack.classList.remove("active", "paused");
      if (atProgressFill) atProgressFill.style.width = "0%";
    }
  }

  // Seek on left-click within the progress track
  if (atProgressTrack) {
    atProgressTrack.addEventListener("click", (e) => {
      if (!atAudio || !atAudio.duration) return;
      const rect = atProgressTrack.getBoundingClientRect();
      atAudio.currentTime = ((e.clientX - rect.left) / rect.width) * atAudio.duration;
    });
    // Middle-click toggles pause
    atProgressTrack.addEventListener("auxclick", (e) => {
      if (e.button !== 1 || !atAudio || !atAudio.src) return;
      e.preventDefault();
      if (atAudio.paused) atAudio.play().catch(() => {});
      else atAudio.pause();
    });
    // Prevent default middle-click scroll icon
    atProgressTrack.addEventListener("mousedown", (e) => {
      if (e.button === 1) e.preventDefault();
    });
  }

  /** Check whether the playback toggle is currently enabled. @returns {boolean} */
  const isPlaybackEnabled = () => el("at-playback")?.getAttribute("aria-pressed") === "true";

  /**
   * Stream and play a Theme.mp3 in the background using the streaming endpoint.
   * Stops any currently playing audio first. Does nothing if the playback toggle is off.
   * @param {string} folderPath - The folder containing the Theme.mp3 to stream.
   */
  function playThemeMp3(folderPath) {
    if (!isPlaybackEnabled() || !folderPath) return;
    try {
      dismissNowPlaying();
      if (!atAudio) {
        atAudio = new Audio();
        atAudio.volume = 0.5;
        const loopToggle = el("at-mode");
        if (loopToggle) atAudio.loop = loopToggle.getAttribute("data-mode") === "loop";
        atAudio.addEventListener("timeupdate", updateProgress);
        atAudio.addEventListener("play", () => {
          syncProgressState();
          syncPlayBtnState();
        });
        atAudio.addEventListener("pause", () => {
          syncProgressState();
          syncPlayBtnState();
        });
        atAudio.addEventListener("ended", async () => {
          syncProgressState();
          syncPlayBtnState();
          // In shuffle mode, auto-play a random theme on ended
          const mode = loopToggle ? loopToggle.getAttribute("data-mode") : "off";
          if (mode === "shuffle") {
            try {
              const resp = await fetch(base + "/animethemes/mp3/random");
              if (resp.ok) {
                const data = await resp.json();
                if (data?.path) {
                  playThemeMp3(data.path);
                  return;
                }
              }
            } catch {
              /* ignore */
            }
          }
          dismissNowPlaying();
        });
      } else {
        atAudio.pause();
      }
      const streamUrl = base + "/animethemes/mp3/stream?path=" + encodeURIComponent(folderPath);
      atAudio.src = streamUrl;
      atAudio.play().catch(() => {
        /* autoplay may be blocked by browser */
      });
      atCurrentFolder = folderPath;
      syncProgressState();
      syncPlayBtnState();
      showNowPlaying(folderPath);
    } catch {
      /* ignore playback errors */
    }
  }

  /** Update the play button icon: show skip-next when audio is actively playing, play icon otherwise. */
  function syncPlayBtnState() {
    const btn = el("at-play");
    if (!btn) return;
    const isPlaying = atAudio && !atAudio.paused && !atAudio.ended && atAudio.src;
    btn.setAttribute("data-state", isPlaying ? "playing" : "idle");
  }

  const atSingleBtn = el("at-single");
  if (atSingleBtn) {
    withButtonAction(atSingleBtn, async function () {
      showToast("AnimeThemes MP3: Generating...", "info", TOAST_MS);
      const params = buildAtParams();
      const res = await fetchJson(base + "/animethemes/mp3?" + params.toString());
      const logLink = makeLogLink(res.data?.logUrl);
      if (res.ok) {
        const summary = summarizeResult(res) || res.data?.Status || res.data?.status || "done";
        const status = res.data?.Status || res.data?.status;
        const errs = getErrorCount(res);
        const toastType = errs > 0 ? "error" : status === "skipped" ? "warning" : "success";
        const isBatch = params.get("batch") === "true";
        const msgPart = status === "skipped" && res.data?.message ? ` (${res.data.message})` : "";
        showToast(`AnimeThemes MP3: ${summary}${msgPart} ${logLink}`, toastType, toastType === "error" ? 0 : TOAST_MS);
        // play the generated MP3 in the background (single, non-batch only)
        if (!isBatch && (status === "ok" || status === "skipped")) {
          const folder = res.data?.folder || res.data?.Folder || params.get("path");
          if (folder) playThemeMp3(folder);
        }
      } else {
        // when force is off and mp3 already exists, the server returns 400 with status "skipped"
        const errStatus = res.data?.status || res.data?.Status;
        const isBatch = params.get("batch") === "true";
        if (!isBatch && errStatus === "skipped") {
          const msgPart = res.data?.message ? ` (${res.data.message})` : "";
          showToast(`AnimeThemes MP3: Skipped${msgPart} ${logLink}`, "warning", TOAST_MS);
          const folder = res.data?.folder || res.data?.Folder || params.get("path");
          if (folder) playThemeMp3(folder);
        } else {
          showToast(`AnimeThemes MP3 Failed: ${res.data?.message || JSON.stringify(res.data)} ${logLink}`, "error", 0);
        }
      }
    });
  }

  /**
   * Initialize AnimeThemes config bindings. Called by loadConfig after config is loaded.
   * @param {Object} config - The current config object.
   * @param {Function} persistConfig - Async function to save config changes.
   */
  window._sr.initAtConfig = (config, persistConfig) => {
    // Bind AnimeThemes MP3 playback toggle to the server config
    const playBtn = el("at-play");
    const modeBtn = el("at-mode");
    const playbackBtn = el("at-playback");
    const syncPlaybackControls = (enabled) => {
      if (playBtn) playBtn.hidden = !enabled;
      if (modeBtn) modeBtn.hidden = !enabled;
    };
    if (playbackBtn) {
      playbackBtn.setAttribute("aria-pressed", String(!!config.AnimeThemesMp3Playback));
      syncPlaybackControls(!!config.AnimeThemesMp3Playback);
      playbackBtn.onclick = async () => {
        const current = playbackBtn.getAttribute("aria-pressed") === "true";
        const next = !current;
        playbackBtn.setAttribute("aria-pressed", String(next));
        syncPlaybackControls(next);
        // stop any playing audio when disabling
        if (!next && atAudio) {
          atAudio.pause();
          atAudio.removeAttribute("src");
          atAudio.load();
          syncProgressState();
          syncPlayBtnState();
          dismissNowPlaying();
        }
        try {
          setValueByPath(config, "AnimeThemesMp3Playback", next);
          await persistConfig(config);
        } catch (e) {
          showToast(`MP3 Playback Save Failed: ${e?.message || e}`, "error", 0);
        }
      };
    }

    // Bind loop toggle -> 3-state: loop -> shuffle -> off -> loop
    const loopModes = ["loop", "shuffle", "off"];
    /** Apply loop mode to the button and audio element. */
    function applyLoopMode(mode) {
      if (modeBtn) modeBtn.setAttribute("data-mode", mode);
      if (atAudio) atAudio.loop = mode === "loop";
    }
    /** Get the current loop mode from the button. */
    const getLoopMode = () => (modeBtn ? modeBtn.getAttribute("data-mode") : "loop") || "loop";
    if (modeBtn) {
      const saved = typeof config.AnimeThemesMp3Mode === "string" ? config.AnimeThemesMp3Mode : config.AnimeThemesMp3Mode === false ? "off" : "loop";
      applyLoopMode(loopModes.includes(saved) ? saved : "loop");
      modeBtn.onclick = async () => {
        const idx = loopModes.indexOf(getLoopMode());
        const next = loopModes[(idx + 1) % loopModes.length];
        applyLoopMode(next);
        try {
          setValueByPath(config, "AnimeThemesMp3Mode", next);
          await persistConfig(config);
        } catch (e) {
          showToast(`MP3 Loop Save Failed: ${e?.message || e}`, "error", 0);
        }
      };
    }

    // Bind AnimeThemes WebM mode button to the server config
    const webmModeEl = el("webm-mode");
    if (webmModeEl) {
      const savedWebm = typeof config.AnimeThemesWebmMode === "string" ? config.AnimeThemesWebmMode : "loop";
      const validModes = ["loop", "shuffle", "next", "off"];
      webmModeEl.setAttribute("data-mode", validModes.includes(savedWebm) ? savedWebm : "loop");
      webmModeEl._persist = async (mode) => {
        try {
          setValueByPath(config, "AnimeThemesWebmMode", mode);
          await persistConfig(config);
        } catch (e) {
          showToast(`WebM Mode Save Failed: ${e?.message || e}`, "error", 0);
        }
      };
    }

    // Bind play button — dual behavior:
    // When audio is playing (skip-next icon visible): pick a new random theme
    // When audio is paused/ended (play icon visible): resume or restart the current theme
    if (playBtn) {
      playBtn.onclick = async () => {
        const isPlaying = atAudio && !atAudio.paused && !atAudio.ended && atAudio.src;
        if (!isPlaying && atAudio && atAudio.src) {
          // Resume paused audio, or restart if ended
          if (atAudio.ended) atAudio.currentTime = 0;
          atAudio.play().catch(() => {});
          if (atCurrentFolder) showNowPlaying(atCurrentFolder);
        } else {
          // Skip to next or pick first random theme
          try {
            const resp = await fetch(base + "/animethemes/mp3/random");
            if (!resp.ok) {
              showToast("Shuffle: No Theme.mp3 Files Found", "error", TOAST_MS);
              return;
            }
            const data = await resp.json();
            if (data?.path) playThemeMp3(data.path);
          } catch (e) {
            showToast(`Shuffle Failed: ${e?.message || e}`, "error", TOAST_MS);
          }
        }
      };
    }
  };
  // #endregion
})();
