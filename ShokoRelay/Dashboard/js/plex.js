/**
 * @file plex.js
 * @description Dedicated logic for the Plex Auth and Automations on the Shoko Relay dashboard.
 */
(() => {
  const { base, configUrl, el, fetchJson, unwrapConfig, saveSettings, getData, withButtonAction } = window._sr;

  let plexPinId = "";
  let plexPollTimer = null;

  // #region Helpers
  /** Enable or disable all Plex-dependent automation controls on the dashboard. */
  function setPlexAutomationControls(enabled) {
    document.querySelectorAll(".plex-auth").forEach((e) => {
      e.disabled = !enabled;
    });
  }

  /** Stops the active Plex authentication polling timer. */
  function stopPlexPolling() {
    if (plexPollTimer) {
      clearInterval(plexPollTimer);
      plexPollTimer = null;
    }
  }

  /** Resets the Plex section to show the 'Start Auth' button. */
  function setPlexStartAction() {
    stopPlexPolling();
    const authAction = el("plex-auth-action");
    if (authAction) {
      authAction.querySelectorAll(".plex-auth-state").forEach((e) => {
        e.style.display = e.tagName === "BUTTON" ? "" : "none";
      });
    }
    el("plex-login")?.remove();
    withButtonAction("plex-start", startPlexAuth);
  }
  // #endregion

  // #region Logic
  /** Initiate the Plex OAuth flow and start polling for status. */
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
        loginLink.className = "plex-login-link";
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

  /** Refresh the full Plex authentication and settings state from the server. */
  async function refreshPlexState() {
    const res = await fetchJson(configUrl);
    if (!res.ok) {
      setPlexAutomationControls(false);
      return;
    }

    const settings = (window.relaySettings = unwrapConfig(res.data));
    const plex = settings.PlexLibrary || {};
    const isLinked = !!plex.HasToken;

    setPlexAutomationControls(isLinked);

    const b = (id, path, type) => window._sr.bindConfig(id, path, settings, saveSettings, type);
    b("extra-plex-users", "Automation.ExtraPlexUsers", "text");
    b("plex-scan-vfs", "Automation.ScanOnVfsRefresh", "check");
    b("plex-scrobble", "Automation.AutoScrobble", "check");

    const authAction = el("plex-auth-action");
    if (authAction) {
      authAction.querySelectorAll(".plex-auth-state").forEach((e) => {
        const shouldShow = isLinked ? e.tagName === "DIV" : e.tagName === "BUTTON";
        e.style.display = shouldShow ? "" : "none";
      });
    }
    if (!isLinked) {
      withButtonAction("plex-start", startPlexAuth);
    } else {
      el("plex-login")?.remove();
      const unlinkBtn = el("plex-unlink");
      if (unlinkBtn) unlinkBtn.onclick = unlinkPlex;
    }

    const libCount = el("plex-libraries-count");
    if (libCount) libCount.textContent = (plex.DiscoveredLibraries || []).length;
  }

  /** Unlink the Plex account and refresh dashboard state. */
  async function unlinkPlex() {
    await fetchJson(base + "/plex/auth/unlink", { method: "POST" });
    refreshPlexState();
  }
  // #endregion

  //Initialization
  refreshPlexState();
})();
