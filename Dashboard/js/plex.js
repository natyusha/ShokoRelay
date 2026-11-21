/**
 * @file plex.js
 * @description Dedicated logic for the Plex Auth and Automations on the Shoko Relay dashboard.
 */
(() => {
  const { base, configUrl, element, fetchJson, unwrapConfig, saveSettings, getData, withButtonAction } = window._sr;

  /** @type {string} The unique identifier for the current Plex PIN request. */
  let plexPinId = "";
  /** @type {number|null} Reference to the active Plex authentication polling timer. */
  let plexPollTimer = null;

  // #region Helpers
  /**
   * Stops the active Plex authentication polling timer.
   * @returns {void}
   */
  function stopPlexPolling() {
    if (plexPollTimer) {
      clearInterval(plexPollTimer);
      plexPollTimer = null;
    }
  }

  /**
   * Resets the Plex section to show the 'Start Auth' button.
   * @returns {void}
   */
  function setPlexStartAction() {
    stopPlexPolling();
    const authAction = element("plex-auth-action");
    if (authAction) authAction.querySelectorAll(".plex-auth-state").forEach((elem) => (elem.hidden = elem.tagName !== "BUTTON"));
    element("plex-login")?.remove();
    withButtonAction("plex-start", startPlexAuth);
  }
  // #endregion

  // #region Logic
  /**
   * Initiate the Plex OAuth flow and start polling for status.
   * @returns {Promise<void>}
   */
  async function startPlexAuth() {
    const response = await fetchJson(base + "/plex/auth");
    const pinData = getData(response);

    if (!response.isOk || !pinData?.pinId || !pinData?.authUrl) return setPlexStartAction();

    plexPinId = pinData.pinId;
    const authAction = element("plex-auth-action");
    if (authAction) {
      // Hide the initial buttons while the login link is active
      authAction.querySelectorAll(".plex-auth-state").forEach((elem) => (elem.hidden = true));

      // Create or update the login link
      let loginLink = element("plex-login");
      if (!loginLink) {
        loginLink = document.createElement("a");
        loginLink.id = "plex-login";
        loginLink.target = "_blank";
        authAction.appendChild(loginLink);
      }
      loginLink.href = pinData.authUrl;
      loginLink.textContent = "Login";
    }

    stopPlexPolling();
    plexPollTimer = setInterval(async () => {
      const statusResponse = await fetchJson(`${base}/plex/auth/status?pinId=${encodeURIComponent(plexPinId)}`);
      const statusData = getData(statusResponse);

      if (statusResponse.isOk && statusData?.tokenSaved) {
        stopPlexPolling();
        refreshPlexState();
      }
    }, 2000);
  }

  /**
   * Refresh the full Plex authentication and settings state from the server.
   * @returns {Promise<void>}
   */
  async function refreshPlexState() {
    await window._sr.flushSaves?.();
    const response = await fetchJson(configUrl);
    if (!response.isOk) {
      window._sr.isPlexLinked = false;
      window._sr.updateControlStates?.();
      return;
    }

    const settings = window._sr.syncConfig(unwrapConfig(response.data));
    const plex = settings.PlexLibrary || {};
    const isLinked = !!plex.HasToken;

    window._sr.isPlexLinked = isLinked;
    window._sr.updateControlStates?.(settings);

    const bindField = (id, path, type) => window._sr.bindConfig(id, path, settings, saveSettings, type);
    bindField("extra-plex-users", "Automation.ExtraPlexUsers", "text");
    bindField("plex-scan-vfs", "Automation.ScanOnVfsRefresh", "check");
    bindField("plex-scrobble", "Automation.AutoScrobble", "check");

    const authAction = element("plex-auth-action");
    if (authAction) authAction.querySelectorAll(".plex-auth-state").forEach((elem) => (elem.hidden = !(isLinked ? elem.tagName === "DIV" : elem.tagName === "BUTTON")));

    if (!isLinked) {
      withButtonAction("plex-start", startPlexAuth);
    } else {
      element("plex-login")?.remove();
      const unlinkBtn = element("plex-unlink");
      if (unlinkBtn) unlinkBtn.onclick = unlinkPlex;
    }

    const libStatus = element("plex-libraries-status");
    if (libStatus) {
      const count = (plex.DiscoveredLibraries || []).length;
      let text = "(No Libraries Detected)";
      if (count === 1) text = "(1 Library)";
      else if (count > 1) text = `(${count} Libraries)`;
      libStatus.textContent = text;
    }

    // Resolve the Plex Web App link dynamically from discovered targets
    const plexWebUrl = settings.Advanced?.PlexServerUrl || plex.DiscoveredLibraries?.[0]?.ServerUrl || plex.DiscoveredServers?.[0]?.PreferredUri || "";
    const webLink = element("plex-web-link");
    if (webLink) {
      webLink.onclick = () => plexWebUrl && window.open(plexWebUrl, "_blank", "noopener,noreferrer");
      webLink.title = plexWebUrl ? `Open Plex Web @ ${plexWebUrl}` : "Open Plex Web App";
    }
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

  // #region Initialization
  window._sr.refreshPlexState = refreshPlexState;
  (async () => {
    await window._sr.authReady;
    refreshPlexState();
  })();
  // #endregion
})();
