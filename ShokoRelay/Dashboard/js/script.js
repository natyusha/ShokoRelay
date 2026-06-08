/**
 * @file script.js
 * @description Core shared layout utilities for the Shoko Relay client interface.
 */
(() => {
  const base = location.pathname.split(/\/(?:dashboard|player|browser)/i)[0];
  const configUrl = base + "/config";
  const el = (id) => document.getElementById(id);

  /** Default auto-dismiss duration (ms) for transient toasts. Use 0 for persistent toasts. */
  const TOAST_MS = 5000;

  /** Standardized labels for playback controls used by both MP3 and Video players. */
  const PLAYBACK_LABELS = { loop: "Loop", shuffle: "Shuffle", next: "Next", off: "Once", idle: "Play", playing: "Next" };

  // #region Helpers
  /**
   * Fetch a URL and parse the response as JSON, returning a normalized result object.
   * @param {string} url - The URL to fetch.
   * @param {RequestInit} [opts] - Optional fetch options.
   * @returns {Promise<{ok: boolean, data: *}>} Normalized response.
   */
  async function fetchJson(url, opts) {
    try {
      const res = await fetch(url, opts);
      const text = await res.text();
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
    return d?.Data !== undefined ? d.Data : d?.data !== undefined ? d.data : d;
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
      const msgs = Object.values(d.errors).flat();
      if (msgs.length > 0) return { text: msgs.join(", "), errorCount: msgs.length };
    }
    if (res.ok === false && d.title && !d.message) return { text: d.title, errorCount: 1 };

    const parts = [];
    const keys = {
      processed: ["SeriesProcessed", "Processed", "ScannedCount", "ProcessedShows"],
      created: ["LinksCreated", "Created", "UpdatedShows"],
      marked: ["Marked", "MarkedWatched", "UpdatedEpisodes"],
      skipped: ["Skipped", "SkippedCount"],
      errors: ["Errors", "ErrorsList", "Message"],
      uploaded: ["Uploaded"],
    };
    let errorCount = 0;

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
    if (timeout > 0) {
      const bar = document.createElement("div");
      bar.className = "toast-progress-bar";
      bar.style.animationDuration = `${timeout}ms`;
      t.appendChild(bar);
    }
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
   * @returns {void}
   */
  function toastOperation(res, label, opts = {}) {
    const { summary, hideOnSucceed, type } = opts;
    const { text, errorCount } = summarizeResult(res);
    const logUrl = res.data?.logUrl || res.data?.LogUrl;
    const logLink = logUrl ? `[<a href="${logUrl}" target="_blank" class="log-link">view log</a>]` : "";

    if (res.ok) {
      const display = summary || res.data?.message || res.data?.Message || (res.data?.status !== "ok" ? res.data?.status : null) || text || "Complete";
      const toastType = type || (errorCount > 0 ? "error" : "success");
      const persistence = errorCount > 0 ? 0 : (hideOnSucceed ?? (logUrl ? 0 : TOAST_MS)); // Always persist if there are errors. Otherwise, respect the hideOnSucceed override.
      showToast(`${label}: ${display} ${logLink}`, toastType, persistence);
    } else {
      const display = summary || res.data?.message || res.data?.Message || text || (typeof res.data === "string" ? res.data : JSON.stringify(res.data));
      showToast(`${label} Failed: ${display} ${logLink}`, "error", 0);
    }
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
   * @returns {void}
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

  // #region Global Object
  // Populate shared global object IMMEDIATELY so feature scripts can destructure it
  window._sr = Object.assign(window._sr || {}, {
    base,
    configUrl,
    el,
    TOAST_MS,
    fetchJson,
    showToast,
    toastOperation,
    openModal,
    updatePlaybackTooltip,
    getData,
    saveSettings,
    summarizeResult,
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
  });
  // #endregion

  // #region Feature Logic
  /**
   * Initializes the custom hover tooltip overlay and configures secure external link target behaviors.
   * @returns {void}
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
    let showTimer;

    const show = (target) => {
      const text = target.dataset.tooltipText || target.getAttribute("data-tooltip") || target.getAttribute("title");
      if (target.disabled || !text) return;
      if (target.dataset.tooltipOverflowOnly === "true" && target.scrollWidth <= target.clientWidth) return;
      content.textContent = text;
      tpl.className = "tooltip-core tooltip-box tooltip-dark tooltip-show";
      tpl.setAttribute("aria-hidden", "false");
      const disabledChild = target.tagName !== "LABEL" ? target.querySelector(":disabled, [disabled]") : null;
      const rect = disabledChild ? disabledChild.getBoundingClientRect() : target.getBoundingClientRect();
      const vw = document.documentElement.clientWidth;
      const vh = document.documentElement.clientHeight;
      const margin = 10;
      let place = "top";
      let top = rect.top - tpl.offsetHeight - margin;
      let left = rect.left + rect.width / 2 - tpl.offsetWidth / 2;
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
      tpl.classList.remove("tooltip-show");
      tpl.classList.add("tooltip-closing");
      tpl.setAttribute("aria-hidden", "true");
      setTimeout(() => tpl.classList.remove("tooltip-closing"), 150);
    };

    window.addEventListener("blur", hide);
    document.addEventListener("mouseleave", hide);

    const attach = (t) => {
      // Tooltip logic
      if (t.title) {
        t.dataset.tooltipText = t.title;
        t.removeAttribute("title");
        if (!t.dataset.tooltipAttached) {
          t.dataset.tooltipAttached = "true";
          t.addEventListener("mouseenter", () => {
            showTimer = setTimeout(() => {
              if (t.matches(":hover")) show(t);
            }, 100);
          });
          t.addEventListener("mouseleave", hide);
          t.addEventListener("mousedown", hide);
        }
      }
      // Automated link behavior: Force new tab and security headers for all links
      if (t.tagName === "A" && t.hasAttribute("href")) {
        const href = t.getAttribute("href");
        if (href && !href.startsWith("#") && !href.startsWith("javascript:")) {
          t.target = "_blank";
          t.rel = "noopener noreferrer";
        }
      }
    };

    document.querySelectorAll("[title], a[href]").forEach(attach);
    new MutationObserver((ms) => {
      ms.forEach((m) => {
        m.addedNodes.forEach((n) => {
          if (n.nodeType === 1) {
            attach(n);
            n.querySelectorAll("[title], a[href]").forEach(attach);
          }
        });
        if (m.type === "attributes" && (m.attributeName === "title" || m.attributeName === "href") && m.target.nodeType === 1) attach(m.target);
      });
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["title", "href"] });
  }
  // #endregion

  // Lifecycle Execution
  initTooltips();
})();
