/**
 * @file player.js
 * @description Logic for the Shoko Relay stand-alone AnimeThemes VFS video player.
 */
(() => {
  const { base, configUrl, el, fetchJson, unwrapConfig, setValueByPath, openModal, updatePlaybackTooltip, saveSettings, getData } = window._sr;

  // DOM Elements - UI Indicators
  const playerTime = el("time-display");
  const playerVolText = el("volume-text");
  const playerFullscreenBtn = el("fullscreen");
  const playerMuteBtn = el("volume-icon");
  const playerContainer = el("video").parentElement;
  const playerVideo = el("video");

  // DOM Elements - Navigation & Search
  const playerTree = el("tree");
  const playerFilter = el("filter");
  const playerFilterClear = el("filter-clear");
  const playerNextBtn = el("next");
  const playerModeBtn = el("mode");

  // DOM Elements - Metadata Header
  const playerTitle = el("title");
  const playerAnime = el("anime");
  const playerNowPlayingFav = el("now-playing-fav");
  const playerLocateBtn = el("locate-current");

  // DOM Elements - Progress Bar
  const playerFill = el("player-progress-fill");
  const playerTrack = el("player-progress-track");

  // DOM Elements - Help
  const helpModal = el("player-help-modal");
  const helpOpenBtn = el("help-open");

  /** @type {WebmEntry[]} */
  let webmTreeData = [];
  /** @type {string} */
  let currentWebmPath = "";
  /** @type {Set<number>} */
  let favourites = new Set();
  /** @type {number|null} */
  let progressRaf;

  /** @type {Set<string>} Track played paths during shuffle to ensure a full cycle before repeats. */
  const shuffleHistory = new Set();
  /** @type {string[]} The specific sequence of paths played during this shuffle session. */
  let navigationStack = [];
  /** @type {number} Current position in the navigationStack. */
  let stackIndex = -1;
  /** @type {string} Used to detect filter changes for history reset. */
  let lastFilterValue = "";

  // #region Utilities

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

  /** Updates the visual progress bar fill width. */
  const syncProgressUI = () => {
    if (playerFill && playerVideo.duration) {
      const percent = (playerVideo.currentTime / playerVideo.duration) * 100;
      playerFill.style.width = percent + "%";
    }
  };

  /** Synchronizes the volume percentage text and updates icon switching level. */
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
  };

  /** Decodes unicode escape sequences in server-provided filenames. */
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

  /** Prevents rapid execution of expensive functions like tree rendering. */
  const debounce = (fn, ms) => {
    let timeout;
    return (...args) => {
      clearTimeout(timeout);
      timeout = setTimeout(() => fn.apply(this, args), ms);
    };
  };

  /**
   * Evaluates the full list of themes against active search terms, heart status, and metadata tags.
   * @returns {WebmEntry[]} The filtered list of theme entries.
   */
  const getFilteredItems = () => {
    const rawFt = (playerFilter?.value || "").toLowerCase().trim();
    if (!rawFt) return webmTreeData;

    const words = rawFt.split(/\s+/).filter((w) => w.length > 0);
    const isFavQuery = words.includes("favs");
    const tagExclusions = [],
      tagInclusions = [],
      searchTerms = [];

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
      for (const tag of tagExclusions) if (matchesTag(tag, item)) return false;
      for (const tag of tagInclusions) if (!matchesTag(tag, item)) return false;
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

  /** Toggles the favourite status of a video on the server and synchronizes UI heart icons. */
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

      if (playerNowPlayingFav && webmTreeData.find((i) => i.path === currentWebmPath)?.videoId === videoId) {
        playerNowPlayingFav.classList.toggle("favourited", isFav);
      }
      if (playerFilter?.value.toLowerCase().includes("favs")) renderTree(getFilteredItems());
    }
  }

  /** Constructs the hierarchical DOM for the theme list. */
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
          return makeNode(sName, files, !!ft);
        });
        rootUl.appendChild(sKeys.length === 1 ? sNodes[0] : makeNode(gName, sNodes, !!ft));
      });
    playerTree.appendChild(rootUl);
  }

  /** Expands folders in the tree view and scrolls the currently playing theme into center view. */
  function locateCurrentInTree() {
    if (!currentWebmPath) return;
    const leaf = playerTree.querySelector(`.leaf[data-path="${CSS.escape(currentWebmPath)}"]`);
    if (!leaf) return;

    let parent = leaf.closest("details");
    while (parent) {
      parent.open = true;
      parent = parent.parentElement.closest("details");
    }
    leaf.scrollIntoView({ behavior: "smooth", block: "center" });
  }
  // #endregion

  // #region Player Engine

  /** Sets the video source and updates header metadata and highlights. */
  function playFile(path) {
    if (!playerVideo) return;
    currentWebmPath = path;
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
      if (item?.seriesId) {
        const shokoBase = location.origin + base.split("/api/")[0]; // Reverse Proxy Support
        playerAnime.href = `${shokoBase}/webui/collection/series/${item.seriesId}/overview`;
        playerAnime.style.pointerEvents = "auto";
      } else {
        playerAnime.href = "#";
        playerAnime.style.pointerEvents = "none";
      }
    }
    playerTree?.querySelectorAll(".leaf").forEach((el) => el.classList.toggle("active", el.dataset.path === path));
  }

  /** Calculates the next track based on shuffle history and current direction. */
  function playMove(isShuffle = false, direction = 1) {
    const items = getFilteredItems();
    if (items.length === 0) return;

    const currentFilter = (playerFilter?.value || "").trim();
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

  /** Updates the data-state attribute of the play button. */
  function syncPlaybackUI() {
    if (playerNextBtn) {
      const isPlaying = playerVideo && !playerVideo.paused && !playerVideo.ended;
      playerNextBtn.setAttribute("data-state", isPlaying ? "playing" : "idle");
      updatePlaybackTooltip(playerNextBtn);
    }
  }

  /** Toggles playback mode and persists it to the server. */
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

  /** Standard toggle for browser fullscreen API. */
  function toggleFullscreen() {
    if (!document.fullscreenElement) playerContainer.requestFullscreen().catch(() => {});
    else document.exitFullscreen();
  }
  // #endregion

  // #region Initialization
  if (playerVideo) {
    ["play", "pause", "ended", "loadstart"].forEach((ev) => playerVideo.addEventListener(ev, syncPlaybackUI));

    const updateProgressLoop = () => {
      syncProgressUI();
      if (!playerVideo.paused && !playerVideo.ended) progressRaf = requestAnimationFrame(updateProgressLoop);
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

    if (playerFilter) {
      const debouncedRender = debounce(() => renderTree(getFilteredItems()), 250);
      playerFilter.oninput = () => {
        if (playerFilterClear) playerFilterClear.hidden = !playerFilter.value;
        debouncedRender();
      };
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

    if (playerModeBtn) playerModeBtn.onclick = () => updateMode(1);
    if (playerFilterClear)
      playerFilterClear.onclick = () => {
        playerFilter.value = "";
        playerFilterClear.hidden = true;
        playerFilter.focus();
        setTimeout(() => {
          renderTree(getFilteredItems()); // Defer DOM Operation to reduce perceived lag
        }, 10);
      };

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

    playerLocateBtn.onclick = locateCurrentInTree;
    helpOpenBtn.onclick = () => openModal(helpModal);

    window.addEventListener("keydown", (e) => {
      if (document.activeElement.tagName === "INPUT" || document.activeElement.tagName === "TEXTAREA") return;
      const handledKeys = [" ", "k", "K", "'", ";", ",", ".", "/", "?", "ArrowDown", "ArrowLeft", "ArrowRight", "ArrowUp", "b", "B", "f", "F", "g", "G", "h", "H", "j", "J", "l", "L", "m", "M", "n", "N"];
      if (!handledKeys.includes(e.key)) return;
      e.preventDefault();
      const isShuffle = playerModeBtn?.getAttribute("data-mode") === "shuffle";
      // prettier-ignore
      switch (e.key) {
        case " ": case "k": case "K": if (playerVideo.src) { if (playerVideo.ended) { playerVideo.currentTime = 0; playerVideo.play(); } else { playerVideo.paused ? playerVideo.play() : playerVideo.pause(); } } else playMove(isShuffle, 1); break;
        case "n": case "N": playMove(isShuffle, 1); break;
        case "b": case "B": playMove(isShuffle, -1); break;
        case "'": updateMode(1); break;
        case ";": updateMode(-1); break;
        case ".": if (playerVideo.src) { playerVideo.pause(); playerVideo.currentTime = Math.min(playerVideo.duration, playerVideo.currentTime + 1 / 24); } break;
        case ",": if (playerVideo.src) { playerVideo.pause(); playerVideo.currentTime = Math.max(0, playerVideo.currentTime - 1 / 24); } break;
        case "ArrowRight": if (playerVideo.src) { playerVideo.currentTime += 5; syncProgressUI(); } break;
        case "ArrowLeft": if (playerVideo.src) { playerVideo.currentTime -= 5; syncProgressUI(); } break;
        case "l": case "L": if (playerVideo.src) { playerVideo.currentTime += 10; syncProgressUI(); } break;
        case "j": case "J": if (playerVideo.src) { playerVideo.currentTime -= 10; syncProgressUI(); } break;
        case "m": case "M": if (playerVideo.src) playerVideo.muted = !playerVideo.muted; break;
        case "ArrowUp": playerVideo.volume = Math.min(1, playerVideo.volume + 0.1); break;
        case "ArrowDown": playerVideo.volume = Math.max(0, playerVideo.volume - 0.1); break;
        case "f": case "F": toggleFullscreen(); break;
        case "g": case "G": locateCurrentInTree(); break;
        case "h": case "H": const hItem = webmTreeData.find((i) => i.path === currentWebmPath); if (hItem) toggleFavourite(hItem.videoId); break;
        case "/": playerFilter?.focus(); break;
        case "?": openModal(helpModal); break;
      }
    });

    (async () => {
      // Show loading placeholder immediately with the same formatting as the empty state
      if (playerTree) playerTree.innerHTML = '<div class="placeholder">Working on it...</div>';
      const [treeRes, cfgRes, favRes] = await Promise.all([fetchJson(base + "/animethemes/webm/tree"), fetchJson(configUrl), fetchJson(base + "/animethemes/webm/favourites")]);
      if (favRes.ok) favourites = new Set(getData(favRes) || []);
      if (treeRes.ok) {
        webmTreeData = (getData(treeRes)?.items || [])
          .map((item) => {
            item._searchIndex = `${item.group} ${item.series} ${decodeUnicode(item.file)}`.toLowerCase().replace(/[\u200B\u200A]/g, "");
            return item;
          })
          .sort((a, b) => {
            const groupComp = a.group.localeCompare(b.group);
            if (groupComp !== 0) return groupComp;
            const seriesComp = a.series.localeCompare(b.series);
            if (seriesComp !== 0) return seriesComp;
            return a.file.localeCompare(b.file);
          });
        renderTree(getFilteredItems());
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
