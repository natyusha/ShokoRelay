/**
 * @file shoko.js
 * @description Dedicated logic for Shoko VFS and Automation tasks on the Shoko Relay dashboard.
 */
(() => {
  const { base, configUrl, element, fetchJson, showToast, toastOperation, summarizeResult, initToggle, openModal, setButtonLoading, getData } = window._sr;

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
  /**
   * Initializes the VFS Overrides Editor modal and binds load/save handlers.
   * @returns {void}
   */
  function initVfsOverridesModal() {
    const overridesBtn = element("vfs-overrides");
    if (!overridesBtn) return;

    const overridesText = element("overrides-text");
    overridesBtn.onclick = async () => {
      const modal = element("overrides-modal");

      // Load current overrides content from server config.
      try {
        const configResponse = await fetchJson(configUrl);
        if (configResponse.isOk) overridesText.value = configResponse.data?.overrides || "";
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
   * Initializes the Sync Watched States modal controls and triggers.
   * @returns {void}
   */
  function initSyncWatchedModal() {
    const triggerButton = element("shoko-sync-watched");
    if (!triggerButton) return;

    triggerButton.onclick = () => {
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

      if (syncUsersSelect) window._sr.updateControlStates?.();

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

        const usersValue = element("sync-users").value;
        const usersMap = { 0: "all", 1: "admin", 2: "extra", 3: "advanced", 4: "none" };
        queryParams.set("users", usersMap[usersValue] || "none");

        const libraryName = element("sync-library")?.value.trim();
        if (libraryName) queryParams.set("libraryName", libraryName);
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
  initToggle("vfs-clean", true);
  initVfsOverridesModal();

  if (syncUsersSelect) syncUsersSelect.addEventListener("change", () => window._sr.updateControlStates?.());

  initSyncWatchedModal();
  window._sr.bindSeriesFilterSanitizer("vfs-filter");
  // #endregion
})();
