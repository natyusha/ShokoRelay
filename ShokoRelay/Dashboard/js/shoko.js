/**
 * @file shoko.js
 * @description Dedicated logic for Shoko VFS and Automation tasks on the Shoko Relay dashboard.
 */
(() => {
  const { base, el, TOAST_MS, fetchJson, showToast, toastOperation, summarizeResult, withButtonAction, initToggle, setIfNotEmpty, openModal, setButtonLoading } = window._sr;

  // #region Helpers
  /**
   * Helper to set a boolean query parameter as a "true"/"false" string.
   * @param {URLSearchParams} ps - The search parameters object.
   * @param {string} k - The parameter key.
   * @param {boolean} v - The boolean value to convert.
   */
  const setBoolParam = (ps, k, v) => ps.set(k, v ? "true" : "false");

  /**
   * Build URLSearchParams for VFS generation requests from the dashboard filter and toggle state.
   * @returns {URLSearchParams}
   */
  const buildVfsParams = () => {
    const ps = new URLSearchParams();
    setIfNotEmpty(ps, "filter", el("vfs-filter")?.value);
    setBoolParam(ps, "clean", el("vfs-clean")?.getAttribute("aria-pressed") === "true");
    setBoolParam(ps, "run", true);
    return ps;
  };
  // #endregion

  // #region Shoko: VFS

  // Initialize the VFS Clean toggle (broom icon) and bind the Generate button action.
  initToggle("vfs-clean", true);
  withButtonAction("vfs-exec", async () => {
    const clean = el("vfs-clean")?.getAttribute("aria-pressed") === "true";
    const filter = el("vfs-filter")?.value.trim();

    const startToast = showToast(`VFS: Generating (clean=${clean})...`, "info", 0);
    const res = await fetchJson(`${base}/vfs?${buildVfsParams()}`);
    startToast?.remove();

    // Persist toast if no filter is applied (full generation)
    const hideOnSucceed = filter ? TOAST_MS : 0;
    toastOperation(res, `VFS (clean=${clean})`, { hideOnSucceed });
  });

  // Wire up the VFS Overrides Editor modal.
  const overridesBtn = el("vfs-overrides");
  if (overridesBtn) {
    overridesBtn.onclick = async () => {
      const modal = el("overrides-modal"),
        txt = el("overrides-text");

      // Load current overrides content from server config.
      try {
        const cfgRes = await fetchJson(base + "/config");
        if (cfgRes.ok) txt.value = cfgRes.data?.overrides || "";
      } catch {}

      if (!txt.value.trim())
        txt.placeholder =
          "This allows shows which are separated on AniDB but part of the same TMDB listing to be combined into a single entry in Plex. Each line should contain a " +
          "comma separated list of AniDB IDs you wish to merge. The first ID is the primary series and the others will be merged into it " +
          "(for both VFS builds and metadata lookups). Lines that are blank or start with a '#' are ignored. An example is shown below:" +
          "\n\n## Shoko Relay VFS Overrides\n\n# Fairy Tail\n6662,8132,9980,13295\n\n# Bleach\n2369,15449,17765,18220,19079";

      const close = openModal(modal);
      el("overrides-cancel").onclick = close;
      el("overrides-save").onclick = async () => {
        const res = await fetchJson(base + "/vfs/overrides", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(txt.value),
        });

        if (res.ok) {
          showToast("VFS Overrides Saved", "success", TOAST_MS);
          close();
        } else {
          toastOperation(res, "VFS Overrides");
        }
      };
    };
  }
  // #endregion

  // #region Shoko: Automation

  // Remove records for files no longer present on disk.
  withButtonAction("shoko-remove-missing", async () => {
    showToast("Remove Missing: Processing...", "info", TOAST_MS);
    const res = await fetchJson(base + "/shoko/remove-missing?dryRun=false", { method: "POST" });
    toastOperation(res, "Remove Missing", {
      summary: typeof res.data?.count === "number" ? `removed ${res.data.count}` : undefined,
    });
  });

  // Trigger a Shoko import detection scan on managed folders.
  withButtonAction("shoko-import-run", async () => {
    showToast("Shoko Import: Scanning...", "info", TOAST_MS);
    const res = await fetchJson(base + "/shoko/import", { method: "POST" });
    toastOperation(res, "Shoko Import", {
      summary: summarizeResult(res) || `scanned ${res.data?.scannedCount ?? ""}`,
    });
  });

  // Managed User and Admin Watched-State Synchronization Modal.
  if (el("shoko-sync-watched")) {
    el("shoko-sync-watched").onclick = () => {
      const modal = el("sync-modal"),
        startBtn = el("sync-start-button"),
        dirToggle = el("sync-direction-toggle"),
        dirArrow = el("sync-direction-arrow");

      /** @type {boolean} dirImport - true if syncing Plex to Shoko, false for Shoko to Plex. */
      let dirImport = localStorage.getItem("shoko-sync-direction") === "import";

      /** Updates the UI arrow and state based on the direction toggle. */
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

      startBtn.onclick = async () => {
        setButtonLoading(startBtn, true);
        const startToast = showToast(`Sync: Plex ${dirImport ? "←" : "→"} Shoko...`, "info", 0);

        const ps = new URLSearchParams({
          dryRun: "false",
          ratings: el("sync-ratings").checked,
          excludeAdmin: el("sync-exclude-admin").checked,
        });

        if (dirImport) ps.set("import", "true");

        const res = await fetchJson(`${base}/sync-watched?${ps}`);
        startToast?.remove();
        setButtonLoading(startBtn, false);

        if (res.ok) {
          const summaryText = `${summarizeResult(res) || `processed ${res.data?.processed ?? 0}`}${res.data?.votesFound ? `, votes: ${res.data.votesFound}` : ""}`;
          toastOperation(res, "Sync", { summary: summaryText });
          close();
        } else {
          toastOperation(res, "Sync");
        }
      };
      el("sync-cancel-button").onclick = close;
    };
  }
  // #endregion
})();
