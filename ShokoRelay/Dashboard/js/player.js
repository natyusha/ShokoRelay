/**
 * @file player.js
 * @description Logic for the Shoko Relay stand-alone AnimeThemes VFS video player.
 */
(() => {
  const { base, configUrl, el, fetchJson, unwrapConfig, setValueByPath, openModal, updatePlaybackTooltip, saveSettings, getData, initSearchInteractions } = window._sr;

  // DOM Elements - UI Indicators
  const playerTime = el("time-display");
  const playerVolText = el("volume-text");
  const playerFullscreenBtn = el("fullscreen");
  const playerCloseBtn = el("close-session");
  const playerMuteBtn = el("volume-icon");
  const playerContainer = el("video").parentElement;
  const playerVideo = el("video");
  const playerVideoBtn = el("video-toggle");

  // DOM Elements - Navigation & Search
  const playerTree = el("tree");
  const uiFilter = el("filter");
  const uiFilterClear = el("filter-clear");
  const playerNextBtn = el("next");
  const playerModeBtn = el("mode");

  // DOM Elements - Metadata Header
  const playerTitle = el("title");
  const playerAnime = el("anime");
  const playerAnidb = el("anidb");
  const playerNowPlayingFav = el("now-playing-fav");
  const playerLocateBtn = el("locate-current");

  // DOM Elements - Progress Bar
  const playerFill = el("player-progress-fill");
  const playerTrack = el("player-progress-track");

  // DOM Elements - Help
  const helpModal = el("player-help-modal");
  const helpOpenBtn = el("help-open");

  /** @type {Function|null} Track the active modal close callback to support toggle hotkeys */
  let activeModalClose = null;
  /** @type {WebmEntry[]} */
  let webmTreeData = [];
  /** @type {string} */
  let currentWebmPath = "";
  /** @type {Set<number>} */
  let favourites = new Set();
  /** @type {number|null} */
  let progressRaf;

  const PATH_KEY = "player-current-path";
  const MISSING_LABEL = "Missing from Collection";

  /** @type {Set<string>} Track played paths during shuffle to ensure a full cycle before repeats. */
  const shuffleHistory = new Set();
  /** @type {string[]} The specific sequence of paths played during this shuffle session. */
  let navigationStack = [];
  /** @type {number} Current position in the navigationStack. */
  let stackIndex = -1;
  /** @type {string} Used to detect filter changes for history reset. */
  let lastFilterValue = "";
  /** @type {number|null} Reference to the timeout that triggers the idle state. */
  let idleTimer;

  // #region Utilities
  /**
   * Resets the idle timer and reveals the UI, suspending it if hovering over controls.
   * @param {MouseEvent} e - The mouse movement event.
   * @returns {void}
   */
  const resetIdle = (e) => {
    playerContainer.classList.remove("idle");
    clearTimeout(idleTimer);
    if (!e.target.closest(".ui-btn, #player-progress-track")) idleTimer = setTimeout(() => playerContainer.classList.add("idle"), 2000);
  };

  /**
   * Toggles the visibility of the video element to save system resources in audio-only mode.
   * @returns {void}
   */
  function toggleVideo() {
    if (!playerVideo) return;
    const currentState = playerVideoBtn.getAttribute("data-state") || "on";
    const newState = currentState === "on" ? "off" : "on";
    playerVideoBtn.setAttribute("data-state", newState);
    playerVideoBtn.title = newState === "on" ? "Turn Video Off" : "Turn Video On";
    playerContainer.classList.toggle("video-off", newState === "off");
    localStorage.setItem("player-video-state", newState);
  }

  /**
   * Converts a numeric time in seconds to a human-readable M:SS format.
   * @param {number} seconds - The time in seconds.
   * @returns {string} The formatted duration string.
   */
  const formatTime = (seconds) => {
    if (isNaN(seconds)) return "0:00";
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s < 10 ? "0" : ""}${s}`;
  };

  /**
   * Updates the visual progress bar fill width.
   * @returns {void}
   */
  const syncProgressUI = () => {
    if (playerFill && playerVideo.duration) {
      const percent = (playerVideo.currentTime / playerVideo.duration) * 100;
      playerFill.style.width = percent + "%";
    }
  };

  /**
   * Synchronizes the volume percentage text and updates icon switching level.
   * @returns {void}
   */
  const syncVolumeUI = () => {
    if (!playerVideo) return;
    const vol = playerVideo.volume;
    const isMuted = playerVideo.muted || vol === 0;

    if (playerVolText) playerVolText.textContent = isMuted ? "Muted" : `${Math.round(vol * 100)}%`;
    if (playerMuteBtn) playerMuteBtn.title = isMuted ? "Unmute" : "Mute";

    let level = "mute";
    if (!isMuted) {
      if (vol > 0.66) level = "high";
      else if (vol > 0.33) level = "med";
      else level = "low";
    }
    if (playerContainer) playerContainer.dataset.volumeLevel = level;

    localStorage.setItem("player-volume", vol);
    localStorage.setItem("player-muted", playerVideo.muted);
  };

  /**
   * Decodes unicode escape sequences back into their actual Unicode characters.
   * @param {string} s - The string containing escape sequences.
   * @returns {string} A decoded string.
   */
  const decodeUnicode = (s) => (s || "").replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)));

  /**
   * Cycles through an array of modes in a specific direction.
   * @param {string} current - The currently active mode.
   * @param {string[]} modes - The list of available modes.
   * @param {number} [dir=1] - Direction to move (1 for next, -1 for previous).
   * @returns {string} The newly selected mode.
   */
  const cycleMode = (current, modes, dir = 1) => {
    const idx = modes.indexOf(current);
    return modes[(idx + dir + modes.length) % modes.length];
  };

  /**
   * Toggles the help modal visibility, safely managing event listeners via the close callback.
   * @returns {void}
   */
  const toggleHelpModal = () => {
    if (helpModal.classList.contains("open")) {
      if (activeModalClose) {
        activeModalClose();
        activeModalClose = null;
      } else {
        helpModal.setAttribute("aria-hidden", "true");
        helpModal.classList.remove("open");
        document.body.style.overflow = "";
      }
    } else {
      activeModalClose = openModal(helpModal);
    }
  };

  /**
   * Evaluates the full list of themes against active search terms, heart status, and metadata tags.
   * @returns {WebmEntry[]} The filtered list of theme entries.
   */
  const getFilteredItems = () => {
    const rawFt = (uiFilter?.value || "").toLowerCase().trim();
    if (!rawFt) return webmTreeData;

    const words = rawFt.split(/\s+/).filter((w) => w.length > 0);
    const isFavQuery = words.includes("favs");
    const tagExclusions = [];
    const tagInclusions = [];
    const searchTerms = [];

    words.forEach((w) => {
      if (w === "favs") return;
      w.startsWith("-") ? tagExclusions.push(w.substring(1)) : w.startsWith("+") ? tagInclusions.push(w.substring(1)) : searchTerms.push(w);
    });

    const matchesTag = (tag, item) => ({ spoil: item.spoiler, nsfw: item.nsfw, lyrics: item.lyrics, subs: item.subs, uncen: item.uncen, nc: item.nc, trans: item.trans, over: item.over })[tag] || false;

    return webmTreeData.filter((item) => {
      if (isFavQuery && (!item.videoId || !favourites.has(item.videoId))) return false;
      if (tagExclusions.some((tag) => matchesTag(tag, item)) || tagInclusions.some((tag) => !matchesTag(tag, item))) return false;
      return searchTerms.every((word) => item._searchIndex.includes(word));
    });
  };

  /** Resets the navigation stack and history tracking. */
  const resetNavigationHistory = () => {
    shuffleHistory.clear();
    navigationStack = [];
    stackIndex = -1;
  };
  // #endregion

  // #region Tree & Navigation
  /**
   * Toggles the favourite status of a video on the server and synchronizes UI heart icons.
   * @param {number} videoId - The AnimeThemes video ID to toggle.
   * @param {HTMLElement} [heartEl] - The optional heart icon element inside the tree to toggle.
   * @returns {Promise<void>}
   */
  async function toggleFavourite(videoId, heartEl) {
    if (!videoId || videoId <= 0) return console.warn("VideoId missing.");
    const res = await fetchJson(base + "/animethemes/webm/favourites", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(videoId),
    });

    const isFav = getData(res)?.isFavourite;
    if (res.ok && isFav !== undefined) {
      isFav ? favourites.add(videoId) : favourites.delete(videoId);

      if (heartEl) heartEl.classList.toggle("favourited", isFav);
      const treeHeart = playerTree.querySelector(`.leaf[data-path="${CSS.escape(currentWebmPath)}"] .fav-icon`);
      if (treeHeart) treeHeart.classList.toggle("favourited", isFav);

      if (playerNowPlayingFav && webmTreeData.find((i) => i.path === currentWebmPath)?.videoId === videoId) playerNowPlayingFav.classList.toggle("favourited", isFav);
      if (uiFilter?.value.toLowerCase().includes("favs")) renderTree(getFilteredItems());
    }
  }

  /**
   * Constructs the hierarchical DOM for the theme list.
   * @param {Object[]} items - The filtered list of theme entries.
   * @returns {void}
   */
  function renderTree(items) {
    if (!playerTree) return;
    playerTree.innerHTML = "";
    if (!items.length) {
      const emptyMsg = 'No webm themes found. Click the "Generate" button under the "AnimeThemes: VFS" section of the dashboard to build your virtual folders.';
      playerTree.innerHTML = `<div class="placeholder">${uiFilter?.value ? "No matches" : emptyMsg}</div>`;
      return;
    }

    const esc = (str) => (str || "").replace(/"/g, "&quot;");
    const groups = new Map();
    items.forEach((i) => {
      if (!groups.has(i.group)) groups.set(i.group, new Map());
      const sMap = groups.get(i.group);
      if (!sMap.has(i.series)) sMap.set(i.series, []);
      sMap.get(i.series).push(i);
    });

    /**
     * Helper to construct a standard folder node in the tree with deferred child rendering.
     * @param {string} name - The display name for the folder.
     * @param {HTMLElement[]} children - The array of child list-item elements.
     * @param {boolean} isOpen - If true, forces children to render immediately.
     * @param {boolean} [isItalic=false] - Whether to italicize the title text.
     * @param {string} [extraHtml=""] - Additional HTML to append to the folder title.
     * @returns {HTMLLIElement} The completed folder list item node.
     */
    const makeNode = (name, children, isOpen, isItalic = false, extraHtml = "") => {
      const li = document.createElement("li");
      const ul = document.createElement("ul");
      const det = window._sr.createLazyDetails(
        "",
        ul,
        (container) => {
          children.forEach((c) => container.appendChild(c));
        },
        isOpen,
      );
      const sum = det.querySelector("summary");
      const titleHtml = `<span class="vfs-title${isItalic ? " tree-italic" : ""}" title="${esc(name)}" data-tooltip-overflow-only="true">${esc(name)}</span>${extraHtml}`;
      sum.insertAdjacentHTML("beforeend", titleHtml);
      sum.title = name;

      li.appendChild(det);
      return li;
    };

    const rootUl = document.createElement("ul");
    rootUl.className = "tree";

    const sortedGroupNames = [...groups.keys()].sort((a, b) => {
      if (a === MISSING_LABEL && b !== MISSING_LABEL) return -1;
      if (b === MISSING_LABEL && a !== MISSING_LABEL) return 1;
      return a.localeCompare(b);
    });

    sortedGroupNames.forEach((gName) => {
      const sMap = groups.get(gName),
        sKeys = [...sMap.keys()].sort((a, b) => a.localeCompare(b)),
        ft = (uiFilter?.value || "").trim();
      const sNodes = sKeys.map((sName) => {
        const files = sMap.get(sName).map((f) => {
          const li = document.createElement("li"),
            leaf = document.createElement("div"),
            name = decodeUnicode(f.file);
          leaf.className = `leaf ${f.path === currentWebmPath ? "active" : ""}`;
          leaf.dataset.path = f.path;

          const heart = document.createElement("span");
          heart.className = `fav-icon ${favourites.has(f.videoId) ? "favourited" : ""}`;
          heart.textContent = "❤";
          heart.onclick = (e) => {
            e.stopPropagation();
            toggleFavourite(f.videoId, heart);
          };

          const text = document.createElement("span");
          text.textContent = name;
          text.title = name;
          text.dataset.tooltipOverflowOnly = "true";

          leaf.onclick = () => {
            resetNavigationHistory();
            playFile(f.path);
          };
          leaf.append(heart, text);
          li.appendChild(leaf);
          return li;
        });

        const firstFile = sMap.get(sName)[0];
        const shokoBase = location.origin + base.split(/\/api\//i)[0];
        let extraHtml = "";
        if (firstFile.seriesId)
          extraHtml += `<a href="${shokoBase}/webui/collection/series/${firstFile.seriesId}/overview" class="vfs-link small" target="_blank" rel="noopener noreferrer">[${firstFile.seriesId}]</a>`;
        if (firstFile.anidbId) extraHtml += `<a href="https://anidb.net/a${firstFile.anidbId}" class="vfs-link small" target="_blank" rel="noopener noreferrer">[a${firstFile.anidbId}]</a>`;

        return makeNode(sName, files, !!ft, gName === MISSING_LABEL, extraHtml);
      });
      rootUl.appendChild(sKeys.length === 1 && gName !== MISSING_LABEL ? sNodes[0] : makeNode(gName, sNodes, !!ft, gName === MISSING_LABEL));
    });
    playerTree.appendChild(rootUl);
  }

  /** Locates the currently playing theme and scrolls it into center view while expanding its parent folder. */
  function locateCurrentInTree() {
    if (!currentWebmPath) return;

    const item = webmTreeData.find((i) => i.path === currentWebmPath);
    if (!item) return;
    const getSummaryTitle = (summary) => (summary ? summary.dataset.tooltipText || summary.getAttribute("title") || "" : "");
    const topDetails = [...playerTree.querySelectorAll(".tree > li > details")];

    // Find and expand the Group folder (if it exists as a parent)
    const groupDet = topDetails.find((d) => getSummaryTitle(d.querySelector("summary")) === item.group);
    if (groupDet) {
      groupDet.open = true;
      if (!groupDet.dataset.rendered) groupDet.dispatchEvent(new Event("toggle"));
    }

    // Find and expand the Series folder (top-level or nested)
    const searchScope = groupDet || playerTree;
    const seriesDet = [...searchScope.querySelectorAll("details")].find((d) => getSummaryTitle(d.querySelector("summary")) === item.series);
    if (seriesDet) {
      seriesDet.open = true;
      if (!seriesDet.dataset.rendered) seriesDet.dispatchEvent(new Event("toggle"));
    }

    const leaf = playerTree.querySelector(`.leaf[data-path="${CSS.escape(currentWebmPath)}"]`);
    if (leaf) {
      playerTree.querySelectorAll(".leaf").forEach((el) => el.classList.toggle("active", el.dataset.path === currentWebmPath)); // Force sync active highlights for newly appended lazy elements
      leaf.scrollIntoView({ behavior: "smooth", block: "center" });
    }
  }
  // #endregion

  // #region Player Engine
  /**
   * Sets the video source, updates header metadata, highlights, and saves the active path.
   * @param {string} path - The relative VFS path to the WebM file.
   * @returns {void}
   */
  function playFile(path) {
    if (!playerVideo) return;
    currentWebmPath = path;
    localStorage.setItem(PATH_KEY, path);
    playerVideo.src = `${base}/animethemes/webm/stream?path=${encodeURIComponent(path)}`;
    playerVideo.play().catch(() => {});

    const item = webmTreeData.find((i) => i.path === path);
    if (playerTitle) playerTitle.textContent = playerTitle.title = item ? decodeUnicode(item.file) : "Video Player";

    if (playerNowPlayingFav && item) {
      playerNowPlayingFav.style.pointerEvents = "auto";
      playerNowPlayingFav.classList.toggle("favourited", favourites.has(item.videoId));
      playerNowPlayingFav.onclick = (e) => {
        e.stopPropagation();
        toggleFavourite(item.videoId, playerNowPlayingFav);
      };
    }
    if (playerLocateBtn && item) playerLocateBtn.style.pointerEvents = "auto";

    if (playerAnime) {
      playerAnime.textContent = playerAnime.title = item ? item.series : "Select a theme to begin...";
      const isMissing = item?.group === MISSING_LABEL;
      playerAnime.classList.toggle("tree-italic", isMissing);

      if (item?.seriesId) {
        const shokoBase = location.origin + base.split("/api/")[0];
        playerAnime.href = `${shokoBase}/webui/collection/series/${item.seriesId}/overview`;
        playerAnime.style.pointerEvents = "auto";
        playerAnime.style.color = "";
      } else {
        playerAnime.href = "#";
        playerAnime.style.pointerEvents = "none";
        playerAnime.style.color = "var(--text-color)";
      }
    }

    if (playerAnidb) {
      playerAnidb.textContent = item && item.anidbId ? `[a${item.anidbId}]` : "";
      playerAnidb.href = item && item.anidbId ? `https://anidb.net/a${item.anidbId}` : "#";
      playerAnidb.style.pointerEvents = item && item.anidbId ? "auto" : "none";
    }
    playerTree?.querySelectorAll(".leaf").forEach((el) => el.classList.toggle("active", el.dataset.path === path));
  }

  /**
   * Calculates and plays the next or previous track based on shuffle history and direction.
   * @param {boolean} [isShuffle=false] - Whether to use shuffle-based random selection.
   * @param {number} [direction=1] - Playback direction (1 for next, -1 for previous).
   * @returns {void}
   */
  function playMove(isShuffle = false, direction = 1) {
    const items = getFilteredItems();
    if (items.length === 0) return;

    const currentFilter = (uiFilter?.value || "").trim();
    if (currentFilter !== lastFilterValue) {
      resetNavigationHistory();
      lastFilterValue = currentFilter;
    }

    if (isShuffle) {
      // Seed history with the currently playing track if entering shuffle mode fresh
      if (navigationStack.length === 0 && currentWebmPath) {
        navigationStack.push(currentWebmPath);
        shuffleHistory.add(currentWebmPath);
        stackIndex = 0;
      }

      if (direction === 1) {
        // Move forward in history or pick new random
        if (stackIndex < navigationStack.length - 1) {
          stackIndex++;
        } else {
          let pool = items.filter((item) => !shuffleHistory.has(item.path));
          if (pool.length === 0) {
            shuffleHistory.clear();
            pool = items;
          }
          if (pool.length > 1) pool = pool.filter((item) => item.path !== currentWebmPath);
          const selectedItem = pool[crypto.getRandomValues(new Uint32Array(1))[0] % pool.length];
          navigationStack.push(selectedItem.path);
          shuffleHistory.add(selectedItem.path);
          stackIndex = navigationStack.length - 1;
        }
      } else if (direction === -1) {
        // Move back in history
        if (stackIndex > 0) stackIndex--;
        else return; // At start of shuffle history
      }
      playFile(navigationStack[stackIndex]);
    } else {
      resetNavigationHistory();
      const currentIdx = items.findIndex((i) => i.path === currentWebmPath);
      const nextIdx = (currentIdx + direction + items.length) % items.length;
      playFile(items[nextIdx].path);
    }
  }

  /**
   * Updates the data-state attribute of the play button.
   * @returns {void}
   */
  function syncPlaybackUI() {
    if (playerNextBtn) {
      const isPlaying = playerVideo && !playerVideo.paused && !playerVideo.ended;
      playerNextBtn.setAttribute("data-state", isPlaying ? "playing" : "idle");
      updatePlaybackTooltip(playerNextBtn);
    }
  }

  /**
   * Toggles or cycles the playback mode on the dashboard and persists it to the server.
   * @param {number} [direction=1] - Cycle direction (1 for forward, -1 for backward).
   * @returns {Promise<void>}
   */
  async function updateMode(direction = 1) {
    if (!playerModeBtn) return;
    const modes = ["loop", "shuffle", "next", "off"];
    const next = cycleMode(playerModeBtn.getAttribute("data-mode") || "off", modes, direction);
    playerModeBtn.setAttribute("data-mode", next);
    updatePlaybackTooltip(playerModeBtn);
    resetNavigationHistory();

    const res = await fetchJson(configUrl);
    if (res.ok) {
      const cfg = unwrapConfig(getData(res));
      setValueByPath(cfg, "Playback.AnimeThemesWebmMode", next);
      await saveSettings(cfg);
    }
  }

  /**
   * Standard toggle for browser fullscreen API.
   * @returns {void}
   */
  function toggleFullscreen() {
    if (!document.fullscreenElement) playerContainer.requestFullscreen().catch(() => {});
    else document.exitFullscreen();
  }
  // #endregion

  // #region Initialization
  if (playerVideo) {
    const savedVol = localStorage.getItem("player-volume");
    const savedMuted = localStorage.getItem("player-muted");
    if (savedVol !== null) playerVideo.volume = parseFloat(savedVol);
    if (savedMuted !== null) playerVideo.muted = savedMuted === "true";

    ["play", "pause", "ended", "loadstart"].forEach((ev) => playerVideo.addEventListener(ev, syncPlaybackUI));

    const updateProgressLoop = () => {
      syncProgressUI();
      if (!playerVideo.paused && !playerVideo.ended) progressRaf = requestAnimationFrame(updateProgressLoop);
    };

    playerContainer.onmousemove = resetIdle;
    playerContainer.onmouseleave = () => {
      playerContainer.classList.remove("idle");
      clearTimeout(idleTimer);
    };

    playerVideo.addEventListener("play", () => {
      playerTrack?.classList.remove("paused");
      cancelAnimationFrame(progressRaf);
      progressRaf = requestAnimationFrame(updateProgressLoop);
    });

    playerVideo.addEventListener("pause", () => {
      playerTrack?.classList.add("paused");
      cancelAnimationFrame(progressRaf);
    });

    playerVideo.addEventListener("ended", () => {
      playerTrack?.classList.add("paused");
      cancelAnimationFrame(progressRaf);
      const m = playerModeBtn?.getAttribute("data-mode");
      setTimeout(() => {
        if (m === "loop") {
          playerVideo.currentTime = 0;
          playerVideo.play().catch(() => {});
        } else if (m === "shuffle" || m === "next") {
          playMove(m === "shuffle", 1);
        }
      }, 0);
    });

    playerVideo.addEventListener("volumechange", syncVolumeUI);
    playerVideo.addEventListener("timeupdate", () => {
      if (playerTime && playerVideo.duration) playerTime.textContent = `${formatTime(playerVideo.currentTime)} / ${formatTime(playerVideo.duration)}`;
      if (playerVideo.paused) syncProgressUI();
    });

    playerVideo.onclick = () => playerVideo.src && (playerVideo.paused ? playerVideo.play() : playerVideo.pause());
    playerVideo.ondblclick = toggleFullscreen;

    if (playerTrack) {
      const updateSeek = (e) => {
        if (!playerVideo.duration) return;
        const rect = playerTrack.getBoundingClientRect();
        const pos = (e.clientX - rect.left) / rect.width;
        playerVideo.currentTime = Math.max(0, Math.min(1, pos)) * playerVideo.duration;
        if (playerVideo.paused) syncProgressUI();
      };
      playerTrack.onmousedown = (e) => {
        if (e.button === 1 && playerVideo.src) {
          e.preventDefault();
          playerVideo.paused ? playerVideo.play() : playerVideo.pause();
          return;
        }
        if (e.button === 0) {
          updateSeek(e);
          const onMouseMove = (moveEv) => updateSeek(moveEv);
          const onMouseUp = () => {
            window.removeEventListener("mousemove", onMouseMove);
            window.removeEventListener("mouseup", onMouseUp);
          };
          window.addEventListener("mousemove", onMouseMove);
          window.addEventListener("mouseup", onMouseUp);
        }
      };
    }

    if (playerMuteBtn)
      playerMuteBtn.onclick = (e) => {
        e.stopPropagation();
        if (playerVideo.src) playerVideo.muted = !playerVideo.muted;
      };

    initSearchInteractions(uiFilter, uiFilterClear, () => renderTree(getFilteredItems()));

    if (playerModeBtn) playerModeBtn.onclick = () => updateMode(1);

    if (playerNextBtn) {
      playerNextBtn.onclick = () => {
        if (playerVideo.src) {
          if (playerVideo.ended) {
            playerVideo.currentTime = 0;
            playerVideo.play();
            return;
          }
          if (playerVideo.paused) {
            playerVideo.play();
            return;
          }
        }
        playMove(playerModeBtn?.getAttribute("data-mode") === "shuffle", 1);
      };
    }

    if (playerFullscreenBtn) playerFullscreenBtn.onclick = toggleFullscreen;
    document.addEventListener("fullscreenchange", () => {
      const isFS = !!document.fullscreenElement;
      playerFullscreenBtn.setAttribute("data-state", isFS ? "fullscreen" : "idle");
      playerFullscreenBtn.title = isFS ? "Exit Fullscreen" : "Fullscreen";
    });

    if (playerCloseBtn) {
      playerCloseBtn.onclick = () => {
        if (!playerVideo) return;
        playerVideo.pause();
        playerVideo.removeAttribute("src");
        playerVideo.load();
        localStorage.removeItem(PATH_KEY);
        currentWebmPath = "";

        if (playerTitle) playerTitle.textContent = playerTitle.title = "AnimeThemes Player";
        if (playerAnime) {
          playerAnime.textContent = playerAnime.title = "Select a theme to begin...";
          playerAnime.href = "#";
          playerAnime.style.pointerEvents = "none";
        }
        if (playerAnidb) {
          playerAnidb.textContent = "";
          playerAnidb.href = "#";
          playerAnidb.style.pointerEvents = "none";
        }
        if (playerNowPlayingFav) {
          playerNowPlayingFav.style.pointerEvents = "none";
          playerNowPlayingFav.classList.remove("favourited");
        }
        if (playerLocateBtn) playerLocateBtn.style.pointerEvents = "none";
        playerTree?.querySelectorAll(".leaf").forEach((el) => el.classList.remove("active"));
        if (playerFill) playerFill.style.width = "0%";
        if (playerTime) playerTime.textContent = "0:00 / 0:00";
      };
    }

    playerLocateBtn.onclick = locateCurrentInTree;
    helpOpenBtn.onclick = toggleHelpModal;

    if (playerVideoBtn) {
      const savedVideoState = localStorage.getItem("player-video-state") || "on";
      playerVideoBtn.setAttribute("data-state", savedVideoState);
      playerVideoBtn.title = savedVideoState === "on" ? "Turn Video Off" : "Turn Video On";
      playerContainer.classList.toggle("video-off", savedVideoState === "off");
      playerVideoBtn.onclick = (e) => {
        e.stopPropagation();
        toggleVideo();
      };
    }

    window.addEventListener("keydown", (e) => {
      if (document.activeElement.tagName === "INPUT" || document.activeElement.tagName === "TEXTAREA") return;
      // prettier-ignore
      const handledKeys = [
        " ", "k", "K", "ArrowLeft", "ArrowRight", "j", "J", "l", "L", ",", ".", "b", "B", "n", "N", ";", "'",
        "ArrowDown", "ArrowUp", "m", "M", "f", "F", "v", "V", "r", "R", "g", "G", "h", "H", "?", "q", "Q"
      ];
      if (!handledKeys.includes(e.key)) return;
      e.preventDefault();
      const isShuffle = playerModeBtn?.getAttribute("data-mode") === "shuffle";
      // prettier-ignore
      switch (e.key) {
        case " ": case "k": case "K": if (playerVideo.src) { if (playerVideo.ended) { playerVideo.currentTime = 0; playerVideo.play(); } else { playerVideo.paused ? playerVideo.play() : playerVideo.pause(); } } else playMove(isShuffle, 1); break;
        case "ArrowLeft": if (playerVideo.src) { playerVideo.currentTime -= 5; syncProgressUI(); } break;
        case "ArrowRight": if (playerVideo.src) { playerVideo.currentTime += 5; syncProgressUI(); } break;
        case "j": case "J": if (playerVideo.src) { playerVideo.currentTime -= 10; syncProgressUI(); } break;
        case "l": case "L": if (playerVideo.src) { playerVideo.currentTime += 10; syncProgressUI(); } break;
        case ",": if (playerVideo.src) { playerVideo.pause(); playerVideo.currentTime = Math.max(0, playerVideo.currentTime - 1 / 24); } break;
        case ".": if (playerVideo.src) { playerVideo.pause(); playerVideo.currentTime = Math.min(playerVideo.duration, playerVideo.currentTime + 1 / 24); } break;
        case "b": case "B": playMove(isShuffle, -1); break;
        case "n": case "N": playMove(isShuffle, 1); break;
        case ";": updateMode(-1); break;
        case "'": updateMode(1); break;
        case "ArrowDown": playerVideo.volume = Math.max(0, playerVideo.volume - 0.1); break;
        case "ArrowUp": playerVideo.volume = Math.min(1, playerVideo.volume + 0.1); break;
        case "m": case "M": if (playerVideo.src) playerVideo.muted = !playerVideo.muted; break;
        case "f": case "F": toggleFullscreen(); break;
        case "v": case "V": toggleVideo(); break;
        case "r": case "R": if (playerVideo.src) { playerVideo.currentTime = 0; playerVideo.play().catch(() => {}); } break;
        case "g": case "G": locateCurrentInTree(); break;
        case "h": case "H": const hItem = webmTreeData.find((i) => i.path === currentWebmPath); if (hItem) toggleFavourite(hItem.videoId); break;
        case "?": toggleHelpModal(); break;
        case "q": case "Q": playerCloseBtn?.click(); break;
      }
    });

    (async () => {
      if (playerTree) playerTree.innerHTML = '<div class="placeholder"><svg class="loading-spinner"><use href="img/icons.svg#loading"></use></svg><div>Loading video tree...</div></div>';
      const [treeRes, cfgRes, favRes] = await Promise.all([fetchJson(base + "/animethemes/webm/tree"), fetchJson(configUrl), fetchJson(base + "/animethemes/webm/favourites")]);
      if (favRes.ok) favourites = new Set(getData(favRes) || []);
      if (treeRes.ok) {
        webmTreeData = (getData(treeRes)?.items || [])
          .map((item) => {
            item._searchIndex = `${item.group} ${item.series} ${decodeUnicode(item.file)}`.toLowerCase().replace(/[\u200B\u200A]/g, "");
            return item;
          })
          .sort((a, b) => {
            if (a.group === MISSING_LABEL && b.group !== MISSING_LABEL) return -1;
            if (b.group === MISSING_LABEL && a.group !== MISSING_LABEL) return 1;

            const groupComp = a.group.localeCompare(b.group);
            if (groupComp !== 0) return groupComp;
            const seriesComp = a.series.localeCompare(b.series);
            if (seriesComp !== 0) return seriesComp;
            return a.file.localeCompare(b.file);
          });
        renderTree(getFilteredItems());

        // Retrieve, validate, and restore the currently playing video across sessions
        const savedPath = localStorage.getItem(PATH_KEY);
        if (savedPath && webmTreeData.some((item) => item.path === savedPath)) {
          currentWebmPath = savedPath;
          playFile(savedPath);
          setTimeout(locateCurrentInTree, 500);
        }
      }
      if (cfgRes.ok && playerModeBtn) {
        const mode = unwrapConfig(getData(cfgRes)).Playback.AnimeThemesWebmMode || "off";
        playerModeBtn.setAttribute("data-mode", mode);
        updatePlaybackTooltip(playerModeBtn);
      }
      syncVolumeUI();
    })();
  }
  // #endregion
})();
