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

  /** @type {Array<{group: string, series: string, file: string, path: string}>} */
  let webmTreeData = [];
  let currentWebmPath = "";

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
   * Filters the tree data based on query words. Each word in the query must be found in either the group, series, or filename. Invisible characters (ZWSP, Hair Space) are stripped before matching.
   * @returns {Object[]}
   */
  const getFilteredItems = () => {
    const rawFt = (playerFilter?.value || "").toLowerCase().trim();
    if (!rawFt) return webmTreeData;
    const queryWords = rawFt.split(/\s+/);
    return webmTreeData.filter((item) => {
      let searchableText = `${item.group} ${item.series} ${decodeUnicode(item.file)}`.toLowerCase();
      searchableText = searchableText.replace(/[\u200B\u200A]/g, ""); // Remove zero-width space and hair space
      return queryWords.every((word) => searchableText.includes(word));
    });
  };
  // #endregion

  // #region Tree
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
          ft = playerFilter?.value;

        const sNodes = sKeys.map((sName) => {
          const files = sMap.get(sName).map((f) => {
            const li = document.createElement("li"),
              leaf = document.createElement("div"),
              name = decodeUnicode(f.file);
            leaf.className = `leaf ${f.path === currentWebmPath ? "active" : ""}`;
            leaf.textContent = name;
            leaf.title = name;
            leaf.dataset.tooltipOverflowOnly = "true";
            leaf.dataset.path = f.path;
            leaf.onclick = () => playFile(f.path);
            li.appendChild(leaf);
            return li;
          });
          return makeNode(sName, files, sKeys.length <= 3 || !!ft);
        });
        rootUl.appendChild(sKeys.length === 1 ? sNodes[0] : makeNode(gName, sNodes, groups.size <= 5 || !!ft));
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
    playerVideo.src = `${base}/animethemes/vfs/webm/stream?path=${encodeURIComponent(path)}`;
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
    if (!items.length) return;
    let idx = isShuffle ? Math.floor(Math.random() * items.length) : (items.findIndex((i) => i.path === currentWebmPath) + 1) % items.length;
    playFile(items[idx < 0 ? 0 : idx].path);
  }
  // #endregion

  // #region Initialization
  if (playerVideo) {
    // Filter Input
    if (playerFilter)
      playerFilter.oninput = () => {
        renderTree(getFilteredItems());
      };

    // Mode Toggle
    if (playerModeBtn) {
      playerModeBtn.onclick = async () => {
        const next = getNextMode(playerModeBtn.getAttribute("data-mode") || "off", ["loop", "shuffle", "next", "off"]);
        playerModeBtn.setAttribute("data-mode", next);

        // Persist setting to server
        const res = await fetchJson(base + "/config");
        if (res.ok) {
          const cfg = unwrapConfig(res.data);
          setValueByPath(cfg, "AnimeThemesWebmMode", next);
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
      const [treeRes, cfgRes] = await Promise.all([fetchJson(base + "/animethemes/vfs/webm/tree"), fetchJson(base + "/config")]);

      if (treeRes.ok) {
        webmTreeData = (treeRes.data?.items || []).sort((a, b) => a.path.localeCompare(b.path));
        renderTree(getFilteredItems());
      }

      if (cfgRes.ok && playerModeBtn) {
        const mode = unwrapConfig(cfgRes.data).AnimeThemesWebmMode || "off";
        playerModeBtn.setAttribute("data-mode", mode);
      }
    })();
  }
  // #endregion
})();
