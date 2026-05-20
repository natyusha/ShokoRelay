/**
 * @file browser.js
 * @description Logic for the Shoko Relay VFS Browser, providing a hierarchical view of the virtual filesystem.
 */
(() => {
  const { base, el, fetchJson, showToast, toastOperation, getData, initSearchInteractions, TOAST_MS } = window._sr;
  const playerTree = el("tree");
  const uiFilter = el("filter");
  const uiFilterClear = el("filter-clear");
  const vfsCount = el("vfs-count");
  const rootTabs = el("root-tabs");

  /** @type {Object[]} vfsRoots - Array of root objects containing series and file hierarchies. */
  let vfsRoots = [];
  /** @type {number} activeRootIndex - The index of the currently selected VFS root tab. */
  let activeRootIndex = 0;

  // #region Utilities
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
  }

  /**
   * Filters the series within the current active root based on user input.
   * @returns {Object[]} A filtered array of series objects.
   */
  function getFilteredItems() {
    const root = vfsRoots[activeRootIndex];
    if (!root) return [];
    const ft = (uiFilter?.value || "").toLowerCase().trim();
    if (!ft) return root.series;
    return root.series.filter((s) => s.title.toLowerCase().includes(ft) || String(s.id).includes(ft) || String(s.anidbId).includes(ft));
  }

  /**
   * Renders the root selection tabs and attaches click handlers.
   * @returns {void}
   */
  function renderTabs() {
    if (!rootTabs) return;
    rootTabs.innerHTML = "";
    vfsRoots.forEach((r, i) => {
      const btn = document.createElement("button");
      btn.className = `tab-btn ${i === activeRootIndex ? "active" : ""}`;
      btn.textContent = r.name;
      btn.onclick = () => {
        activeRootIndex = i;
        renderTabs();
        renderActiveRoot();
      };
      rootTabs.appendChild(btn);
    });
  }

  // #endregion

  // #region Tree & Navigation

  /**
   * Renders the hierarchical tree for the currently active VFS root.
   * @returns {void}
   */
  function renderActiveRoot() {
    if (!playerTree) return;
    const root = vfsRoots[activeRootIndex];
    if (!root) {
      playerTree.innerHTML = '<div class="placeholder">No VFS roots found. Ensure you have generated the VFS once.</div>';
      return;
    }

    const items = getFilteredItems();
    playerTree.innerHTML = "";
    if (!items.length) {
      playerTree.innerHTML = `<div class="placeholder">${uiFilter?.value ? "No matching folders" : "This root is empty"}</div>`;
      return;
    }

    const rootUl = document.createElement("ul");
    rootUl.className = "tree";

    const shokoBase = location.origin + base.split(/\/api\//i)[0];
    const ft = (uiFilter?.value || "").trim();

    items.forEach((g) => {
      const li = document.createElement("li");
      const det = document.createElement("details");
      const sum = document.createElement("summary");
      const ul = document.createElement("ul");
      det.open = !!ft;

      sum.innerHTML = '<span class="tree-icon expand"></span><span class="tree-icon collapse"></span>';

      const ref = document.createElement("span");
      ref.className = "refresh-icon";
      ref.textContent = "↻";
      ref.title = "Refresh VFS Folder";
      ref.onclick = (e) => {
        e.preventDefault();
        e.stopPropagation();
        refreshSeries(g.id);
      };

      const titleHtml = `
        <a href="${shokoBase}/webui/collection/series/${g.id}/overview" class="vfs-link vfs-id-link">${g.id}</a>
        <span class="vfs-sep">❯</span>
        <span class="vfs-title">${g.title}</span>
        <a href="https://anidb.net/a${g.anidbId}" class="vfs-link small" target="_blank" rel="noopener noreferrer">[a${g.anidbId}]</a>
        <a href="${base}/metadata/${g.id}?includeChildren=1" class="vfs-link small" target="_blank" rel="noopener noreferrer">[m${g.id}]</a>
      `;

      det.ontoggle = () => {
        if (det.open) {
          const subDetails = ul.querySelectorAll(":scope > li > details");
          if (subDetails.length === 1) subDetails[0].open = true;
        }
      };

      sum.appendChild(ref);
      sum.insertAdjacentHTML("beforeend", titleHtml);

      (g.rootFiles || []).forEach((f) => {
        const fLi = document.createElement("li");
        const leaf = document.createElement("div");
        leaf.className = "leaf";
        leaf.innerHTML = `<span>${f.name}</span><span class="source-path">${f.source || "Unknown"}</span>`;
        fLi.appendChild(leaf);
        ul.appendChild(fLi);
      });

      (g.seasons || []).forEach((s) => {
        const sLi = document.createElement("li");
        const sDet = document.createElement("details");
        const sSum = document.createElement("summary");
        const sUl = document.createElement("ul");
        sDet.open = !!ft;
        const seasonMatch = s.name.match(/^Season\s*(\d+)$/i);
        const isSpecials = /^Specials$/i.test(s.name.trim());
        const seasonId = seasonMatch ? seasonMatch[1] : isSpecials ? "0" : null;
        const seasonLink = seasonId
          ? `<a href="${base}/metadata/${g.id}s${seasonId}?includeChildren=1" class="vfs-link" target="_blank" rel="noopener noreferrer"><small>[m${g.id}s${seasonId}]</small></a>`
          : "";
        sSum.innerHTML = `<span class="tree-icon expand"></span><span class="tree-icon collapse"></span>${s.name} ${seasonLink}`;

        s.files.forEach((f) => {
          const eLi = document.createElement("li");
          const leaf = document.createElement("div");
          leaf.className = "leaf";
          leaf.innerHTML = `<span>${f.name}</span><span class="source-path">${f.source || "Unknown"}</span>`;
          eLi.appendChild(leaf);
          sUl.appendChild(eLi);
        });

        sDet.append(sSum, sUl);
        sLi.appendChild(sDet);
        ul.appendChild(sLi);
      });

      det.append(sum, ul);
      li.appendChild(det);
      rootUl.appendChild(li);
    });

    playerTree.appendChild(rootUl);
    if (vfsCount) vfsCount.textContent = `${items.length} Series`;
  }

  // #endregion

  // #region Initialization
  initSearchInteractions(uiFilter, uiFilterClear, () => renderActiveRoot());

  (async () => {
    const res = await fetchJson(base + "/vfs/tree");
    if (res.ok) {
      vfsRoots = getData(res)?.roots || [];
      renderTabs();
      renderActiveRoot();
    } else if (playerTree) playerTree.innerHTML = '<div class="placeholder">Failed to load VFS blueprint.</div>';
  })();
  // #endregion
})();
