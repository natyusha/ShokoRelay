/**
 * @file script.js
 * @description Main dashboard script for UI interactions, server fetching, and shared utilities like toasts/modals.
 */
(() => {
  const base = location.pathname.split("/dashboard")[0];
  const configUrl = base + "/config";
  const el = (id) => document.getElementById(id);

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

  /** List of values that correspond to server task names for spinner synchronization. Fallback to empty if tasks missing. */
  const MANAGED_TASK_IDS = Object.values(window._sr.tasks || {});

  // #region Helpers
  /**
   * Fetch a URL and parse the response as JSON, returning a normalized result object.
   * @param {string} url - The URL to fetch.
   * @param {RequestInit} [opts] - Optional fetch options.
   * @returns {Promise<{ok: boolean, data: *}>} Normalized response.
   */
  async function fetchJson(url, opts) {
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
  }

  /**
   * Extracts the inner data object from a standardized server response envelope.
   * @param {Object} res - The normalized fetch response.
   * @returns {*} The actual result data.
   */
  function getData(res) {
    const d = res?.data;
    if (!d) return null;
    if (d.Data !== undefined && d.Data !== null) return d.Data;
    if (d.data !== undefined && d.data !== null) return d.data;
    return d;
  }

  /**
   * Build a human-readable summary string and error count from common API response fields.
   * @param {Object} res - The response object.
   * @returns {{text: string, errorCount: number}} Summarized details.
   */
  function summarizeResult(res) {
    const d = getData(res);
    if (!d) return { text: "", errorCount: 0 };

    if (d.errors && typeof d.errors === "object" && !Array.isArray(d.errors)) {
      const messages = Object.values(d.errors).flat();
      if (messages.length > 0) return { text: messages.join(", "), errorCount: messages.length };
    }

    if (res.ok === false && d.title && !d.message) return { text: d.title, errorCount: 1 };

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

  /**
   * Show success/error toasts for HTTP responses with log-link injection and summary.
   * @param {Object} res - The fetchJson response object.
   * @param {string} label - Identifies the operation.
   * @param {Object} [opts] - Display options.
   */
  function toastOperation(res, label, opts = {}) {
    const { summary, hideOnSucceed, type } = opts;
    const { text, errorCount } = summarizeResult(res);
    const logUrl = res.data?.logUrl || res.data?.LogUrl;
    const logLink = logUrl ? `[<a href="${logUrl}" target="_blank" class="log-link">view log</a>]` : "";

    const envMsg = res.data?.message || res.data?.Message,
      envStatus = res.data?.status || res.data?.Status;

    if (res.ok) {
      const display = summary || envMsg || (envStatus !== "ok" ? envStatus : null) || text || "Complete";
      const toastType = type || (errorCount > 0 ? "error" : "success");
      const persistence = logUrl || errorCount > 0 ? 0 : (hideOnSucceed ?? TOAST_MS); // Log-enabled tasks or tasks with errors are persistent (timeout=0).
      showToast(`${label}: ${display} ${logLink}`, toastType, persistence);
    } else {
      const display = summary || envMsg || text || (typeof res.data === "string" ? res.data : JSON.stringify(res.data));
      showToast(`${label} Failed: ${display} ${logLink}`, "error", 0);
    }
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

  /** Polls the server for active tasks and completed results, synchronizing the UI state. */
  async function syncActiveTasks() {
    const res = await fetchJson(window._sr.base + "/tasks/active");
    if (!res.ok) return;
    const activeTasks = res.data || [];
    MANAGED_TASK_IDS.forEach((id) => {
      const btn = el(id);
      if (!btn) return;
      const isActive = activeTasks.includes(id);
      if (!isActive && !btn.classList.contains("clicking")) setButtonLoading(btn, false);
      else if (isActive) setButtonLoading(btn, true);
    });

    const completeRes = await fetchJson(window._sr.base + "/tasks/completed");
    if (completeRes.ok && completeRes.data) {
      for (const [taskName, result] of Object.entries(completeRes.data)) {
        const btn = el(taskName);
        if (btn?.classList.contains("clicking")) continue;
        const label = taskName.replace(/-/g, " ");
        const isOk = (result.status || result.Status || "").toLowerCase() === "ok"; // Ensure the ok status is correctly identified regardless of property casing

        toastOperation({ ok: isOk, data: result }, label, { hideOnSucceed: 0 }); // Polled task completions with logs are always persistent
        await fetch(window._sr.base + `/tasks/clear/${taskName}`, { method: "POST" });
      }
    }
  }

  /**
   * Core logic to wrap an async action with loading states and task management.
   * @param {HTMLElement} btn - The button element trigger.
   * @param {Function} handler - The async function to execute.
   * @returns {Promise<void>}
   */
  async function runAction(btn, handler) {
    if (!btn || btn.classList.contains("clicking")) return;
    const MANAGED_TASK_IDS = Object.values(window._sr.tasks || {});
    btn.classList.add("clicking");
    setButtonLoading(btn, true);
    const taskId = btn.id;
    try {
      await handler(btn);
      if (taskId && MANAGED_TASK_IDS.includes(taskId)) {
        await fetch(window._sr.base + `/tasks/clear/${taskId}`, { method: "POST" });
      }
    } finally {
      btn.classList.remove("clicking");
      if (taskId && !MANAGED_TASK_IDS.includes(taskId)) {
        setTimeout(() => setButtonLoading(btn, false), TOAST_MS);
      } else {
        syncActiveTasks();
      }
    }
  }

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
  function updatePlaybackTooltip(el) {
    if (!el) return;
    const key = el.getAttribute("data-mode") || el.getAttribute("data-state");
    const label = PLAYBACK_LABELS[key];
    if (label) {
      if (el.dataset.tooltipText !== undefined) el.dataset.tooltipText = label;
      else el.title = label;
    }
  }

  /**
   * Persists the plugin configuration to the server.
   * @param {Object} config - The configuration object to save.
   * @returns {Promise<Object>} The server response.
   */
  async function saveSettings(config) {
    const cleanCfg = JSON.parse(JSON.stringify(config));
    delete cleanCfg.PlexLibrary;
    delete cleanCfg.PlexAuth;
    const res = await fetchJson(configUrl, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(cleanCfg),
    });
    if (!res.ok) toastOperation(res, "Settings Save");
    return res;
  }
  // #endregion

  // #region Glogal Object
  // Populate shared global object IMMEDIATELY so feature scripts can destructure it
  window._sr = Object.assign(window._sr || {}, {
    base,
    configUrl,
    el,
    TOAST_MS,
    fetchJson,
    showToast,
    toastOperation,
    summarizeResult,
    runAction,
    initToggle,
    openModal,
    setButtonLoading,
    updatePlaybackTooltip,
    syncActiveTasks,
    saveSettings,
    getData,
    actions: {},
    unwrapConfig: (data) => (data?.payload !== undefined ? data.payload || {} : data || {}),
    getValueByPath: (obj, path) => path.split(".").reduce((o, k) => o?.[k], obj),
    setValueByPath: (obj, path, value) => {
      const parts = path.split("."),
        last = parts.pop();
      const target = parts.reduce((o, k) => ((o[k] ??= {}), o[k]), obj);
      target[last] = value;
    },
    bindConfig: (target, path, config, persistFn, type = "text") => {
      const e = typeof target === "string" ? el(target) : target;
      if (!e) return;
      const val = window._sr.getValueByPath(config, path);
      if (type === "check") e.checked = !!val;
      else e.value = val ?? "";
      e.onchange = async () => {
        let newVal = type === "check" ? e.checked : type === "number" ? Number(e.value) || 0 : e.value;
        window._sr.setValueByPath(config, path, newVal);
        await persistFn(config);
      };
    },
    setIfNotEmpty: (ps, k, v) => {
      if (v != null && String(v) !== "") ps.set(k, String(v));
    },
    withButtonAction: (btn, handler) => {
      const elBtn = typeof btn === "string" ? el(btn) : btn;
      if (elBtn) elBtn.onclick = () => runAction(elBtn, handler);
    },
  });
  // #endregion

  // #region Feature Logic
  /** Initializes the dashboard color theme from storage. */
  function initTheme() {
    const THEME_KEY = "dashboard-theme";
    const getSavedTheme = () => localStorage.getItem(THEME_KEY) || (window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    const applyTheme = (theme) => {
      document.documentElement.setAttribute("data-theme", theme);
      el("theme-toggle")?.setAttribute("aria-pressed", String(theme === "dark"));
    };
    applyTheme(getSavedTheme());
    const toggle = el("theme-toggle");
    if (toggle)
      toggle.onclick = () => {
        const next = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
        localStorage.setItem(THEME_KEY, next);
        applyTheme(next);
      };
  }

  /** Initializes the custom tooltip system for elements with titles. */
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
        top = rect.top - tpl.offsetHeight - margin,
        left = rect.left + rect.width / 2 - tpl.offsetWidth / 2;
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
      if (arrow) arrow.style.marginLeft = `${left - clampedLeft}px`;
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
      if (t.dataset.tooltipAttached) return;
      t.dataset.tooltipAttached = "true";
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

  // #region Global Dispatcher
  document.addEventListener("click", (e) => {
    const target = e.target.closest("[data-relay-endpoint], [data-relay-action]");
    if (!target) return;

    const action = async (btn) => {
      const endpoint = btn.dataset.relayEndpoint,
        actionKey = btn.dataset.relayAction;
      const label = btn.dataset.relayLabel,
        paramFnName = btn.dataset.relayParams;
      const method = btn.dataset.relayMethod || "GET",
        persistAttr = btn.dataset.relayPersist === "true";
      const persistIfEmptySelector = btn.dataset.relayPersistIfEmpty;

      if (endpoint) {
        let url = base + endpoint;
        if (paramFnName && typeof window._sr[paramFnName] === "function") {
          const ps = window._sr[paramFnName]();
          url += (url.includes("?") ? "&" : "?") + ps.toString();
        }
        let hideOnSucceed = persistAttr ? 0 : TOAST_MS;
        if (persistIfEmptySelector) {
          const input = document.querySelector(persistIfEmptySelector);
          if (input && !input.value.trim()) hideOnSucceed = 0;
        }
        showToast(`${label}: Processing...`, "info", TOAST_MS);
        const res = await fetchJson(url, { method });
        toastOperation(res, label, { hideOnSucceed });
      } else if (actionKey) {
        const handler = window._sr.actions[actionKey];
        if (handler) await handler(btn);
      }
    };

    e.preventDefault();
    const confirmMsg = target.dataset.relayConfirm;
    if (confirmMsg) {
      const modal = el("confirm-modal"),
        msg = el("confirm-message"),
        execBtn = el("confirm-exec");
      msg.innerHTML = confirmMsg;
      execBtn.textContent = target.dataset.relayConfirmButton || "Confirm";
      const close = openModal(modal);
      el("confirm-cancel").onclick = close;
      execBtn.onclick = () => {
        close();
        runAction(target, action);
      };
    } else {
      runAction(target, action);
    }
  });
  // #endregion

  // Lifecycle Execution
  initTooltips();
  initTheme();
  setInterval(syncActiveTasks, 3000);
  syncActiveTasks();
})();
