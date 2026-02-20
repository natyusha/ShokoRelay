(() => {
  const base = location.pathname.replace(/\/dashboard$/, "");
  const el = (id) => document.getElementById(id);

  /* --- toast notifications --- */
  function ensureToastContainerFixed() {
    const tc = el("toast-container");
    if (!tc) return;
    // ensure it is a direct child of body so position:fixed behaves as expected
    if (tc.parentElement !== document.body) document.body.appendChild(tc);
  }

  function getErrorCount(res) {
    if (!res || !res.data) return 0;
    const d = res.data;
    if (Array.isArray(d.errors)) return d.errors.length;
    if (Array.isArray(d.Errors)) return d.Errors.length;
    if (Array.isArray(d.errorsList)) return d.errorsList.length;
    if (typeof d.errors === "number") return d.errors;
    if (typeof d.Errors === "number") return d.Errors;
    if (typeof d.errors === "string" && /^\d+$/.test(d.errors)) return Number(d.errors);
    return 0;
  }

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
    msg.textContent = message;
    t.appendChild(msg);

    container.appendChild(t);
    requestAnimationFrame(() => t.classList.add("visible"));

    // helper to remove the toast with animation
    function dismissToast(elm) {
      elm.classList.remove("visible");
      setTimeout(() => {
        try {
          elm.remove();
        } catch {}
      }, 300);
    }

    // Auto-dismiss timer (if a timeout > 0 was supplied)
    let timer = null;
    if (timeout > 0) {
      timer = setTimeout(() => {
        if (!t.parentElement) return;
        dismissToast(t);
      }, timeout);
    }

    // Clicking anywhere on the toast dismisses it (applies to persistent/error toasts too)
    t.addEventListener("click", (ev) => {
      ev.stopPropagation();
      if (timer) clearTimeout(timer);
      if (t.parentElement) dismissToast(t);
    });

    // Keyboard: Escape dismisses focused toast (accessibility)
    t.addEventListener("keydown", (ev) => {
      if (ev.key === "Escape" || ev.key === "Esc") {
        if (timer) clearTimeout(timer);
        if (t.parentElement) dismissToast(t);
      }
    });

    return t;
  }

  function summarizeResult(res) {
    if (!res || !res.data) return "";
    const d = res.data;
    const parts = [];
    const addIf = (keys, label) => {
      for (const k of keys) {
        if (d[k] !== undefined && d[k] !== null) {
          // only include numeric error counts when > 0 (omit 0)
          if (label === "errors") {
            const v = d[k];
            if (Array.isArray(v)) {
              if (v.length > 0) parts.push(`${label}: ${v.length}`);
            } else if (typeof v === "number") {
              if (v > 0) parts.push(`${label}: ${v}`);
            } else if (typeof v === "string" && /^\d+$/.test(v)) {
              const n = Number(v);
              if (n > 0) parts.push(`${label}: ${n}`);
            }
          } else {
            parts.push(`${label}: ${d[k]}`);
          }
          return true;
        }
      }
      return false;
    };

    addIf(["seriesProcessed", "processed", "scannedCount"], "processed");
    addIf(["linksCreated", "created", "createdLinks", "LinksCreated"], "created");
    addIf(["marked", "markedWatched"], "marked");
    addIf(["skipped"], "skipped");

    // Only show 'errors' in the summary when there are > 0 errors
    addIf(["errors", "Errors", "errorsList", "ErrorsList"], "errors");

    addIf(["uploaded"], "uploaded");

    if (parts.length === 0 && typeof d === "string") {
      return d.length > 200 ? d.substring(0, 200) + "..." : d;
    }
    return parts.join(", ");
  }

  /* --- small reusable helpers --- */
  const fetchJson = async (url, opts) => {
    try {
      const res = await fetch(url, opts);
      const text = await res.text();
      try {
        return { ok: res.ok, data: JSON.parse(text) };
      } catch {
        return { ok: res.ok, data: text };
      }
    } catch (err) {
      console.error("fetchJson error for", url, err);
      return { ok: false, data: { error: String(err), message: err?.message ?? String(err), url } };
    }
  };

  const setBoolParam = (ps, k, v) => ps.set(k, v ? "true" : "false");
  const setIfNotEmpty = (ps, k, v) => {
    if (v !== undefined && v !== null && String(v) !== "") ps.set(k, String(v));
  };

  // Initialize a button as an aria-pressed toggle and attach a simple click handler.
  // btn can be an element or an element id string. defaultState boolean indicates initial pressed state.
  function initToggle(btn, defaultState = false) {
    const elBtn = typeof btn === "string" ? document.getElementById(btn) : btn;
    if (!elBtn) return null;
    if (!elBtn.hasAttribute("aria-pressed")) elBtn.setAttribute("aria-pressed", defaultState ? "true" : "false");
    elBtn.onclick = () => {
      const cur = elBtn.getAttribute("aria-pressed") === "true";
      elBtn.setAttribute("aria-pressed", cur ? "false" : "true");
    };
    return elBtn;
  }

  // Wrap a button's click handler with loading state management (avoids repeating try/finally)
  function withButtonAction(btn, handler) {
    const elBtn = typeof btn === "string" ? document.getElementById(btn) : btn;
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

  /* --- status helper — important messages now surface via toasts --- */
  function setColStatus(message, level) {
    // The inline status label was removed from the dashboard. Move important
    // outcomes (errors/success) to toasts. Transient 'running' updates are silent.
    if (!message) message = "pending";

    // If a numeric errors value exists in the message, prefer to show that as an error toast
    const m = String(message).match(/errors:?\s*(\d+)/i);
    if (m) {
      const n = Number(m[1]);
      if (n > 0) {
        showToast(message, "error", 0);
        return;
      }
      // strip 'errors: 0' from the message to reduce noise
      message = message.replace(/,?\s*errors:?\s*0/i, "").trim();
      // fallthrough to normal handling
    }

    if (level === "error") {
      showToast(message, "error", 0);
    } else if (level === "ok") {
      showToast(message, "success", 6000);
    } else {
      // don't show transient/running messages as toasts to avoid noise
      return;
    }
  }

  /* --- button loading helper (overlay spinner + disabled look) --- */
  function setButtonLoading(btn, isLoading) {
    if (!btn) return;
    if (isLoading) {
      btn.classList.add("loading");
      btn.setAttribute("disabled", "");
      if (!btn.querySelector(".button-spinner")) {
        const span = document.createElement("span");
        span.className = "button-spinner";
        span.innerHTML = '<svg viewBox="0 0 24 24" class="icon-svg"><use href="img/icons.svg#loading"></use></svg>';
        btn.appendChild(span);
      }
    } else {
      btn.classList.remove("loading");
      btn.removeAttribute("disabled");
      const sp = btn.querySelector(".button-spinner");
      if (sp) sp.remove();
    }
  }

  /* --- small builders to avoid repeating URLSearchParams logic --- */
  const buildVfsParams = () => {
    const ps = new URLSearchParams();
    setIfNotEmpty(ps, "filter", el("vfs-filter")?.value);
    // 'run' is always performed; 'clean' follows the inline toggle button state.
    setBoolParam(ps, "clean", el("vfs-clean")?.getAttribute("aria-pressed") === "true");
    setBoolParam(ps, "run", true);
    return ps;
  };

  const buildAtParams = () => {
    const ps = new URLSearchParams();
    setIfNotEmpty(ps, "path", el("at-path")?.value);
    setIfNotEmpty(ps, "slug", el("at-slug")?.value);
    setIfNotEmpty(ps, "offset", el("at-offset")?.value);
    setIfNotEmpty(ps, "filter", el("at-filter")?.value);
    if (el("at-force")?.getAttribute("aria-pressed") === "true") ps.set("force", "true");
    if (el("at-batch")?.getAttribute("aria-pressed") === "true") ps.set("batch", "true");
    return ps;
  };

  const buildAtMapParams = () => {
    const ps = new URLSearchParams();
    setIfNotEmpty(ps, "filter", el("at-filter-map")?.value);
    return ps;
  };

  /* --- Plex / page helpers --- */
  let plexPinId = "";
  let plexPollTimer = null;

  function setPlexAction(html) {
    const container = el("plex-auth-action");
    if (container) container.innerHTML = html;
  }
  function setPlexLinkAction(authUrl) {
    setPlexAction('<a id="plex-login" href="' + authUrl + '" target="_blank" class="plex-login-link">Login</a>');
  }
  function setPlexStartAction() {
    setPlexAction('<button id="plex-start">Start Plex Auth</button>');
    const btn = el("plex-start");
    if (btn) btn.onclick = startPlexAuth;
  }
  function setPlexUnlinkAction() {
    setPlexAction(
      '<button id="plex-unlink" class="danger">Unlink Plex</button> <button id="plex-refresh" title="Refresh Plex Libraries"><svg class="icon-svg" viewBox="0 0 24 24"><use href="img/icons.svg#refresh"></use></svg></button>',
    );
    const unlinkBtn = el("plex-unlink");
    if (unlinkBtn) unlinkBtn.onclick = unlinkPlex;
    const refreshBtn = el("plex-refresh");
    if (refreshBtn) refreshBtn.onclick = refreshPlexLibraries;
  }
  function startPlexPolling() {
    stopPlexPolling();
    plexPollTimer = setInterval(checkPlexAuthStatus, 2000);
  }
  function stopPlexPolling() {
    if (plexPollTimer) {
      clearInterval(plexPollTimer);
      plexPollTimer = null;
    }
  }

  async function startPlexAuth() {
    const btn = el("plex-start");
    setButtonLoading(btn, true);
    const res = await fetchJson(base + "/plexauth");
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

  async function checkPlexAuthStatus() {
    if (!plexPinId) return;
    const res = await fetchJson(base + "/plexauth/status?pinId=" + encodeURIComponent(plexPinId));
    if (res.ok && res.data && res.data.status === "ok") {
      plexPinId = "";
      stopPlexPolling();
      await refreshPlexState();
    }
  }

  async function refreshPlexLibraries() {
    const btn = el("plex-refresh");
    setButtonLoading(btn, true);
    setColStatus("Refreshing libraries...", "running");
    try {
      const rr = await fetchJson(base + "/plex/libraries/refresh", { method: "POST" });
      if (rr.ok && rr.data) {
        await refreshPlexState();
        setColStatus("Libraries refreshed", "ok");
      } else {
        setColStatus("Error refreshing libraries", "error");
        console.error("Refresh libraries failed", rr.data);
      }
    } catch (ex) {
      setColStatus("Error refreshing libraries", "error");
      console.error(ex);
    } finally {
      setButtonLoading(btn, false);
    }
  }

  function clearPlexLibraries() {
    const libs = el("plex-libraries");
    if (libs) {
      libs.innerHTML = "";
      libs.disabled = true;
    }
  }

  function populateLibraries(libraries, selectedKeys) {
    const libs = el("plex-libraries");
    if (!libs) return;
    libs.innerHTML = "";
    const selected = new Set((selectedKeys || []).map(String));
    (libraries || []).forEach((lib) => {
      if (!lib) return;
      const key = lib.uuid || (lib.serverId ? lib.serverId + "::" + lib.id : String(lib.id));
      const serverName = lib.serverName || lib.ServerName || lib.serverUrl || lib.ServerUrl || "";
      const opt = document.createElement("option");
      opt.value = String(key);
      opt.textContent = (serverName ? serverName + " ❯ " : "") + (lib.title || "");
      if (selected.has(String(key))) opt.selected = true;
      libs.appendChild(opt);
    });
    libs.disabled = (libraries || []).length === 0;
    libs.onchange = onLibrariesChange;
  }

  async function ensurePlexEnabled() {
    try {
      const cfgRes = await fetchJson(base + "/config");
      if (!cfgRes.ok || !cfgRes.data) {
        setColStatus("Error loading config", "error");
        return false;
      }
      const plex = cfgRes.data.PlexLibrary || {};
      if (!plex.ServerUrl || !plex.Token) {
        setColStatus("Plex server or token missing. Link Plex in Plex Auth.", "error");
        return false;
      }
      const hasSelected = (plex.SelectedLibraries && plex.SelectedLibraries.length > 0) || (plex.LibrarySectionId && plex.LibrarySectionId > 0);
      if (!hasSelected) {
        setColStatus("No Plex library selected. Select a library in Plex Auth.", "error");
        return false;
      }
      return true;
    } catch (ex) {
      setColStatus("Error checking Plex config", "error");
      console.error(ex);
      return false;
    }
  }

  async function refreshPlexState() {
    clearPlexLibraries();
    const res = await fetchJson(base + "/config");
    if (!res.ok || !res.data) {
      setPlexStartAction();
      return;
    }
    const cfgAll = res.data || {};
    const plex = cfgAll.PlexLibrary || {};

    const extraEl = el("extra-plex-users");
    if (extraEl) {
      extraEl.value = cfgAll.ExtraPlexUsers || "";
      extraEl.onchange = savePlexSettings;
    }
    const scanEl = el("plex-scan-vfs");
    if (scanEl) {
      scanEl.checked = !!plex.ScanOnVfsRefresh;
      scanEl.onchange = savePlexSettings;
    }

    const plexWatchedEl = el("plex-watched");
    if (plexWatchedEl) {
      // 'Auto Scrobble' is separate from periodic SyncWatched
      plexWatchedEl.checked = !!cfgAll.AutoScrobble;
      plexWatchedEl.onchange = savePlexSettings;
    }

    if (!plex.Token) {
      setPlexStartAction();
      return;
    }
    setPlexUnlinkAction();
    const libraries = (plex.DiscoveredLibraries || []).map((l) => ({ id: l.Id, title: l.Title, type: l.Type, uuid: l.Uuid, serverId: l.ServerId, serverName: l.ServerName, serverUrl: l.ServerUrl }));
    const selectedKeys = (plex.SelectedLibraries || []).map((s) => s.Uuid || (s.ServerId ? s.ServerId + "::" + s.SectionId : String(s.SectionId)));
    if ((!libraries || libraries.length === 0) && plex.DiscoveredServers && plex.DiscoveredServers.length > 0) {
      const rr = await fetchJson(base + "/plex/libraries/refresh", { method: "POST" });
      if (rr.ok && rr.data && rr.data.libraries) {
        const libs = (rr.data.libraries || []).map((l) => ({ id: l.Id, title: l.Title, type: l.Type, uuid: l.Uuid, serverId: l.ServerId, serverName: l.ServerName, serverUrl: l.ServerUrl }));
        populateLibraries(libs, selectedKeys);
        return;
      }
    }
    populateLibraries(libraries, selectedKeys);
  }

  async function onLibrariesChange() {
    const libs = el("plex-libraries");
    if (!libs) return;
    const selected = Array.from(libs.selectedOptions).map((o) => o.value);
    const cfgRes = await fetchJson(base + "/config");
    if (!cfgRes.ok || !cfgRes.data) {
      return;
    }
    const cfg = cfgRes.data;
    const discovered = cfg.PlexLibrary?.DiscoveredLibraries || [];
    const newSelected = (discovered || [])
      .filter((d) => {
        const key = d.Uuid || (d.ServerId ? d.ServerId + "::" + d.Id : String(d.Id));
        return selected.includes(key);
      })
      .map((d) => ({ SectionId: d.Id, Title: d.Title, Type: d.Type, Uuid: d.Uuid, ServerId: d.ServerId, ServerName: d.ServerName, ServerUrl: d.ServerUrl }));
    cfg.PlexLibrary.SelectedLibraries = newSelected;
    cfg.PlexLibrary.LibrarySectionId = newSelected[0]?.SectionId || 0;
    cfg.PlexLibrary.SelectedLibraryName = newSelected[0]?.Title || "";
    cfg.PlexLibrary.SectionUuid = newSelected[0]?.Uuid || "";
    cfg.PlexLibrary.ServerIdentifier = newSelected[0]?.ServerId || "";
    cfg.PlexLibrary.ServerUrl = newSelected[0]?.ServerUrl || cfg.PlexLibrary.ServerUrl || "";
    const saveRes = await fetchJson(base + "/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(cfg) });
    if (!saveRes.ok) {
      return;
    }
  }

  async function savePlexSettings() {
    try {
      const cfgRes = await fetchJson(base + "/config");
      if (!cfgRes.ok || !cfgRes.data) {
        setColStatus("Error loading config", "error");
        return false;
      }
      const cfg = cfgRes.data;
      cfg.PlexLibrary = cfg.PlexLibrary || {};
      cfg.PlexLibrary.ScanOnVfsRefresh = !!el("plex-scan-vfs")?.checked;
      cfg.ExtraPlexUsers = el("extra-plex-users")?.value || "";
      // 'Auto Scrobble' is handled independently via webhook
      cfg.AutoScrobble = !!el("plex-watched")?.checked;
      const saveRes = await fetchJson(base + "/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(cfg) });
      if (!saveRes.ok) {
        setColStatus("Error saving Plex settings", "error");
        console.error(saveRes.data);
        return false;
      }
      setColStatus("Plex settings saved", "ok");
      return true;
    } catch (ex) {
      setColStatus("Error saving Plex settings", "error");
      console.error(ex);
      return false;
    }
  }

  async function unlinkPlex() {
    const res = await fetchJson(base + "/plex/unlink", { method: "POST" });
    if (!res.ok) {
      console.error("Failed to revoke Plex token.");
      return;
    }
    clearPlexLibraries();
    setPlexStartAction();
  }

  /* --- event handlers use helpers above to keep code compact --- */
  initToggle("vfs-clean", true);

  const vfsExecBtn = el("vfs-exec");
  if (vfsExecBtn) {
    withButtonAction(vfsExecBtn, async function () {
      const clean = el("vfs-clean")?.getAttribute("aria-pressed") === "true";
      showToast(`VFS generation started (clean=${clean})`, "info", 3000);
      const params = buildVfsParams();
      const res = await fetchJson(base + "/vfs?" + params.toString());
      if (res.ok) {
        const summary = summarizeResult(res) || "VFS build completed";
        const vfsErrCount = getErrorCount(res);
        showToast(`VFS (clean=${clean}): ${summary}`, vfsErrCount > 0 ? "error" : "success", vfsErrCount > 0 ? 0 : 6000);
      } else {
        showToast(`VFS failed (clean=${clean}): ${res.data?.message || JSON.stringify(res.data)}`, "error", 0);
      }
    });
  }

  // --- Shoko Automation  ---
  const shokoRemoveMissingBtn = el("shoko-remove-missing");
  if (shokoRemoveMissingBtn) {
    withButtonAction(shokoRemoveMissingBtn, async function () {
      showToast("Remove missing files: started", "info", 3000);
      const res = await fetchJson(base + "/shoko/remove-missing", { method: "POST" });
      if (res.ok) {
        const summary = summarizeResult(res) || "Remove missing completed";
        const rmErr = getErrorCount(res);
        showToast(`Remove missing: ${summary}`, rmErr > 0 ? "error" : "success", rmErr > 0 ? 0 : 6000);
      } else {
        showToast(`Remove missing failed: ${res.data?.message || JSON.stringify(res.data)}`, "error", 0);
      }
    });
  }

  const shokoImportRunBtn = el("shoko-import-run");
  if (shokoImportRunBtn) {
    withButtonAction(shokoImportRunBtn, async function () {
      showToast("Shoko import requested", "info", 3000);
      const res = await fetchJson(base + "/shoko/import", { method: "POST" });
      if (!res.ok) {
        showToast(`Shoko import failed: ${res.data?.message || JSON.stringify(res.data)}`, "error", 0);
        return;
      }
      const summary = summarizeResult(res) || `scanned ${res.data?.scannedCount ?? ""}`;
      const shokoImportErr = getErrorCount(res);
      showToast(`Shoko import complete: ${summary}`, shokoImportErr > 0 ? "error" : "success", shokoImportErr > 0 ? 0 : 5000);
    });
  }

  const shokoSyncBtn = el("shoko-sync-watched");
  if (shokoSyncBtn) {
    // Open configuration modal instead of an immediate sync
    shokoSyncBtn.onclick = () => {
      const modal = el("sync-modal");
      const startBtn = el("sync-start-button");
      const cancelBtn = el("sync-cancel-button");
      const dirToggle = el("sync-direction-toggle");
      const dirArrow = el("sync-direction-arrow");
      const ratingsEl = el("sync-ratings");
      const excludeAdminEl = el("sync-exclude-admin");
      if (!modal || !startBtn) {
        // modal unavailable — abort and do nothing
        return;
      }

      let directionImport = false; // false = Plex → Shoko (default), true = Shoko → Plex
      function updateDirectionUI() {
        const iconRight = dirArrow.querySelector('.dir-icon-right');
        const iconLeft = dirArrow.querySelector('.dir-icon-left');

        // accessibility state
        if (directionImport) {
          dirToggle.setAttribute('aria-pressed', 'true');
          dirToggle.setAttribute('aria-label', 'Direction: Shoko to Plex');
        } else {
          dirToggle.setAttribute('aria-pressed', 'false');
          dirToggle.setAttribute('aria-label', 'Direction: Plex to Shoko');
        }

        // Fallback glyph if icons missing
        if (!iconRight || !iconLeft) {
          dirArrow.textContent = directionImport ? '❮' : '❯';
          return;
        }

        // Simple visibility toggle (no animations): show the correct SVG and hide the other
        if (directionImport) {
          iconRight.classList.add('hidden');
          iconLeft.classList.remove('hidden');
        } else {
          iconRight.classList.remove('hidden');
          iconLeft.classList.add('hidden');
        }
      }

      // initialize defaults and respect persisted configuration for the ratings checkbox
      // restore last-used direction (persisted to localStorage); default = Plex -> Shoko
      try {
        directionImport = localStorage.getItem("shoko-sync-direction") === "import";
      } catch (e) {
        directionImport = false;
      }

      // Render initial direction state
      updateDirectionUI();

      // show modal
      modal.setAttribute("aria-hidden", "false");
      modal.classList.add("open");
      document.body.style.overflow = "hidden";
      startBtn.focus();

      // Handlers
      const onClose = () => {
        modal.setAttribute("aria-hidden", "true");
        modal.classList.remove("open");
        document.body.style.overflow = "";
        // remove temporary listeners
        dirToggle.removeEventListener("click", onToggle);
        startBtn.removeEventListener("click", onStart);
        cancelBtn.removeEventListener("click", onClose);
        modal.removeEventListener("click", onOverlayClick);
        document.removeEventListener("keydown", onKeydown);
      };
      const onToggle = () => {
        directionImport = !directionImport;
        updateDirectionUI();
        try {
          localStorage.setItem("shoko-sync-direction", directionImport ? "import" : "export");
        } catch (err) {
          /* ignore */
        }
      };
      const onOverlayClick = (ev) => {
        if (ev.target === modal) onClose();
      };
      const onKeydown = (ev) => {
        if (ev.key === "Escape") onClose();
      };

      const onStart = async () => {
        setButtonLoading(startBtn, true);
        try {
          showToast("Sync started (applying changes)...", "info", 3000);
          const ps = new URLSearchParams();
          ps.set("dryRun", "false"); // dashboard always does a real run
          setBoolParam(ps, "ratings", ratingsEl.checked);
          setBoolParam(ps, "excludeAdmin", excludeAdminEl.checked);
          if (directionImport) setBoolParam(ps, "import", true); // Shoko → Plex
          const url = base + "/syncwatched?" + ps.toString();
          const res = await fetchJson(url, { method: "POST" });
          if (res.ok) {
            const summary = summarizeResult(res) || `processed ${res.data?.processed ?? 0}`;
            const votesFound = res.data?.votesFound;
            const votesPart = votesFound !== undefined ? `, votesFound: ${votesFound}` : "";
            const syncErr = getErrorCount(res);
            showToast(`Sync complete: ${summary}${votesPart}`, syncErr > 0 ? "error" : "success", syncErr > 0 ? 0 : 6000);
            onClose();
          } else {
            showToast(`Sync failed: ${res.data?.message || JSON.stringify(res.data)}`, "error", 0);
          }
        } catch (ex) {
          showToast(`Sync error: ${ex?.message || ex}`, "error", 0);
        } finally {
          setButtonLoading(startBtn, false);
        }
      };

      // Attach listeners
      dirToggle.addEventListener("click", onToggle);
      startBtn.addEventListener("click", onStart);
      cancelBtn.addEventListener("click", onClose);
      modal.addEventListener("click", onOverlayClick);
      document.addEventListener("keydown", onKeydown);
    };
  }

  const colBuildBtn = el("col-build");
  if (colBuildBtn) {
    withButtonAction(colBuildBtn, async function () {
      showToast("Collection build started", "info", 3000);
      setColStatus("Checking Plex configuration...", "running");
      const ok = await ensurePlexEnabled();
      if (!ok) return;
      setColStatus("Running collection build...", "running");
      const res = await fetchJson(base + "/plex/collections/build");
      if (!res.ok) {
        setColStatus("Error: " + (res.data?.message || "Request failed"), "error");
        console.error(res.data);
        return;
      }
      const data = res.data;
      if (data?.status === "ok") {
        const uploadedText = data?.uploaded !== undefined ? `, uploaded ${data.uploaded}` : "";
        setColStatus(`OK: processed ${data.processed}, created ${data.created}${uploadedText}, skipped ${data.skipped}, errors ${data.errors}`, "ok");
      } else {
        setColStatus("Error: " + (data?.message || JSON.stringify(data)), "error");
      }
    });
  }

  // at-force / at-batch toggles
  initToggle("at-force", false);
  initToggle("at-batch", false);

  // Ensure the AnimeThemes 'Slug' input resets to a sensible default when cleared
  const atSlugEl = el("at-slug");
  if (atSlugEl) {
    // Restore placeholder/default when user leaves an empty field
    atSlugEl.addEventListener("blur", () => {
      if (!String(atSlugEl.value || "").trim()) atSlugEl.value = atSlugEl.placeholder || "OP2";
    });
    // If user presses Enter while empty, restore default immediately
    atSlugEl.addEventListener("keydown", (ev) => {
      if (ev.key === "Enter" && !String(atSlugEl.value || "").trim()) atSlugEl.value = atSlugEl.placeholder || "OP2";
    });
  }

  const atSingleBtn = el("at-single");
  if (atSingleBtn) {
    withButtonAction(atSingleBtn, async function () {
      // if slug is empty, restore the default before sending
      const atSlugEl_local = el("at-slug");
      if (atSlugEl_local && !String(atSlugEl_local.value || "").trim()) atSlugEl_local.value = atSlugEl_local.placeholder || "OP2";
      showToast("AnimeThemes: generating mp3...", "info", 3000);
      const params = buildAtParams();
      const res = await fetchJson(base + "/animethemes/mp3?" + params.toString());
      if (res.ok) {
        const summary = summarizeResult(res) || res.data?.status || "Done";
        const atMp3Err = getErrorCount(res);
        showToast(`AnimeThemes mp3: ${summary}`, atMp3Err > 0 ? "error" : "success", atMp3Err > 0 ? 0 : 5000);
      } else {
        showToast(`AnimeThemes mp3 failed: ${res.data?.message || JSON.stringify(res.data)}`, "error", 0);
      }
    });
  }

  const atMappingBtn = el("at-mapping");
  if (atMappingBtn) {
    withButtonAction(atMappingBtn, async function () {
      showToast("AnimeThemes: building mapping...", "info", 3000);
      const p = buildAtMapParams();
      p.set("mapping", "true");
      const res = await fetchJson(base + "/animethemes/vfs?" + p.toString());
      if (res.ok) {
        const summary = summarizeResult(res) || "Mapping complete";
        const atMapErr = getErrorCount(res);
        showToast(`AnimeThemes mapping: ${summary}`, atMapErr > 0 ? "error" : "success", atMapErr > 0 ? 0 : 5000);
      } else {
        showToast(`AnimeThemes mapping failed: ${res.data?.message || JSON.stringify(res.data)}`, "error", 0);
      }
    });
  }
  const atApplyBtn = el("at-apply");
  if (atApplyBtn) {
    withButtonAction(atApplyBtn, async function () {
      showToast("AnimeThemes: applying mapping...", "info", 3000);
      const p = buildAtMapParams();
      p.set("applyMapping", "true");
      const res = await fetchJson(base + "/animethemes/vfs?" + p.toString());
      if (res.ok) {
        const summary = summarizeResult(res) || "Applied mapping";
        const atApplyErr = getErrorCount(res);
        showToast(`AnimeThemes apply: ${summary}`, atApplyErr > 0 ? "error" : "success", atApplyErr > 0 ? 0 : 5000);
      } else {
        showToast(`AnimeThemes apply failed: ${res.data?.message || JSON.stringify(res.data)}`, "error", 0);
      }
    });
  }

  function getValueByPath(obj, path) {
    return path.split(".").reduce((o, k) => (o ? o[k] : undefined), obj);
  }
  function setValueByPath(obj, path, value) {
    const parts = path.split(".");
    let cur = obj;
    for (let i = 0; i < parts.length - 1; i++) {
      const key = parts[i];
      if (!cur[key]) cur[key] = {};
      cur = cur[key];
    }
    cur[parts[parts.length - 1]] = value;
  }

  async function loadConfig() {
    const schemaRes = await fetchJson(base + "/config/schema");
    const configRes = await fetchJson(base + "/config");
    if (!schemaRes.ok || !configRes.ok) {
      showToast("Failed to load config.", "error", 0);
      return;
    }
    const schema = schemaRes.data.properties || [];
    const config = configRes.data;
    const container = el("config-form");
    container.innerHTML = "";

    // Persist updated config to the server (used by auto-save handlers)
    async function persistConfig(updated) {
      try {
        const res = await fetchJson(base + "/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(updated) });
        if (!res.ok) showToast(`Config save failed: ${res.data?.message || JSON.stringify(res.data)}`, "error", 0);
        return res;
      } catch (err) {
        showToast(`Save failed: ${err?.message || err}`, "error", 0);
        return { ok: false, data: err };
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
      const value = getValueByPath(config, p.Path);

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
          '<svg class="unchecked" viewBox="0 0 24 24"><use href="img/icons.svg#checkbox-blank-circle-outline"></use></svg>' +
          '<svg class="checked" viewBox="0 0 24 24"><use href="img/icons.svg#checkbox-marked-circle-outline"></use></svg>';

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
        } else if (p.Type === "number") {
          input = document.createElement("input");
          input.type = "number";
          input.value = value !== undefined && value !== null ? value : "";
        } else if (p.Path === "PathMappings") {
          const left = document.createElement("textarea");
          const right = document.createElement("textarea");
          left.id = "path-mappings-left";
          right.id = "path-mappings-right";
          const mappings = value || {};
          const keys = Object.keys(mappings || {}).sort();
          const leftLines = keys.map((k) => k);
          const rightLines = keys.map((k) => mappings[k]);
          left.value = leftLines.join("\n");
          right.value = rightLines.join("\n");
          left.placeholder = "e.g. /anime";
          right.placeholder = "e.g. M:\\Anime";
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
                const k = (leftLines[i] || "").trim();
                const v = (rightLines[i] || "").trim();
                if (k && v) mapping[k] = v;
              }
              setValueByPath(config, p.Path, mapping);
              await persistConfig(config);
            } catch (e) {
              showToast(`Auto-save path mappings error: ${e?.message || e}`, "error", 0);
            }
          };
          const col = document.createElement("div");
          col.className = "full";
          const leftWrap = document.createElement("div");
          const rightWrap = document.createElement("div");
          const leftLabel = document.createElement("label");
          leftLabel.textContent = "Shoko Base Paths (one per line)";
          const rightLabel = document.createElement("label");
          rightLabel.textContent = "Plex Base Paths (match lines)";
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
          input.value = value ? JSON.stringify(value, null, 2) : "";
        } else if (p.Path === "TagBlacklist") {
          input = document.createElement("textarea");
          input.value = value !== undefined && value !== null ? value : "";
        } else {
          input = document.createElement("input");
          input.type = "text";
          input.value = value !== undefined && value !== null ? value : "";
        }
      }

      if (input) {
        input.dataset.path = p.Path;
        input.dataset.type = p.Type;
        // auto-save this provider configuration field on change
        input.onchange = async () => {
          try {
            let val;
            const type = input.dataset.type;
            if (type === "bool") {
              val = input.checked;
            } else if (type === "enum" || type === "number") {
              val = input.value === "" ? 0 : Number(input.value);
            } else if (type === "json") {
              try {
                val = input.value ? JSON.parse(input.value) : null;
              } catch (e) {
                val = null;
              }
            } else {
              val = input.value;
            }
            setValueByPath(config, input.dataset.path, val);
            await persistConfig(config);
          } catch (e) {
            showToast(`Auto-save error: ${e?.message || e}`, "error", 0);
          }
        };
        if (!input.parentNode) {
          wrap.appendChild(input);
        }
      }
      container.appendChild(wrap);
    });

    // Manual config save removed — provider inputs auto-save via onchange handlers.

    // Populate Shoko Automation custom inputs from the loaded config (keeps UI in sync)
    try {
      const apiKeyVal = String(config.ShokoApiKey ?? "");
      const importVal = Number(config.ShokoImportFrequencyHours ?? 0);
      const syncVal = Number(config.ShokoSyncWatchedFrequencyHours ?? 0);
      const plexWatchedVal = !!config.AutoScrobble;
      const apiKeyEl = el("shoko-api-key");
      const importFreqEl = el("shoko-import-frequency");
      const syncFreqEl = el("shoko-sync-frequency");
      if (apiKeyEl) {
        apiKeyEl.value = apiKeyVal;
        apiKeyEl.onchange = async () => {
          try {
            setValueByPath(config, "ShokoApiKey", String(apiKeyEl.value || ""));
            await persistConfig(config);
          } catch (e) {
            showToast(`Auto-save Shoko API key error: ${e?.message || e}`, "error", 0);
          }
        };
      }
      if (importFreqEl) {
        importFreqEl.value = importVal === 0 ? "" : String(importVal);
        importFreqEl.onchange = async () => {
          try {
            const newVal = importFreqEl.value === "" ? 0 : Number(importFreqEl.value);
            setValueByPath(config, "ShokoImportFrequencyHours", newVal);
            await persistConfig(config);
          } catch (e) {
            showToast(`Auto-save import frequency error: ${e?.message || e}`, "error", 0);
          }
        };
      }
      if (syncFreqEl) {
        syncFreqEl.value = syncVal === 0 ? "" : String(syncVal);
        syncFreqEl.onchange = async () => {
          try {
            setValueByPath(config, "ShokoSyncWatchedFrequencyHours", syncFreqEl.value === "" ? 0 : Number(syncFreqEl.value));
            await persistConfig(config);
          } catch (e) {
            showToast(`Auto-save sync frequency error: ${e?.message || e}`, "error", 0);
          }
        };
      }
      if (el("plex-watched")) el("plex-watched").checked = plexWatchedVal;

      // Persist the 'Include Ratings' checkbox from the modal so scheduled automation follows it
      const syncRatingsEl = el("sync-ratings");
      if (syncRatingsEl) {
        try {
          syncRatingsEl.checked = !!config.ShokoSyncWatchedIncludeRatings;
        } catch (e) {
          syncRatingsEl.checked = false;
        }
        syncRatingsEl.onchange = async () => {
          try {
            setValueByPath(config, "ShokoSyncWatchedIncludeRatings", syncRatingsEl.checked);
            await persistConfig(config);
          } catch (err) {
            showToast(`Failed to save 'Include Ratings' setting: ${err?.message || err}`, "error", 0);
          }
        };
      }
    } catch (e) {
      /* ignore */
    }
  }

  setPlexStartAction();
  refreshPlexState();
  loadConfig();

  /* Theme toggle - persists to localStorage and sets data-theme on <html> */
  const THEME_KEY = "dashboard-theme";
  function getSavedTheme() {
    try {
      const v = localStorage.getItem(THEME_KEY);
      if (v === "dark" || v === "light") return v;
    } catch (e) {
      /* ignore */
    }
    return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }
  function applyTheme(theme) {
    try {
      document.documentElement.setAttribute("data-theme", theme === "dark" ? "dark" : "light");
    } catch (e) {
      /* ignore */
    }
    const btn = el("theme-toggle");
    if (btn) btn.setAttribute("aria-pressed", theme === "dark" ? "true" : "false");
  }
  function initTheme() {
    applyTheme(getSavedTheme());
    const t = el("theme-toggle");
    if (!t) return;
    t.onclick = () => {
      const cur = document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light";
      const next = cur === "dark" ? "light" : "dark";
      try {
        localStorage.setItem(THEME_KEY, next);
      } catch (e) {
        /* ignore */
      }
      applyTheme(next);
    };
  }
  /* Tooltip helper - mirrors Shoko WebUI tooltip styling (uses elements' `title` attributes) */
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

    let showTimer = null;
    let hideTimer = null;

    function showForElement(el) {
      const text = el.dataset.tooltipText || el.getAttribute("data-tooltip") || "";
      if (!text) return;
      content.textContent = text;
      tpl.setAttribute("aria-hidden", "false");
      tpl.classList.remove("tooltip-closing");
      tpl.classList.add("tooltip-show");
      el.setAttribute("aria-describedby", "shoko-tooltip");

      requestAnimationFrame(() => {
        const rect = el.getBoundingClientRect();
        const ttRect = tpl.getBoundingClientRect();
        const margin = 8;
        let top = rect.top - ttRect.height - margin;
        let place = "top";
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

    function hideTooltipForElement(el) {
      tpl.classList.remove("tooltip-show");
      tpl.classList.add("tooltip-closing");
      tpl.setAttribute("aria-hidden", "true");
      if (el) el.removeAttribute("aria-describedby");
    }

    function attach(el) {
      if (!el || el.dataset.tooltipInitialized) return;
      const title = el.getAttribute("title");
      if (!title) return;
      el.dataset.tooltipText = title;
      el.removeAttribute("title");
      el.dataset.tooltipInitialized = "1";

      el.addEventListener("mouseenter", () => {
        if (hideTimer) {
          clearTimeout(hideTimer);
          hideTimer = null;
        }
        showTimer = setTimeout(() => showForElement(el), 75);
      });
      el.addEventListener("mouseleave", () => {
        if (showTimer) {
          clearTimeout(showTimer);
          showTimer = null;
        }
        hideTimer = setTimeout(() => hideTooltipForElement(el), 100);
      });
      el.addEventListener("focus", () => {
        if (showTimer) clearTimeout(showTimer);
        showForElement(el);
      });
      el.addEventListener("blur", () => hideTooltipForElement(el));
    }

    document.querySelectorAll("[title]").forEach(attach);

    const mo = new MutationObserver((mutations) => {
      for (const m of mutations) {
        for (const node of m.addedNodes) {
          if (node.nodeType !== 1) continue;
          const nn = node;
          if (nn.hasAttribute && nn.hasAttribute("title")) attach(nn);
          nn.querySelectorAll && nn.querySelectorAll("[title]").forEach(attach);
        }
      }
    });
    mo.observe(document.body, { childList: true, subtree: true });
  }

  initTooltips();

  initTheme();
})();
