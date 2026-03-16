/**
 * @file script.js
 * @description The main dashboard script, responsible for wiring up UI interactions, fetching data from the server, and managing shared utilities like toasts and modals.
 */
(() => {
  const base = location.pathname.split("/dashboard")[0];
  const el = (id) => document.getElementById(id);

  // Initialize global namespace immediately so subsequent scripts can see it
  window._sr = { base };

  /** Default auto-dismiss duration (ms) for transient toasts. Use 0 for persistent toasts that require manual dismissal. */
  const TOAST_MS = 5000;

  /** Standardized labels for playback controls used by both MP3 and Video players. */
  const PLAYBACK_LABELS = {
    loop: "Loop",
    shuffle: "Shuffle",
    next: "Next",
    off: "Once",
    idle: "Play",
    playing: "Next",
  };

  /** List of button IDs that correspond exactly to server task names for spinner synchronization. */
  const MANAGED_TASK_IDS = ["vfs-build", "at-vfs-build", "at-map-build", "plex-collections-build", "plex-ratings-apply", "shoko-remove-missing", "at-mp3-build"];

  // #region Helpers

  /**
   * Extracts the inner data object from a standardized server response envelope.
   * @param {Object} res - The normalized fetch response.
   * @returns {*} The actual result data.
   */
  const getData = (res) => (res?.data?.data !== undefined && res?.data?.data !== null ? res.data.data : res?.data);

  /**
   * Helper to append a key-value pair to a URLSearchParams object only if the value is provided and non-empty.
   * @param {URLSearchParams} ps - The search parameters object to modify.
   * @param {string} k - The parameter key.
   * @param {*} v - The value to check and potentially append.
   */
  const setIfNotEmpty = (ps, k, v) => {
    if (v != null && String(v) !== "") ps.set(k, String(v));
  };

  /**
   * Polls the server for active tasks and completed results, synchronizing the UI state.
   */
  async function syncActiveTasks() {
    /** Check Active Tasks for spinners */
    const res = await fetchJson(window._sr.base + "/tasks/active");
    if (!res.ok) return;
    const activeTasks = res.data || [];
    MANAGED_TASK_IDS.forEach((id) => {
      const btn = el(id);
      if (!btn) return;
      const isActive = activeTasks.includes(id);
      // Only set idle if not currently in the middle of a click event
      if (!isActive && !btn.classList.contains("clicking")) setButtonLoading(btn, false);
      else if (isActive) setButtonLoading(btn, true);
    });

    /** Check Completed Tasks for toasts */
    const completeRes = await fetchJson(window._sr.base + "/tasks/completed");
    if (completeRes.ok && completeRes.data) {
      for (const [taskName, result] of Object.entries(completeRes.data)) {
        const label = taskName.replace(/-/g, " "); // Use default label if friendly label can't be found
        toastOperation({ ok: result.status === "ok", data: result }, label);
        await fetch(window._sr.base + `/tasks/clear/${taskName}`, { method: "POST" });
      }
    }
  }

  /**
   * Wrap a button's click handler to manage loading states and task synchronization.
   * @param {HTMLElement|string} btn - The button element or its DOM id.
   * @param {Function} handler - Async click handler.
   */
  function withButtonAction(btn, handler) {
    const elBtn = typeof btn === "string" ? el(btn) : btn;
    if (!elBtn) return;
    elBtn.onclick = async (...args) => {
      elBtn.classList.add("clicking");
      setButtonLoading(elBtn, true);
      try {
        await handler.apply(elBtn, args);
      } finally {
        elBtn.classList.remove("clicking");
        syncActiveTasks();
      }
    };
  }

  /**
   * @param {string} url - The log URL to wrap in an anchor tag.
   * @returns {string} An HTML anchor link or an empty string if no URL was provided.
   */
  const makeLogLink = (url) => (url ? `[<a href="${url}" target="_blank" class="log-link">view log</a>]` : "");

  /**
   * Show success/error toasts for HTTP responses with automatic log-link injection and result summarization.
   * @param {{ok: boolean, data: *}} res - The fetchJson response object.
   * @param {string} label - Identifies the operation.
   * @param {{summary?: string, hideOnSucceed?: number}} [opts] - Display options.
   */
  function toastOperation(res, label, opts = {}) {
    const { summary, hideOnSucceed } = opts;
    const { text, errorCount } = summarizeResult(res);
    const logLink = makeLogLink(res.data?.logUrl);

    if (res.ok) {
      const display = summary || text || `${label} Complete`;
      showToast(`${label}: ${display} ${logLink}`, errorCount > 0 ? "error" : "success", errorCount > 0 ? 0 : (hideOnSucceed ?? TOAST_MS));
    } else {
      showToast(`${label} Failed: ${summary || res.data?.message || JSON.stringify(res.data)} ${logLink}`, "error", 0);
    }
  }

  /**
   * Build a human-readable summary string and error count from common API response fields.
   * @param {Object} res - The response object.
   * @returns {{text: string, errorCount: number}} Summarized details.
   */
  function summarizeResult(res) {
    const d = getData(res);
    if (!d) return { text: "", errorCount: 0 };

    const parts = [];
    let errorCount = 0;
    const keys = {
      processed: ["SeriesProcessed", "Processed", "ScannedCount", "ProcessedShows"],
      created: ["LinksCreated", "Created", "UpdatedShows"],
      marked: ["Marked", "MarkedWatched", "UpdatedEpisodes"],
      skipped: ["Skipped", "SkippedCount"],
      errors: ["Errors", "ErrorsList", "Message"],
      uploaded: ["Uploaded"],
    };

    Object.entries(keys).forEach(([label, aliases]) => {
      const match = aliases.find((k) => d[k] !== undefined && d[k] !== null);
      if (match === undefined) return;
      const v = d[match];
      const n = label === "errors" ? (Array.isArray(v) ? v.length : Number(v) || 0) : v;
      if (label === "errors") errorCount = n;
      if (label !== "errors" || n > 0) parts.push(`${label}: ${n}`);
    });

    const text = parts.length ? parts.join(", ") : typeof d === "string" ? (d.length > 200 ? d.substring(0, 200) + "..." : d) : "";
    return { text, errorCount };
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
   * Toggle a button's loading state by adding/removing a spinner overlay and disabled attribute.
   * @param {HTMLElement} btn - The button element to modify.
   * @param {boolean} isLoading - Whether to enable or disable the loading state.
   */
  function setButtonLoading(btn, isLoading) {
    if (!btn) return;
    btn.classList.toggle("loading", isLoading);
    isLoading ? btn.setAttribute("disabled", "") : btn.removeAttribute("disabled");
    if (isLoading && !btn.querySelector(".button-spinner")) {
      const s = document.createElement("span");
      s.className = "button-spinner";
      s.innerHTML = '<svg class="icon-svg"><use href="img/icons.svg#loading"></use></svg>';
      btn.appendChild(s);
    } else if (!isLoading) btn.querySelector(".button-spinner")?.remove();
  }

  /** Attach a smooth open/close animation to a &lt;details&gt; element using the Web Animations API. */
  function initDetailsAnimation(details, content, duration = 300) {
    let anim = null;
    details.querySelector("summary")?.addEventListener("click", (e) => {
      e.preventDefault();
      if (anim) anim.cancel();
      const isOpening = !details.open;
      if (isOpening) details.open = true;
      const startH = isOpening ? "0px" : content.offsetHeight + "px";
      const endH = isOpening ? content.offsetHeight + "px" : "0px";
      anim = content.animate({ height: [startH, endH] }, { duration, easing: "ease" });
      anim.onfinish = anim.oncancel = () => {
        if (!isOpening) details.open = false;
        anim = null;
        content.style.height = "";
      };
    });
  }

  /** @param {Object} obj @param {string} path - Dot-separated key path. @returns {*} The resolved value, or undefined. */
  const getValueByPath = (obj, path) => path.split(".").reduce((o, k) => o?.[k], obj);

  /** @param {Object} obj @param {string} path - Dot-separated key path. @param {*} value - The value to set at the resolved path. */
  function setValueByPath(obj, path, value) {
    const parts = path.split("."),
      last = parts.pop();
    const target = parts.reduce((o, k) => ((o[k] ??= {}), o[k]), obj);
    target[last] = value;
  }

  /**
   * Binds a UI element to a config path with automatic persistence.
   * @param {HTMLElement|string} target - The DOM element or ID.
   * @param {string} path - Config path.
   * @param {Object} config - Config object.
   * @param {Function} persistFn - Save callback.
   * @param {"text"|"number"|"check"} [type="text"] - Input type.
   */
  const bindConfig = (target, path, config, persistFn, type = "text") => {
    const e = typeof target === "string" ? el(target) : target;
    if (!e) return;

    const val = getValueByPath(config, path);
    if (type === "check") e.checked = !!val;
    else e.value = val ?? "";

    e.onchange = async () => {
      let newVal = type === "check" ? e.checked : type === "number" ? Number(e.value) || 0 : e.value;
      setValueByPath(config, path, newVal);
      await persistFn(config);
    };
  };

  /** Unwrap a config response that may contain a payload wrapper. @param {Object} data @returns {Object} The inner config object. */
  const unwrapConfig = (data) => (data?.payload !== undefined ? data.payload || {} : data || {});

  /**
   * Opens a modal and attaches automated close handlers for overlay clicks and Escape key.
   * @param {HTMLElement} modal - The modal element.
   * @returns {Function} Close function.
   */
  function openModal(modal) {
    if (!modal) return () => {};
    modal.setAttribute("aria-hidden", "false");
    modal.classList.add("open");
    document.body.style.overflow = "hidden";

    const close = () => {
      modal.setAttribute("aria-hidden", "true");
      modal.classList.remove("open");
      document.body.style.overflow = "";
      modal.removeEventListener("click", onOverlay);
      document.removeEventListener("keydown", onKey);
    };

    const onOverlay = (ev) => ev.target === modal && close();
    const onKey = (ev) => ev.key === "Escape" && close();
    modal.addEventListener("click", onOverlay);
    document.addEventListener("keydown", onKey);

    const firstInput = modal.querySelector("button, textarea, input:not([type='hidden'])");
    setTimeout(() => firstInput?.focus(), 10);

    return close;
  }

  /** Updates element title from data-mode or data-state for tooltips. @param {HTMLElement} el */
  const updatePlaybackTooltip = (el) => {
    if (!el) return;
    const key = el.getAttribute("data-mode") || el.getAttribute("data-state");
    if (PLAYBACK_LABELS[key]) el.title = PLAYBACK_LABELS[key];
  };

  /**
   * Extract the error count from an API response by checking common error fields.
   * @param {{ok: boolean, data: *}} res - The fetchJson response object.
   * @returns {number} The number of errors found, or 0 if none.
   */
  const getErrorCount = (res) => summarizeResult(res).errorCount;

  /**
   * Displays a toast notification.
   * @param {string} message - HTML message content.
   * @param {"info"|"success"|"warning"|"error"} [type="info"] - Toast severity/style.
   * @param {number} [timeout=5000] - Auto-dismiss delay in ms.
   * @returns {HTMLElement|null}
   */
  function showToast(message, type = "info", timeout = 5000) {
    const container = el("toast-container");
    if (!container) return null;
    const t = document.createElement("div");
    t.className = `toast ${type || "info"}`;
    t.tabIndex = 0;
    t.innerHTML = `<span class="toast-message">${message}</span>`;
    container.appendChild(t);
    requestAnimationFrame(() => t.classList.add("visible"));
    const dismiss = () => {
      t.classList.remove("visible");
      setTimeout(() => t.remove(), 300);
    };
    let timer = timeout > 0 ? setTimeout(dismiss, timeout) : null;
    t.onclick = () => {
      clearTimeout(timer);
      dismiss();
    };
    return t;
  }

  // Populate shared global object
  Object.assign(window._sr, {
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
    getValueByPath,
    setValueByPath,
    bindConfig,
    unwrapConfig,
    openModal,
    setButtonLoading,
    updatePlaybackTooltip,
    syncActiveTasks,
    getErrorCount,
  });
  // #endregion

  // #region Provider Settings
  /**
   * Builds the configuration settings form dynamically based on server schema.
   */
  async function loadConfig() {
    if (!el("config-form")) return;
    const [schemaRes, configRes] = await Promise.all([fetchJson(base + "/config/schema"), fetchJson(base + "/config")]);
    if (!schemaRes.ok || !configRes.ok) return showToast("Failed To Load Config", "error", 0);

    const schema = schemaRes.data.properties || [],
      rawCfg = configRes.data || {},
      config = unwrapConfig(rawCfg);
    const overridesBtn = el("vfs-overrides");
    const tmdbEnabled = getValueByPath(config, "TmdbEpNumbering") ?? getValueByPath(config, "Advanced.TmdbEpNumbering");
    if (overridesBtn) overridesBtn.disabled = !tmdbEnabled;

    el("config-form").innerHTML = "";
    el("overrides-text") && (el("overrides-text").value = rawCfg.overrides || "");

    const advSection = document.createElement("details"),
      advContent = document.createElement("div");
    advSection.className = "details-anim";
    advContent.className = "details-content";
    advSection.innerHTML = "<summary>Advanced Settings</summary>";
    advContent.appendChild(document.createElement("hr"));

    const persistConfig = async (updated) => {
      const res = await fetchJson(base + "/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(updated) });
      if (!res.ok) toastOperation(res, "Config Save");
      return res;
    };

    schema.forEach((p) => {
      const wrap = document.createElement("div"),
        label = document.createElement("label");
      let input,
        value = getValueByPath(config, p.Path);

      if (p.Type === "bool") {
        wrap.innerHTML = `<label class="shoko-checkbox"><input type="checkbox">
          <span class="shoko-checkbox-icon" aria-hidden="true"><svg class="unchecked"><use href="img/icons.svg#checkbox-blank-circle-outline"></use></svg><svg class="checked"><use href="img/icons.svg#checkbox-marked-circle-outline"></use></svg></span>
          <span class="shoko-checkbox-text"><span class="shoko-checkbox-title">${p.Display || p.Path}</span><small class="shoko-checkbox-desc" style="display:block">${p.Description || ""}</small></span></label>`;
        input = wrap.querySelector("input");
        bindConfig(input, p.Path, config, persistConfig, "check");
      } else if (p.Path.endsWith("PathMappings")) {
        label.innerHTML = `<span>${p.Display || p.Path.split(".").pop()}</span>${p.Description ? `<small>${p.Description}</small>` : ""}`;
        wrap.appendChild(label);
        const mappingContainer = document.createElement("div");
        mappingContainer.innerHTML = `<div class="full"><div><small>Plex Base Paths</small><textarea id="path-mappings-left"></textarea></div><div><small>Shoko Base Paths</small><textarea id="path-mappings-right"></textarea></div></div>`;
        wrap.appendChild(mappingContainer);
        const l = mappingContainer.querySelector("#path-mappings-left"),
          r = mappingContainer.querySelector("#path-mappings-right"),
          m = value || {};
        const keys = Object.keys(m).sort();
        l.value = keys.map((k) => m[k]).join("\n");
        r.value = keys.join("\n");
        const onMapChange = async () => {
          const val = {};
          const lLines = l.value.split("\n"),
            rLines = r.value.split("\n");
          lLines.forEach((lv, idx) => {
            if (lv.trim() && rLines[idx]?.trim()) val[rLines[idx].trim()] = lv.trim();
          });
          setValueByPath(config, p.Path, val);
          await persistConfig(config);
        };
        l.onchange = r.onchange = onMapChange;
      } else {
        label.innerHTML = `<span>${p.Display || p.Path.split(".").pop()}</span>${p.Description ? `<small>${p.Description}</small>` : ""}`;
        wrap.appendChild(label);
        input = document.createElement(p.Type === "enum" ? "select" : p.Type === "json" || p.Path.endsWith("TagBlacklist") ? "textarea" : "input");

        if (p.Type === "enum") {
          (p.EnumValues || []).forEach((ev) => {
            const opt = new Option(ev.name, ev.value);
            opt.selected = String(ev.value) === String(value);
            input.add(opt);
          });
        } else if (p.Type === "number") {
          input.type = "number";
        } else if (p.Type === "json") {
          input.placeholder = "JSON object";
        } else {
          input.type = "text";
          // Placeholder for Shoko URL
          if (p.Path.endsWith("ShokoServerUrl")) {
            input.placeholder = "e.g. http://localhost:8111";
          }
        }

        wrap.appendChild(input);

        // Custom validation for ShokoServerUrl
        if (p.Path.endsWith("ShokoServerUrl")) {
          input.value = value ?? "";
          input.onchange = async () => {
            const urlRegex = /^https?:\/\/[a-zA-Z0-9.-]+(:\d+)?$/;
            const cleanVal = input.value.trim().replace(/\/+$/, "");

            if (cleanVal && !urlRegex.test(cleanVal)) {
              showToast("Invalid Shoko URL. Use http(s)://HOST:PORT", "error", 5000);
              input.value = getValueByPath(config, p.Path) || "";
              return;
            }

            input.value = cleanVal;
            setValueByPath(config, p.Path, cleanVal);
            await persistConfig(config);
          };
        } else {
          // Standard binding for all other generic fields
          bindConfig(input, p.Path, config, persistConfig, p.Type === "bool" ? "check" : p.Type === "number" ? "number" : "text");
        }
      }
      (p.Advanced ? advContent : el("config-form")).appendChild(wrap);
    });

    if (advContent.children.length > 1) {
      el("config-form").appendChild(advSection);
      advSection.appendChild(advContent);
      initDetailsAnimation(advSection, advContent);
    }

    const b = (id, path, type) => bindConfig(id, path, config, persistConfig, type);
    b("shoko-utc-offset", "Automation.UtcOffsetHours", "number");
    b("shoko-import-frequency", "Automation.ShokoImportFrequencyHours", "number");
    b("shoko-sync-frequency", "Automation.ShokoSyncWatchedFrequencyHours", "number");
    b("plex-auto-frequency", "Automation.PlexAutomationFrequencyHours", "number");
    b("sync-ratings", "Automation.ShokoSyncWatchedIncludeRatings", "check");
    b("sync-exclude-admin", "Automation.ShokoSyncWatchedExcludeAdmin", "check");
    b("plex-scrobble", "Automation.AutoScrobble", "check");

    window._sr.initAtConfig?.(config, persistConfig);
  }
  // #endregion

  // #region Theme Toggle
  const THEME_KEY = "dashboard-theme";
  const getSavedTheme = () => localStorage.getItem(THEME_KEY) || (window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);
    el("theme-toggle")?.setAttribute("aria-pressed", String(theme === "dark"));
  }
  function initTheme() {
    applyTheme(getSavedTheme());
    if (el("theme-toggle"))
      el("theme-toggle").onclick = () => {
        const next = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
        localStorage.setItem(THEME_KEY, next);
        applyTheme(next);
      };
  }
  // #endregion

  // #region Tooltips
  function initTooltips() {
    if (el("shoko-tooltip")) return;
    const tpl = document.createElement("div");
    tpl.id = "shoko-tooltip";
    tpl.className = "tooltip-core tooltip-box tooltip-dark tooltip-place-top";
    tpl.setAttribute("role", "status");
    tpl.setAttribute("aria-hidden", "true");
    tpl.innerHTML = '<div class="tooltip-arrow"></div><div class="rt-content"></div>';
    document.body.appendChild(tpl);
    const content = tpl.querySelector(".rt-content");
    let showTimer, hideTimer;

    const show = (target) => {
      const text = target.dataset.tooltipText || target.getAttribute("data-tooltip") || target.getAttribute("title");
      if (target.disabled || !text) return;
      if (target.dataset.tooltipOverflowOnly === "true" && target.scrollWidth <= target.clientWidth) return;

      content.textContent = text;
      tpl.className = "tooltip-core tooltip-box tooltip-dark tooltip-show";
      tpl.setAttribute("aria-hidden", "false");

      const rect = target.getBoundingClientRect(),
        vw = document.documentElement.clientWidth,
        vh = document.documentElement.clientHeight,
        margin = 10;
      let place = "top",
        top = rect.top - tpl.offsetHeight - margin;
      const left = rect.left + rect.width / 2 - tpl.offsetWidth / 2;

      if (top < margin) {
        const spaceBelow = vh - rect.bottom;
        if (spaceBelow > tpl.offsetHeight + margin) {
          top = rect.bottom + margin;
          place = "bottom";
        }
      }
      const minLeft = margin,
        maxLeft = vw - tpl.offsetWidth - margin;
      const clampedLeft = Math.max(minLeft, Math.min(left, maxLeft));

      const arrow = tpl.querySelector(".tooltip-arrow");
      if (arrow) {
        arrow.style.marginLeft = `${left - clampedLeft}px`;
        arrow.style.marginTop = "0px";
      }

      tpl.classList.add(`tooltip-place-${place}`);
      tpl.style.left = `${Math.round(clampedLeft + window.scrollX)}px`;
      tpl.style.top = `${Math.round(top + window.scrollY)}px`;
      target.setAttribute("aria-describedby", "shoko-tooltip");
    };

    const hide = () => {
      clearTimeout(showTimer);
      clearTimeout(hideTimer);
      tpl.classList.remove("tooltip-show");
      tpl.classList.add("tooltip-closing");
      tpl.setAttribute("aria-hidden", "true");
      setTimeout(() => tpl.classList.remove("tooltip-closing"), 150);
    };

    window.addEventListener("blur", hide);
    document.addEventListener("mouseleave", hide);

    const attach = (t) => {
      if (!t.title || t.dataset.tooltipText) return;
      t.dataset.tooltipText = t.title;
      t.removeAttribute("title");
      t.addEventListener("mouseenter", () => {
        clearTimeout(hideTimer);
        showTimer = setTimeout(() => {
          if (t.matches(":hover")) show(t);
        }, 100);
      });
      t.addEventListener("mouseleave", hide);
      t.addEventListener("mousedown", hide);
    };

    document.querySelectorAll("[title]").forEach(attach);
    new MutationObserver((ms) => {
      ms.forEach((m) => {
        m.addedNodes.forEach((n) => n.nodeType === 1 && (n.title ? attach(n) : n.querySelectorAll("[title]").forEach(attach)));
        if (m.type === "attributes" && m.attributeName === "title" && m.target.title) attach(m.target);
      });
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["title"] });
  }
  // #endregion

  initTooltips();
  initTheme();
  loadConfig();
  setInterval(syncActiveTasks, 3000);
  syncActiveTasks();
})();
