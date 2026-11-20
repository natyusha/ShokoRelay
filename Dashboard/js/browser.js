/**
 * @file browser.js
 * @description Logic for the Shoko Relay VFS Browser, providing a hierarchical view of the virtual filesystem.
 */
(() => {
  const { base, shokoBase, element, esc, fetchJson, showToast, toastOperation, getData, initSearchInteractions, TOAST_MS } = window._sr;

  /** @type {HTMLElement} The container element for the VFS tree. */
  const vfsTree = element("tree");
  /** @type {HTMLInputElement} The search filter input element. */
  const uiFilter = element("filter");
  /** @type {HTMLButtonElement} The clear button for the search filter. */
  const uiFilterClear = element("filter-clear");
  /** @type {HTMLElement} The element displaying the total series count. */
  const vfsCount = element("vfs-count");
  /** @type {HTMLButtonElement} The button used to refresh VFS Browser series titles and tree data. */
  const vfsRefresh = element("vfs-refresh");
  /** @type {HTMLElement} The container element for the root selection tabs. */
  const rootTabs = element("root-tabs");

  /** @type {string} Local storage key for persisting the active tab. */
  const TABS_KEY = "vfs-active-tab";

  /** @type {Object[]} Array of root objects (including virtual "All" tab) containing series hierarchies. */
  let displayRoots = [];
  /** @type {string} The name of the currently selected VFS root tab. */
  let activeTabName = localStorage.getItem(TABS_KEY) || "All";

  // #region Helpers
  /**
   * Refreshes the VFS for a specific series and displays the result toast.
   * @param {number} id - The Shoko Series ID to rebuild.
   * @returns {Promise<void>}
   */
  async function refreshSeries(id) {
    const label = `VFS [${id}]`;
    const taskName = window._sr.tasks.vfsBuild;

    showToast(`${label}: Refreshing...`, "info", TOAST_MS);
    const response = await fetchJson(`${base}/vfs?run=true&clean=false&filter=${id}`);

    // Immediately clear the task result on the server. This prevents syncActiveTasks in script.js from showing a redundant fallback toast for this operation.
    if (taskName) await fetchJson(`${base}/tasks/clear/${taskName}`, { method: "POST" }, false);

    toastOperation(response, label, { hideOnSucceed: TOAST_MS });

    if (response.isOk) await loadVfsTree(false); // Reload the tree data in-place without showing the full loading spinner
  }

  /**
   * Filters the series within the current active root based on user input. Raw integers will match Shoko Series IDs and titles. Supports prefix-based searching:
   * - "a{id}" (e.g., a1234) to restrict results to AniDB IDs starting with the query.
   * - "m{id}" (e.g., m1234) to restrict results to Shoko Series IDs starting with the query.
   * - Appending a trailing space (e.g., "a1234 ") forces an exact ID match.
   * @returns {Object[]} A filtered array of series objects.
   */
  function getFilteredItems() {
    const root = displayRoots.find((rootItem) => rootItem.name === activeTabName);
    if (!root) return [];
    const rawFt = (uiFilter?.value || "").toLowerCase();
    const filterText = rawFt.trim();
    if (!filterText) return root.series;

    const isExact = rawFt.endsWith(" ");
    if (/^a\d+\s*$/.test(rawFt.trimStart())) {
      const aid = filterText.substring(1);
      return root.series.filter((series) => (isExact ? String(series.anidbId) === aid : String(series.anidbId).startsWith(aid)));
    }
    if (/^m\d+\s*$/.test(rawFt.trimStart())) {
      const sid = filterText.substring(1);
      return root.series.filter((series) => (isExact ? String(series.id) === sid : String(series.id).startsWith(sid)));
    }
    return root.series.filter((series) => series.title.toLowerCase().includes(filterText) || String(series.id).includes(filterText));
  }

  /**
   * Resolves the physical series folder path for a file, stepping out of immediate Season or Specials subfolders.
   * @param {string} sourcePath - The full physical path to a media or metadata file.
   * @returns {string} The resolved series directory path.
   */
  function getSeriesPhysicalDir(sourcePath) {
    const parts = sourcePath.replace(/\\/g, "/").split("/");
    parts.pop();
    if (parts.length > 1 && /^(?:season\s*\d+|specials|s\d+)$/i.test(parts[parts.length - 1])) parts.pop();
    return parts.join("/");
  }

  /**
   * Aggregates series data across all roots to construct a virtual "All" tab, cloning on collision only to avoid mutating single-root objects.
   * @param {Object[]} roots - The raw list of VFS roots from the server.
   * @returns {Object} A unified virtual root object containing merged series listings.
   */
  function buildAllTab(roots) {
    const seriesMap = new Map();
    const clonedIds = new Set();

    roots.forEach((root) => {
      root.series.forEach((series) => {
        if (!seriesMap.has(series.id)) {
          seriesMap.set(series.id, series);
        } else {
          let merged = seriesMap.get(series.id);
          if (!clonedIds.has(series.id)) {
            merged = {
              id: merged.id,
              anidbId: merged.anidbId,
              title: merged.title,
              rootFiles: [...(merged.rootFiles || [])],
              seasons: (merged.seasons || []).map((season) => ({ name: season.name, seasonId: season.seasonId, files: [...season.files] })),
            };
            seriesMap.set(series.id, merged);
            clonedIds.add(series.id);
          }

          (series.rootFiles || []).forEach((file) => {
            if (!merged.rootFiles.some((existingFile) => existingFile.name === file.name && existingFile.source === file.source)) merged.rootFiles.push(file);
          });
          (series.seasons || []).forEach((season) => {
            const existSeason = merged.seasons.find((existingSeason) => existingSeason.name === season.name);
            if (existSeason) {
              season.files.forEach((file) => {
                if (!existSeason.files.some((existingFile) => existingFile.name === file.name && existingFile.source === file.source)) existSeason.files.push(file);
              });
            } else merged.seasons.push({ name: season.name, seasonId: season.seasonId, files: [...season.files] });
          });
        }
      });
    });
    return { name: "All", series: [...seriesMap.values()].sort((left, right) => left.title.localeCompare(right.title)) };
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

    // Validate that the activeTabName actually exists in displayRoots, fallback to the first tab (usually "All") if not
    if (displayRoots.length > 0 && !displayRoots.some((root) => root.name === activeTabName)) {
      activeTabName = displayRoots[0].name;
      localStorage.setItem(TABS_KEY, activeTabName);
    }

    const existing = rootTabs.querySelectorAll(".tab-btn");
    const matchesRoots = existing.length === displayRoots.length && [...existing].every((btn, index) => btn.textContent === displayRoots[index].name);
    if (matchesRoots && existing.length > 0) {
      existing.forEach((btn) => btn.classList.toggle("active", btn.textContent === activeTabName));
      return;
    }
    rootTabs.innerHTML = "";

    displayRoots.forEach((root) => {
      const btn = document.createElement("button");
      btn.className = `tab-btn ${root.name === activeTabName ? "active" : ""}`;
      btn.textContent = root.name;
      btn.onclick = () => {
        activeTabName = root.name;
        localStorage.setItem(TABS_KEY, root.name);
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
   * @param {boolean} [refresh=false] - If true, asks the server to re-resolve series titles from current settings before returning the tree.
   * @returns {Promise<void>}
   */
  async function loadVfsTree(showLoading = true, refresh = false) {
    if (showLoading) showLoadingSpinner();

    const treeEndpoint = refresh ? `${base}/vfs/tree?refresh=true` : `${base}/vfs/tree`;
    const [treeRes, mp3Res] = await Promise.all([fetchJson(treeEndpoint), fetchJson(`${base}/animethemes/mp3/cache`)]);

    window._sr.mp3Cache = {};
    if (mp3Res.isOk) {
      const data = getData(mp3Res) || {};
      for (const [key, value] of Object.entries(data)) {
        const parts = (value || "").split("|");
        window._sr.mp3Cache[key.replace(/\\/g, "/").toLowerCase()] = {
          slug: parts[0] || "",
          upgrade: parts[1] || "",
        };
      }
    }

    if (treeRes.isOk) {
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
   * Helper to construct a file leaf node with source path tooltip.
   * @param {Object} file - The file metadata object.
   * @returns {HTMLLIElement} The file list item element.
   */
  const createFileLeaf = (file) => {
    const listItem = document.createElement("li");
    const leaf = document.createElement("div");
    leaf.className = "leaf";

    const nameSpan = document.createElement("span");
    nameSpan.textContent = file.name;
    nameSpan.title = file.name;
    nameSpan.dataset.tooltipOverflowOnly = "true";

    const src = file.source || "Unknown";
    const sourceSpan = document.createElement("span");
    sourceSpan.className = "source-path";
    sourceSpan.textContent = src;
    sourceSpan.title = src;
    sourceSpan.dataset.tooltipOverflowOnly = "true";

    leaf.append(nameSpan, sourceSpan);
    listItem.appendChild(leaf);
    return listItem;
  };

  /**
   * Dynamically renders and appends file nodes inside a season container, automatically nesting subdirectories.
   * @param {Object} season - The season data object.
   * @param {HTMLUListElement} seasonUl - The sub-list container element to append file nodes to.
   * @returns {void}
   */
  function renderSeasonContents(season, seasonUl) {
    const subfolders = new Map();
    const directFiles = [];

    season.files.forEach((file) => {
      const parts = file.name.replace(/\\/g, "/").split("/");
      if (parts.length > 1) {
        const subName = parts[0];
        const actualName = parts.slice(1).join("/");
        if (!subfolders.has(subName)) subfolders.set(subName, []);
        subfolders.get(subName).push({ ...file, name: actualName });
      } else {
        directFiles.push(file);
      }
    });

    directFiles.forEach((file) => seasonUl.appendChild(createFileLeaf(file)));

    [...subfolders.entries()]
      .sort((left, right) => left[0].localeCompare(right[0]))
      .forEach(([subName, files]) => {
        const seasonLi = document.createElement("li");
        const subUl = document.createElement("ul");
        const subDet = window._sr.createLazyDetails(subName, subUl, (container) => renderSeasonContents({ files }, container), false);
        seasonLi.appendChild(subDet);
        seasonUl.appendChild(seasonLi);
      });
  }

  /**
   * Dynamically renders and appends the seasons and root files for a specific series.
   * @param {Object} series - The series data object.
   * @param {HTMLUListElement} unorderedList - The container element to append items to.
   * @param {boolean} [forceRender=false] - If true, forces nested season files to render immediately.
   * @returns {void}
   */
  function renderSeriesContents(series, unorderedList, forceRender = false) {
    const movieSeasons = (series.seasons || []).filter((season) => season.name.startsWith("Movie ❯ "));
    const regularSeasons = (series.seasons || []).filter((season) => !season.name.startsWith("Movie ❯ "));
    let movieCount = 0;

    const renderSeason = (season) => {
      const seasonLi = document.createElement("li");
      const seasonUl = document.createElement("ul");
      const isMovie = season.name.startsWith("Movie ❯ ");
      const movieId = isMovie ? season.name.split(" ❯ ")[1] : null;

      let displayName = season.name;
      if (isMovie) {
        movieCount++;
        displayName = movieSeasons.length > 1 ? `Movie ${movieCount}` : `Movie`;
      }

      const seasonLink = isMovie
        ? `<a href="${base}/metadata/m${movieId}?includeChildren=1" class="vfs-link small" target="_blank" rel="noopener noreferrer">[m${movieId}]</a>`
        : season.seasonId !== null && season.seasonId !== undefined
          ? `<a href="${base}/metadata/${series.id}s${season.seasonId}?includeChildren=1" class="vfs-link small" target="_blank" rel="noopener noreferrer">[m${series.id}s${season.seasonId}]</a>`
          : "";

      const seasonDet = window._sr.createLazyDetails(
        displayName,
        seasonUl,
        (container) => {
          renderSeasonContents(season, container);
        },
        forceRender,
        seasonLink ? ` ${seasonLink}` : "",
      );

      seasonLi.appendChild(seasonDet);
      unorderedList.appendChild(seasonLi);
    };

    // Movie Branches First
    movieSeasons.forEach(renderSeason);

    // Root Files Second (e.g. Theme.mp3)
    (series.rootFiles || []).forEach((file) => unorderedList.appendChild(createFileLeaf(file)));

    // Regular TV Seasons Third
    regularSeasons.forEach(renderSeason);
  }

  /**
   * Renders the hierarchical tree for the currently active VFS root.
   * @returns {void}
   */
  function renderActiveRoot() {
    if (!vfsTree) return;
    const root = displayRoots.find((rootItem) => rootItem.name === activeTabName);
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

    items.forEach((series) => {
      const listItem = document.createElement("li");
      const unorderedList = document.createElement("ul");
      const details = window._sr.createLazyDetails(
        "",
        unorderedList,
        (container) => {
          renderSeriesContents(series, container, false);
        },
        false,
      );
      const summary = details.querySelector("summary");

      const refresh = document.createElement("span");
      refresh.className = "refresh-btn";
      refresh.textContent = "⟳";
      refresh.title = "Refresh VFS Folder";
      refresh.onclick = (event) => {
        event.preventDefault();
        event.stopPropagation();
        refreshSeries(series.id);
      };

      const hasTheme = (series.rootFiles || []).some((file) => file.name === "Theme.mp3");
      const validSeasons = (series.seasons || []).filter((season) => season.name.toLowerCase().startsWith("season") || season.name.toLowerCase() === "specials");
      const firstFile =
        series.rootFiles && series.rootFiles.length > 0 ? series.rootFiles[0] : validSeasons.length > 0 && validSeasons[0].files && validSeasons[0].files.length > 0 ? validSeasons[0].files[0] : null;
      let slug = "";
      let upgrade = "";

      if (hasTheme && firstFile && firstFile.source && window._sr.mp3Cache) {
        const sourcePath = firstFile.source.replace(/\\/g, "/");
        const directDir = sourcePath.substring(0, sourcePath.lastIndexOf("/"));
        const seriesDir = getSeriesPhysicalDir(sourcePath);
        const entry = window._sr.mp3Cache[directDir.toLowerCase()] || window._sr.mp3Cache[seriesDir.toLowerCase()];
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
        theme.onclick = async (event) => {
          event.preventDefault();
          event.stopPropagation();

          if (!firstFile || !firstFile.source) {
            showToast("VFS: No physical source files found to resolve paths", "error", TOAST_MS);
            return;
          }

          const physicalPath = getSeriesPhysicalDir(firstFile.source);

          const label = `Theme MP3 [${series.id}]`;
          showToast(`${label}: Generating...`, "info", TOAST_MS);

          const response = await fetchJson(`${base}/animethemes/mp3?path=${encodeURIComponent(physicalPath)}`);
          toastOperation(response, label, { hideOnSucceed: TOAST_MS });

          const themeData = getData(response);
          if (response.isOk && (themeData?.Status === "ok" || themeData?.status === "ok")) refreshSeries(series.id);
        };
      } else {
        // Swallows the click to prevent the parent VFS details folder from toggling
        theme.onclick = (event) => {
          event.preventDefault();
          event.stopPropagation();
        };
      }

      const anidbLink = series.anidbId ? `<a href="https://anidb.net/a${series.anidbId}" class="vfs-link small" target="_blank" rel="noopener noreferrer">[a${series.anidbId}]</a>` : "";
      const titleHtml =
        `<a href="${shokoBase}/webui/collection/series/${series.id}/overview" class="vfs-link vfs-id-link" target="_blank" rel="noopener noreferrer">${series.id}</a><span class="vfs-sep">❯</span>` +
        `<span class="vfs-title" title="${esc(series.title)}" data-tooltip-overflow-only="true">${esc(series.title)}</span>` +
        anidbLink +
        `<a href="${base}/metadata/${series.id}?includeChildren=1" class="vfs-link small" target="_blank" rel="noopener noreferrer">[m${series.id}]</a>`;

      summary.appendChild(refresh);
      summary.appendChild(theme);
      summary.insertAdjacentHTML("beforeend", titleHtml);
      summary.title = series.title; // Set exact title override
      listItem.appendChild(details);
      rootUl.appendChild(listItem);
    });

    vfsTree.appendChild(rootUl);
    if (vfsCount) vfsCount.textContent = `${items.length} Series`;
  }
  // #endregion

  // #region Initialization
  initSearchInteractions(uiFilter, uiFilterClear, () => renderActiveRoot());
  if (vfsRefresh) vfsRefresh.onclick = () => loadVfsTree(true, true);

  (async () => {
    await window._sr.authReady;
    await loadVfsTree(true);
  })();
  // #endregion
})();
