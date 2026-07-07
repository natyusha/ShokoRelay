/**
 * @file browser.js
 * @description Logic for the Shoko Relay VFS Browser, providing a hierarchical view of the virtual filesystem.
 */
(() => {
  const { base, el, fetchJson, showToast, toastOperation, getData, initSearchInteractions, TOAST_MS } = window._sr;
  const vfsTree = el("tree");
  const uiFilter = el("filter");
  const uiFilterClear = el("filter-clear");
  const vfsCount = el("vfs-count");
  const rootTabs = el("root-tabs");

  const TABS_KEY = "vfs-active-tab";

  /** @type {Object[]} displayRoots - Array of root objects (including virtual "All" tab) containing series hierarchies. */
  let displayRoots = [];
  /** @type {string} activeTabName - The name of the currently selected VFS root tab. */
  let activeTabName = localStorage.getItem(TABS_KEY) || "All";

  // #region Utilities
  /**
   * Escapes double quotes inside a string to prevent HTML attribute syntax corruption.
   * @param {string} str - The raw string to escape.
   * @returns {string} The HTML-safe escaped string.
   */
  const esc = (str) => (str || "").replace(/"/g, "&quot;");

  /**
   * Refreshes the VFS for a specific series and displays the result toast.
   * @param {number} id - The Shoko Series ID to rebuild.
   * @returns {Promise<void>}
   */
  async function refreshSeries(id) {
    const label = `VFS [${id}]`;
    const taskName = window._sr.tasks.vfsBuild;

    showToast(`${label}: Refreshing...`, "info", TOAST_MS);
    const res = await fetchJson(`${base}/vfs?run=true&clean=false&filter=${id}`);

    // Immediately clear the task result on the server. This prevents syncActiveTasks in script.js from showing a redundant fallback toast for this operation.
    if (taskName) await fetch(`${base}/tasks/clear/${taskName}`, { method: "POST" });

    toastOperation(res, label, { hideOnSucceed: TOAST_MS });

    if (res.ok) await loadVfsTree(false); // Reload the tree data in-place without showing the full loading spinner
  }

  /**
   * Filters the series within the current active root based on user input. Raw integers will match Shoko Series IDs and titles. Supports prefix-based searching:
   * - "a{id}" (e.g., a1234) to restrict results to AniDB IDs starting with the query.
   * - "m{id}" (e.g., m1234) to restrict results to Shoko Series IDs starting with the query.
   * - Appending a trailing space (e.g., "a1234 ") forces an exact ID match.
   * @returns {Object[]} A filtered array of series objects.
   */
  function getFilteredItems() {
    const root = displayRoots.find((r) => r.name === activeTabName);
    if (!root) return [];
    const rawFt = (uiFilter?.value || "").toLowerCase();
    const ft = rawFt.trim();
    if (!ft) return root.series;
    if (/^a\d+\s*$/.test(rawFt.trimStart())) {
      const aid = ft.substring(1);
      const exact = rawFt.endsWith(" ");
      return root.series.filter((s) => (exact ? String(s.anidbId) === aid : String(s.anidbId).startsWith(aid)));
    }
    if (/^m\d+\s*$/.test(rawFt.trimStart())) {
      const sid = ft.substring(1);
      const exact = rawFt.endsWith(" ");
      return root.series.filter((s) => (exact ? String(s.id) === sid : String(s.id).startsWith(sid)));
    }
    return root.series.filter((s) => s.title.toLowerCase().includes(ft) || String(s.id).includes(ft));
  }

  /**
   * Aggregates and deduplicates series data across all roots to construct a virtual "All" tab.
   * @param {Object[]} roots - The raw list of VFS roots from the server.
   * @returns {Object} A unified virtual root object containing merged series listings.
   */
  function buildAllTab(roots) {
    const seriesMap = new Map();
    roots.forEach((root) => {
      root.series.forEach((s) => {
        if (!seriesMap.has(s.id)) {
          // Deep copy to avoid modifying the original root series objects in memory
          seriesMap.set(s.id, {
            id: s.id,
            anidbId: s.anidbId,
            title: s.title,
            rootFiles: [...(s.rootFiles || [])],
            seasons: (s.seasons || []).map((se) => ({ name: se.name, seasonId: se.seasonId, files: [...se.files] })),
          });
        } else {
          const existing = seriesMap.get(s.id);
          existing.rootFiles.push(...(s.rootFiles || []));
          (s.seasons || []).forEach((se) => {
            const existSeason = existing.seasons.find((ese) => ese.name === se.name);
            if (existSeason) existSeason.files.push(...se.files);
            else existing.seasons.push({ name: se.name, seasonId: se.seasonId, files: [...se.files] });
          });
        }
      });
    });
    return { name: "All", series: [...seriesMap.values()].sort((a, b) => a.title.localeCompare(b.title)) };
  }

  /**
   * Renders a loading spinner inside the tree container to indicate active processing.
   * @returns {void}
   */
  function showLoadingSpinner() {
    if (vfsTree) vfsTree.innerHTML = '<div class="placeholder"><svg class="loading-spinner"><use href="img/icons.svg#loading"></use></svg><div>Loading VFS tree...</div></div>';
  }

  /**
   * Renders the root selection tabs and attaches click handlers.
   * @returns {void}
   */
  function renderTabs() {
    if (!rootTabs) return;

    const existing = rootTabs.querySelectorAll(".tab-btn");
    if (existing.length > 0) {
      existing.forEach((btn) => btn.classList.toggle("active", btn.textContent === activeTabName));
      return;
    }
    rootTabs.innerHTML = "";

    // Validate that the activeTabName actually exists in displayRoots, fallback to the first tab (usually "All") if not
    if (displayRoots.length > 0 && !displayRoots.some((r) => r.name === activeTabName)) {
      activeTabName = displayRoots[0].name;
      localStorage.setItem(TABS_KEY, activeTabName);
    }

    displayRoots.forEach((r) => {
      const btn = document.createElement("button");
      btn.className = `tab-btn ${r.name === activeTabName ? "active" : ""}`;
      btn.textContent = r.name;
      btn.onclick = () => {
        activeTabName = r.name;
        localStorage.setItem(TABS_KEY, r.name);
        renderTabs();
        showLoadingSpinner();
        setTimeout(renderActiveRoot, 300);
      };
      rootTabs.appendChild(btn);
    });
  }

  /**
   * Fetches the latest VFS blueprint and MP3 cache from the server and renders the tabs and tree view.
   * @param {boolean} [showLoading=true] - If true, displays the loading spinner before fetching.
   * @returns {Promise<void>}
   */
  async function loadVfsTree(showLoading = true) {
    if (showLoading) showLoadingSpinner();

    const [treeRes, mp3Res] = await Promise.all([fetchJson(base + "/vfs/tree"), fetchJson(base + "/animethemes/mp3/cache")]);

    window._sr.mp3Cache = {};
    if (mp3Res.ok) {
      const data = getData(mp3Res) || {};
      for (const [k, v] of Object.entries(data)) {
        const parts = (v || "").split("|");
        window._sr.mp3Cache[k.replace(/\\/g, "/").toLowerCase()] = {
          slug: parts[0] || "",
          upgrade: parts[1] || "",
        };
      }
    }

    if (treeRes.ok) {
      const roots = getData(treeRes)?.roots || [];
      if (roots.length > 1) {
        const allRoot = buildAllTab(roots);
        displayRoots = [allRoot, ...roots];
      } else if (roots.length === 1) {
        displayRoots = roots;
      } else {
        displayRoots = [];
        if (vfsTree) vfsTree.innerHTML = '<div class="placeholder">No VFS directories found. Click the "Generate" button under the "Shoko: VFS" section on the dashboard to build your virtual folders.</div>';
      }
      renderTabs();
      renderActiveRoot();
    } else if (vfsTree) {
      vfsTree.innerHTML = '<div class="placeholder">Failed to load VFS blueprint.</div>';
    }
  }
  // #endregion

  // #region Tree & Navigation
  /**
   * Dynamically renders and appends file nodes inside a season container.
   * @param {Object} s - The season data object.
   * @param {HTMLUListElement} sUl - The sub-list container element to append file nodes to.
   * @returns {void}
   */
  function renderSeasonContents(s, sUl) {
    s.files.forEach((f) => {
      const eLi = document.createElement("li");
      const leaf = document.createElement("div");
      leaf.className = "leaf";
      leaf.innerHTML =
        `<span title="${esc(f.name)}" data-tooltip-overflow-only="true">${f.name}</span>` +
        `<span class="source-path" title="${esc(f.source || "Unknown")}" data-tooltip-overflow-only="true">${f.source || "Unknown"}</span>`;
      eLi.appendChild(leaf);
      sUl.appendChild(eLi);
    });
  }

  /**
   * Dynamically renders and appends the seasons and root files for a specific series.
   * @param {Object} g - The series data object.
   * @param {HTMLUListElement} ul - The container element to append items to.
   * @param {boolean} [forceRender=false] - If true, forces nested season files to render immediately.
   * @returns {void}
   */
  function renderSeriesContents(g, ul, forceRender = false) {
    (g.rootFiles || []).forEach((f) => {
      const fLi = document.createElement("li");
      const leaf = document.createElement("div");
      leaf.className = "leaf";
      leaf.innerHTML =
        `<span title="${esc(f.name)}" data-tooltip-overflow-only="true">${f.name}</span>` +
        `<span class="source-path" title="${esc(f.source || "Unknown")}" data-tooltip-overflow-only="true">${f.source || "Unknown"}</span>`;
      fLi.appendChild(leaf);
      ul.appendChild(fLi);
    });

    (g.seasons || []).forEach((s) => {
      const sLi = document.createElement("li");
      const sUl = document.createElement("ul");

      const seasonLink =
        s.seasonId !== null && s.seasonId !== undefined
          ? `<a href="${base}/metadata/${g.id}s${s.seasonId}?includeChildren=1" class="vfs-link small" target="_blank" rel="noopener noreferrer">[m${g.id}s${s.seasonId}]</a>`
          : "";

      const sDet = window._sr.createLazyDetails(
        `${s.name} ${seasonLink}`,
        sUl,
        (container) => {
          renderSeasonContents(s, container);
        },
        forceRender,
      );

      sLi.appendChild(sDet);
      ul.appendChild(sLi);
    });
  }

  /**
   * Renders the hierarchical tree for the currently active VFS root.
   * @returns {void}
   */
  function renderActiveRoot() {
    if (!vfsTree) return;
    const root = displayRoots.find((r) => r.name === activeTabName);
    if (!root) {
      vfsTree.innerHTML = '<div class="placeholder">No VFS roots found. Ensure you have generated the VFS once.</div>';
      return;
    }

    const items = getFilteredItems();
    vfsTree.innerHTML = "";
    if (!items.length) {
      vfsTree.innerHTML = `<div class="placeholder">${uiFilter?.value ? "No matching folders" : "This root is empty"}</div>`;
      return;
    }

    const rootUl = document.createElement("ul");
    rootUl.className = "tree";

    const shokoBase = location.origin + base.split(/\/api\//i)[0];

    items.forEach((g) => {
      const li = document.createElement("li");
      const ul = document.createElement("ul");
      const det = window._sr.createLazyDetails(
        "",
        ul,
        (container) => {
          renderSeriesContents(g, container, false);
        },
        false,
      );
      const sum = det.querySelector("summary");

      const refresh = document.createElement("span");
      refresh.className = "refresh-btn";
      refresh.textContent = "⟳";
      refresh.title = "Refresh VFS Folder";
      refresh.onclick = (e) => {
        e.preventDefault();
        e.stopPropagation();
        refreshSeries(g.id);
      };

      const hasTheme = (g.rootFiles || []).some((f) => f.name === "Theme.mp3");
      const validSeasons = (g.seasons || []).filter((s) => s.name.toLowerCase().startsWith("season") || s.name.toLowerCase() === "specials");
      const firstFile = g.rootFiles && g.rootFiles.length > 0 ? g.rootFiles[0] : validSeasons.length > 0 && validSeasons[0].files && validSeasons[0].files.length > 0 ? validSeasons[0].files[0] : null;
      let slug = "";
      let upgrade = "";

      if (hasTheme && firstFile && firstFile.source && window._sr.mp3Cache) {
        const sourcePath = firstFile.source.replace(/\\/g, "/");
        const dir = sourcePath.substring(0, sourcePath.lastIndexOf("/"));
        const entry = window._sr.mp3Cache[dir.toLowerCase()];
        if (entry) {
          slug = entry.slug;
          upgrade = entry.upgrade;
        }
      }

      const isOp = slug && slug.toUpperCase().startsWith("OP");
      const isOp1 = slug && slug.toUpperCase() === "OP1";
      const isOverridden = isOp && !isOp1;
      const isOpAvail = hasTheme && upgrade !== "";
      const theme = document.createElement("span");

      if (hasTheme) {
        const btnClass = isOpAvail ? " op-avail" : isOverridden ? " user-over" : "";
        theme.className = `theme-btn exists${btnClass}`;
        theme.textContent = "♬";
        theme.title = `Theme.mp3 Exists${slug ? ` (${slug})` : ""}${isOpAvail ? " (Opening Available)" : ""}`;
      } else {
        theme.className = "theme-btn missing";
        theme.textContent = "♬";
        theme.title = "Theme.mp3 Missing (Click to Generate)";
      }

      if (!hasTheme) {
        theme.onclick = async (e) => {
          e.preventDefault();
          e.stopPropagation();

          if (!firstFile || !firstFile.source) {
            showToast("VFS: No physical source files found to resolve paths", "error", TOAST_MS);
            return;
          }

          const parts = firstFile.source.replace(/\\/g, "/").split("/");
          parts.pop();
          const physicalPath = parts.join("/");

          const label = `Theme MP3 [${g.id}]`;
          showToast(`${label}: Generating...`, "info", TOAST_MS);

          const res = await fetchJson(`${base}/animethemes/mp3?path=${encodeURIComponent(physicalPath)}`);
          toastOperation(res, label, { hideOnSucceed: TOAST_MS });

          const d = getData(res);
          if (res.ok && (d?.Status === "ok" || d?.status === "ok")) refreshSeries(g.id);
        };
      } else {
        // Swallows the click to prevent the parent VFS details folder from toggling
        theme.onclick = (e) => {
          e.preventDefault();
          e.stopPropagation();
        };
      }

      const titleHtml =
        `<a href="${shokoBase}/webui/collection/series/${g.id}/overview" class="vfs-link vfs-id-link" target="_blank" rel="noopener noreferrer">${g.id}</a><span class="vfs-sep">❯</span>` +
        `<span class="vfs-title" title="${esc(g.title)}" data-tooltip-overflow-only="true">${g.title}</span>` +
        `<a href="https://anidb.net/a${g.anidbId}" class="vfs-link small" target="_blank" rel="noopener noreferrer">[a${g.anidbId}]</a>` +
        `<a href="${base}/metadata/${g.id}?includeChildren=1" class="vfs-link small" target="_blank" rel="noopener noreferrer">[m${g.id}]</a>`;

      sum.appendChild(refresh);
      sum.appendChild(theme);
      sum.insertAdjacentHTML("beforeend", titleHtml);
      sum.title = g.title; // Set exact title override
      li.appendChild(det);
      rootUl.appendChild(li);
    });

    vfsTree.appendChild(rootUl);
    if (vfsCount) vfsCount.textContent = `${items.length} Series`;
  }
  // #endregion

  // #region Initialization
  initSearchInteractions(uiFilter, uiFilterClear, () => renderActiveRoot());

  (async () => {
    await loadVfsTree(true);
  })();
  // #endregion
})();
