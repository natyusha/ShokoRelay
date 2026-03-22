/**
 * @file shoko.js
 * @description Dedicated logic for Shoko VFS and Automation tasks on the Shoko Relay dashboard.
 */
(() => {
  const { base, configUrl, el, fetchJson, showToast, toastOperation, summarizeResult, initToggle, openModal, setButtonLoading, getData } = window._sr;

  /** Set the dynamic href for the Shoko Dashboard link based on the current origin. */
  const dashLink = el("shoko-dashboard-link");
  if (dashLink) {
    dashLink.href = location.origin + "/webui/dashboard";
  }

  /** Placeholder text for the VFS Overrides Editor, concatenated to stay under the column limit. */
  const OVERRIDES_PLACEHOLDER =
    "This allows shows which are separated on AniDB but part of the same TMDB listing to be combined into a single entry in Plex.\n" +
    "Each line should contain a comma separated list of AniDB IDs you wish to merge.\n" +
    "The first ID is the primary series and the others will be merged into it (for both VFS builds and metadata lookups).\n" +
    "Lines that are blank or start with a '#' are ignored. An example is shown below:\n\n" +
    "## Shoko Relay VFS Overrides\n\n# Fairy Tail\n6662,8132,9980,13295\n\n# Bleach\n2369,15449,17765,18220,19079";

  // #region Param Providers

  /**
   * Collects VFS build parameters for the Action Dispatcher.
   * @returns {URLSearchParams} The compiled parameters.
   */
  window._sr.getVfsParams = () => {
    const ps = new URLSearchParams();
    const filter = el("vfs-filter")?.value;
    const clean = el("vfs-clean")?.getAttribute("aria-pressed") === "true";
    if (filter) ps.set("filter", filter);
    ps.set("clean", clean);
    ps.set("run", "true");
    return ps;
  };
  // #endregion

  // #region Shoko: VFS
  // Initialize the VFS Clean toggle (broom icon).
  initToggle("vfs-clean", true);

  // Wire up the VFS Overrides Editor modal.
  const overridesBtn = el("vfs-overrides");
  if (overridesBtn) {
    const txt = el("overrides-text");
    if (txt) txt.placeholder = OVERRIDES_PLACEHOLDER;
    overridesBtn.onclick = async () => {
      const modal = el("overrides-modal");

      // Load current overrides content from server config.
      try {
        const cfgRes = await fetchJson(configUrl);
        if (cfgRes.ok) txt.value = cfgRes.data?.overrides || "";
      } catch {}
      const close = openModal(modal);
      el("overrides-cancel").onclick = close;
      el("overrides-save").onclick = async () => {
        const res = await fetchJson(base + "/vfs/overrides", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(txt.value) });
        if (res.ok) {
          showToast("VFS Overrides Saved", "success");
          close();
        }
      };
    };
  }
  // #endregion

  // #region Shoko: Automation
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
          const d = getData(res);

          // Generate a robust summary combining standard counts and custom "Votes" data
          const summary = (summarizeResult(res).text || `processed ${d.Processed ?? 0}`) + (d.VotesFound ? `, votes: ${d.VotesFound}` : "");

          toastOperation(res, "Sync", { summary, hideOnSucceed: 0 });
          close();
        } else {
          toastOperation(res, "Sync", { hideOnSucceed: 0 });
        }
      };
      el("sync-cancel-button").onclick = close;
    };
  }
  // #endregion
})();
