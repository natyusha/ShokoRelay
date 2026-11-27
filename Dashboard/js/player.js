/**
 * @file player.js
 * @description Logic for the Shoko Relay stand-alone AnimeThemes VFS video player.
 */
(() => {
  const { base, shokoBase, configUrl, element, fetchJson, unwrapConfig, setValueByPath, openModal, updatePlaybackTooltip, saveSettings, getData, initSearchInteractions } = window._sr;

  // DOM Elements
  /** @type {HTMLElement} */ const playerTime = element("time-display");
  /** @type {HTMLElement} */ const playerVolText = element("volume-text");
  /** @type {HTMLElement} */ const playerFullscreenBtn = element("fullscreen");
  /** @type {HTMLElement} */ const playerCloseBtn = element("close-session");
  /** @type {HTMLElement} */ const playerMuteBtn = element("volume-icon");
  /** @type {HTMLVideoElement} */ const playerVideo = element("video");
  /** @type {HTMLElement} */ const playerContainer = playerVideo.parentElement;
  /** @type {HTMLElement} */ const playerVideoBtn = element("video-toggle");
  /** @type {HTMLElement} */ const playerTree = element("tree");
  /** @type {HTMLInputElement} */ const uiFilter = element("filter");
  /** @type {HTMLElement} */ const uiFilterClear = element("filter-clear");
  /** @type {HTMLElement} */ const playerNextBtn = element("next");
  /** @type {HTMLElement} */ const playerModeBtn = element("mode");
  /** @type {HTMLElement} */ const playerSortBtn = element("sort-toggle");
  /** @type {HTMLElement} */ const playerTitle = element("title");
  /** @type {HTMLElement} */ const playerAnime = element("anime");
  /** @type {HTMLElement} */ const playerAnidb = element("anidb");
  /** @type {HTMLElement} */ const playerNowPlayingFav = element("now-playing-fav");
  /** @type {HTMLElement} */ const playerLocateBtn = element("locate-current");
  /** @type {HTMLElement} */ const playerFill = element("player-progress-fill");
  /** @type {HTMLElement} */ const playerTrack = element("player-progress-track");
  /** @type {HTMLElement} */ const helpModal = element("player-help-modal");
  /** @type {HTMLElement} */ const helpOpenBtn = element("help-open");

  /** @type {Function|null} Reference to the active modal close callback to support toggle hotkeys. */
  let activeModalClose = null;
  /** @type {Object[]} The pre-sorted list of WebM themes from the server. */
  let webmTreeData = [];
  /** @type {string} The relative VFS path of the currently playing theme. */
  let currentWebmPath = "";
  /** @type {Set<number>} Set of video IDs marked as favourite. */
  let favourites = new Set();
  /** @type {number|null} Reference to the active requestAnimationFrame for the progress bar. */
  let progressFrameId;
  /** @type {boolean} Determines if the tree renders using original folder structures instead of metadata groups. */
  let useFolderView = localStorage.getItem("player-folder-view") === "true";

  /** @type {string} Local storage key for persisting the currently playing path. */
  const PATH_KEY = "player-current-path";
  /** @type {string} Label used for themes missing from the Shoko collection. */
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

  // #region Helpers
  /**
   * Resets the idle timer and reveals the UI, suspending it if hovering over controls.
   * @param {MouseEvent} event - The mouse movement event.
   * @returns {void}
   */
  const resetIdle = (event) => {
    playerContainer.classList.remove("idle");
    clearTimeout(idleTimer);
    if (!event.target.closest(".ui-btn, #player-progress-track, #volume-text")) idleTimer = setTimeout(() => playerContainer.classList.add("idle"), 2000);
  };

  /**
   * Toggles the visibility of the video element to save system resources in audio-only mode.
   * @returns {void}
   */
  function toggleVideo() {
    if (!playerVideo) return;
    const isOff = playerVideoBtn.getAttribute("data-state") === "on";
    const newState = isOff ? "off" : "on";
    playerVideoBtn.setAttribute("data-state", newState);
    playerVideoBtn.title = `Turn Video ${isOff ? "On" : "Off"}`;
    playerContainer.classList.toggle("video-off", isOff);
    localStorage.setItem("player-video-state", newState);
  }

  /**
   * Converts a numeric time in seconds to a human-readable M:SS format.
   * @param {number} totalSeconds - The time in seconds.
   * @returns {string} The formatted duration string.
   */
  const formatTime = (totalSeconds) => {
    if (isNaN(totalSeconds)) return "0:00";
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = Math.floor(totalSeconds % 60);
    return `${minutes}:${seconds < 10 ? "0" : ""}${seconds}`;
  };

  /**
   * Updates the visual progress bar fill width.
   * @returns {void}
   */
  const syncProgressUI = () => {
    if (playerFill && playerVideo.duration) playerFill.style.width = `${(playerVideo.currentTime / playerVideo.duration) * 100}%`;
  };

  /**
   * Synchronizes the volume percentage text and updates icon switching level.
   * @returns {void}
   */
  const syncVolumeUI = () => {
    if (!playerVideo) return;
    const volume = playerVideo.volume;
    const isMuted = playerVideo.muted || volume === 0;
    if (playerVolText) playerVolText.textContent = isMuted ? "Muted" : `${Math.round(volume * 100)}%`;
    if (playerMuteBtn) playerMuteBtn.title = isMuted ? "Unmute" : "Mute";
    if (playerContainer) playerContainer.dataset.volumeLevel = isMuted ? "mute" : volume > 0.66 ? "high" : volume > 0.33 ? "med" : "low";
    localStorage.setItem("player-volume", volume);
    localStorage.setItem("player-muted", playerVideo.muted);
  };

  /**
   * Synchronizes the sort button UI with the active folder view state.
   * @returns {void}
   */
  const syncSortUI = () => {
    if (!playerSortBtn) return;
    playerSortBtn.setAttribute("data-sort", useFolderView ? "source" : "series");
    playerSortBtn.title = useFolderView ? "Sort by Collected Series" : "Sort by Source Folder";
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
    } else activeModalClose = openModal(helpModal, () => (activeModalClose = null));
  };

  /**
   * Evaluates the full list of themes against active search terms, heart status, and pre-indexed server tags.
   * @returns {Object[]} The filtered list of theme entries.
   */
  const getFilteredItems = () => {
    const rawFilter = (uiFilter?.value || "").toLowerCase().trim();
    if (!rawFilter) return webmTreeData;

    const words = rawFilter.split(/\s+/).filter(Boolean);
    const isFavQuery = words.includes("favs");
    const tagExclusions = [];
    const tagInclusions = [];
    const searchTerms = [];

    words.forEach((word) => {
      if (word === "favs") return;
      word.startsWith("-") ? tagExclusions.push(word.substring(1)) : word.startsWith("+") ? tagInclusions.push(word.substring(1)) : searchTerms.push(word);
    });

    return webmTreeData.filter((item) => {
      if (isFavQuery && (!item.videoId || !favourites.has(item.videoId))) return false;
      const tags = item.tags || [];
      if (tagExclusions.some((tag) => tags.includes(tag)) || tagInclusions.some((tag) => !tags.includes(tag))) return false;
      return searchTerms.every((word) => item.searchIndex.includes(word));
    });
  };

  /**
   * Computes the set of group names that contain multiple series within the provided item list.
   * @param {Object[]} items - The list of theme entries.
   * @returns {Set<string>} Set of group names requiring a parent group folder.
   */
  const getMultiSeriesGroups = (items) => {
    const groupSeries = new Map();
    const multiGroups = new Set();
    items.forEach((item) => {
      const existing = groupSeries.get(item.group);
      if (existing === undefined) groupSeries.set(item.group, item.series);
      else if (existing !== item.series) multiGroups.add(item.group);
    });
    return multiGroups;
  };

  /**
   * Resolves the ordered folder path segments for an item under the active view mode.
   * @param {Object} item - The theme entry.
   * @param {Set<string>} multiSeriesGroups - Groups that contain multiple series.
   * @returns {string[]} The folder path segments leading to the item.
   */
  const getItemPathSegments = (item, multiSeriesGroups) => (useFolderView ? item.folderSegments : multiSeriesGroups.has(item.group) || item.group === MISSING_LABEL ? [item.group, item.series] : [item.series]);

  /**
   * Resets the navigation stack and history tracking.
   * @returns {void}
   */
  const resetNavigationHistory = () => {
    shuffleHistory.clear();
    navigationStack = [];
    stackIndex = -1;
  };

  /**
   * Toggles the tree view between standard metadata grouping and source folder structures.
   * @returns {void}
   */
  function toggleFolderView() {
    useFolderView = !useFolderView;
    syncSortUI();
    localStorage.setItem("player-folder-view", useFolderView);
    renderTree(getFilteredItems());
    setTimeout(locateCurrentInTree, 100);
  }

  /**
   * Determines whether the current session is permitted to modify server-persisted player state such as favourites and playback mode.
   * @returns {boolean} True if authentication is disabled or the user is an authenticated administrator.
   */
  const canModifyServerState = () => {
    const auth = window._sr.authState;
    return !!auth && (!auth.required || !!auth.authenticated);
  };

  /**
   * Updates the disabled state and tooltips of favourite controls based on the current authentication state.
   * @returns {void}
   */
  const syncFavouriteControls = () => {
    const allowed = canModifyServerState();
    element("player")?.classList.toggle("fav-disabled", !allowed);
    if (playerNowPlayingFav) {
      playerNowPlayingFav.setAttribute("aria-disabled", String(!allowed));
      playerNowPlayingFav.title = allowed ? "Favourite" : "Authentication required to modify favourites";
    }
  };

  /**
   * Standard toggle for browser fullscreen API.
   * @returns {void}
   */
  function toggleFullscreen() {
    if (!document.fullscreenElement) playerContainer.requestFullscreen().catch(() => {});
    else document.exitFullscreen();
  }
  // #endregion

  // #region Tree & Navigation
  /**
   * Toggles the favourite status of a video on the server and synchronizes UI heart icons.
   * @param {number} videoId - The AnimeThemes video ID to toggle.
   * @returns {Promise<void>}
   */
  async function toggleFavourite(videoId) {
    if (!canModifyServerState()) return;
    if (!videoId || videoId <= 0) return console.warn("VideoId missing.");
    const response = await fetchJson(base + "/animethemes/webm/favourites", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(videoId),
    });

    const isFav = getData(response)?.isFavourite;
    if (response.isOk && isFav !== undefined) {
      isFav ? favourites.add(videoId) : favourites.delete(videoId);
      playerTree?.querySelectorAll(`.leaf[data-video-id="${videoId}"] .fav-icon`).forEach((icon) => icon.classList.toggle("favourited", isFav));
      const isCurrentVideo = webmTreeData.find((item) => item.path === currentWebmPath)?.videoId === videoId;
      if (playerNowPlayingFav && isCurrentVideo) playerNowPlayingFav.classList.toggle("favourited", isFav);
      if (uiFilter?.value.toLowerCase().includes("favs")) renderTree(getFilteredItems());
    }
  }

  /**
   * Constructs the hierarchical DOM for the theme list using server-computed sort keys and segments.
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

    const rootUl = document.createElement("ul");
    rootUl.className = "tree";
    const hasFilter = !!uiFilter?.value.trim();
    const favAllowed = canModifyServerState();
    const orderedItems = useFolderView ? [...items].sort((left, right) => (left.folderSortKey < right.folderSortKey ? -1 : left.folderSortKey > right.folderSortKey ? 1 : 0)) : items;
    const multiSeriesGroups = useFolderView ? null : getMultiSeriesGroups(orderedItems);
    const rootNode = { folders: new Map(), files: [] };

    orderedItems.forEach((item) => {
      let current = rootNode;
      getItemPathSegments(item, multiSeriesGroups).forEach((segment) => {
        if (!current.folders.has(segment)) current.folders.set(segment, { folders: new Map(), files: [] });
        current = current.folders.get(segment);
      });
      current.isSeries = true;
      current.seriesId = item.seriesId;
      current.anidbId = item.anidbId;
      if (item.group === MISSING_LABEL) current.isMissing = true;
      current.files.push(item);
    });

    /**
     * Generates external links for a given series.
     * @param {number} seriesId - The Shoko Series ID.
     * @param {number} anidbId - The AniDB ID.
     * @returns {string} The concatenated anchor HTML.
     */
    const buildSeriesLinks = (seriesId, anidbId) =>
      `${seriesId ? `<a href="${shokoBase}/webui/collection/series/${seriesId}/overview" class="vfs-link small" target="_blank" rel="noopener noreferrer">[${seriesId}]</a>` : ""}` +
      `${anidbId ? `<a href="https://anidb.net/a${anidbId}" class="vfs-link small" target="_blank" rel="noopener noreferrer">[a${anidbId}]</a>` : ""}`;

    /**
     * Constructs a single theme leaf node in the tree.
     * @param {Object} item - The theme entry metadata.
     * @returns {HTMLLIElement} The completed file list item node.
     */
    const createFileLeaf = (item) => {
      const listItem = document.createElement("li");
      const leaf = document.createElement("div");
      leaf.className = `leaf ${item.path === currentWebmPath ? "active" : ""}`;
      leaf.dataset.path = item.path;
      if (item.videoId) leaf.dataset.videoId = String(item.videoId);

      const heart = document.createElement("span");
      heart.className = `fav-icon ${favourites.has(item.videoId) ? "favourited" : ""}`;
      heart.textContent = "❤";
      heart.setAttribute("aria-disabled", String(!favAllowed));
      if (!favAllowed) heart.title = "Authentication Required to Modify Favourites";
      heart.onclick = (event) => {
        event.stopPropagation();
        if (canModifyServerState()) toggleFavourite(item.videoId);
      };

      const text = document.createElement("span");
      text.textContent = item.file;
      text.title = item.file;
      text.dataset.tooltipOverflowOnly = "true";
      if (item.group === MISSING_LABEL) text.classList.add("tree-missing");

      leaf.onclick = () => {
        resetNavigationHistory();
        playFile(item.path);
      };
      leaf.append(heart, text);
      listItem.appendChild(leaf);
      return listItem;
    };

    /**
     * Recursively renders a pre-ordered folder hierarchy into the target list container.
     * @param {Object} node - The current hierarchy node.
     * @param {HTMLUListElement} container - The target unordered list element.
     * @returns {void}
     */
    const renderHierarchy = (node, container) => {
      node.folders.forEach((childNode, folderName) => {
        const listItem = document.createElement("li");
        const childList = document.createElement("ul");
        const extraHtml = childNode.isSeries ? buildSeriesLinks(childNode.seriesId, childNode.anidbId) : "";
        const details = window._sr.createLazyDetails(folderName, childList, (subContainer) => renderHierarchy(childNode, subContainer), hasFilter, extraHtml);
        if (childNode.isMissing || folderName === MISSING_LABEL) details.querySelector("summary .vfs-title")?.classList.add("tree-missing");
        listItem.appendChild(details);
        container.appendChild(listItem);
      });
      node.files.forEach((file) => container.appendChild(createFileLeaf(file)));
    };

    renderHierarchy(rootNode, rootUl);
    playerTree.appendChild(rootUl);
  }

  /**
   * Locates the currently playing theme and scrolls it into center view while expanding its parent folder.
   * @returns {void}
   */
  function locateCurrentInTree() {
    if (!currentWebmPath) return;
    const currentItem = webmTreeData.find((themeItem) => themeItem.path === currentWebmPath);
    if (!currentItem) return;

    playerTree.querySelectorAll("details[open]").forEach((details) => (details.open = false));
    const multiSeriesGroups = useFolderView ? null : getMultiSeriesGroups(getFilteredItems());

    let currentScope = playerTree;
    getItemPathSegments(currentItem, multiSeriesGroups).forEach((segment) => {
      const details = [...currentScope.querySelectorAll("details")].find((detailsElement) => {
        const summary = detailsElement.querySelector("summary");
        return (summary?.dataset.tooltipText || summary?.getAttribute("title") || "") === segment;
      });
      if (details) {
        details.open = true;
        if (!details.dataset.rendered) details.dispatchEvent(new Event("toggle"));
        currentScope = details;
      }
    });

    const leaf = playerTree.querySelector(`.leaf[data-path="${CSS.escape(currentWebmPath)}"]`);
    if (leaf) {
      playerTree.querySelectorAll(".leaf").forEach((leafElement) => leafElement.classList.toggle("active", leafElement.dataset.path === currentWebmPath));
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
    const streamUrl = `${base}/animethemes/webm/stream?path=${encodeURIComponent(path)}`;
    playerVideo.onerror = async () => {
      playerVideo.onerror = null;
      try {
        const verifyResponse = await fetch(`${base}/auth/verify?player=true`);
        if ((verifyResponse.status === 401 || verifyResponse.status === 403) && (await window._sr.ensureAuth?.({ dismissable: false })) && currentWebmPath === path) {
          playerVideo.src = streamUrl;
          playerVideo.play().catch(() => {});
        }
      } catch {}
    };
    playerVideo.src = streamUrl;
    playerVideo.play().catch(() => {});

    const item = webmTreeData.find((themeItem) => themeItem.path === path);
    if (playerTitle) playerTitle.textContent = playerTitle.title = item ? item.file : "AnimeThemes: VFS Video Player";

    if (playerNowPlayingFav && item) {
      playerNowPlayingFav.style.pointerEvents = "auto";
      playerNowPlayingFav.classList.toggle("favourited", favourites.has(item.videoId));
      playerNowPlayingFav.onclick = (event) => {
        event.stopPropagation();
        if (canModifyServerState()) toggleFavourite(item.videoId);
      };
    }
    if (playerLocateBtn && item) playerLocateBtn.style.pointerEvents = "auto";

    if (playerAnime) {
      playerAnime.textContent = playerAnime.title = item ? item.series : "Select a theme to begin...";
      playerAnime.classList.toggle("tree-missing", item?.group === MISSING_LABEL);
      playerAnime.href = item?.seriesId ? `${shokoBase}/webui/collection/series/${item.seriesId}/overview` : "#";
      playerAnime.style.pointerEvents = item?.seriesId ? "auto" : "none";
      playerAnime.style.color = item?.seriesId ? "" : "var(--text-color)";
    }

    if (playerAnidb) {
      playerAnidb.textContent = item?.anidbId ? `[a${item.anidbId}]` : "";
      playerAnidb.href = item?.anidbId ? `https://anidb.net/a${item.anidbId}` : "#";
      playerAnidb.style.pointerEvents = item?.anidbId ? "auto" : "none";
    }
    playerTree?.querySelectorAll(".leaf").forEach((leafElement) => leafElement.classList.toggle("active", leafElement.dataset.path === path));
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
      if (navigationStack.length === 0 && currentWebmPath) {
        navigationStack.push(currentWebmPath);
        shuffleHistory.add(currentWebmPath);
        stackIndex = 0;
      }

      if (direction === 1) {
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
        if (stackIndex > 0) stackIndex--;
        else return;
      }
      playFile(navigationStack[stackIndex]);
    } else {
      resetNavigationHistory();
      const currentIndex = items.findIndex((item) => item.path === currentWebmPath);
      playFile(items[(currentIndex + direction + items.length) % items.length].path);
    }
  }

  /**
   * Updates the data-state attribute of the play button.
   * @returns {void}
   */
  function syncPlaybackUI() {
    if (!playerNextBtn) return;
    const isPlaying = playerVideo && !playerVideo.paused && !playerVideo.ended;
    playerNextBtn.setAttribute("data-state", isPlaying ? "playing" : "idle");
    updatePlaybackTooltip(playerNextBtn);
  }

  /**
   * Toggles or cycles the playback mode on the dashboard and persists it to the server.
   * @param {number} [direction=1] - Cycle direction (1 for forward, -1 for backward).
   * @returns {Promise<void>}
   */
  async function updateMode(direction = 1) {
    if (!playerModeBtn) return;
    const nextMode = window._sr.cycleMode(playerModeBtn.getAttribute("data-mode") || "off", ["loop", "shuffle", "next", "off"], direction);
    playerModeBtn.setAttribute("data-mode", nextMode);
    updatePlaybackTooltip(playerModeBtn);
    resetNavigationHistory();

    if (!canModifyServerState()) {
      localStorage.setItem("player-playback-mode", nextMode);
      return;
    }

    const response = await fetchJson(configUrl);
    if (response.isOk) {
      const config = unwrapConfig(getData(response));
      setValueByPath(config, "Playback.AnimeThemesWebmMode", nextMode);
      await saveSettings(config);
    }
  }
  // #endregion

  // #region Initialization
  if (playerSortBtn) {
    syncSortUI();
    playerSortBtn.onclick = toggleFolderView;
  }

  if (playerVideo) {
    const savedVolume = localStorage.getItem("player-volume");
    const savedMuted = localStorage.getItem("player-muted");
    if (savedVolume !== null) playerVideo.volume = parseFloat(savedVolume);
    if (savedMuted !== null) playerVideo.muted = savedMuted === "true";

    ["play", "pause", "ended", "loadstart"].forEach((event) => playerVideo.addEventListener(event, syncPlaybackUI));

    /**
     * Drives smooth requestAnimationFrame updates for the playback progress bar while playing.
     * @returns {void}
     */
    const updateProgressLoop = () => {
      syncProgressUI();
      if (!playerVideo.paused && !playerVideo.ended) progressFrameId = requestAnimationFrame(updateProgressLoop);
    };

    playerContainer.onmousemove = resetIdle;
    playerContainer.onmouseleave = () => {
      playerContainer.classList.remove("idle");
      clearTimeout(idleTimer);
    };

    playerVideo.addEventListener("play", () => {
      playerTrack?.classList.remove("paused");
      cancelAnimationFrame(progressFrameId);
      progressFrameId = requestAnimationFrame(updateProgressLoop);
    });

    playerVideo.addEventListener("pause", () => {
      playerTrack?.classList.add("paused");
      cancelAnimationFrame(progressFrameId);
    });

    playerVideo.addEventListener("ended", () => {
      playerTrack?.classList.add("paused");
      cancelAnimationFrame(progressFrameId);
      const playbackMode = playerModeBtn?.getAttribute("data-mode");
      setTimeout(() => {
        if (playbackMode === "loop") {
          playerVideo.currentTime = 0;
          playerVideo.play().catch(() => {});
        } else if (playbackMode === "shuffle" || playbackMode === "next") playMove(playbackMode === "shuffle", 1);
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
      /**
       * Seeks the video element proportionally to the pointer's horizontal position along the progress track.
       * @param {MouseEvent} event - The pointer event over the progress track.
       * @returns {void}
       */
      const updateSeek = (event) => {
        if (!playerVideo.duration) return;
        const bounds = playerTrack.getBoundingClientRect();
        playerVideo.currentTime = Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width)) * playerVideo.duration;
        if (playerVideo.paused) syncProgressUI();
      };
      playerTrack.onmousedown = (event) => {
        if (event.button === 1 && playerVideo.src) {
          event.preventDefault();
          playerVideo.paused ? playerVideo.play() : playerVideo.pause();
        } else if (event.button === 0) {
          updateSeek(event);
          const onMouseUp = () => {
            window.removeEventListener("mousemove", updateSeek);
            window.removeEventListener("mouseup", onMouseUp);
          };
          window.addEventListener("mousemove", updateSeek);
          window.addEventListener("mouseup", onMouseUp);
        }
      };
    }

    /**
     * Handles mouse wheel scrolling over volume controls to adjust playback volume.
     * @param {WheelEvent} event - The wheel event.
     * @returns {void}
     */
    const onVolumeWheel = (event) => {
      event.preventDefault();
      event.stopPropagation();
      resetIdle(event);
      const delta = event.deltaY < 0 ? 0.05 : -0.05;
      if (playerVideo.muted && delta > 0) playerVideo.muted = false;
      playerVideo.volume = Math.max(0, Math.min(1, Math.round((playerVideo.volume + delta) * 100) / 100));
    };

    if (playerMuteBtn) {
      playerMuteBtn.onclick = (event) => {
        event.stopPropagation();
        if (playerVideo.src) playerVideo.muted = !playerVideo.muted;
      };
      playerMuteBtn.addEventListener("wheel", onVolumeWheel, { passive: false });
    }

    if (playerVolText) {
      playerVolText.onclick = (event) => event.stopPropagation();
      playerVolText.addEventListener("wheel", onVolumeWheel, { passive: false });
    }

    initSearchInteractions(uiFilter, uiFilterClear, () => renderTree(getFilteredItems()));

    if (playerModeBtn) playerModeBtn.onclick = () => updateMode(1);

    if (playerNextBtn) {
      playerNextBtn.onclick = () => {
        if (playerVideo.src && (playerVideo.ended || playerVideo.paused)) {
          if (playerVideo.ended) playerVideo.currentTime = 0;
          playerVideo.play();
        } else playMove(playerModeBtn?.getAttribute("data-mode") === "shuffle", 1);
      };
    }

    if (playerFullscreenBtn) playerFullscreenBtn.onclick = toggleFullscreen;
    document.addEventListener("fullscreenchange", () => {
      const isFullscreen = !!document.fullscreenElement;
      playerFullscreenBtn.setAttribute("data-state", isFullscreen ? "fullscreen" : "idle");
      playerFullscreenBtn.title = isFullscreen ? "Exit Fullscreen" : "Fullscreen";
    });

    if (playerCloseBtn) {
      playerCloseBtn.onclick = () => {
        if (!playerVideo) return;
        playerVideo.pause();
        playerVideo.removeAttribute("src");
        playerVideo.load();
        localStorage.removeItem(PATH_KEY);
        currentWebmPath = "";

        if (playerTitle) playerTitle.textContent = playerTitle.title = "AnimeThemes: VFS Video Player";
        if (playerAnime) {
          playerAnime.textContent = playerAnime.title = "Select a theme to begin...";
          playerAnime.classList.remove("tree-missing");
          playerAnime.href = "#";
          playerAnime.style.pointerEvents = "none";
          playerAnime.style.color = "";
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
        playerTree?.querySelectorAll(".leaf").forEach((leafElement) => leafElement.classList.remove("active"));
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
      playerVideoBtn.onclick = (event) => {
        event.stopPropagation();
        toggleVideo();
      };
    }

    window.addEventListener("keydown", (event) => {
      if (event.ctrlKey || event.metaKey || event.altKey) return;
      if (document.activeElement.tagName === "INPUT" || document.activeElement.tagName === "TEXTAREA" || document.querySelector(".modal-overlay.open")) return;
      const key = event.key.toLowerCase();
      const handledKeys = [" ", "k", "arrowleft", "arrowright", "j", "l", ",", ".", "b", "n", ";", "'", "arrowdown", "arrowup", "m", "f", "v", "r", "g", "h", "?", "q"];
      if (!handledKeys.includes(key)) return;
      event.preventDefault();
      const isShuffle = playerModeBtn?.getAttribute("data-mode") === "shuffle";
      // prettier-ignore
      switch (key) {
        case " ": case "k": if (playerVideo.src) { if (playerVideo.ended) { playerVideo.currentTime = 0; playerVideo.play(); } else { playerVideo.paused ? playerVideo.play() : playerVideo.pause(); } } else playMove(isShuffle, 1); break;
        case "arrowleft": if (playerVideo.src) { playerVideo.currentTime -= 5; syncProgressUI(); } break;
        case "arrowright": if (playerVideo.src) { playerVideo.currentTime += 5; syncProgressUI(); } break;
        case "j": if (playerVideo.src) { playerVideo.currentTime -= 10; syncProgressUI(); } break;
        case "l": if (playerVideo.src) { playerVideo.currentTime += 10; syncProgressUI(); } break;
        case ",": if (playerVideo.src) { playerVideo.pause(); playerVideo.currentTime = Math.max(0, playerVideo.currentTime - 1 / 24); } break;
        case ".": if (playerVideo.src && Number.isFinite(playerVideo.duration)) { playerVideo.pause(); playerVideo.currentTime = Math.min(playerVideo.duration, playerVideo.currentTime + 1 / 24); } break;
        case "b": playMove(isShuffle, -1); break;
        case "n": playMove(isShuffle, 1); break;
        case ";": updateMode(1); break;
        case "'": toggleFolderView(); break;
        case "arrowdown": playerVideo.volume = Math.max(0, playerVideo.volume - 0.05); break;
        case "arrowup": playerVideo.volume = Math.min(1, playerVideo.volume + 0.05); break;
        case "m": if (playerVideo.src) playerVideo.muted = !playerVideo.muted; break;
        case "f": toggleFullscreen(); break;
        case "v": toggleVideo(); break;
        case "r": if (playerVideo.src) { playerVideo.currentTime = 0; playerVideo.play().catch(() => {}); } break;
        case "g": locateCurrentInTree(); break;
        case "h": { const favItem = webmTreeData.find((item) => item.path === currentWebmPath); if (favItem) toggleFavourite(favItem.videoId); break; }
        case "?": toggleHelpModal(); break;
        case "q": playerCloseBtn?.click(); break;
      }
    });

    (async () => {
      await window._sr.authReady;
      syncFavouriteControls();
      if (playerTree) playerTree.innerHTML = '<div class="placeholder"><svg class="loading-spinner"><use href="img/icons.svg#loading"></use></svg><div>Loading video tree...</div></div>';
      const canReadConfig = canModifyServerState();
      const [treeResponse, configResponse] = await Promise.all([fetchJson(base + "/animethemes/webm/tree"), canReadConfig ? fetchJson(configUrl) : Promise.resolve({ isOk: false })]);
      const treePayload = getData(treeResponse);
      if (treeResponse.isOk) {
        favourites = new Set(treePayload?.favourites || []);
        webmTreeData = treePayload?.items || [];
        renderTree(getFilteredItems());

        const savedPath = localStorage.getItem(PATH_KEY);
        if (savedPath && webmTreeData.some((item) => item.path === savedPath)) {
          currentWebmPath = savedPath;
          playFile(savedPath);
          setTimeout(locateCurrentInTree, 500);
        }
      }
      if (playerModeBtn) {
        const mode =
          canReadConfig && configResponse.isOk ? unwrapConfig(getData(configResponse)).Playback.AnimeThemesWebmMode || "off" : localStorage.getItem("player-playback-mode") || treePayload?.playbackMode || "off";
        playerModeBtn.setAttribute("data-mode", mode);
        updatePlaybackTooltip(playerModeBtn);
      }
      syncVolumeUI();
    })();
  }
  // #endregion
})();
