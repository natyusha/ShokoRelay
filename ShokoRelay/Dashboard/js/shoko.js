/**
 * @file shoko.js
 * @description Dedicated logic for Shoko VFS and Automation tasks on the Shoko Relay dashboard.
 */
(() => {
  const { base, el, TOAST_MS, fetchJson, showToast, toastOperation, summarizeResult, withButtonAction, initToggle, setIfNotEmpty, openModal, setButtonLoading, syncActiveTasks } = window._sr;

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
   * @returns {URLSearchParams} The compiled search parameters.
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
  withButtonAction(window._sr.tasks.vfsBuild, async () => {
    const clean = el("vfs-clean")?.getAttribute("aria-pressed") === "true";
    const filter = el("vfs-filter")?.value.trim();

    const startToast = showToast(`VFS: Generating... [clean=${clean}]`, "info", 0);
    const res = await fetchJson(`${base}/vfs?${buildVfsParams()}`);
    startToast?.remove();

    // Persist toast if no filter is applied (full generation)
    const hideOnSucceed = filter ? TOAST_MS : 0;

    // Use custom summary to handle the PascalCase properties from VfsBuildResult
    const summary = res.ok ? `processed ${res.data.data.SeriesProcessed}, created ${res.data.data.CreatedLinks}` : undefined;

    toastOperation(res, `VFS [clean=${clean}]`, { summary, hideOnSucceed });
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
  const removeBtn = el(window._sr.tasks.shokoRemoveMissing);
  if (removeBtn) {
    removeBtn.onclick = () => {
      const modal = el("confirm-modal");
      const msg = el("confirm-message");
      const execBtn = el("confirm-exec");
      const cancelBtn = el("confirm-cancel");

      msg.innerHTML = "Are you sure you want to remove all records for missing files?<br><br><small>This will permanently remove them from Shoko's database and your AniDB MyList.</small>";
      execBtn.textContent = "Remove Files";

      const close = openModal(modal);

      execBtn.onclick = async () => {
        close();

        // Manually trigger the loading state on the dashboard button only after confirmation
        setButtonLoading(removeBtn, true);
        removeBtn.classList.add("clicking");

        try {
          showToast("Remove Missing: Processing...", "info", TOAST_MS);
          const res = await fetchJson(base + "/shoko/remove-missing?dryRun=false", { method: "POST" });

          // Persistent toast for completion
          toastOperation(res, "Remove Missing", { hideOnSucceed: 0 });

          // Explicitly clear the task on the server to prevent the background poller from showing a second toast
          await fetch(base + "/tasks/clear/" + window._sr.tasks.shokoRemoveMissing, { method: "POST" });
        } finally {
          removeBtn.classList.remove("clicking");
          setButtonLoading(removeBtn, false);
          syncActiveTasks();
        }
      };

      cancelBtn.onclick = close;
    };
  }

  // Trigger a Shoko import detection scan on managed folders.
  withButtonAction("shoko-import-run", async () => {
    showToast("Shoko Import: Scanning...", "info", TOAST_MS);
    const res = await fetchJson(base + "/shoko/import", { method: "POST" });
    // summarizeResult handles the envelope internally
    toastOperation(res, "Shoko Import");
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
          // Drill into nested PascalCase properties for the sync result
          const d = res.data.data;
          const summaryText = `${summarizeResult(res).text || `processed ${d.Processed ?? 0}`}${d.VotesFound ? `, votes: ${d.VotesFound}` : ""}`;
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
