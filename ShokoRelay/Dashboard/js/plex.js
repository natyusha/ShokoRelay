/**
 * @file plex.js
 * @description Dedicated logic for the Plex Auth and Automations on the Shoko Relay dashboard.
 */
(() => {
  const { base, el, fetchJson, showToast, toastOperation, withButtonAction, unwrapConfig } = window._sr;

  /** Enable or disable all Plex-dependent automation controls on the dashboard. */
  const setPlexAutomationControls = (enabled) =>
    document.querySelectorAll(".plex-auth").forEach((e) => {
      e.disabled = !enabled;
    });

  // #region Authentication
  let plexPinId = "",
    plexPollTimer = null;

  const setPlexAction = (html) => {
    const c = el("plex-auth-action");
    if (c) c.innerHTML = html;
  };

  const setPlexStartAction = () => {
    setPlexAction('<button id="plex-start">Start Plex Auth</button>');
    withButtonAction(el("plex-start"), startPlexAuth);
  };

  const setPlexUnlinkAction = () => {
    setPlexAction(
      '<button id="plex-unlink" class="danger">Unlink Plex</button><button id="plex-refresh" class="w46-button" title="Refresh Plex Libraries"><svg class="icon-svg"><use href="img/icons.svg#refresh"></use></svg></button>',
    );
    el("plex-unlink").onclick = unlinkPlex;
    withButtonAction(el("plex-refresh"), refreshPlexLibraries);
  };

  const stopPlexPolling = () => {
    if (plexPollTimer) {
      clearInterval(plexPollTimer);
      plexPollTimer = null;
    }
  };

  /** Initiate the Plex OAuth flow by fetching a pin and redirecting the user to the auth URL. */
  async function startPlexAuth() {
    const res = await fetchJson(base + "/plex/auth");
    if (!res.ok || !res.data.pinId || !res.data.authUrl) return setPlexStartAction();

    plexPinId = res.data.pinId;
    setPlexAction(`<a id="plex-login" href="${res.data.authUrl}" target="_blank" class="plex-login-link">Login</a>`);
    stopPlexPolling();
    plexPollTimer = setInterval(async () => {
      const s = await fetchJson(`${base}/plex/auth/status?pinId=${encodeURIComponent(plexPinId)}`);
      if (s.ok && s.data?.status === "ok") {
        plexPinId = "";
        stopPlexPolling();
        refreshPlexState();
      }
    }, 2000);
  }

  /** Poll the server for Plex OAuth completion; stops polling and refreshes state on success. */
  async function checkPlexAuthStatus() {
    /* Integrated into polling loop above for conciseness */
  }

  /** POST to the server to rediscover Plex libraries, then refresh the dashboard state. */
  async function refreshPlexLibraries() {
    try {
      const rr = await fetchJson(base + "/plex/auth/refresh", { method: "POST" });
      if (rr.ok) {
        await refreshPlexState();
        showToast("Plex Libraries Refreshed", "success");
      } else showToast("Failed To Refresh Plex Libraries", "error");
    } catch (e) {
      showToast("Failed To Refresh Plex Libraries", "error");
    }
  }

  /** @param {Array} libraries - Array of discovered Plex library objects. */
  const updateLibraryCount = (libs = []) => {
    if (el("plex-libraries-count")) el("plex-libraries-count").textContent = libs.length;
  };

  /**
   * Validate that Plex is linked and libraries are available before running automation.
   * @returns {Promise<boolean>} True if Plex is ready for automation, false otherwise.
   */
  async function ensurePlexEnabled() {
    const fail = (msg) => {
      showToast(msg, "error");
      setPlexAutomationControls(false);
      return false;
    };
    try {
      const res = await fetchJson(base + "/config");
      const plex = unwrapConfig(res.data).PlexLibrary || {};
      if (!plex.HasToken) return fail("Plex Token Missing - Link Plex First");
      if (!plex.DiscoveredLibraries?.length) return fail("No Plex Libraries Found - Refresh Libraries");
      setPlexAutomationControls(true);
      return true;
    } catch (e) {
      return fail("Error checking Plex config");
    }
  }

  /** Refresh the full Plex authentication state (token, libraries, settings bindings) from the server config. */
  async function refreshPlexState() {
    updateLibraryCount();
    const res = await fetchJson(base + "/config");
    if (!res.ok) {
      setPlexAutomationControls(false);
      setPlexStartAction();
      return;
    }

    const settings = (window.relaySettings = unwrapConfig(res.data));
    const plex = settings.PlexLibrary || {};
    setPlexAutomationControls(!!plex.HasToken);

    const bind = (id, prop, val) => {
      const e = el(id);
      if (!e) return;
      e[prop] = val;
      e.onchange = savePlexSettings;
    };

    bind("extra-plex-users", "value", settings.ExtraPlexUsers || "");
    bind("plex-scan-vfs", "checked", !!settings.ScanOnVfsRefresh);
    bind("plex-watched", "checked", !!settings.AutoScrobble);

    if (!plex.HasToken) return setPlexStartAction();
    setPlexUnlinkAction();

    const mapLib = (l) => ({ id: l.Id, title: l.Title, type: l.Type, uuid: l.Uuid });
    let libs = (plex.DiscoveredLibraries || []).map(mapLib);

    if (!libs.length && plex.DiscoveredServers?.length) {
      const rr = await fetchJson(base + "/plex/auth/refresh", { method: "POST" });
      if (rr.ok && rr.data?.libraries) libs = rr.data.libraries.map(mapLib);
    }
    updateLibraryCount(libs);
  }

  /** Unlink the Plex account by posting to the server and refreshing the dashboard state. */
  async function unlinkPlex() {
    await fetchJson(base + "/plex/auth/unlink", { method: "POST" });
    refreshPlexState();
  }
  // #endregion

  // #region Automation
  /**
   * Read current config, merge Plex-specific settings from the dashboard, and POST the result.
   * @returns {Promise<boolean>} True if the settings were saved successfully.
   */
  async function savePlexSettings() {
    try {
      const res = await fetchJson(base + "/config");
      const cfg = unwrapConfig(res.data);
      cfg.ScanOnVfsRefresh = !!el("plex-scan-vfs")?.checked;
      cfg.ExtraPlexUsers = el("extra-plex-users")?.value || "";
      cfg.AutoScrobble = !!el("plex-watched")?.checked;
      cfg.ShokoSyncWatchedExcludeAdmin = !!el("sync-exclude-admin")?.checked;

      // Clean up sensitive/redundant objects before saving
      delete cfg.PlexLibrary;
      delete cfg.PlexAuth;

      const save = await fetchJson(base + "/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(cfg) });
      if (save.ok) showToast("Plex Settings Saved", "success");
      else showToast("Plex Settings Save Failed", "error");
      return save.ok;
    } catch (e) {
      showToast("Plex Settings Save Failed", "error");
      return false;
    }
  }

  /**
   * Create a click handler for Plex automation buttons (collections, ratings) with toast feedback.
   * @param {HTMLElement} btn - The button element to bind the action to.
   * @param {string} label - Display label for toast messages.
   * @param {string} startMsg - Toast message shown when the action begins.
   * @param {string} endpoint - Server API endpoint to call.
   * @param {Function} summarizeFn - Callback that receives response data and returns a summary string.
   */
  function plexBuildAction(btn, label, startMsg, endpoint, summarizeFn) {
    withButtonAction(btn, async () => {
      const startToast = showToast(startMsg, "info", 0);
      if (!(await ensurePlexEnabled())) return;

      const res = await fetchJson(base + endpoint);
      startToast?.remove();

      if (res.ok && res.data?.status === "ok") {
        toastOperation(res, label, { summary: summarizeFn(res.data) });
      } else {
        toastOperation({ ok: false, data: res.data || res }, label, { summary: res.data?.message || JSON.stringify(res.data) });
      }
    });
  }

  plexBuildAction(
    el("col-build"),
    "Collections",
    "Generating Collections...",
    "/plex/collections/build",
    (d) => `processed ${d.processed}, created ${d.created}${d.uploaded ? `, uploaded ${d.uploaded}` : ""}, skipped ${d.skipped}, errors ${d.errors}`,
  );

  plexBuildAction(
    el("rtg-build"),
    "Critic Ratings",
    "Applying Critic Ratings...",
    "/plex/ratings/apply",
    (d) => `shows ${d.processedShows}/${d.updatedShows}, episodes ${d.processedEpisodes}/${d.updatedEpisodes}`,
  );
  // #endregion

  // Initialize
  setPlexStartAction();
  setPlexAutomationControls(false);
  refreshPlexState();
})();
