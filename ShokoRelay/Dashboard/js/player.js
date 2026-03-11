/**
 * @file player.js
 * @description Dedicated logic for the Shoko Relay stand-alone AnimeThemes VFS video player.
 */
(() => {
  const { base, el, fetchJson, unwrapConfig, setValueByPath } = window._sr;

  const playerVideo = el("video");
  const playerTree = el("tree");
  const playerFilter = el("filter");
  const playerNextBtn = el("next");
  const playerModeBtn = el("mode");
  const playerTitle = el("title");
  const playerAnime = el("anime");

  /** @type {Array<{group: string, series: string, file: string, path: string, videoId: number}>} */
  let webmTreeData = [];
  let currentWebmPath = "";
  let favourites = new Set();

  /** @type {Set<string>} */
  const shuffleHistory = new Set();
  let lastFilterValue = "";

  // #region Utilities
  /**
   * Decode unicode escape sequences in a string.
   * @param {string} s
   * @returns {string}
   */
  const decodeUnicode = (s) => s.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)));

  /**
   * Get next mode in sequence.
   * @param {string} current
   * @param {string[]} modes
   * @returns {string}
   */
  const getNextMode = (current, modes) => modes[(modes.indexOf(current) + 1) % modes.length];

  /**
   * Debounce helper to prevent lag during rapid typing
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
   * If the keyword "favs" is present, the result is restricted to favourited items. Invisible characters (ZWSP, Hair Space) are stripped before matching.
   * Tags can be excluded or included by prepending with "-", e.g. "-spoil" or a a "+", e.g. "+spoil" for inclusions.
   * @returns {Object[]}
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
        default:
          return false;
      }
    };

    return webmTreeData.filter((item) => {
      // 1. Favourites check
      if (isFavQuery && (!item.videoId || !favourites.has(item.videoId))) return false;

      // 2. Metadata exclusion checks (-spoil, -nsfw, etc)
      // Returns false if the item HAS a tag the user wants to exclude
      for (const tag of tagExclusions) {
        if (matchesTag(tag, item)) return false;
      }

      // 3. Metadata inclusion checks (+lyrics, +subs, etc)
      // Returns false if the item is MISSING a tag the user explicitly requested
      for (const tag of tagInclusions) {
        if (!matchesTag(tag, item)) return false;
      }

      // 4. Inclusion text search (using pre-calculated index)
      return searchTerms.every((word) => item._searchIndex.includes(word));
    });
  };
  // #endregion

  // #region Tree
  /**
   * Toggles the favourite status of a videoId on the server and updates local state.
   * @param {number} videoId
   * @param {HTMLElement} heartEl
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
      if (isFav) {
        favourites.add(videoId);
      } else {
        favourites.delete(videoId);
      }

      heartEl.classList.toggle("favourited", isFav);

      // Re-run filter if current view is "favs"
      if (playerFilter?.value.toLowerCase().includes("favs")) {
        renderTree(getFilteredItems());
      }
    }
  }

  /**
   * Renders the VFS items into a hierarchical tree.
   * @param {Object[]} items
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

            // Favourites Heart Icon
            const heart = document.createElement("span");
            const isFavourited = favourites.has(f.videoId);
            heart.className = `fav-icon ${isFavourited ? "favourited" : ""}`;
            heart.textContent = "❤";
            heart.onclick = (e) => {
              e.stopPropagation();
              toggleFavourite(f.videoId, heart);
            };

            // Theme Name Text
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
  /**
   * Sets the video source and starts playback.
   * @param {string} path
   */
  function playFile(path) {
    if (!playerVideo) return;
    currentWebmPath = path;
    playerVideo.src = `${base}/animethemes/webm/stream?path=${encodeURIComponent(path)}`;
    playerVideo.play().catch(() => {});

    // Update UI Header
    const item = webmTreeData.find((i) => i.path === path);
    const themeName = item ? decodeUnicode(item.file) : "Video Player";
    const animeName = item ? item.series : "";

    if (playerTitle) playerTitle.textContent = playerTitle.title = themeName;
    if (playerAnime) playerAnime.textContent = playerAnime.title = animeName;

    playerTree?.querySelectorAll(".leaf").forEach((el) => el.classList.toggle("active", el.dataset.path === path));
  }

  /**
   * Advances playback based on mode (Next/Shuffle).
   * @param {boolean} isShuffle
   */
  function playMove(isShuffle = false) {
    const items = getFilteredItems();
    if (items.length === 0) return;

    // Reset history if the filter has changed
    const currentFilter = (playerFilter?.value || "").trim();
    if (currentFilter !== lastFilterValue) {
      shuffleHistory.clear();
      lastFilterValue = currentFilter;
    }

    let idx;
    if (isShuffle) {
      // Identify items that haven't been played yet in this cycle
      let pool = items.filter((item) => !shuffleHistory.has(item.path));

      // If everything has been played, reset the cycle
      if (pool.length === 0) {
        shuffleHistory.clear();
        pool = items;
      }

      // Avoid playing the current song again immediately if other options exist
      if (pool.length > 1) {
        pool = pool.filter((item) => item.path !== currentWebmPath);
      }

      // Pick random item from the remaining pool
      const selectedItem = pool[Math.floor(Math.random() * pool.length)];
      idx = items.indexOf(selectedItem);

      // Record this item in history
      shuffleHistory.add(selectedItem.path);
    } else {
      // Normal sequential logic
      shuffleHistory.clear(); // Clear history when leaving shuffle mode
      const currentIdx = items.findIndex((i) => i.path === currentWebmPath);
      idx = (currentIdx + 1) % items.length;
    }

    playFile(items[idx].path);
  }
  // #endregion

  // #region Initialization
  if (playerVideo) {
    // Filter Input (Debounced)
    if (playerFilter) {
      const debouncedRender = debounce(() => {
        renderTree(getFilteredItems());
      }, 250); // 250ms delay

      playerFilter.oninput = debouncedRender;
    }

    // Mode Toggle
    if (playerModeBtn) {
      playerModeBtn.onclick = async () => {
        const next = getNextMode(playerModeBtn.getAttribute("data-mode") || "off", ["loop", "shuffle", "next", "off"]);
        playerModeBtn.setAttribute("data-mode", next);
        shuffleHistory.clear(); // Clear shuffle history if switching modes
        // Persist setting to server
        const res = await fetchJson(base + "/config");
        if (res.ok) {
          const cfg = unwrapConfig(res.data);
          setValueByPath(cfg, "Playback.AnimeThemesWebmMode", next);
          await fetchJson(base + "/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(cfg) });
        }
      };
    }

    // Controls
    if (playerNextBtn) playerNextBtn.onclick = () => playMove(playerModeBtn?.getAttribute("data-mode") === "shuffle");

    // Video Events
    playerVideo.addEventListener("ended", () => {
      const m = playerModeBtn?.getAttribute("data-mode");
      if (m === "loop") {
        playerVideo.currentTime = 0;
        playerVideo.play();
      } else if (m === "shuffle" || m === "next") playMove(m === "shuffle");
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
      }
    })();
  }
  // #endregion
})();
