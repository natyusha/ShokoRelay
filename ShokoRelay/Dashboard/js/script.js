(() => {
  const base = location.pathname.replace(/\/dashboard$/, "");
  const el = (id) => document.getElementById(id);

  /** Default auto-dismiss duration (ms) for transient toasts. Use 0 for persistent toasts that require manual dismissal. */
  const TOAST_MS = 5000;

  // #region Helpers
  /** @param {string} url - The log URL to wrap in an anchor tag. @returns {string} An HTML anchor link or an empty string if no URL was provided. */
  const makeLogLink = (url) => (url ? `[<a href="${url}" target="_blank" class="log-link">view log</a>]` : "");

  /**
   * Show success/error toasts for HTTP responses with automatic log-link injection.
   * @param {{ok: boolean, data: *}} res - The fetchJson response object.
   * @param {string} label - A short label identifying the operation (e.g. "Collection build").
   * @param {{hasFilter?: boolean, summary?: string, hideOnSucceed?: number}} [opts] - Display options.
   */
  function toastOperation(res, label, opts = {}) {
    const { summary, hideOnSucceed } = opts;
    const logLink = makeLogLink(res.data?.logUrl);
    if (res.ok) {
      const text = summary || summarizeResult(res) || `${label} Complete`;
      const errs = getErrorCount(res);
      const timeout = errs > 0 ? 0 : hideOnSucceed !== undefined ? hideOnSucceed : TOAST_MS;
      showToast(`${label}: ${text} ${logLink}`, errs > 0 ? "error" : "success", timeout);
    } else {
      showToast(`${label} Failed: ${summary || res.data?.message || JSON.stringify(res.data)} ${logLink}`, "error", 0);
    }
  }

  /**
   * Build a human-readable summary string from common API response fields.
   * @param {{ok: boolean, data: *}} res - The fetchJson response object.
   * @returns {string} A comma-separated summary (e.g. "processed: 5, created: 3") or empty string.
   */
  function summarizeResult(res) {
    if (!res?.data) return "";
    const d = res.data,
      parts = [];
    const add = (keys, label) =>
      keys.some((k) => {
        const v = d[k];
        if (v === undefined || v === null) return false;
        if (label === "errors") {
          const n = Array.isArray(v) ? v.length : typeof v === "number" ? v : /^\d+$/.test(String(v)) ? Number(v) : 0;
          if (n > 0) parts.push(`${label}: ${n}`);
        } else parts.push(`${label}: ${v}`);
        return true;
      });
    add(["seriesProcessed", "processed", "scannedCount"], "processed");
    add(["linksCreated", "created", "createdLinks", "LinksCreated"], "created");
    add(["marked", "markedWatched"], "marked");
    add(["skipped"], "skipped");
    add(["errors", "Errors", "errorsList", "ErrorsList"], "errors");
    add(["uploaded"], "uploaded");
    if (!parts.length && typeof d === "string") return d.length > 200 ? d.substring(0, 200) + "..." : d;
    return parts.join(", ");
  }

  /**
   * Fetch a URL and parse the response as JSON, returning a normalized result object.
   * @param {string} url - The URL to fetch.
   * @param {RequestInit} [opts] - Optional fetch options (method, headers, body, etc.).
   * @returns {Promise<{ok: boolean, data: *}>} Normalized response with parsed data.
   */
  const fetchJson = async (url, opts) => {
    try {
      const res = await fetch(url, opts),
        text = await res.text();
      try {
        return { ok: res.ok, data: JSON.parse(text) };
      } catch {
        return { ok: res.ok, data: text };
      }
    } catch (e) {
      console.error("fetchJson error for", url, e);
      return { ok: false, data: { error: String(e), message: e?.message ?? String(e), url } };
    }
  };

  const setBoolParam = (ps, k, v) => ps.set(k, v ? "true" : "false");
  const setIfNotEmpty = (ps, k, v) => {
    if (v != null && String(v) !== "") ps.set(k, String(v));
  };

  /**
   * Initialize a button as an aria-pressed toggle with a click handler that flips its state.
   * @param {HTMLElement|string} btn - The button element or its DOM id.
   * @param {boolean} [defaultState=false] - Initial pressed state.
   * @returns {HTMLElement|null} The resolved button element, or null if not found.
   */
  function initToggle(btn, defaultState = false) {
    const elBtn = typeof btn === "string" ? el(btn) : btn;
    if (!elBtn) return null;
    if (!elBtn.hasAttribute("aria-pressed")) elBtn.setAttribute("aria-pressed", String(!!defaultState));
    elBtn.onclick = () => elBtn.setAttribute("aria-pressed", elBtn.getAttribute("aria-pressed") === "true" ? "false" : "true");
    return elBtn;
  }

  /**
   * Wrap a button's click handler so it automatically shows/hides a loading spinner.
   * @param {HTMLElement|string} btn - The button element or its DOM id.
   * @param {Function} handler - Async click handler to execute while the button is in a loading state.
   */
  function withButtonAction(btn, handler) {
    const elBtn = typeof btn === "string" ? el(btn) : btn;
    if (!elBtn) return;
    elBtn.onclick = async (...args) => {
      setButtonLoading(elBtn, true);
      try {
        await handler.apply(elBtn, args);
      } finally {
        setButtonLoading(elBtn, false);
      }
    };
  }

  /**
   * Surface important outcomes via toasts; transient 'running' updates are silently ignored.
   * @param {string} message - Status message text (may contain an "errors: N" substring).
   * @param {"error"|"ok"|"running"} level - Severity level controlling toast type.
   */
  function setColStatus(message, level) {
    message = message || "pending";
    const m = String(message).match(/errors:?\s*(\d+)/i);
    if (m && Number(m[1]) > 0) return showToast(message, "error", 0);
    if (m) message = message.replace(/,?\s*errors:?\s*0/i, "").trim();
    if (level === "error") showToast(message, "error", 0);
    else if (level === "ok") showToast(message, "success", TOAST_MS);
  }

  /**
   * Toggle a button's loading state by adding/removing a spinner overlay and disabled attribute.
   * @param {HTMLElement} btn - The button element to modify.
   * @param {boolean} isLoading - Whether to enable or disable the loading state.
   */
  function setButtonLoading(btn, isLoading) {
    if (!btn) return;
    if (isLoading) {
      btn.classList.add("loading");
      btn.setAttribute("disabled", "");
      if (!btn.querySelector(".button-spinner")) {
        const s = document.createElement("span");
        s.className = "button-spinner";
        s.innerHTML = '<svg class="icon-svg"><use href="img/icons.svg#loading"></use></svg>';
        btn.appendChild(s);
      }
    } else {
      btn.classList.remove("loading");
      btn.removeAttribute("disabled");
      btn.querySelector(".button-spinner")?.remove();
    }
  }

  /**
   * Attach a smooth open/close animation to a &lt;details&gt; element using the Web Animations API.
   * @param {HTMLDetailsElement} details - The details element.
   * @param {HTMLElement} content - The inner content wrapper (`.details-content`).
   * @param {number} [duration=300] - Animation duration in ms.
   */
  function initDetailsAnimation(details, content, duration = 300) {
    let anim = null;
    details.querySelector("summary")?.addEventListener("click", (e) => {
      e.preventDefault();
      if (anim) anim.cancel();
      if (details.open) {
        // collapse: animate from current height to 0, then remove open
        const startH = content.offsetHeight + "px";
        anim = content.animate({ height: [startH, "0px"] }, { duration, easing: "ease" });
        anim.onfinish = anim.oncancel = () => {
          anim = null;
          content.style.height = "";
          details.open = false;
        };
      } else {
        // expand: set open, animate from 0 to natural height
        details.open = true;
        const endH = content.offsetHeight + "px";
        anim = content.animate({ height: ["0px", endH] }, { duration, easing: "ease" });
        anim.onfinish = anim.oncancel = () => {
          anim = null;
          content.style.height = "";
        };
      }
    });
  }

  /** Build URLSearchParams for VFS generation requests from the dashboard filter and toggle state. @returns {URLSearchParams} */
  const buildVfsParams = () => {
    const ps = new URLSearchParams();
    setIfNotEmpty(ps, "filter", el("vfs-filter")?.value);
    // 'run' is always performed; 'clean' follows the inline toggle button state.
    setBoolParam(ps, "clean", el("vfs-clean")?.getAttribute("aria-pressed") === "true");
    setBoolParam(ps, "run", true);
    return ps;
  };

  /** @param {Object} obj @param {string} path - Dot-separated key path. @returns {*} The resolved value, or undefined. */
  const getValueByPath = (obj, path) => path.split(".").reduce((o, k) => o?.[k], obj);
  /** @param {Object} obj @param {string} path - Dot-separated key path. @param {*} value - The value to set at the resolved path. */
  function setValueByPath(obj, path, value) {
    const parts = path.split(".");
    const last = parts.pop();
    const target = parts.reduce((o, k) => ((o[k] ??= {}), o[k]), obj);
    target[last] = value;
  }

  /** Unwrap a config response that may contain a payload wrapper. @param {Object} data @returns {Object} The inner config object. */
  const unwrapConfig = (data) => (data?.payload !== undefined ? data.payload || {} : data || {});

  /**
   * Attach overlay-click and Escape-key close listeners to a modal element.
   * @param {HTMLElement} modal - The modal element to attach handlers to.
   * @returns {Function} A close() function that hides the modal and removes its listeners.
   */
  function attachModalCloseHandlers(modal) {
    if (!modal) return () => {};
    const onOverlay = (ev) => {
      if (ev.target === modal) close();
    };
    const onKey = (ev) => {
      if (ev.key === "Escape") close();
    };
    function close() {
      modal.setAttribute("aria-hidden", "true");
      modal.classList.remove("open");
      document.body.style.overflow = "";
      modal.removeEventListener("click", onOverlay);
      document.removeEventListener("keydown", onKey);
    }
    modal.addEventListener("click", onOverlay);
    document.addEventListener("keydown", onKey);
    return close;
  }

  /** Enable or disable all Plex-dependent automation controls on the dashboard. @param {boolean} enabled */
  const setPlexAutomationControls = (enabled) =>
    document.querySelectorAll(".plex-auth").forEach((e) => {
      e.disabled = !enabled;
    });

  /** Expose shared helpers for animethemes.js */
  window._sr = {
    base,
    el,
    TOAST_MS,
    fetchJson,
    showToast,
    toastOperation,
    summarizeResult,
    makeLogLink,
    withButtonAction,
    initToggle,
    setIfNotEmpty,
    setValueByPath,
    getErrorCount,
    initDetailsAnimation,
    attachModalCloseHandlers,
  };
  // #endregion

  // #region Plex: Authentication
  let plexPinId = "";
  let plexPollTimer = null;

  const setPlexAction = (html) => {
    const c = el("plex-auth-action");
    if (c) c.innerHTML = html;
  };
  const setPlexLinkAction = (url) => setPlexAction(`<a id="plex-login" href="${url}" target="_blank" class="plex-login-link">Login</a>`);
  function setPlexStartAction() {
    setPlexAction('<button id="plex-start">Start Plex Auth</button>');
    const btn = el("plex-start");
    if (btn) btn.onclick = startPlexAuth;
  }
  function setPlexUnlinkAction() {
    setPlexAction(`<button id="plex-unlink" class="danger">Unlink Plex</button>
      <button id="plex-refresh" class="w46-button" title="Refresh Plex Libraries"><svg class="icon-svg"><use href="img/icons.svg#refresh"></use></svg></button>`);
    const u = el("plex-unlink");
    if (u) u.onclick = unlinkPlex;
    const r = el("plex-refresh");
    if (r) r.onclick = refreshPlexLibraries;
  }
  const startPlexPolling = () => {
    stopPlexPolling();
    plexPollTimer = setInterval(checkPlexAuthStatus, 2000);
  };
  const stopPlexPolling = () => {
    if (plexPollTimer) {
      clearInterval(plexPollTimer);
      plexPollTimer = null;
    }
  };

  /** Initiate the Plex OAuth flow by fetching a pin and redirecting the user to the auth URL. */
  async function startPlexAuth() {
    const btn = el("plex-start");
    setButtonLoading(btn, true);
    const res = await fetchJson(base + "/plex/auth");
    if (!res.ok) {
      console.error("Plex auth start failed", res.data);
      setButtonLoading(btn, false);
      return;
    }
    plexPinId = res.data.pinId || "";
    if (!plexPinId || !res.data.authUrl) {
      setPlexStartAction();
      return;
    }
    setPlexLinkAction(res.data.authUrl);
    startPlexPolling();
  }

  /** Poll the server for Plex OAuth completion; stops polling and refreshes state on success. */
  async function checkPlexAuthStatus() {
    if (!plexPinId) return;
    const res = await fetchJson(base + "/plex/auth/status?pinId=" + encodeURIComponent(plexPinId));
    if (res.ok && res.data?.status === "ok") {
      plexPinId = "";
      stopPlexPolling();
      await refreshPlexState();
    }
  }

  /** POST to the server to rediscover Plex libraries, then refresh the dashboard state. */
  async function refreshPlexLibraries() {
    const btn = el("plex-refresh");
    setButtonLoading(btn, true);
    try {
      const rr = await fetchJson(base + "/plex/auth/refresh", { method: "POST" });
      if (rr.ok && rr.data) {
        await refreshPlexState();
        setColStatus("Plex Libraries Refreshed", "ok");
      } else {
        setColStatus("Failed To Refresh Plex Libraries", "error");
        console.error("Refresh libraries failed", rr.data);
      }
    } catch (e) {
      setColStatus("Failed To Refresh Plex Libraries", "error");
      console.error(e);
    } finally {
      setButtonLoading(btn, false);
    }
  }

  /** @param {Array} libraries - Array of discovered Plex library objects. */
  const updateLibraryCount = (libraries) => {
    const c = el("plex-libraries-count");
    if (c) c.textContent = String((libraries || []).length);
  };

  /**
   * Validate that Plex is linked and libraries are available before running automation.
   * @returns {Promise<boolean>} True if Plex is ready for automation, false otherwise.
   */
  async function ensurePlexEnabled() {
    const fail = (msg, e) => {
      setColStatus(msg, "error");
      if (e) console.error(e);
      setPlexAutomationControls(false);
      return false;
    };
    try {
      const cfgRes = await fetchJson(base + "/config");
      if (!cfgRes.ok || !cfgRes.data) return fail("Error loading config");
      const plex = unwrapConfig(cfgRes.data).PlexLibrary || {};
      if (!plex.HasToken) return fail("Plex Token Missing — Link Plex First");
      if (!plex.DiscoveredLibraries?.length) return fail("No Plex Libraries Found — Refresh Libraries");
      setPlexAutomationControls(true);
      return true;
    } catch (e) {
      return fail("Error checking Plex config", e);
    }
  }

  /** Refresh the full Plex authentication state (token, libraries, settings bindings) from the server config. */
  async function refreshPlexState() {
    updateLibraryCount();
    const res = await fetchJson(base + "/config");
    if (!res.ok || !res.data) {
      setPlexAutomationControls(false);
      setPlexStartAction();
      return;
    }
    const settings = (window.relaySettings = unwrapConfig(res.data));
    if (settings.ExtraPlexUsers && typeof settings.ExtraPlexUsers !== "string") settings.ExtraPlexUsers = "";
    const plex = settings.PlexLibrary || {};
    setPlexAutomationControls(!!plex.HasToken);
    const bind = (id, prop, val) => {
      const e = el(id);
      if (e) {
        e[prop] = val;
        e.onchange = savePlexSettings;
      }
    };
    bind("extra-plex-users", "value", settings.ExtraPlexUsers || "");
    bind("plex-scan-vfs", "checked", !!settings.ScanOnVfsRefresh);
    bind("plex-watched", "checked", !!settings.AutoScrobble);
    if (!plex.HasToken) {
      setPlexStartAction();
      return;
    }
    setPlexUnlinkAction();
    const mapLib = (l) => ({ id: l.Id, title: l.Title, type: l.Type, uuid: l.Uuid, serverId: l.ServerId, serverName: l.ServerName, serverUrl: l.ServerUrl });
    const libraries = (plex.DiscoveredLibraries || []).map(mapLib);
    if (!libraries.length && plex.DiscoveredServers?.length) {
      const rr = await fetchJson(base + "/plex/auth/refresh", { method: "POST" });
      if (rr.ok && rr.data?.libraries) {
        updateLibraryCount(rr.data.libraries.map(mapLib));
        return;
      }
    }
    updateLibraryCount(libraries);
  }

  /** Unlink the Plex account by posting to the server and refreshing the dashboard state. */
  async function unlinkPlex() {
    await fetchJson(base + "/plex/auth/unlink", { method: "POST" });
    await refreshPlexState();
  }
  // #endregion

  // #region Plex: Automation
  /**
   * Read current config, merge Plex-specific settings from the dashboard, and POST the result.
   * @returns {Promise<boolean>} True if the settings were saved successfully.
   */
  async function savePlexSettings() {
    try {
      const cfgRes = await fetchJson(base + "/config");
      if (!cfgRes.ok || !cfgRes.data) {
        setColStatus("Failed To Load Config", "error");
        return false;
      }
      const cfg = unwrapConfig(cfgRes.data);
      if (cfg.ExtraPlexUsers && typeof cfg.ExtraPlexUsers !== "string") cfg.ExtraPlexUsers = "";
      cfg.ScanOnVfsRefresh = !!el("plex-scan-vfs")?.checked;
      cfg.ExtraPlexUsers = el("extra-plex-users")?.value || "";
      cfg.AutoScrobble = !!el("plex-watched")?.checked;
      cfg.ShokoSyncWatchedExcludeAdmin = !!el("sync-exclude-admin")?.checked;
      delete cfg.PlexLibrary;
      delete cfg.PlexAuth;
      const saveRes = await fetchJson(base + "/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(cfg) });
      if (!saveRes.ok) {
        setColStatus("Plex Settings Save Failed", "error");
        console.error(saveRes.data);
        return false;
      }
      setColStatus("Plex Settings Saved", "ok");
      return true;
    } catch (e) {
      setColStatus("Plex Settings Save Failed", "error");
      console.error(e);
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
    withButtonAction(btn, async function () {
      const startToast = showToast(startMsg, "info", 0);
      if (!(await ensurePlexEnabled())) {
        setButtonLoading(btn, false);
        return;
      }
      const res = await fetchJson(base + endpoint);
      if (startToast?.parentElement) startToast.remove();
      if (!res.ok) {
        toastOperation(res, label);
        return;
      }
      const data = res.data;
      if (data?.status === "ok") {
        const summary = summarizeFn(data);
        toastOperation(res, label, { summary });
      } else {
        const summary = data?.message || JSON.stringify(data);
        toastOperation({ ok: false, data }, label, { summary });
      }
    });
  }

  const colBuildBtn = el("col-build");
  if (colBuildBtn)
    plexBuildAction(colBuildBtn, "Collections", "Generating Collections...", "/plex/collections/build", (d) => {
      const up = d.uploaded !== undefined ? `, uploaded ${d.uploaded}` : "";
      return `processed ${d.processed}, created ${d.created}${up}, skipped ${d.skipped}, errors ${d.errors}`;
    });

  const rtgBuildBtn = el("rtg-build");
  if (rtgBuildBtn)
    plexBuildAction(
      rtgBuildBtn,
      "Critic Ratings",
      "Applying Critic Ratings...",
      "/plex/ratings/apply",
      (d) => `shows ${d.processedShows}/${d.updatedShows}, episodes ${d.processedEpisodes}/${d.updatedEpisodes}`,
    );
  // #endregion

  // #region Shoko: VFS
  initToggle("vfs-clean", true);

  const vfsExecBtn = el("vfs-exec");
  if (vfsExecBtn) {
    withButtonAction(vfsExecBtn, async function () {
      const clean = el("vfs-clean")?.getAttribute("aria-pressed") === "true";
      const startToast = showToast(`VFS: Generating (clean=${clean})...`, "info", 0);
      const params = buildVfsParams();
      const res = await fetchJson(base + "/vfs?" + params.toString());
      if (startToast?.parentElement) startToast.remove();
      toastOperation(res, `VFS (clean=${clean})`);
    });
  }

  // override editor panel handlers
  const overridesBtn = el("vfs-overrides");
  const overridesModal = el("overrides-modal");
  const overridesText = el("overrides-text");
  const overridesCancel = el("overrides-cancel");
  const overridesSave = el("overrides-save");

  if (overridesBtn && overridesModal && overridesText && overridesCancel && overridesSave) {
    overridesBtn.onclick = async () => {
      try {
        const cfgRes = await fetchJson(base + "/config");
        if (cfgRes.ok && cfgRes.data) overridesText.value = cfgRes.data.overrides || "";
      } catch {}
      if (overridesText.value.trim() === "") {
        overridesText.placeholder =
          "This allows shows which are separated on AniDB but part of the same TMDB listing to be combined into a single entry in Plex. Each line should contain a " +
          "comma separated list of AniDB IDs you wish to merge. The first ID is the primary series and the others will be merged into it " +
          "(for both VFS builds and metadata lookups). Lines that are blank or start with a '#' are ignored. An example is shown below:" +
          "\n\n## Shoko Relay VFS Overrides\n\n# Fairy Tail\n6662,8132,9980,13295\n\n# Bleach\n2369,15449,17765,18220,19079";
      }
      overridesModal.setAttribute("aria-hidden", "false");
      overridesModal.classList.add("open");
      document.body.style.overflow = "hidden";
      overridesText.focus();
      const closeModal = attachModalCloseHandlers(overridesModal);
      const closeOverrides = () => {
        closeModal();
      };
      overridesCancel.onclick = closeOverrides;
      overridesSave.onclick = async () => {
        try {
          const res = await fetchJson(base + "/vfs/overrides", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(overridesText.value) });
          if (res.ok) {
            showToast("VFS Overrides Saved", "success", TOAST_MS);
            closeOverrides();
          } else toastOperation(res, "VFS Overrides");
        } catch (e) {
          showToast(`VFS Overrides Save Failed: ${e?.message || e}`, "error", 0);
        }
      };
    };
  }
  // #endregion

  // #region Shoko: Automation
  const shokoRemoveMissingBtn = el("shoko-remove-missing");
  if (shokoRemoveMissingBtn) {
    withButtonAction(shokoRemoveMissingBtn, async function () {
      showToast("Remove Missing: Processing...", "info", TOAST_MS);
      const res = await fetchJson(base + "/shoko/remove-missing?dryRun=false", { method: "POST" });
      const summary = res.data && typeof res.data.count === "number" ? `removed ${res.data.count}` : undefined;
      toastOperation(res, "Remove Missing", { summary });
    });
  }

  const shokoImportRunBtn = el("shoko-import-run");
  if (shokoImportRunBtn) {
    withButtonAction(shokoImportRunBtn, async function () {
      showToast("Shoko Import: Scanning...", "info", TOAST_MS);
      const res = await fetchJson(base + "/shoko/import", { method: "POST" });
      if (!res.ok) {
        toastOperation(res, "Shoko Import");
        return;
      }
      const summary = summarizeResult(res) || `scanned ${res.data?.scannedCount ?? ""}`;
      toastOperation(res, "Shoko Import", { summary });
    });
  }

  const shokoSyncBtn = el("shoko-sync-watched");
  if (shokoSyncBtn) {
    shokoSyncBtn.onclick = () => {
      const modal = el("sync-modal"),
        startBtn = el("sync-start-button"),
        cancelBtn = el("sync-cancel-button");
      const dirToggle = el("sync-direction-toggle"),
        dirArrow = el("sync-direction-arrow");
      const ratingsEl = el("sync-ratings"),
        excludeAdminEl = el("sync-exclude-admin");
      if (!modal || !startBtn) return;

      let dirImport = false;
      try {
        dirImport = localStorage.getItem("shoko-sync-direction") === "import";
      } catch {}
      const updateDir = () => {
        dirToggle.setAttribute("aria-pressed", String(dirImport));
        dirToggle.setAttribute("aria-label", dirImport ? "Direction: Shoko to Plex" : "Direction: Plex to Shoko");
        const iconR = dirArrow.querySelector(".dir-icon-right"),
          iconL = dirArrow.querySelector(".dir-icon-left");
        if (!iconR || !iconL) {
          dirArrow.textContent = dirImport ? "❮" : "❯";
          return;
        }
        iconR.classList.toggle("hidden", dirImport);
        iconL.classList.toggle("hidden", !dirImport);
      };
      updateDir();

      modal.setAttribute("aria-hidden", "false");
      modal.classList.add("open");
      document.body.style.overflow = "hidden";
      startBtn.focus();

      const closeModal = attachModalCloseHandlers(modal);
      const onClose = () => {
        closeModal();
        dirToggle.onclick = null;
        startBtn.onclick = null;
        cancelBtn.onclick = null;
      };
      dirToggle.onclick = () => {
        dirImport = !dirImport;
        updateDir();
        try {
          localStorage.setItem("shoko-sync-direction", dirImport ? "import" : "export");
        } catch {}
      };
      startBtn.onclick = async () => {
        setButtonLoading(startBtn, true);
        const arrow = dirImport ? "\u2190" : "\u2192";
        const startToast = showToast(`Sync: Plex${arrow}Shoko...`, "info", 0);
        try {
          const ps = new URLSearchParams();
          ps.set("dryRun", "false");
          setBoolParam(ps, "ratings", ratingsEl.checked);
          setBoolParam(ps, "excludeAdmin", excludeAdminEl.checked);
          if (dirImport) setBoolParam(ps, "import", true);
          const res = await fetchJson(base + "/sync-watched?" + ps.toString(), { method: "POST" });
          if (startToast?.parentElement) startToast.remove();
          if (res.ok) {
            const summary = summarizeResult(res) || `processed ${res.data?.processed ?? 0}`;
            const vp = res.data?.votesFound !== undefined ? `, votes: ${res.data.votesFound}` : "";
            toastOperation(res, "Sync", { summary: `${summary}${vp}` });
            onClose();
          } else toastOperation(res, "Sync");
        } catch (e) {
          if (startToast?.parentElement) startToast.remove();
          showToast(`Sync Failed: ${e?.message || e}`, "error", 0);
        } finally {
          setButtonLoading(startBtn, false);
        }
      };
      cancelBtn.onclick = onClose;
    };
  }
  // #endregion

  // #region Provider Settings
  /**
   * Fetch the config schema and current values from the server, then dynamically build
   * the settings form with appropriate input types (bool, enum, number, textarea, etc.).
   */
  async function loadConfig() {
    const schemaRes = await fetchJson(base + "/config/schema");
    const configRes = await fetchJson(base + "/config");
    if (!schemaRes.ok || !configRes.ok) {
      showToast("Failed To Load Config", "error", 0);
      return;
    }
    const schema = schemaRes.data.properties || [];
    // extract overrides before we unwrap the payload
    const rawCfg = configRes.data || {};
    const overridesValue = rawCfg.overrides !== undefined ? rawCfg.overrides : "";
    const config = unwrapConfig(rawCfg);

    if (overridesBtn) overridesBtn.disabled = !config.TmdbEpNumbering;

    const container = el("config-form");
    container.innerHTML = "";

    try {
      const otext = el("overrides-text");
      if (otext) otext.value = overridesValue || "";
    } catch {}

    const advKeys = new Set(["PathMappings", "VfsRootPath", "AnimeThemesRootPath", "CollectionPostersRootPath", "ShokoServerUrl", "FFmpegPath", "Parallelism"]);
    const advSection = document.createElement("details");
    advSection.className = "details-anim";
    const advSum = document.createElement("summary");
    advSum.textContent = "Advanced Settings";
    advSection.appendChild(advSum);
    const advContent = document.createElement("div");
    advContent.className = "details-content";
    advContent.appendChild(document.createElement("hr"));

    /**
     * POST the updated config object to the server and surface any errors via toast.
     * @param {Object} updated - The full config object to persist.
     * @returns {Promise<{ok: boolean, data: *}>} The server response.
     */
    async function persistConfig(updated) {
      try {
        const res = await fetchJson(base + "/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(updated) });
        if (!res.ok) toastOperation(res, "Config Save");
        return res;
      } catch (e) {
        showToast(`Config Save Failed: ${e?.message || e}`, "error", 0);
        return { ok: false, data: e };
      }
    }

    schema.forEach((p) => {
      const wrap = document.createElement("div");
      const label = document.createElement("label");
      // Title + optional description (description wrapped in <small> and stays inline)
      const titleText = p.Display || p.Path || "";
      const titleNode = document.createElement("span");
      titleNode.textContent = titleText;
      label.appendChild(titleNode);
      if (p.Description) {
        const small = document.createElement("small");
        small.textContent = p.Description;
        label.appendChild(small);
      }

      let input;
      let value = getValueByPath(config, p.Path);

      if (p.Type === "bool") {
        input = document.createElement("input");
        input.type = "checkbox";
        input.checked = !!value;
        const outer = document.createElement("label");
        outer.className = "shoko-checkbox";
        const icon = document.createElement("span");
        icon.className = "shoko-checkbox-icon";
        icon.setAttribute("aria-hidden", "true");
        icon.innerHTML =
          '<svg class="unchecked"><use href="img/icons.svg#checkbox-blank-circle-outline"></use></svg>' + '<svg class="checked"><use href="img/icons.svg#checkbox-marked-circle-outline"></use></svg>';

        // Create a two-line label: title on first line, description (if present) in a <small> on the second line
        const textWrap = document.createElement("span");
        textWrap.className = "shoko-checkbox-text";
        const titleSpan = document.createElement("span");
        titleSpan.className = "shoko-checkbox-title";
        titleSpan.textContent = p.Display || p.Path || "";
        textWrap.appendChild(titleSpan);

        if (p.Description) {
          const desc = document.createElement("small");
          desc.className = "shoko-checkbox-desc";
          desc.textContent = p.Description;
          // force description to its own line next to the checkbox
          desc.style.display = "block";
          textWrap.appendChild(desc);
        }

        outer.appendChild(input);
        outer.appendChild(icon);
        outer.appendChild(textWrap);
        wrap.appendChild(outer);
      } else {
        wrap.appendChild(label);
        if (p.Type === "enum") {
          input = document.createElement("select");
          (p.EnumValues || []).forEach((ev) => {
            const opt = document.createElement("option");
            opt.value = ev.value;
            opt.textContent = ev.name;
            if (String(ev.value) === String(value)) opt.selected = true;
            input.appendChild(opt);
          });
          // enums will update on change; blank not applicable
        } else if (p.Type === "number") {
          input = document.createElement("input");
          input.type = "number";
          input.value = value != null && value !== "" ? value : (p.DefaultValue ?? "");
        } else if (p.Path === "PathMappings") {
          const left = document.createElement("textarea");
          const right = document.createElement("textarea");
          left.id = "path-mappings-left";
          right.id = "path-mappings-right";
          const mappings = value || {};
          const keys = Object.keys(mappings || {}).sort();
          // left textarea shows Plex paths (values), right shows Shoko paths (keys)
          const leftLines = keys.map((k) => mappings[k]);
          const rightLines = keys.map((k) => k);
          left.value = leftLines.join("\n");
          right.value = rightLines.join("\n");
          left.placeholder = "e.g. M:\\Anime";
          right.placeholder = "e.g. /anime";
          left.dataset.path = p.Path;
          right.dataset.path = p.Path;
          left.dataset.type = "pathMappingsLeft";
          right.dataset.type = "pathMappingsRight";
          // auto-save path mappings when either textarea changes
          left.onchange = right.onchange = async () => {
            try {
              const leftLines = left.value.split("\n").map((s) => s.trim());
              const rightLines = right.value.split("\n").map((s) => s.trim());
              const mapping = {};
              const max = Math.max(leftLines.length, rightLines.length);
              for (let i = 0; i < max; i++) {
                const plex = (leftLines[i] || "").trim();
                const shoko = (rightLines[i] || "").trim();
                if (plex && shoko) mapping[shoko] = plex;
              }
              setValueByPath(config, p.Path, mapping);
              await persistConfig(config);
            } catch (e) {
              showToast(`Path Mappings Save Failed: ${e?.message || e}`, "error", 0);
            }
          };
          const col = document.createElement("div");
          col.className = "full";
          const leftWrap = document.createElement("div");
          const rightWrap = document.createElement("div");
          const leftLabel = document.createElement("label");
          leftLabel.textContent = "Plex Base Paths";
          const rightLabel = document.createElement("label");
          rightLabel.textContent = "Shoko Base Paths";
          leftWrap.appendChild(leftLabel);
          leftWrap.appendChild(left);
          rightWrap.appendChild(rightLabel);
          rightWrap.appendChild(right);
          col.appendChild(leftWrap);
          col.appendChild(rightWrap);
          wrap.appendChild(col);
          const help = document.createElement("small");
          help.textContent = "Enter one mapping per line. Line N in the left textarea maps to line N in the right textarea.";
          wrap.appendChild(help);
        } else if (p.Type === "json") {
          input = document.createElement("textarea");
          if (value != null && value !== "") input.value = JSON.stringify(value, null, 2);
          else if (p.DefaultValue != null) {
            try {
              input.value = JSON.stringify(p.DefaultValue, null, 2);
            } catch {
              input.value = String(p.DefaultValue);
            }
          } else input.value = "";
        } else if (p.Path === "TagBlacklist") {
          input = document.createElement("textarea");
          input.value = value != null && value !== "" ? value : (p.DefaultValue ?? "");
        } else {
          input = document.createElement("input");
          input.type = "text";
          input.value = value != null && value !== "" ? value : (p.DefaultValue ?? "");
        }
      }

      if (input) {
        input.dataset.path = p.Path;
        input.dataset.type = p.Type;
        if (p.DefaultValue != null) input.dataset.default = JSON.stringify(p.DefaultValue);
        input.onchange = async () => {
          try {
            let val,
              usedDefault = false;
            const type = input.dataset.type,
              hasDef = input.dataset.default !== undefined;
            if (type === "bool") val = input.checked;
            else if (type === "enum" || type === "number") {
              if (input.value === "" && hasDef) {
                val = Number(JSON.parse(input.dataset.default));
                usedDefault = true;
              } else val = input.value === "" ? 0 : Number(input.value);
            } else if (type === "json") {
              try {
                if (input.value === "" && hasDef) {
                  val = JSON.parse(input.dataset.default);
                  usedDefault = true;
                } else val = input.value ? JSON.parse(input.value) : null;
              } catch {
                val = null;
              }
            } else {
              if (input.value === "" && hasDef) {
                val = JSON.parse(input.dataset.default);
                usedDefault = true;
              } else val = input.value;
            }
            if (usedDefault)
              try {
                input.value = String(val);
              } catch {}
            setValueByPath(config, input.dataset.path, val);
            await persistConfig(config);
            if (input.dataset.path === "TmdbEpNumbering" && overridesBtn) overridesBtn.disabled = !input.checked;
          } catch (e) {
            showToast(`Setting Save Failed: ${e?.message || e}`, "error", 0);
          }
        };
        if (!input.parentNode) wrap.appendChild(input);
      }
      (advKeys.has(p.Path) ? advContent : container).appendChild(wrap);
    });

    advSection.appendChild(advContent);
    if (advContent.children.length > 1) {
      container.appendChild(advSection);
      initDetailsAnimation(advSection, advContent);
    }

    try {
      const bindFreq = (id, key, label, { min, max } = {}) => {
        const e = el(id);
        if (!e) return;
        const v = Number(config[key] ?? 0);
        e.value = v === 0 ? "" : String(v);
        e.onchange = async () => {
          try {
            let n = Number(e.value) || 0;
            if (min != null && n < min) n = min;
            if (max != null && n > max) n = max;
            if (min != null || max != null) e.value = String(n);
            setValueByPath(config, key, n);
            await persistConfig(config);
          } catch (err) {
            showToast(`${label} Save Failed: ${err?.message || err}`, "error", 0);
          }
        };
      };
      bindFreq("shoko-utc-offset", "UtcOffsetHours", "UTC offset", { min: -12, max: 14 });
      bindFreq("shoko-import-frequency", "ShokoImportFrequencyHours", "import frequency");
      bindFreq("shoko-sync-frequency", "ShokoSyncWatchedFrequencyHours", "sync frequency");
      bindFreq("plex-auto-frequency", "PlexAutomationFrequencyHours", "plex automation frequency");

      if (el("plex-watched")) el("plex-watched").checked = !!config.AutoScrobble;

      const bindCheck = (id, key, label) => {
        const chk = el(id);
        if (!chk) return;
        chk.checked = !!config[key];
        chk.onchange = async () => {
          try {
            setValueByPath(config, key, chk.checked);
            await persistConfig(config);
          } catch (e) {
            showToast(`${label} Save Failed: ${e?.message || e}`, "error", 0);
          }
        };
      };
      bindCheck("sync-ratings", "ShokoSyncWatchedIncludeRatings", "Include Ratings");
      bindCheck("sync-exclude-admin", "ShokoSyncWatchedExcludeAdmin", "Exclude Admin");

      // Initialize AnimeThemes config bindings (playback toggle, loop/webm mode, play button)
      if (window._sr.initAtConfig) window._sr.initAtConfig(config, persistConfig);
    } catch (e) {
      /* ignore */
    }
  }

  setPlexStartAction();
  setPlexAutomationControls(false);
  refreshPlexState();
  loadConfig();
  // #endregion

  // #region Theme Toggle
  // Persists to localStorage and sets data-theme on <html>
  const THEME_KEY = "dashboard-theme";
  /** @returns {string} The saved theme preference or the OS default ("dark" or "light"). */
  const getSavedTheme = () => {
    try {
      const v = localStorage.getItem(THEME_KEY);
      if (v === "dark" || v === "light") return v;
    } catch {}
    return window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  };
  /** Apply a theme by setting the data-theme attribute and updating the toggle button state. @param {string} theme - "dark" or "light". */
  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme === "dark" ? "dark" : "light");
    const btn = el("theme-toggle");
    if (btn) btn.setAttribute("aria-pressed", String(theme === "dark"));
  }
  /** Initialize the theme toggle button with the saved preference and attach the click handler. */
  function initTheme() {
    applyTheme(getSavedTheme());
    const t = el("theme-toggle");
    if (!t) return;
    t.onclick = () => {
      const next = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
      try {
        localStorage.setItem(THEME_KEY, next);
      } catch {}
      applyTheme(next);
    };
  }
  // #endregion

  // #region Tooltips
  /** Create the shared tooltip element and attach show/hide listeners to all elements with a title attribute. Observes the DOM for dynamically added elements. */
  function initTooltips() {
    if (document.getElementById("shoko-tooltip")) return;
    const tpl = document.createElement("div");
    tpl.id = "shoko-tooltip";
    tpl.className = "tooltip-core tooltip-box tooltip-dark tooltip-place-top";
    tpl.setAttribute("role", "status");
    tpl.setAttribute("aria-hidden", "true");
    tpl.innerHTML = '<div class="tooltip-arrow"></div><div class="rt-content"></div>';
    document.body.appendChild(tpl);
    const content = tpl.querySelector(".rt-content");
    let showTimer = null,
      hideTimer = null;

    function showForElement(target) {
      if (target.disabled) return;
      const text = target.dataset.tooltipText || target.getAttribute("data-tooltip") || "";
      if (!text) return;
      content.textContent = text;
      tpl.setAttribute("aria-hidden", "false");
      tpl.classList.remove("tooltip-closing");
      tpl.classList.add("tooltip-show");
      target.setAttribute("aria-describedby", "shoko-tooltip");
      requestAnimationFrame(() => {
        const rect = target.getBoundingClientRect(),
          ttRect = tpl.getBoundingClientRect(),
          margin = 8;
        let top = rect.top - ttRect.height - margin,
          place = "top";
        if (top < 8) {
          top = rect.bottom + margin;
          place = "bottom";
        }
        let left = rect.left + (rect.width - ttRect.width) / 2;
        if (left < 8) left = 8;
        if (left + ttRect.width > window.innerWidth - 8) left = Math.max(8, window.innerWidth - ttRect.width - 8);
        tpl.style.left = `${Math.round(left + window.scrollX)}px`;
        tpl.style.top = `${Math.round(top + window.scrollY)}px`;
        tpl.classList.remove("tooltip-place-top", "tooltip-place-bottom", "tooltip-place-left", "tooltip-place-right");
        tpl.classList.add(`tooltip-place-${place}`);
      });
    }

    const hideForElement = (target) => {
      tpl.classList.remove("tooltip-show");
      tpl.classList.add("tooltip-closing");
      tpl.setAttribute("aria-hidden", "true");
      if (target) target.removeAttribute("aria-describedby");
    };

    function attach(target) {
      if (!target || target.dataset.tooltipInitialized) return;
      const title = target.getAttribute("title");
      if (!title) return;
      target.dataset.tooltipText = title;
      target.removeAttribute("title");
      target.dataset.tooltipInitialized = "1";
      target.addEventListener("mouseenter", () => {
        if (hideTimer) {
          clearTimeout(hideTimer);
          hideTimer = null;
        }
        showTimer = setTimeout(() => showForElement(target), 75);
      });
      target.addEventListener("mouseleave", () => {
        if (showTimer) {
          clearTimeout(showTimer);
          showTimer = null;
        }
        hideTimer = setTimeout(() => hideForElement(target), 100);
      });
      target.addEventListener("focus", () => {
        if (showTimer) clearTimeout(showTimer);
        showForElement(target);
      });
      target.addEventListener("blur", () => hideForElement(target));
    }

    document.querySelectorAll("[title]").forEach(attach);
    new MutationObserver((mutations) => {
      for (const m of mutations)
        for (const node of m.addedNodes) {
          if (node.nodeType !== 1) continue;
          if (node.hasAttribute?.("title")) attach(node);
          node.querySelectorAll?.("[title]").forEach(attach);
        }
    }).observe(document.body, { childList: true, subtree: true });
  }
  // #endregion

  // #region Toasts
  const ensureToastContainerFixed = () => {
    const tc = el("toast-container");
    if (tc && tc.parentElement !== document.body) document.body.appendChild(tc);
  };

  /**
   * Extract the error count from an API response by checking common error fields.
   * @param {{ok: boolean, data: *}} res - The fetchJson response object.
   * @returns {number} The number of errors found, or 0 if none.
   */
  function getErrorCount(res) {
    if (!res?.data) return 0;
    const d = res.data;
    for (const k of ["errors", "Errors", "errorsList"]) {
      if (Array.isArray(d[k])) return d[k].length;
      if (typeof d[k] === "number") return d[k];
      if (typeof d[k] === "string" && /^\d+$/.test(d[k])) return Number(d[k]);
    }
    return 0;
  }

  /**
   * Display a dismissible toast notification in the toast container.
   * @param {string} message - HTML message content.
   * @param {"info"|"success"|"warning"|"error"} [type="info"] - Toast severity/style.
   * @param {number} [timeout=5000] - Auto-dismiss delay in ms; use 0 to keep the toast visible until manually dismissed.
   * @returns {HTMLElement|null} The created toast element, or null if the container is missing.
   */
  function showToast(message, type = "info", timeout = 5000) {
    ensureToastContainerFixed();
    const container = el("toast-container");
    if (!container) {
      console.info(`[toast:${type}] ${message}`);
      return null;
    }

    const t = document.createElement("div");
    t.className = "toast " + (type || "info");
    t.setAttribute("role", "status");
    t.setAttribute("tabindex", "0");
    const msg = document.createElement("span");
    msg.className = "toast-message";
    msg.innerHTML = message;
    t.appendChild(msg);
    container.appendChild(t);
    requestAnimationFrame(() => t.classList.add("visible"));

    const dismiss = (elm) => {
      elm.classList.remove("visible");
      setTimeout(() => {
        try {
          elm.remove();
        } catch {}
      }, 300);
    };
    let timer =
      timeout > 0
        ? setTimeout(() => {
            if (t.parentElement) dismiss(t);
          }, timeout)
        : null;
    const cancelAndDismiss = (ev) => {
      ev?.stopPropagation?.();
      if (timer) clearTimeout(timer);
      if (t.parentElement) dismiss(t);
    };
    t.addEventListener("click", cancelAndDismiss);
    t.addEventListener("keydown", (ev) => {
      if (ev.key === "Escape" || ev.key === "Esc") cancelAndDismiss(ev);
    });
    return t;
  }
  // #endregion

  initTooltips();

  initTheme();
})();
