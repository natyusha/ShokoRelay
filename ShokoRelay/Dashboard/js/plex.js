/**
 * @file plex.js
 * @description Dedicated logic for the Plex Auth and Automations on the Shoko Relay dashboard.
 */
(() => {
  const { base, el, fetchJson, showToast, unwrapConfig, saveSettings } = window._sr;

  /**
   * Enable or disable all Plex-dependent automation controls on the dashboard.
   * @param {boolean} enabled - True to enable, false to disable.
   */
  const setPlexAutomationControls = (enabled) =>
    document.querySelectorAll(".plex-auth").forEach((e) => {
      e.disabled = !enabled;
    });

  // #region Authentication
  let plexPinId = "",
    plexPollTimer = null;

  /**
   * Set the HTML content of the Plex authentication container.
   * @param {string} html - HTML string.
   */
  const setPlexAction = (html) => {
    const c = el("plex-auth-action");
    if (c) c.innerHTML = html;
  };

  /** Resets the Plex section to show the 'Start Auth' button. */
  const setPlexStartAction = () => {
    setPlexAction('<button id="plex-start">Start Plex Auth</button>');
    window._sr.withButtonAction("plex-start", startPlexAuth);
  };

  /** Updates the Plex section to show 'Unlink' and 'Refresh' buttons. */
  const setPlexUnlinkAction = () => {
    setPlexAction(
      '<button id="plex-unlink" class="danger">Unlink Plex</button>' +
        '<button id="plex-refresh" class="w46-button" data-relay-endpoint="/plex/auth/refresh" data-relay-method="POST" data-relay-label="Plex Discovery"><svg class="icon-svg"><use href="img/icons.svg#refresh"></use></svg></button>',
    );
    el("plex-unlink").onclick = unlinkPlex;
  };

  /** Stops the active Plex authentication polling timer. */
  const stopPlexPolling = () => {
    if (plexPollTimer) {
      clearInterval(plexPollTimer);
      plexPollTimer = null;
    }
  };

  /**
   * Initiate the Plex OAuth flow and start polling for status.
   * @returns {Promise<void>}
   */
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

  /**
   * Updates the UI text indicating the number of discovered libraries.
   * @param {Array} [libs=[]] - Array of discovered Plex library objects.
   */
  const updateLibraryCount = (libs = []) => {
    if (el("plex-libraries-count")) el("plex-libraries-count").textContent = libs.length;
  };

  /**
   * Refresh the full Plex authentication and settings state from the server.
   * @returns {Promise<void>}
   */
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

    // Enable or disable automation buttons based on whether we have a token
    setPlexAutomationControls(!!plex.HasToken);

    // Helper to bind inputs to the Automation nested object
    const b = (id, path, type) => window._sr.bindConfig(id, path, settings, saveSettings, type);

    b("extra-plex-users", "Automation.ExtraPlexUsers", "text");
    b("plex-scan-vfs", "Automation.ScanOnVfsRefresh", "check");
    b("plex-scrobble", "Automation.AutoScrobble", "check");

    // Toggle between Start Auth and Unlink buttons
    if (!plex.HasToken) return setPlexStartAction();
    setPlexUnlinkAction();

    const libCount = el("plex-libraries-count");
    if (libCount) libCount.textContent = (plex.DiscoveredLibraries || []).length;
  }

  /**
   * Unlink the Plex account and refresh dashboard state.
   * @returns {Promise<void>}
   */
  async function unlinkPlex() {
    await fetchJson(base + "/plex/auth/unlink", { method: "POST" });
    refreshPlexState();
  }
  // #endregion

  // Initialize
  setPlexStartAction();
  setPlexAutomationControls(false);
  refreshPlexState();
})();
