/**
 * @file script.js
 * @description Main dashboard script for UI interactions, server fetching, and shared utilities like toasts/modals.
 */
(() => {
  const base = location.pathname.split("/dashboard")[0];
  const el = (id) => document.getElementById(id);

  // Initialize global namespace immediately so subsequent scripts can see it
  window._sr = { base };

  /** Default auto-dismiss duration (ms) for transient toasts. Use 0 for persistent toasts. */
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
   * Helper to append a key-value pair to a URLSearchParams object if the value is non-empty.
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
        const btn = el(taskName);
        // If the button for this task is currently active, the button handler will manage the toast/clearing.
        if (btn && btn.classList.contains("clicking")) continue;

        const label = taskName.replace(/-/g, " ");
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
        // Explicitly clear the task immediately if it's a managed ID to prevent the poller from double-toasting
        if (MANAGED_TASK_IDS.includes(elBtn.id)) {
          await fetch(window._sr.base + `/tasks/clear/${elBtn.id}`, { method: "POST" });
        }
      } finally {
        elBtn.classList.remove("clicking");

        if (!MANAGED_TASK_IDS.includes(elBtn.id)) {
          setTimeout(() => setButtonLoading(elBtn, false), TOAST_MS); // Instant tasks: Keep the spinner active for TOAST_MS to prevent accidental multi runs
        } else {
          syncActiveTasks();
        }
      }
    };
  }

  /**
   * Wraps a log URL in an anchor tag if provided.
   * @param {string} url - The log URL to wrap.
   * @returns {string} An HTML anchor link or empty string.
   */
  const makeLogLink = (url) => (url ? `[<a href="${url}" target="_blank" class="log-link">view log</a>]` : "");

  /**
   * Show success/error toasts for HTTP responses with log-link injection and summary.
   * @param {{ok: boolean, data: *}} res - The fetchJson response object.
   * @param {string} label - Identifies the operation.
   * @param {{summary?: string, hideOnSucceed?: number, type?: string}} [opts] - Display options.
   */
  function toastOperation(res, label, opts = {}) {
    const { summary, hideOnSucceed, type } = opts;
    const { text, errorCount } = summarizeResult(res);
    const logLink = makeLogLink(res.data?.logUrl);

    if (res.ok) {
      const display = summary || text || `${label} Complete`;
      const toastType = type || (errorCount > 0 ? "error" : "success");
      showToast(`${label}: ${display} ${logLink}`, toastType, errorCount > 0 ? 0 : (hideOnSucceed ?? TOAST_MS));
    } else {
      // Prioritize the processed 'text' from summarizeResult to avoid raw JSON blobs
      const display = summary || text || res.data?.message || (typeof res.data === "string" ? res.data : JSON.stringify(res.data));
      showToast(`${label} Failed: ${display} ${logLink}`, "error", 0);
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

    // Handle ASP.NET Validation Problem Details
    if (d.errors && typeof d.errors === "object" && !Array.isArray(d.errors)) {
      const messages = Object.values(d.errors).flat();
      if (messages.length > 0) return { text: messages.join(", "), errorCount: messages.length };
    }

    // Fallback for generic error objects with a title but no specific error array
    if (res.ok === false && d.title && !d.message) {
      return { text: d.title, errorCount: 1 };
    }

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
   * @param {RequestInit} [opts] - Optional fetch options.
   * @returns {Promise<{ok: boolean, data: *}>} Normalized response.
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
   * Initialize a button as an aria-pressed toggle with a click handler.
   * @param {HTMLElement|string} btn - The button element or its DOM id.
   * @param {boolean} [defaultState=false] - Initial pressed state.
   * @returns {HTMLElement|null} The resolved button element.
   */
  function initToggle(btn, defaultState = false) {
    const elBtn = typeof btn === "string" ? el(btn) : btn;
    if (!elBtn) return null;
    if (!elBtn.hasAttribute("aria-pressed")) elBtn.setAttribute("aria-pressed", String(!!defaultState));
    elBtn.onclick = () => elBtn.setAttribute("aria-pressed", elBtn.getAttribute("aria-pressed") === "true" ? "false" : "true");
    return elBtn;
  }

  /**
   * Toggle a button's loading state by adding/removing a spinner overlay.
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
   * Resolves a value from an object using a dot-separated key path.
   * @param {Object} obj - The source object.
   * @param {string} path - Dot-separated key path.
   * @returns {*} The resolved value.
   */
  const getValueByPath = (obj, path) => path.split(".").reduce((o, k) => o?.[k], obj);

  /**
   * Sets a value in an object at the specified dot-separated path.
   * @param {Object} obj - The target object.
   * @param {string} path - Dot-separated key path.
   * @param {*} value - The value to set.
   */
  function setValueByPath(obj, path, value) {
    const parts = path.split("."),
      last = parts.pop();
    const target = parts.reduce((o, k) => ((o[k] ??= {}), o[k]), obj);
    target[last] = value;
  }

  /**
   * Unwrap a config response that may contain a payload wrapper.
   * @param {Object} data - The raw config response.
   * @returns {Object} The inner config object.
   */
  const unwrapConfig = (data) => (data?.payload !== undefined ? data.payload || {} : data || {});

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

  /**
   * Opens a modal and attaches automated close handlers.
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

  /**
   * Updates element title from data-mode or data-state for tooltips.
   * @param {HTMLElement} el - The target element.
   */
  const updatePlaybackTooltip = (el) => {
    if (!el) return;
    const key = el.getAttribute("data-mode") || el.getAttribute("data-state");
    if (PLAYBACK_LABELS[key]) el.title = PLAYBACK_LABELS[key];
  };

  /**
   * Displays a toast notification.
   * @param {string} message - HTML message content.
   * @param {"info"|"success"|"warning"|"error"} [type="info"] - Toast severity.
   * @param {number} [timeout=5000] - Auto-dismiss delay in ms.
   * @returns {HTMLElement|null} The toast element.
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
    unwrapConfig,
    bindConfig,
    openModal,
    setButtonLoading,
    updatePlaybackTooltip,
    syncActiveTasks,
  });
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
  setInterval(syncActiveTasks, 3000);
  syncActiveTasks();
})();
