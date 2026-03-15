/**
 * @file player.js
 * @description Dedicated logic for the Shoko Relay stand-alone AnimeThemes VFS video player.
 */
(() => {
  const { base, el, fetchJson, unwrapConfig, setValueByPath, openModal, updatePlaybackTooltip } = window._sr;

  const playerTime = el("time-display");
  const playerVolText = el("volume-text");
  const playerFullscreenBtn = el("fullscreen");
  const playerMuteBtn = el("volume-mute-btn");
  const playerContainer = el("video").parentElement;
  const playerVideo = el("video");
  const playerTree = el("tree");
  const playerFilter = el("filter");
  const playerFilterClear = el("filter-clear");
  const playerNextBtn = el("next");
  const playerModeBtn = el("mode");
  const playerTitle = el("title");
  const playerAnime = el("anime");
  const playerFill = el("player-progress-fill");
  const playerTrack = el("player-progress-track");
  const playerNowPlayingFav = el("now-playing-fav");
  const playerLocateBtn = el("locate-current");
  const helpModal = el("help-modal");
  const helpOpenBtn = el("help-open");

  /** @type {Array<{group: string, series: string, file: string, path: string, videoId: number, _searchIndex: string}>} */
  let webmTreeData = [];
  let currentWebmPath = "";
  let favourites = new Set();

  /** @type {Set<string>} */
  const shuffleHistory = new Set();
  let lastFilterValue = "";

  // #region Utilities
  /**
   * Converts seconds to M:SS format
   * @param {number} seconds
   * @returns {string}
   */
  const formatTime = (seconds) => {
    if (isNaN(seconds)) return "0:00";
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s < 10 ? "0" : ""}${s}`;
  };

  /** Synchronizes the volume percentage and mute icon classes */
  const syncVolumeUI = () => {
    if (!playerVideo) return;
    const vol = playerVideo.volume;
    const isMuted = playerVideo.muted || vol === 0;

    if (playerVolText) {
      playerVolText.textContent = isMuted ? "Muted" : `${Math.round(vol * 100)}%`;
    }
    if (playerMuteBtn) {
      playerMuteBtn.title = isMuted ? "Unmute" : "Mute";
    }
    let level = "mute";
    if (!isMuted) {
      if (vol > 0.66) level = "high";
      else if (vol > 0.33) level = "med";
      else level = "low";
    }
    if (playerContainer) {
      playerContainer.dataset.volumeLevel = level;
    }
  };

  /**
   * Decode unicode escape sequences in a string.
   * @param {string} s - The encoded string.
   * @returns {string} The decoded string.
   */
  const decodeUnicode = (s) => s.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)));

  /**
   * Get next or previous mode in sequence.
   * @param {string} current - Current mode string.
   * @param {string[]} modes - Array of modes to cycle through.
   * @param {number} [dir=1] - 1 for forward, -1 for backward.
   * @returns {string} The next mode string.
   */
  const cycleMode = (current, modes, dir = 1) => {
    const idx = modes.indexOf(current);
    return modes[(idx + dir + modes.length) % modes.length];
  };

  /**
   * Debounce helper to prevent lag during rapid typing.
   * @param {Function} fn - The function to debounce.
   * @param {number} ms - The debounce delay in milliseconds.
   * @returns {Function} The debounced function.
   */
  const debounce = (fn, ms) => {
    let timeout;
    return (...args) => {
      clearTimeout(timeout);
      timeout = setTimeout(() => fn.apply(this, args), ms);
    };
  };

  /**
   * Filters the tree data based on query words. Each word in the query must be found in either the group, series, or filename.
   * @returns {Object[]} The filtered list of items.
   */
  const getFilteredItems = () => {
    const rawFt = (playerFilter?.value || "").toLowerCase().trim();
    if (!rawFt) return webmTreeData;

    const words = rawFt.split(/\s+/);
    const isFavQuery = words.includes("favs");

    const tagExclusions = [];
    const tagInclusions = [];
    const searchTerms = [];

    // Categorize search tokens
    words.forEach((w) => {
      if (w === "favs") return;
      if (w.startsWith("-")) tagExclusions.push(w.substring(1));
      else if (w.startsWith("+")) tagInclusions.push(w.substring(1));
      else searchTerms.push(w);
    });

    const matchesTag = (tag, item) => {
      switch (tag) {
        case "spoil":
          return item.spoiler;
        case "nsfw":
          return item.nsfw;
        case "lyrics":
          return item.lyrics;
        case "subs":
          return item.subs;
        case "uncen":
          return item.uncen;
        case "nc":
          return item.nc;
        case "trans":
          return item.trans;
        case "over":
          return item.over;
        default:
          return false;
      }
    };

    return webmTreeData.filter((item) => {
      if (isFavQuery && (!item.videoId || !favourites.has(item.videoId))) return false;
      for (const tag of tagExclusions) {
        if (matchesTag(tag, item)) return false;
      }
      for (const tag of tagInclusions) {
        if (!matchesTag(tag, item)) return false;
      }
      return searchTerms.every((word) => item._searchIndex.includes(word));
    });
  };
  // #endregion

  // #region Tree
  /**
   * Toggles the favourite status of a videoId on the server and updates local state.
   * @param {number} videoId - The AnimeThemes video ID.
   * @param {HTMLElement} [heartEl] - The heart icon element.
   * @returns {Promise<void>}
   */
  async function toggleFavourite(videoId, heartEl) {
    if (!videoId || videoId <= 0) {
      console.warn("Cannot favourite: VideoId is missing. Run 'Build Mapping File' on dashboard.");
      return;
    }

    const res = await fetchJson(base + "/animethemes/webm/favourites", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(videoId),
    });

    if (res.ok) {
      const isFav = res.data.isFavourite;
      if (isFav) favourites.add(videoId);
      else favourites.delete(videoId);

      // Sync the heart that was clicked (if any)
      if (heartEl) heartEl.classList.toggle("favourited", isFav);

      // Sync the "Now Playing" header heart if this video is currently playing
      const currentItem = webmTreeData.find((i) => i.path === currentWebmPath);
      if (currentItem && currentItem.videoId === videoId) {
        playerNowPlayingFav?.classList.toggle("favourited", isFav);
      }

      // Sync the tree leaf icon (especially important if hotkey was used)
      const treeHeart = playerTree.querySelector(`.leaf[data-path="${CSS.escape(currentWebmPath)}"] .fav-icon`);
      if (treeHeart) treeHeart.classList.toggle("favourited", isFav);

      if (playerFilter?.value.toLowerCase().includes("favs")) {
        renderTree(getFilteredItems());
      }
    }
  }

  /**
   * Renders the VFS items into a hierarchical tree.
   * @param {Object[]} items - The filtered items to display.
   */
  function renderTree(items) {
    if (!playerTree) return;
    playerTree.innerHTML = "";
    if (!items.length) {
      playerTree.innerHTML = `<div class="placeholder">${playerFilter?.value ? "No matches" : "Generate AnimeThemes VFS to populate"}</div>`;
      return;
    }

    const groups = new Map();
    items.forEach((i) => {
      if (!groups.has(i.group)) groups.set(i.group, new Map());
      const sMap = groups.get(i.group);
      if (!sMap.has(i.series)) sMap.set(i.series, []);
      sMap.get(i.series).push(i);
    });

    const makeNode = (name, children, isOpen) => {
      const li = document.createElement("li"),
        det = document.createElement("details"),
        sum = document.createElement("summary"),
        ul = document.createElement("ul");
      det.open = isOpen;
      sum.title = name;
      sum.dataset.tooltipOverflowOnly = "true";
      sum.innerHTML = `<span class="tree-icon expand"></span><span class="tree-icon collapse"></span>${name}`;
      det.append(sum, ul);
      children.forEach((c) => ul.appendChild(c));
      li.appendChild(det);
      return li;
    };

    const rootUl = document.createElement("ul");
    rootUl.className = "tree";
    [...groups.keys()]
      .sort((a, b) => a.localeCompare(b))
      .forEach((gName) => {
        const sMap = groups.get(gName),
          sKeys = [...sMap.keys()].sort((a, b) => a.localeCompare(b)),
          ft = (playerFilter?.value || "").trim();

        const sNodes = sKeys.map((sName) => {
          const files = sMap.get(sName).map((f) => {
            const li = document.createElement("li"),
              leaf = document.createElement("div"),
              name = decodeUnicode(f.file);
            leaf.className = `leaf ${f.path === currentWebmPath ? "active" : ""}`;
            leaf.dataset.path = f.path;

            const heart = document.createElement("span");
            const isFavourited = favourites.has(f.videoId);
            heart.className = `fav-icon ${isFavourited ? "favourited" : ""}`;
            heart.textContent = "❤";
            heart.onclick = (e) => {
              e.stopPropagation();
              toggleFavourite(f.videoId, heart);
            };

            const text = document.createElement("span");
            text.textContent = name;
            text.title = name;
            text.dataset.tooltipOverflowOnly = "true";
            text.style.overflow = "hidden";
            text.style.textOverflow = "ellipsis";

            leaf.onclick = () => playFile(f.path);
            leaf.append(heart, text);
            li.appendChild(leaf);
            return li;
          });
          return makeNode(sName, files, !!ft);
        });

        rootUl.appendChild(sKeys.length === 1 ? sNodes[0] : makeNode(gName, sNodes, !!ft));
      });
    playerTree.appendChild(rootUl);
  }
  // #endregion

  // #region Player
  /** Expands all parent details elements of the currently playing theme and scrolls it into view. */
  function locateCurrentInTree() {
    if (!currentWebmPath) return;
    const leaf = playerTree.querySelector(`.leaf[data-path="${CSS.escape(currentWebmPath)}"]`);
    if (!leaf) return;

    // Expand all parent <details> elements to make the leaf visible
    let parent = leaf.closest("details");
    while (parent) {
      parent.open = true;
      parent = parent.parentElement.closest("details");
    }

    // Scroll the tree container to the element
    leaf.scrollIntoView({ behavior: "smooth", block: "center" });
  }

  /** Toggles fullscreen mode on the video container. */
  function toggleFullscreen() {
    if (!document.fullscreenElement) {
      playerContainer.requestFullscreen().catch(() => {});
    } else {
      document.exitFullscreen();
    }
  }

  /**
   * Sets the video source and starts playback.
   * @param {string} path - The relative VFS path to the WebM file.
   */
  function playFile(path) {
    if (!playerVideo) return;
    currentWebmPath = path;
    playerVideo.src = `${base}/animethemes/webm/stream?path=${encodeURIComponent(path)}`;
    playerVideo.play().catch(() => {});

    const item = webmTreeData.find((i) => i.path === path);
    const themeName = item ? decodeUnicode(item.file) : "Video Player";
    const animeName = item ? item.series : "Select a theme to begin...";

    if (playerTitle) playerTitle.textContent = playerTitle.title = themeName;

    // Header Favorite Heart Logic
    if (item) {
      if (playerLocateBtn) playerLocateBtn.style.visibility = "visible";

      if (playerNowPlayingFav) {
        playerNowPlayingFav.style.visibility = "visible";
        playerNowPlayingFav.classList.toggle("favourited", favourites.has(item.videoId));
        playerNowPlayingFav.onclick = (e) => {
          e.stopPropagation();
          toggleFavourite(item.videoId, playerNowPlayingFav);
        };
      }
    }

    if (playerAnime) {
      playerAnime.textContent = playerAnime.title = animeName;
      if (item && item.seriesId) {
        const shokoBase = base.split("/api/")[0];
        playerAnime.href = `${shokoBase}/webui/collection/series/${item.seriesId}/overview`;
        playerAnime.style.pointerEvents = "auto";
      } else {
        playerAnime.href = "#";
        playerAnime.style.pointerEvents = "none";
      }
    }

    playerTree?.querySelectorAll(".leaf").forEach((el) => el.classList.toggle("active", el.dataset.path === path));
  }

  /**
   * Advances or reverses playback based on mode.
   * @param {boolean} isShuffle - Whether shuffle mode is active.
   * @param {number} [direction=1] - 1 for next, -1 for previous.
   */
  function playMove(isShuffle = false, direction = 1) {
    const items = getFilteredItems();
    if (items.length === 0) return;

    const currentFilter = (playerFilter?.value || "").trim();
    if (currentFilter !== lastFilterValue) {
      shuffleHistory.clear();
      lastFilterValue = currentFilter;
    }

    let idx;
    if (isShuffle) {
      let pool = items.filter((item) => !shuffleHistory.has(item.path));
      if (pool.length === 0) {
        shuffleHistory.clear();
        pool = items;
      }
      if (pool.length > 1) pool = pool.filter((item) => item.path !== currentWebmPath);
      const selectedItem = pool[Math.floor(Math.random() * pool.length)];
      idx = items.indexOf(selectedItem);
      shuffleHistory.add(selectedItem.path);
    } else {
      shuffleHistory.clear();
      const currentIdx = items.findIndex((i) => i.path === currentWebmPath);
      idx = (currentIdx + direction + items.length) % items.length;
    }
    playFile(items[idx].path);
  }

  /** Synchronizes the Play/Next button appearance and progress bar with the video playback state. */
  function syncPlaybackUI() {
    if (playerNextBtn) {
      const isPlaying = playerVideo && !playerVideo.paused && !playerVideo.ended;
      playerNextBtn.setAttribute("data-state", isPlaying ? "playing" : "idle");
      // Use the centralized script.js tooltip update logic
      updatePlaybackTooltip(playerNextBtn);
    }
  }

  /**
   * Updates and persists the playback mode.
   * @param {number} [direction=1] - 1 for forward, -1 for backward.
   * @returns {Promise<void>}
   */
  async function updateMode(direction = 1) {
    if (!playerModeBtn) return;
    const modes = ["loop", "shuffle", "next", "off"];
    const current = playerModeBtn.getAttribute("data-mode") || "off";
    const next = cycleMode(current, modes, direction);

    playerModeBtn.setAttribute("data-mode", next);
    // Refresh tooltip label
    updatePlaybackTooltip(playerModeBtn);
    shuffleHistory.clear();

    const res = await fetchJson(base + "/config");
    if (res.ok) {
      const cfg = unwrapConfig(res.data);
      setValueByPath(cfg, "Playback.AnimeThemesWebmMode", next);
      await fetchJson(base + "/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(cfg) });
    }
  }
  // #endregion

  // #region Initialization
  if (playerVideo) {
    // Sync UI on video state changes
    ["play", "pause", "ended", "loadstart"].forEach((ev) => playerVideo.addEventListener(ev, syncPlaybackUI));

    /** Handle progress bar updates */
    playerVideo.ontimeupdate = () => {
      if (playerFill && playerVideo.duration) {
        playerFill.style.width = (playerVideo.currentTime / playerVideo.duration) * 100 + "%";
      }
    };

    /** Toggle play/pause when clicking the video element directly */
    playerVideo.onclick = () => {
      if (!playerVideo.src) return;
      playerVideo.paused ? playerVideo.play() : playerVideo.pause();
    };

    /** Handle progress bar clicking/seeking */
    if (playerTrack) {
      playerTrack.onclick = (e) => {
        if (playerVideo.duration) {
          playerVideo.currentTime = ((e.clientX - playerTrack.getBoundingClientRect().left) / playerTrack.offsetWidth) * playerVideo.duration;
        }
      };

      /** Toggle play/pause on middle-click (button 1) to match dashboard MP3 player */
      playerTrack.onmousedown = (e) => {
        if (e.button === 1 && playerVideo.src) {
          e.preventDefault();
          playerVideo.paused ? playerVideo.play() : playerVideo.pause();
        }
      };
    }

    /** Toggle Mute */
    if (playerMuteBtn) {
      playerMuteBtn.onclick = (e) => {
        e.stopPropagation(); // Prevent the video click-to-pause logic from firing
        if (playerVideo.src) {
          playerVideo.muted = !playerVideo.muted;
        }
      };
    }

    /** Synchronizes the fullscreen button icon and title. Listens to the document-level event to catch 'Esc' key exits. */
    document.addEventListener("fullscreenchange", () => {
      if (!playerFullscreenBtn) return;

      const isFS = !!document.fullscreenElement;
      playerFullscreenBtn.setAttribute("data-state", isFS ? "fullscreen" : "idle");
      playerFullscreenBtn.title = isFS ? "Exit Fullscreen" : "Fullscreen";
    });

    // Filter Input (Debounced)
    if (playerFilter) {
      const debouncedRender = debounce(() => {
        renderTree(getFilteredItems());
      }, 250); // 250ms delay

      playerFilter.oninput = () => {
        if (playerFilterClear) playerFilterClear.hidden = !playerFilter.value; // Toggle the 'x' button visibility based on content
        debouncedRender();
      };

      /** Handle specific keys while input is focused */
      playerFilter.onkeydown = (e) => {
        if (e.key === "Enter") {
          e.preventDefault();
          playerFilter.blur();
        } else if (e.key === "Escape") {
          e.preventDefault();
          playerFilter.value = "";
          if (playerFilterClear) playerFilterClear.hidden = true;
          renderTree(getFilteredItems());
          playerFilter.blur();
        }
      };
    }

    // Mode Toggle
    if (playerModeBtn) {
      playerModeBtn.onclick = () => updateMode(1);
    }

    // Filter Clear Action
    if (playerFilterClear) {
      playerFilterClear.onclick = () => {
        playerFilter.value = "";
        playerFilterClear.hidden = true;
        renderTree(getFilteredItems()); // Re-render the full tree immediately
        playerFilter.focus();
      };
    }

    // Controls
    if (playerNextBtn) {
      /** @returns {void} */
      playerNextBtn.onclick = () => {
        if (playerVideo.src) {
          // If video has finished, restart it from the beginning
          if (playerVideo.ended) {
            playerVideo.currentTime = 0;
            playerVideo.play();
            return;
          }
          // If video is loaded and paused, resume playback
          if (playerVideo.paused) {
            playerVideo.play();
            return;
          }
        }
        // Otherwise, proceed to the next video based on playback mode
        playMove(playerModeBtn?.getAttribute("data-mode") === "shuffle", 1);
      };
    }

    if (playerFullscreenBtn) {
      playerFullscreenBtn.onclick = () => {
        if (!document.fullscreenElement) {
          playerContainer.requestFullscreen().catch(() => {});
        } else {
          document.exitFullscreen();
        }
      };
    }
    if (playerLocateBtn) {
      playerLocateBtn.onclick = locateCurrentInTree;
    }
    if (helpOpenBtn) {
      helpOpenBtn.onclick = () => openModal(helpModal);
    }

    // Video Events
    playerVideo.addEventListener("ended", () => {
      const m = playerModeBtn?.getAttribute("data-mode");
      if (m === "loop") {
        playerVideo.currentTime = 0;
        playerVideo.play();
      } else if (m === "shuffle" || m === "next") {
        playMove(m === "shuffle", 1);
      }
    });

    /** Handle Time/Duration Updates */
    playerVideo.addEventListener("timeupdate", () => {
      if (playerTime && playerVideo.duration) {
        playerTime.textContent = `${formatTime(playerVideo.currentTime)} / ${formatTime(playerVideo.duration)}`;
      }
    });

    /** Handle Volume/Mute Changes */
    playerVideo.addEventListener("volumechange", syncVolumeUI);

    // Initialize Volume UI on load
    syncVolumeUI();

    // Hotkeys
    window.addEventListener("keydown", (e) => {
      // Prevent hotkeys if typing in inputs
      if (document.activeElement.tagName === "INPUT" || document.activeElement.tagName === "TEXTAREA") return;

      const isShuffle = playerModeBtn?.getAttribute("data-mode") === "shuffle";

      // prettier-ignore
      switch (e.key) {
        case " ": case "k": case "K": e.preventDefault(); if (playerVideo.src) playerVideo.paused ? playerVideo.play() : playerVideo.pause(); else playMove(isShuffle, 1); break;
        case ".": e.preventDefault(); if (playerVideo.src) { playerVideo.pause(); playerVideo.currentTime = Math.min(playerVideo.duration, playerVideo.currentTime + 1 / 24); } break;
        case ",": e.preventDefault(); if (playerVideo.src) { playerVideo.pause(); playerVideo.currentTime = Math.max(0, playerVideo.currentTime - 1 / 24); } break;
        case "ArrowRight": e.preventDefault(); playerVideo.currentTime += 5; break;
        case "ArrowLeft": e.preventDefault(); playerVideo.currentTime -= 5; break;
        case "l": case "L": e.preventDefault(); playerVideo.currentTime += 10; break;
        case "j": case "J": e.preventDefault(); playerVideo.currentTime -= 10; break;
        case "n": case "N": e.preventDefault(); playMove(isShuffle, 1); break;
        case "b": case "B": e.preventDefault(); playMove(isShuffle, -1); break;
        case "m": case "M": e.preventDefault(); if (playerVideo.src) playerVideo.muted = !playerVideo.muted; break;
        case "ArrowUp": e.preventDefault(); playerVideo.volume = Math.min(1, playerVideo.volume + 0.1); break;
        case "ArrowDown": e.preventDefault(); playerVideo.volume = Math.max(0, playerVideo.volume - 0.1); break;
        case "f": case "F": e.preventDefault(); toggleFullscreen(); break;
        case "'": e.preventDefault(); updateMode(1); break;
        case ";": e.preventDefault(); updateMode(-1); break;
        case "g": case "G": e.preventDefault(); locateCurrentInTree(); break;
        case "h": case "H": e.preventDefault(); let hItem = webmTreeData.find((i) => i.path === currentWebmPath); if (hItem) toggleFavourite(hItem.videoId); break;
        case "/": e.preventDefault(); playerFilter?.focus(); break;
        case "?": e.preventDefault(); openModal(helpModal); break;
      }
    });

    // Initial Data Load
    (async () => {
      const [treeRes, cfgRes, favRes] = await Promise.all([fetchJson(base + "/animethemes/webm/tree"), fetchJson(base + "/config"), fetchJson(base + "/animethemes/webm/favourites")]);

      if (favRes.ok) favourites = new Set(favRes.data);

      if (treeRes.ok) {
        // Pre-calculate search index for every item for speed
        webmTreeData = (treeRes.data?.items || [])
          .map((item) => {
            item._searchIndex = `${item.group} ${item.series} ${decodeUnicode(item.file)}`.toLowerCase().replace(/[\u200B\u200A]/g, "");
            return item;
          })
          .sort((a, b) => a.path.localeCompare(b.path));

        renderTree(getFilteredItems());
      }

      if (cfgRes.ok && playerModeBtn) {
        const mode = unwrapConfig(cfgRes.data).Playback.AnimeThemesWebmMode || "off";
        playerModeBtn.setAttribute("data-mode", mode);
        // Refresh tooltip for initial state
        updatePlaybackTooltip(playerModeBtn);
      }
    })();
  }
  // #endregion
})();
