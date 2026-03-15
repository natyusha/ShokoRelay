/**
 * @file script.js
 * @description The main dashboard script, responsible for wiring up UI interactions, fetching data from the server, and managing shared utilities like toasts and modals.
 */
(() => {
  const base = location.pathname.split("/dashboard")[0];
  const el = (id) => document.getElementById(id);

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

  /** List of button IDs that correspond exactly to server task names */
  const MANAGED_TASK_IDS = ["vfs-build", "at-vfs-build", "at-map-build", "plex-collections-build", "plex-ratings-apply", "shoko-remove-missing", "at-mp3-build"];

  // #region Helpers
  /**
   * Polls the server for active tasks and syncs the UI button states.
   * This ensures that if the page is refreshed or opened in a new tab, the "Loading" state and spinners are restored for running operations.
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
      if (!isActive && !btn.classList.contains("clicking")) {
        setButtonLoading(btn, false);
      } else if (isActive) {
        setButtonLoading(btn, true);
      }
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
   * Wrap a button's click handler so it automatically shows/hides a loading spinner. Adds 'clicking' class to manage race conditions between manual clicks and background polling.
   * @param {HTMLElement|string} btn - The button element or its DOM id.
   * @param {Function} handler - Async click handler to execute while the button is in a loading state.
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
        window._sr.syncActiveTasks();
      }
    };
  }

  /**
   * @param {string} url - The log URL to wrap in an anchor tag.
   * @returns {string} An HTML anchor link or an empty string if no URL was provided.
   */
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
      const errs = getErrorCount(res);
      const text = summary || summarizeResult(res) || `${label} Complete`;
      showToast(`${label}: ${text} ${logLink}`, errs > 0 ? "error" : "success", errs > 0 ? 0 : (hideOnSucceed ?? TOAST_MS));
    } else {
      showToast(`${label} Failed: ${summary || res.data?.message || JSON.stringify(res.data)} ${logLink}`, "error", 0);
    }
  }

  /**
   * Build a human-readable summary string from common API response fields.
   * Standardized to PascalCase to match the C# backend records.
   * @param {Object} res - The response object containing data.
   * @returns {string} A comma-separated summary string.
   */
  function summarizeResult(res) {
    if (!res?.data) return "";

    // Access nested result data if present (LogAndReturn envelope), otherwise use root
    const d = res.data.data !== undefined && res.data.data !== null ? res.data.data : res.data;

    const parts = [];
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
      if (label !== "errors" || n > 0) parts.push(`${label}: ${n}`);
    });

    return parts.length ? parts.join(", ") : typeof d === "string" ? (d.length > 200 ? d.substring(0, 200) + "..." : d) : "";
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
   * Helper to append a key-value pair to a URLSearchParams object only if the value is provided and non-empty.
   * @param {URLSearchParams} ps - The search parameters object to modify.
   * @param {string} k - The parameter key.
   * @param {*} v - The value to check and potentially append.
   */
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

  /**
   * Get a value from a nested object using a dot-separated path.
   * @param {Object} obj - The source object.
   * @param {string} path - Dot-separated key path.
   * @returns {*} The resolved value, or undefined.
   */
  const getValueByPath = (obj, path) => path.split(".").reduce((o, k) => o?.[k], obj);

  /**
   * Set a value in a nested object using a dot-separated path.
   * @param {Object} obj - The target object.
   * @param {string} path - Dot-separated key path.
   * @param {*} value - The value to set at the resolved path.
   */
  function setValueByPath(obj, path, value) {
    const parts = path.split("."),
      last = parts.pop();
    const target = parts.reduce((o, k) => ((o[k] ??= {}), o[k]), obj);
    target[last] = value;
  }

  /**
   * Binds a UI element to a config path with automatic persistence.
   * @param {string} id - The DOM element ID.
   * @param {string} path - The dot-notation path in the config (e.g., "Automation.UtcOffsetHours").
   * @param {Object} config - The global config object.
   * @param {Function} persistFn - The function that POSTs the config to the server.
   * @param {"text"|"number"|"check"} [type="text"] - The type of input handling.
   */
  const bindConfig = (id, path, config, persistFn, type = "text") => {
    const e = el(id);
    if (!e) return;

    // Set initial state
    const val = getValueByPath(config, path);
    if (type === "check") e.checked = !!val;
    else e.value = val ?? "";

    // Auto-save on change
    e.onchange = async () => {
      let newVal = type === "check" ? e.checked : type === "number" ? Number(e.value) || 0 : e.value;
      setValueByPath(config, path, newVal);
      await persistFn(config);
    };
  };

  /**
   * Unwrap a config response that may contain a payload wrapper.
   * @param {Object} data - Response data.
   * @returns {Object} The inner config object.
   */
  const unwrapConfig = (data) => (data?.payload !== undefined ? data.payload || {} : data || {});

  /**
   * Attach overlay-click and Escape-key close listeners to a modal element.
   * @param {HTMLElement} modal - The modal element to attach handlers to.
   * @returns {Function} A close() function that hides the modal and removes its listeners.
   */
  function attachModalCloseHandlers(modal) {
    if (!modal) return () => {};
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
    return close;
  }

  /**
   * Centralized modal opener logic.
   * @param {HTMLElement} modal - The modal element to open.
   * @returns {Function} The close handler for the modal.
   */
  function openModal(modal) {
    modal.setAttribute("aria-hidden", "false");
    modal.classList.add("open");
    document.body.style.overflow = "hidden";
    const firstBtn = modal.querySelector("button, textarea, input");
    setTimeout(() => firstBtn?.focus(), 10);
    return attachModalCloseHandlers(modal);
  }

  /**
   * Updates an element's title attribute based on its current data-mode or data-state.
   * This triggers the MutationObserver to update the custom project tooltip.
   * @param {HTMLElement} el - The element to update.
   */
  const updatePlaybackTooltip = (el) => {
    if (!el) return;
    const key = el.getAttribute("data-mode") || el.getAttribute("data-state");
    if (PLAYBACK_LABELS[key]) {
      el.title = PLAYBACK_LABELS[key];
    }
  };

  /** Expose shared helpers */
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
    getValueByPath,
    setValueByPath,
    bindConfig,
    getErrorCount,
    unwrapConfig,
    openModal,
    setButtonLoading,
    updatePlaybackTooltip,
    syncActiveTasks,
  };
  // #endregion

  // #region Provider Settings
  /**
   * Fetch the config schema and current values from the server, then dynamically build
   * the settings form with appropriate input types (bool, enum, number, textarea, etc.).
   * @returns {Promise<void>}
   */
  async function loadConfig() {
    if (!el("config-form")) return;

    const [schemaRes, configRes] = await Promise.all([fetchJson(base + "/config/schema"), fetchJson(base + "/config")]);
    if (!schemaRes.ok || !configRes.ok) return showToast("Failed To Load Config", "error", 0);

    const schema = schemaRes.data.properties || [],
      rawCfg = configRes.data || {},
      config = unwrapConfig(rawCfg);

    const overridesBtn = el("vfs-overrides");

    // Disable overrides button if TMDB Ep Numbering is off.
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

    /**
     * POST the updated config object to the server.
     * @param {Object} updated - The updated config object.
     * @returns {Promise<Object>} The fetch response.
     */
    async function persistConfig(updated) {
      const res = await fetchJson(base + "/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(updated) });
      if (!res.ok) toastOperation(res, "Config Save");
      return res;
    }

    schema.forEach((p) => {
      const wrap = document.createElement("div"),
        label = document.createElement("label");
      let input,
        value = getValueByPath(config, p.Path);

      if (p.Type === "bool") {
        wrap.innerHTML = `<label class="shoko-checkbox"><input type="checkbox" ${value ? "checked" : ""}>
          <span class="shoko-checkbox-icon" aria-hidden="true"><svg class="unchecked"><use href="img/icons.svg#checkbox-blank-circle-outline"></use></svg><svg class="checked"><use href="img/icons.svg#checkbox-marked-circle-outline"></use></svg></span>
          <span class="shoko-checkbox-text"><span class="shoko-checkbox-title">${p.Display || p.Path}</span><small class="shoko-checkbox-desc" style="display:block">${p.Description || ""}</small></span></label>`;
        input = wrap.querySelector("input");
      } else if (p.Path.endsWith("PathMappings")) {
        label.innerHTML = `<span>${p.Display || p.Path.split(".").pop()}</span>${p.Description ? `<small>${p.Description}</small>` : ""}`;
        wrap.appendChild(label);
        const mappingContainer = document.createElement("div");
        mappingContainer.innerHTML = `<div class="full"><div><small>Plex Base Paths</small><textarea id="path-mappings-left" placeholder="e.g. M:\\Anime"></textarea></div><div><small>Shoko Base Paths</small><textarea id="path-mappings-right" placeholder="e.g. /anime"></textarea></div></div>`;
        wrap.appendChild(mappingContainer);
        const l = wrap.querySelector("#path-mappings-left"),
          r = wrap.querySelector("#path-mappings-right"),
          m = value || {};
        const keys = Object.keys(m).sort();
        l.value = keys.map((k) => m[k]).join("\n");
        r.value = keys.join("\n");
        input = [l, r];
      } else {
        label.innerHTML = `<span>${p.Display || p.Path.split(".").pop()}</span>${p.Description ? `<small>${p.Description}</small>` : ""}`;
        wrap.appendChild(label);
        input = document.createElement(p.Type === "enum" ? "select" : p.Type === "json" || p.Path.endsWith("TagBlacklist") ? "textarea" : "input");
        if (p.Type === "enum")
          (p.EnumValues || []).forEach((ev) => {
            const opt = new Option(ev.name, ev.value);
            opt.selected = String(ev.value) === String(value);
            input.add(opt);
          });
        else if (p.Type === "number") {
          input.type = "number";
          input.value = value ?? p.DefaultValue ?? "";
        } else if (p.Type === "json") input.value = value ? JSON.stringify(value, null, 2) : p.DefaultValue ? JSON.stringify(p.DefaultValue, null, 2) : "";
        else {
          input.type = "text";
          input.value = value ?? p.DefaultValue ?? "";
        }
        wrap.appendChild(input);
      }

      const inputs = Array.isArray(input) ? input : [input];
      inputs.forEach((i) => {
        i.onchange = async () => {
          let val = p.Type === "bool" ? i.checked : i.value;
          if (p.Path.endsWith("PathMappings")) {
            val = {};
            const lLines = inputs[0].value.split("\n"),
              rLines = inputs[1].value.split("\n");
            lLines.forEach((l, idx) => {
              if (l.trim() && rLines[idx]?.trim()) val[rLines[idx].trim()] = l.trim();
            });
          } else if (p.Type === "number") val = val === "" ? (p.DefaultValue ?? 0) : Number(val);
          else if (p.Type === "json")
            try {
              val = val === "" ? p.DefaultValue : JSON.parse(val);
            } catch {
              val = null;
            }

          setValueByPath(config, p.Path, val);
          await persistConfig(config);

          // Update overrides button state if numbering changed
          if (p.Path.endsWith("TmdbEpNumbering")) {
            const btn = el("vfs-overrides");
            if (btn) btn.disabled = !val;
          }
        };
      });

      // Place in advanced or standard container
      (p.Advanced ? advContent : el("config-form")).appendChild(wrap);
    });

    if (advContent.children.length > 1) {
      el("config-form").appendChild(advSection);
      advSection.appendChild(advContent);
      initDetailsAnimation(advSection, advContent);
    }

    // Static bindings for automation fields (outside the main preferences)
    const b = (id, path, type) => window._sr.bindConfig(id, path, config, persistConfig, type);

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
  /**
   * @returns {string} The preferred theme name ('dark' or 'light').
   */
  const getSavedTheme = () => localStorage.getItem(THEME_KEY) || (window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light");

  /**
   * Apply a theme name to the document root and update toggle state.
   * @param {string} theme - Theme name.
   */
  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);
    el("theme-toggle")?.setAttribute("aria-pressed", String(theme === "dark"));
  }

  /**
   * Initializes the theme toggle button and sets initial theme.
   */
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
  /**
   * Create the shared tooltip element and attach show/hide listeners to all elements with a title attribute.
   * Observes the DOM for dynamically added elements.
   */
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

      if (target.dataset.tooltipOverflowOnly === "true") {
        if (target.scrollWidth <= target.clientWidth) return;
      }

      content.textContent = text;

      tpl.className = "tooltip-core tooltip-box tooltip-dark tooltip-show";
      tpl.setAttribute("aria-hidden", "false");

      const rect = target.getBoundingClientRect();
      const vw = document.documentElement.clientWidth;
      const vh = document.documentElement.clientHeight;
      const margin = 10;

      let place = "top";
      let top = rect.top - tpl.offsetHeight - margin;
      const left = rect.left + rect.width / 2 - tpl.offsetWidth / 2;

      if (top < margin) {
        const spaceBelow = vh - rect.bottom;
        if (spaceBelow > tpl.offsetHeight + margin) {
          top = rect.bottom + margin;
          place = "bottom";
        }
      }

      const minLeft = margin;
      const maxLeft = vw - tpl.offsetWidth - margin;
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
      if (!t.title) return;
      t.dataset.tooltipText = t.title;
      t.removeAttribute("title");

      t.addEventListener("mouseenter", () => {
        clearTimeout(hideTimer);
        showTimer = setTimeout(() => {
          if (t.matches(":hover")) show(t);
        }, 100);
      });

      // Immediate hide on leave or click
      t.addEventListener("mouseleave", hide);
      t.addEventListener("mousedown", hide);
    };

    document.querySelectorAll("[title]").forEach(attach);
    new MutationObserver((ms) => {
      ms.forEach((m) => {
        m.addedNodes.forEach((n) => n.nodeType === 1 && (n.title ? attach(n) : n.querySelectorAll("[title]").forEach(attach)));

        // Watch for title attribute changes on existing elements (like the Player Header)
        if (m.type === "attributes" && m.attributeName === "title" && m.target.title) {
          attach(m.target);
        }
      });
    }).observe(document.body, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["title"],
    });
  }
  // #endregion

  // #region Toasts
  /**
   * Extract the error count from an API response by checking common error fields.
   * @param {{ok: boolean, data: *}} res - The fetchJson response object.
   * @returns {number} The number of errors found, or 0 if none.
   */
  function getErrorCount(res) {
    const d = res.data && res.data.data !== undefined ? res.data.data : res.data || {};
    const errs = d.Errors || d.ErrorsList;
    if (Array.isArray(errs)) return errs.length;
    return Number(errs) || 0;
  }

  /**
   * Display a dismissible toast notification in the toast container.
   * @param {string} message - HTML message content.
   * @param {"info"|"success"|"warning"|"error"} [type="info"] - Toast severity/style.
   * @param {number} [timeout=5000] - Auto-dismiss delay in ms; use 0 to keep the toast visible until manually dismissed.
   * @returns {HTMLElement|null} The created toast element, or null if the container is missing.
   */
  function showToast(message, type = "info", timeout = 5000) {
    const container = el("toast-container");
    if (!container) return null;
    const t = document.createElement("div");
    t.className = `toast ${type || "info"}`;
    t.setAttribute("role", "status");
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
  // #endregion

  // Initialize
  initTooltips();
  initTheme();
  loadConfig();
  setInterval(syncActiveTasks, 3000); // Sync Tasks
  syncActiveTasks();
})();
