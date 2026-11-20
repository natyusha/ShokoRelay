/**
 * @file shoko.js
 * @description Dedicated logic for Shoko VFS and Automation tasks on the Shoko Relay dashboard.
 */
(() => {
  const { base, shokoBase, configUrl, element, fetchJson, showToast, toastOperation, summarizeResult, initToggle, openModal, setButtonLoading, getData } = window._sr;

  // #region Helpers
  /**
   * Set the dynamic href for Shoko system links based on the current origin.
   * @returns {void}
   */
  const resolveSystemLinks = () => {
    const links = {
      "relay-api-link": "/swagger/index.html?urls.primaryName=Shoko+Relay+V1",
      "shoko-api-link": "/swagger/index.html",
      "shoko-dashboard-link": "/webui/dashboard",
    };

    Object.entries(links).forEach(([id, path]) => {
      const anchor = element(id);
      if (anchor) anchor.href = shokoBase + path;
    });
  };
  resolveSystemLinks();

  /** @type {string} Placeholder text for the VFS Overrides Editor. */
  const OVERRIDES_PLACEHOLDER =
    "This allows shows which are separated on AniDB but part of the same TMDB listing to be combined into a single entry in Plex.\n" +
    "Each line should contain a comma separated list of AniDB IDs you wish to merge.\n" +
    "The first ID is the primary series and the others will be merged into it (for both VFS builds and metadata lookups).\n" +
    "If the series do not share a TMDB link, you must append a specific season and episode starting number: ';S##E##'.\n" +
    "You can also map specific episodes, types, and ranges using '|' as a separator: ';1-10s1e1|11-20s2e1|s1s0e101|o1s0e102'.\n" +
    "Lines that are blank or start with a '#' are ignored. An example is shown below:\n\n" +
    "## Shoko Relay VFS Overrides\n\n# Fairy Tail (Shared TMDB Link)\n6662,8132,9980,13295\n\n" +
    "# Bleach (Movies as Specials)\n2369,4515;s0e101,5352;s0e102,6041;s0e103,7666;s0e104\n\n" +
    "# Fairy Tail (Manual Assignment)\n6662;1-48s1e1|49-96s2e1|97-150s3e1|151-175s4e1,8132;s0e1,9980;1-51s5e1|52-90s6e1|91-102s7e1,13295;s8e1";

  /**
   * Initializes real-time input sanitization event listeners for Shoko inputs.
   * @returns {void}
   */
  function initShokoSanitizers() {
    const vfsFilter = element("vfs-filter");
    if (vfsFilter) {
      vfsFilter.oninput = () => {
        vfsFilter.value = vfsFilter.value.replace(/[^aA0-9,\s]/g, "").toLowerCase();
      };
    }
  }
  // #endregion

  // #region Param Providers
  /**
   * Collects VFS build parameters for the Action Dispatcher.
   * @returns {URLSearchParams} The compiled parameters.
   */
  window._sr.getVfsParams = () => {
    const params = new URLSearchParams();
    const filter = element("vfs-filter")?.value;
    const clean = element("vfs-clean")?.getAttribute("aria-pressed") === "true";
    if (filter) params.set("filter", filter);
    params.set("clean", clean);
    params.set("run", "true");
    return params;
  };
  // #endregion

  // #region Shoko: VFS
  // Initialize the VFS Clean toggle (broom icon).
  initToggle("vfs-clean", true);

  // Wire up the VFS Overrides Editor modal.
  const overridesBtn = element("vfs-overrides");
  if (overridesBtn) {
    const overridesText = element("overrides-text");
    if (overridesText) overridesText.placeholder = OVERRIDES_PLACEHOLDER;
    overridesBtn.onclick = async () => {
      const modal = element("overrides-modal");

      // Load current overrides content from server config.
      try {
        const cfgRes = await fetchJson(configUrl);
        if (cfgRes.isOk) overridesText.value = cfgRes.data?.overrides || "";
      } catch {}
      const close = openModal(modal);
      element("overrides-cancel").onclick = close;
      element("overrides-save").onclick = async () => {
        const response = await fetchJson(base + "/vfs/overrides", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(overridesText.value) });
        if (response.isOk) {
          showToast("VFS Overrides Saved", "success");
          close();
        }
      };
    };
  }
  // #endregion

  // #region Shoko: Automation
  /** @type {HTMLSelectElement} Dropdown element for configuring synchronized users. */
  const syncUsersSelect = element("sync-users");

  /**
   * Refreshes the active control states when the sync users settings change.
   * @returns {void}
   */
  const updateSyncUserState = () => {
    window._sr.updateControlStates?.();
  };

  window._sr = window._sr || {};
  window._sr.updateSyncUserState = updateSyncUserState;

  if (syncUsersSelect) {
    syncUsersSelect.addEventListener("change", updateSyncUserState);
    updateSyncUserState();
  }

  // Managed User and Admin Watched-State Synchronization Modal.
  if (element("shoko-sync-watched")) {
    element("shoko-sync-watched").onclick = () => {
      const modal = element("sync-modal");
      const startBtn = element("sync-start-button");
      const dryBtn = element("sync-dry-button");
      const dirToggle = element("sync-direction-toggle");
      const dirArrow = element("sync-direction-arrow");

      /** @type {boolean} True if syncing Plex to Shoko, false for Shoko to Plex. */
      let dirImport = localStorage.getItem("shoko-sync-direction") === "import";

      /**
       * Updates the UI arrow and state based on the direction toggle.
       * @returns {void}
       */
      const updateDir = () => {
        dirToggle.setAttribute("aria-pressed", String(dirImport));
        dirArrow.querySelector(".dir-icon-right")?.classList.toggle("hidden", dirImport);
        dirArrow.querySelector(".dir-icon-left")?.classList.toggle("hidden", !dirImport);
      };
      updateDir();
      const close = openModal(modal);
      dirToggle.onclick = () => {
        dirImport = !dirImport;
        updateDir();
        localStorage.setItem("shoko-sync-direction", dirImport ? "import" : "export");
      };

      if (syncUsersSelect) updateSyncUserState();

      /**
       * Orchestrates the watched state synchronization request.
       * @param {boolean} isDryRun - Whether to perform a dry run preview.
       * @returns {Promise<void>}
       */
      const performSync = async (isDryRun) => {
        const targetBtn = isDryRun ? dryBtn : startBtn;
        setButtonLoading(targetBtn, true);
        if (document.activeElement && ["INPUT", "SELECT", "TEXTAREA"].includes(document.activeElement.tagName)) document.activeElement.blur();
        await window._sr.flushSaves?.();
        const startToast = showToast(`Sync: Plex ${dirImport ? "←" : "→"} Shoko${isDryRun ? " (Dry Run)" : ""}...`, "info", 0);

        const queryParams = new URLSearchParams({
          dryRun: isDryRun,
          ratings: element("sync-ratings").checked,
          progress: element("sync-progress").checked,
        });

        const usersVal = element("sync-users").value;
        const usersMap = { 0: "all", 1: "admin", 2: "extra", 3: "advanced", 4: "none" };
        queryParams.set("users", usersMap[usersVal] || "none");

        const libName = element("sync-library")?.value.trim();
        if (libName) queryParams.set("libraryName", libName);
        if (dirImport) queryParams.set("import", "true");

        const response = await fetchJson(`${base}/sync-watched?${queryParams}`);
        startToast?.remove();
        setButtonLoading(targetBtn, false);

        if (window._sr.tasks.shokoSyncWatched) await fetchJson(`${base}/tasks/clear/${window._sr.tasks.shokoSyncWatched}`, { method: "POST" }, false); // Clear the task immediately on the server to prevent the background poller from firing a duplicate toast
        if (response.isOk) {
          const syncData = getData(response);

          // Generate a robust summary combining standard counts and custom "Votes" data
          const summary = (summarizeResult(response).text || `processed ${syncData.Processed ?? 0}`) + (syncData.VotesFound ? `, votes: ${syncData.VotesFound}` : "");

          toastOperation(response, "Sync", { summary, hideOnSucceed: 0 });
        } else {
          toastOperation(response, "Sync", { hideOnSucceed: 0 });
        }
      };

      startBtn.onclick = () => performSync(false);
      dryBtn.onclick = () => performSync(true);
      element("sync-cancel-button").onclick = close;
    };
  }
  // #endregion

  // #region Initialization
  initShokoSanitizers(); // Initialize sanitizers immediately on script load
  // #endregion
})();
