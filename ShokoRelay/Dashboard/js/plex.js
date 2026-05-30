/**
 * @file plex.js
 * @description Dedicated logic for the Plex Auth and Automations on the Shoko Relay dashboard.
 */
(() => {
  const { base, configUrl, el, fetchJson, unwrapConfig, saveSettings, getData, withButtonAction } = window._sr;

  let plexPinId = "";
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
    const authAction = el("plex-auth-action");
    if (authAction) authAction.querySelectorAll(".plex-auth-state").forEach((e) => (e.style.display = e.tagName === "BUTTON" ? "" : "none"));
    el("plex-login")?.remove();
    withButtonAction("plex-start", startPlexAuth);
  }
  // #endregion

  // #region Logic
  /**
   * Initiate the Plex OAuth flow and start polling for status.
   * @returns {Promise<void>}
   */
  async function startPlexAuth() {
    const res = await fetchJson(base + "/plex/auth");
    const d = getData(res);

    if (!res.ok || !d?.pinId || !d?.authUrl) return setPlexStartAction();

    plexPinId = d.pinId;
    const authAction = el("plex-auth-action");
    if (authAction) {
      // Hide the initial buttons while the login link is active
      authAction.querySelectorAll(".plex-auth-state").forEach((e) => (e.style.display = "none"));

      // Create or update the login link
      let loginLink = el("plex-login");
      if (!loginLink) {
        loginLink = document.createElement("a");
        loginLink.id = "plex-login";
        loginLink.target = "_blank";
        authAction.appendChild(loginLink);
      }
      loginLink.href = d.authUrl;
      loginLink.textContent = "Login";
    }

    stopPlexPolling();
    plexPollTimer = setInterval(async () => {
      const sRes = await fetchJson(`${base}/plex/auth/status?pinId=${encodeURIComponent(plexPinId)}`);
      const sData = getData(sRes);

      if (sRes.ok && sData?.tokenSaved) {
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
    const res = await fetchJson(configUrl);
    if (!res.ok) {
      window._sr.isPlexLinked = false;
      window._sr.updateControlStates?.();
      return;
    }

    const settings = (window.relaySettings = unwrapConfig(res.data));
    const plex = settings.PlexLibrary || {};
    const isLinked = !!plex.HasToken;

    window._sr.isPlexLinked = isLinked;
    window._sr.updateControlStates?.(settings);

    const b = (id, path, type) => window._sr.bindConfig(id, path, settings, saveSettings, type);
    b("extra-plex-users", "Automation.ExtraPlexUsers", "text");
    b("plex-scan-vfs", "Automation.ScanOnVfsRefresh", "check");
    b("plex-scrobble", "Automation.AutoScrobble", "check");

    const authAction = el("plex-auth-action");
    if (authAction) authAction.querySelectorAll(".plex-auth-state").forEach((e) => (e.style.display = (isLinked ? e.tagName === "DIV" : e.tagName === "BUTTON") ? "" : "none"));

    if (!isLinked) {
      withButtonAction("plex-start", startPlexAuth);
    } else {
      el("plex-login")?.remove();
      const unlinkBtn = el("plex-unlink");
      if (unlinkBtn) unlinkBtn.onclick = unlinkPlex;
    }

    const libStatus = el("plex-libraries-status");
    if (libStatus) {
      const count = (plex.DiscoveredLibraries || []).length;
      let text = "(No Libraries Detected)";
      if (count === 1) text = "(1 Library)";
      else if (count > 1) text = `(${count} Libraries)`;
      libStatus.textContent = text;
    }

    // Resolve the Plex Web App link dynamically from discovered targets
    const plexWebUrl = plex.DiscoveredLibraries?.[0]?.ServerUrl || plex.DiscoveredServers?.[0]?.PreferredUri || "";
    const webLink = el("plex-web-link");
    if (webLink) webLink.onclick = () => plexWebUrl && window.open(plexWebUrl, "_blank", "noopener,noreferrer");
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

  //Initialization
  refreshPlexState();
})();
