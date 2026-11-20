/**
 * @file player.js
 * @description Logic for the Shoko Relay stand-alone AnimeThemes VFS video player.
 */
(() => {
  const { base, shokoBase, configUrl, el, fetchJson, unwrapConfig, setValueByPath, openModal, updatePlaybackTooltip, saveSettings, getData, initSearchInteractions } = window._sr;

  // DOM Elements - UI Indicators
  /** @type {HTMLElement} */ const playerTime = el("time-display");
  /** @type {HTMLElement} */ const playerVolText = el("volume-text");
  /** @type {HTMLElement} */ const playerFullscreenBtn = el("fullscreen");
  /** @type {HTMLElement} */ const playerCloseBtn = el("close-session");
  /** @type {HTMLElement} */ const playerMuteBtn = el("volume-icon");
  /** @type {HTMLElement} */ const playerContainer = el("video").parentElement;
  /** @type {HTMLVideoElement} */ const playerVideo = el("video");
  /** @type {HTMLElement} */ const playerVideoBtn = el("video-toggle");

  // DOM Elements - Navigation & Search
  /** @type {HTMLElement} */ const playerTree = el("tree");
  /** @type {HTMLInputElement} */ const uiFilter = el("filter");
  /** @type {HTMLElement} */ const uiFilterClear = el("filter-clear");
  /** @type {HTMLElement} */ const playerNextBtn = el("next");
  /** @type {HTMLElement} */ const playerModeBtn = el("mode");
  /** @type {HTMLElement} */ const playerSortBtn = el("sort-toggle");

  // DOM Elements - Metadata Header
  /** @type {HTMLElement} */ const playerTitle = el("title");
  /** @type {HTMLElement} */ const playerAnime = el("anime");
  /** @type {HTMLElement} */ const playerAnidb = el("anidb");
  /** @type {HTMLElement} */ const playerNowPlayingFav = el("now-playing-fav");
  /** @type {HTMLElement} */ const playerLocateBtn = el("locate-current");

  // DOM Elements - Progress Bar
  /** @type {HTMLElement} */ const playerFill = el("player-progress-fill");
  /** @type {HTMLElement} */ const playerTrack = el("player-progress-track");

  // DOM Elements - Help
  /** @type {HTMLElement} */ const helpModal = el("player-help-modal");
  /** @type {HTMLElement} */ const helpOpenBtn = el("help-open");

  /** @type {Function|null} Reference to the active modal close callback to support toggle hotkeys. */
  let activeModalClose = null;
  /** @type {Object[]} The hierarchical list of WebM themes. */
  let webmTreeData = [];
  /** @type {string} The relative VFS path of the currently playing theme. */
  let currentWebmPath = "";
  /** @type {Set<number>} Set of video IDs marked as favourite. */
  let favourites = new Set();
  /** @type {number|null} Reference to the active requestAnimationFrame for the progress bar. */
  let progressRaf;
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
   * @param {MouseEvent} ev - The mouse movement event.
   * @returns {void}
   */
  const resetIdle = (ev) => {
    playerContainer.classList.remove("idle");
    clearTimeout(idleTimer);
    if (!ev.target.closest(".ui-btn, #player-progress-track, #volume-text")) idleTimer = setTimeout(() => playerContainer.classList.add("idle"), 2000);
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
   * @param {number} seconds - The time in seconds.
   * @returns {string} The formatted duration string.
   */
  const formatTime = (seconds) => {
    if (isNaN(seconds)) return "0:00";
    const mins = Math.floor(seconds / 60);
    const secs = Math.floor(seconds % 60);
    return `${mins}:${secs < 10 ? "0" : ""}${secs}`;
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
   * Synchronizes the sort button UI with the active folder view state.
   * @returns {void}
   */
  const syncSortUI = () => {
    if (!playerSortBtn) return;
    playerSortBtn.setAttribute("data-sort", useFolderView ? "source" : "series");
    playerSortBtn.title = useFolderView ? "Sort by Collected Series" : "Sort by Source Folder";
  };

  /**
   * Decodes unicode escape sequences back into their actual Unicode characters.
   * @param {string} str - The string containing escape sequences.
   * @returns {string} A decoded string.
   */
  const decodeUnicode = (str) => (str || "").replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)));

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
   * @returns {Object[]} The filtered list of theme entries.
   */
  const getFilteredItems = () => {
    const rawFt = (uiFilter?.value || "").toLowerCase().trim();
    if (!rawFt) return webmTreeData;

    const words = rawFt.split(/\s+/).filter((word) => word.length > 0);
    const isFavQuery = words.includes("favs");
    const tagExclusions = [];
    const tagInclusions = [];
    const searchTerms = [];

    words.forEach((word) => {
      if (word === "favs") return;
      word.startsWith("-") ? tagExclusions.push(word.substring(1)) : word.startsWith("+") ? tagInclusions.push(word.substring(1)) : searchTerms.push(word);
    });

    /**
     * Checks if a theme item possesses a given attribute tag without object allocation.
     * @param {string} tag - The tag identifier to test.
     * @param {Object} item - The theme item to evaluate.
     * @returns {boolean} True if the item matches the tag.
     */
    const matchesTag = (tag, item) => {
      switch (tag) {
        case "spoil":
          return !!item.spoiler;
        case "nsfw":
          return !!item.nsfw;
        case "lyrics":
          return !!item.lyrics;
        case "subs":
          return !!item.subs;
        case "uncen":
          return !!item.uncen;
        case "nc":
          return !!item.nc;
        case "trans":
          return !!item.trans;
        case "over":
          return !!item.over;
        default:
          return false;
      }
    };

    return webmTreeData.filter((item) => {
      if (isFavQuery && (!item.videoId || !favourites.has(item.videoId))) return false;
      if (tagExclusions.some((tag) => matchesTag(tag, item)) || tagInclusions.some((tag) => !matchesTag(tag, item))) return false;
      return searchTerms.every((word) => item._searchIndex.includes(word));
    });
  };

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
    el("player")?.classList.toggle("fav-disabled", !allowed);
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
   * @param {HTMLElement} [heartEl] - The optional heart icon element inside the tree to toggle.
   * @returns {Promise<void>}
   */
  async function toggleFavourite(videoId, heartEl) {
    if (!canModifyServerState()) return;
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
      const isCurrentVideo = webmTreeData.find((item) => item.path === currentWebmPath)?.videoId === videoId;
      const treeHeart = isCurrentVideo ? playerTree.querySelector(`.leaf[data-path="${CSS.escape(currentWebmPath)}"] .fav-icon`) : null;
      if (treeHeart) treeHeart.classList.toggle("favourited", isFav);

      if (playerNowPlayingFav && isCurrentVideo) playerNowPlayingFav.classList.toggle("favourited", isFav);
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

    const rootUl = document.createElement("ul");
    rootUl.className = "tree";
    const hasFilter = !!uiFilter?.value.trim();

    /**
     * Helper to construct a standard file node in the tree.
     * @param {Object} item - The theme entry metadata.
     * @param {string} displayName - The text to display for the file.
     * @returns {HTMLLIElement} The completed file list item node.
     */
    const createFileLeaf = (item, displayName) => {
      const li = document.createElement("li");
      const leaf = document.createElement("div");
      leaf.className = `leaf ${item.path === currentWebmPath ? "active" : ""}`;
      leaf.dataset.path = item.path;

      const favAllowed = canModifyServerState();
      const heart = document.createElement("span");
      heart.className = `fav-icon ${favourites.has(item.videoId) ? "favourited" : ""}`;
      heart.textContent = "❤";
      heart.setAttribute("aria-disabled", String(!favAllowed));
      if (!favAllowed) heart.title = "Authentication Required to Modify Favourites";
      heart.onclick = (ev) => {
        ev.stopPropagation();
        if (canModifyServerState()) toggleFavourite(item.videoId, heart);
      };

      const text = document.createElement("span");
      text.textContent = displayName;
      text.title = displayName;
      text.dataset.tooltipOverflowOnly = "true";

      if (item.group === MISSING_LABEL) text.classList.add("tree-missing");

      leaf.onclick = () => {
        resetNavigationHistory();
        playFile(item.path);
      };
      leaf.append(heart, text);
      li.appendChild(leaf);
      return li;
    };

    /**
     * Helper to construct a standard folder node in the tree with deferred child rendering.
     * @param {string} name - The display name for the folder.
     * @param {Function} renderFn - Callback that appends child list-item elements when expanded.
     * @param {boolean} isOpen - If true, forces children to render immediately.
     * @param {boolean} [isMissing=false] - Whether to apply missing collection styling to the title.
     * @param {string} [extraHtml=""] - Additional HTML to append to the folder title.
     * @returns {HTMLLIElement} The completed folder list item node.
     */
    const createFolderNode = (name, renderFn, isOpen, isMissing = false, extraHtml = "") => {
      const li = document.createElement("li");
      const ul = document.createElement("ul");
      const details = window._sr.createLazyDetails(name, ul, renderFn, isOpen, extraHtml);
      const summary = details.querySelector("summary");
      if (isMissing) summary.querySelector(".vfs-title")?.classList.add("tree-missing");
      li.appendChild(details);
      return li;
    };

    /**
     * Generates external links for a given series.
     * @param {number} seriesId - The Shoko Series ID.
     * @param {number} anidbId - The AniDB ID.
     * @returns {string} The concatenated anchor HTML.
     */
    const buildSeriesLinks = (seriesId, anidbId) => {
      let html = "";
      if (seriesId) html += `<a href="${shokoBase}/webui/collection/series/${seriesId}/overview" class="vfs-link small" target="_blank" rel="noopener noreferrer">[${seriesId}]</a>`;
      if (anidbId) html += `<a href="https://anidb.net/a${anidbId}" class="vfs-link small" target="_blank" rel="noopener noreferrer">[a${anidbId}]</a>`;
      return html;
    };

    if (useFolderView) {
      // Source Folder Structure View (Year -> Season -> Series -> Song)
      const rootNode = { folders: new Map(), files: [] };
      items.forEach((item) => {
        const parts = (item.relPath || `Unknown/${item.file}`).replace(/\\/g, "/").split("/").filter(Boolean);
        item._originalName = parts.pop(); // Remove original filename
        parts.push(item.series); // Group by series within the source folders
        let current = rootNode;
        parts.forEach((part) => {
          if (!current.folders.has(part)) current.folders.set(part, { folders: new Map(), files: [] });
          current = current.folders.get(part);
        });
        current.isSeries = true;
        current.seriesId = item.seriesId;
        current.anidbId = item.anidbId;
        if (item.group === MISSING_LABEL) current.isMissing = true;

        current.files.push(item);
      });

      const seasonOrder = { winter: 1, spring: 2, summer: 3, fall: 4 };
      const getSeasonWeight = (name) => seasonOrder[name.toLowerCase()] || 99;

      const renderFolderTree = (node, isRoot, container) => {
        const sortedFolders = [...node.folders.keys()].sort((left, right) => {
          const weightLeft = getSeasonWeight(left);
          const weightRight = getSeasonWeight(right);
          if (weightLeft !== 99 || weightRight !== 99) return weightLeft !== weightRight ? weightLeft - weightRight : left.localeCompare(right, undefined, { numeric: true });
          return left.localeCompare(right, undefined, { numeric: true });
        });

        sortedFolders.forEach((folderName) => {
          const childNode = node.folders.get(folderName);
          const extraHtml = childNode.isSeries ? buildSeriesLinks(childNode.seriesId, childNode.anidbId) : "";
          container.appendChild(createFolderNode(folderName, (childContainer) => renderFolderTree(childNode, false, childContainer), hasFilter, childNode.isMissing, extraHtml));
        });

        const sortedFiles = node.files.sort((left, right) => left.file.localeCompare(right.file, undefined, { numeric: true }));
        sortedFiles.forEach((file) => container.appendChild(createFileLeaf(file, decodeUnicode(file.file))));
      };

      renderFolderTree(rootNode, true, rootUl);
    } else {
      // Standard Metadata View (Group -> Series -> File)
      const groups = new Map();
      items.forEach((item) => {
        if (!groups.has(item.group)) groups.set(item.group, new Map());
        const seriesMap = groups.get(item.group);
        if (!seriesMap.has(item.series)) seriesMap.set(item.series, []);
        seriesMap.get(item.series).push(item);
      });

      const sortedGroupNames = [...groups.keys()].sort((leftGroup, rightGroup) => {
        if (leftGroup === MISSING_LABEL && rightGroup !== MISSING_LABEL) return -1;
        if (rightGroup === MISSING_LABEL && leftGroup !== MISSING_LABEL) return 1;
        return leftGroup.localeCompare(rightGroup);
      });

      sortedGroupNames.forEach((groupName) => {
        const seriesMap = groups.get(groupName);
        const isMissing = groupName === MISSING_LABEL;
        const seriesKeys = [...seriesMap.keys()].sort((leftName, rightName) => leftName.localeCompare(rightName));

        const renderSeriesFolders = (container) => {
          seriesKeys.forEach((seriesName) => {
            const seriesFiles = seriesMap.get(seriesName);
            const firstFile = seriesFiles[0];
            const extraHtml = buildSeriesLinks(firstFile.seriesId, firstFile.anidbId);
            const renderFiles = (fileContainer) => seriesFiles.forEach((file) => fileContainer.appendChild(createFileLeaf(file, decodeUnicode(file.file))));
            container.appendChild(createFolderNode(seriesName, renderFiles, hasFilter, isMissing, extraHtml));
          });
        };

        if (seriesKeys.length === 1 && !isMissing) {
          const seriesName = seriesKeys[0];
          const seriesFiles = seriesMap.get(seriesName);
          const firstFile = seriesFiles[0];
          const extraHtml = buildSeriesLinks(firstFile.seriesId, firstFile.anidbId);
          const renderFiles = (container) => seriesFiles.forEach((file) => container.appendChild(createFileLeaf(file, decodeUnicode(file.file))));
          rootUl.appendChild(createFolderNode(seriesName, renderFiles, hasFilter, isMissing, extraHtml));
        } else {
          rootUl.appendChild(createFolderNode(groupName, renderSeriesFolders, hasFilter, isMissing));
        }
      });
    }

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

    // Collapse all open folders to reset the tree state before locating
    playerTree.querySelectorAll("details[open]").forEach((details) => (details.open = false));

    const getSummaryTitle = (summary) => (summary ? summary.dataset.tooltipText || summary.getAttribute("title") || "" : "");
    const pathSegments = [];

    if (useFolderView) {
      const parts = (currentItem.relPath || `Unknown/${currentItem.file}`).replace(/\\/g, "/").split("/").filter(Boolean);
      parts.pop();
      parts.push(currentItem.series);
      pathSegments.push(...parts);
    } else {
      const groupSeriesCount = new Set(
        getFilteredItems()
          .filter((entry) => entry.group === currentItem.group)
          .map((entry) => entry.series),
      ).size;
      if (groupSeriesCount > 1 || currentItem.group === MISSING_LABEL) pathSegments.push(currentItem.group);
      pathSegments.push(currentItem.series);
    }

    let currentScope = playerTree;
    pathSegments.forEach((segment) => {
      const details = [...currentScope.querySelectorAll("details")].find((detailsEl) => getSummaryTitle(detailsEl.querySelector("summary")) === segment);
      if (details) {
        details.open = true;
        if (!details.dataset.rendered) details.dispatchEvent(new Event("toggle"));
        currentScope = details;
      }
    });

    const leaf = playerTree.querySelector(`.leaf[data-path="${CSS.escape(currentWebmPath)}"]`);
    if (leaf) {
      playerTree.querySelectorAll(".leaf").forEach((leafEl) => leafEl.classList.toggle("active", leafEl.dataset.path === currentWebmPath));
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
        const verifyRes = await fetch(`${base}/auth/verify?player=true`);
        if ((verifyRes.status === 401 || verifyRes.status === 403) && (await window._sr.ensureAuth?.({ dismissable: false })) && currentWebmPath === path) {
          playerVideo.src = streamUrl;
          playerVideo.play().catch(() => {});
        }
      } catch {}
    };
    playerVideo.src = streamUrl;
    playerVideo.play().catch(() => {});

    const item = webmTreeData.find((themeItem) => themeItem.path === path);
    if (playerTitle) playerTitle.textContent = playerTitle.title = item ? decodeUnicode(item.file) : "Video Player";

    if (playerNowPlayingFav && item) {
      playerNowPlayingFav.style.pointerEvents = "auto";
      playerNowPlayingFav.classList.toggle("favourited", favourites.has(item.videoId));
      playerNowPlayingFav.onclick = (ev) => {
        ev.stopPropagation();
        if (canModifyServerState()) toggleFavourite(item.videoId, playerNowPlayingFav);
      };
    }
    if (playerLocateBtn && item) playerLocateBtn.style.pointerEvents = "auto";

    if (playerAnime) {
      playerAnime.textContent = playerAnime.title = item ? item.series : "Select a theme to begin...";
      const isMissing = item?.group === MISSING_LABEL;
      playerAnime.classList.toggle("tree-missing", isMissing);

      if (item?.seriesId) {
        playerAnime.href = `${shokoBase}/webui/collection/series/${item.seriesId}/overview`;
        playerAnime.target = "_blank";
        playerAnime.rel = "noopener noreferrer";
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
      playerAnidb.target = "_blank";
      playerAnidb.rel = "noopener noreferrer";
      playerAnidb.style.pointerEvents = item && item.anidbId ? "auto" : "none";
    }
    playerTree?.querySelectorAll(".leaf").forEach((leafEl) => leafEl.classList.toggle("active", leafEl.dataset.path === path));
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
      const currentIdx = items.findIndex((item) => item.path === currentWebmPath);
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

    if (!canModifyServerState()) {
      localStorage.setItem("player-playback-mode", next);
      return;
    }

    const res = await fetchJson(configUrl);
    if (res.ok) {
      const cfg = unwrapConfig(getData(res));
      setValueByPath(cfg, "Playback.AnimeThemesWebmMode", next);
      await saveSettings(cfg);
    }
  }
  // #endregion

  // #region Initialization
  if (playerSortBtn) {
    syncSortUI();
    playerSortBtn.onclick = toggleFolderView;
  }

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
      const playbackMode = playerModeBtn?.getAttribute("data-mode");
      setTimeout(() => {
        if (playbackMode === "loop") {
          playerVideo.currentTime = 0;
          playerVideo.play().catch(() => {});
        } else if (playbackMode === "shuffle" || playbackMode === "next") {
          playMove(playbackMode === "shuffle", 1);
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
      const updateSeek = (ev) => {
        if (!playerVideo.duration) return;
        const rect = playerTrack.getBoundingClientRect();
        const pos = (ev.clientX - rect.left) / rect.width;
        playerVideo.currentTime = Math.max(0, Math.min(1, pos)) * playerVideo.duration;
        if (playerVideo.paused) syncProgressUI();
      };
      playerTrack.onmousedown = (ev) => {
        if (ev.button === 1 && playerVideo.src) {
          ev.preventDefault();
          playerVideo.paused ? playerVideo.play() : playerVideo.pause();
          return;
        }
        if (ev.button === 0) {
          updateSeek(ev);
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

    /**
     * Adjusts the video player volume by a relative delta and unmutes when increasing from mute.
     * @param {number} delta - Volume change step in the range [-1, 1].
     * @returns {void}
     */
    const stepVolume = (delta) => {
      if (playerVideo.muted && delta > 0) playerVideo.muted = false;
      playerVideo.volume = Math.max(0, Math.min(1, Math.round((playerVideo.volume + delta) * 100) / 100));
    };

    if (playerMuteBtn) {
      playerMuteBtn.onclick = (ev) => {
        ev.stopPropagation();
        if (playerVideo.src) playerVideo.muted = !playerVideo.muted;
      };
      playerMuteBtn.addEventListener(
        "wheel",
        (ev) => {
          ev.preventDefault();
          ev.stopPropagation();
          resetIdle(ev);
          stepVolume(ev.deltaY < 0 ? 0.05 : -0.05);
        },
        { passive: false },
      );
    }

    if (playerVolText) {
      playerVolText.onclick = (ev) => ev.stopPropagation();
      playerVolText.addEventListener(
        "wheel",
        (ev) => {
          ev.preventDefault();
          ev.stopPropagation();
          resetIdle(ev);
          stepVolume(ev.deltaY < 0 ? 0.05 : -0.05);
        },
        { passive: false },
      );
    }

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
        playerTree?.querySelectorAll(".leaf").forEach((leafEl) => leafEl.classList.remove("active"));
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
      playerVideoBtn.onclick = (ev) => {
        ev.stopPropagation();
        toggleVideo();
      };
    }

    window.addEventListener("keydown", (ev) => {
      if (document.activeElement.tagName === "INPUT" || document.activeElement.tagName === "TEXTAREA" || document.querySelector(".modal-overlay.open")) return;
      const key = ev.key.toLowerCase();
      const handledKeys = [" ", "k", "arrowleft", "arrowright", "j", "l", ",", ".", "b", "n", ";", "'", "arrowdown", "arrowup", "m", "f", "v", "r", "g", "h", "?", "q"];
      if (!handledKeys.includes(key)) return;
      ev.preventDefault();
      const isShuffle = playerModeBtn?.getAttribute("data-mode") === "shuffle";
      // prettier-ignore
      switch (key) {
        case " ": case "k": if (playerVideo.src) { if (playerVideo.ended) { playerVideo.currentTime = 0; playerVideo.play(); } else { playerVideo.paused ? playerVideo.play() : playerVideo.pause(); } } else playMove(isShuffle, 1); break;
        case "arrowleft": if (playerVideo.src) { playerVideo.currentTime -= 5; syncProgressUI(); } break;
        case "arrowright": if (playerVideo.src) { playerVideo.currentTime += 5; syncProgressUI(); } break;
        case "j": if (playerVideo.src) { playerVideo.currentTime -= 10; syncProgressUI(); } break;
        case "l": if (playerVideo.src) { playerVideo.currentTime += 10; syncProgressUI(); } break;
        case ",": if (playerVideo.src) { playerVideo.pause(); playerVideo.currentTime = Math.max(0, playerVideo.currentTime - 1 / 24); } break;
        case ".": if (playerVideo.src) { playerVideo.pause(); playerVideo.currentTime = Math.min(playerVideo.duration, playerVideo.currentTime + 1 / 24); } break;
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
        case "h": const favItem = webmTreeData.find((item) => item.path === currentWebmPath); if (favItem) toggleFavourite(favItem.videoId); break;
        case "?": toggleHelpModal(); break;
        case "q": playerCloseBtn?.click(); break;
      }
    });

    (async () => {
      await window._sr.authReady;
      syncFavouriteControls();
      if (playerTree) playerTree.innerHTML = '<div class="placeholder"><svg class="loading-spinner"><use href="img/icons.svg#loading"></use></svg><div>Loading video tree...</div></div>';
      const canReadConfig = canModifyServerState();
      const [treeRes, cfgRes, favRes] = await Promise.all([
        fetchJson(base + "/animethemes/webm/tree"),
        canReadConfig ? fetchJson(configUrl) : Promise.resolve({ ok: false }),
        fetchJson(base + "/animethemes/webm/favourites"),
      ]);
      if (favRes.ok) favourites = new Set(getData(favRes) || []);
      const treePayload = getData(treeRes);
      if (treeRes.ok) {
        webmTreeData = (treePayload?.items || [])
          .map((item) => {
            item._searchIndex = `${item.group} ${item.series} ${decodeUnicode(item.file)} ${item.relPath || ""}`.toLowerCase().replace(/[\u200B\u200A]/g, "");
            return item;
          })
          .sort((left, right) => {
            if (left.group === MISSING_LABEL && right.group !== MISSING_LABEL) return -1;
            if (right.group === MISSING_LABEL && left.group !== MISSING_LABEL) return 1;

            const groupComp = left.group.localeCompare(right.group);
            if (groupComp !== 0) return groupComp;
            const seriesComp = left.series.localeCompare(right.series);
            if (seriesComp !== 0) return seriesComp;
            return left.file.localeCompare(right.file);
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
      if (playerModeBtn) {
        const mode = canReadConfig && cfgRes.ok ? unwrapConfig(getData(cfgRes)).Playback.AnimeThemesWebmMode || "off" : localStorage.getItem("player-playback-mode") || treePayload?.playbackMode || "off";
        playerModeBtn.setAttribute("data-mode", mode);
        updatePlaybackTooltip(playerModeBtn);
      }
      syncVolumeUI();
    })();
  }
  // #endregion
})();
