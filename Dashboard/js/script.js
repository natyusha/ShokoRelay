/**
 * @file script.js
 * @description Core shared layout utilities, administrator session authentication, and fetch wrappers for the Shoko Relay client interface.
 */
(() => {
  /** @type {string} Base path for API requests determined dynamically from the location. */
  const base = location.pathname.split(/\/(?:dashboard|player|browser)/i)[0];

  /** @type {string} Root base URL for Shoko Server, safe for reverse-proxy sub-paths. */
  const shokoBase = location.origin + base.split(/\/api\//i)[0];

  /** @type {string} URL endpoint for configuration data. */
  const configUrl = base + "/config";

  /** @type {Promise<boolean>|null} Shared in-flight promise preventing duplicate auth modals. */
  let authPromptPromise = null;

  /** @type {number} Monotonically incremented each time an interactive login succeeds to prevent stale 401 retries from re-opening the modal. */
  let authGeneration = 0;

  /** @type {Object|null} Shared in-memory configuration object synchronized across all dashboard panels. */
  let sharedConfig = null;

  /**
   * Shorthand helper for document.getElementById.
   * @param {string} id - The ID of the element to retrieve.
   * @returns {HTMLElement|null} The DOM element.
   */
  const element = (id) => document.getElementById(id);

  /**
   * Escapes characters in a string to prevent HTML attribute and content syntax corruption.
   * @param {string} stringValue - The raw string to escape.
   * @returns {string} The HTML-safe escaped string.
   */
  const esc = (stringValue) => (stringValue ?? "").toString().replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");

  /** @type {number} Default auto-dismiss duration (ms) for transient toasts. Use 0 for persistent toasts. */
  const TOAST_MS = 5000;

  /** @type {Object<string, string>} Standardized labels for playback controls used by both MP3 and Video players. */
  const PLAYBACK_LABELS = { loop: "Loop", shuffle: "Shuffle", next: "Next", off: "Once", idle: "Play", playing: "Next" };

  // #region Auth Helpers
  /**
   * Enables or disables interactive dashboard controls while waiting for authentication.
   * @param {boolean} locked - True to grey out and disable dashboard controls; false to restore them.
   * @returns {void}
   */
  function setAuthLocked(locked) {
    const dashboard = element("dashboard");
    if (!dashboard) return;
    dashboard.classList.toggle("auth-locked", locked);
    dashboard.querySelectorAll("button, input, select, textarea").forEach((control) => {
      if (locked) {
        if (!control.disabled) {
          control.dataset.authLockedDisabled = "true";
          control.disabled = true;
        }
      } else if (control.dataset.authLockedDisabled === "true") {
        control.disabled = false;
        delete control.dataset.authLockedDisabled;
      }
    });
  }

  /**
   * Prompts the user for Shoko administrator credentials and creates an authenticated session cookie.
   * @param {Object} [opts] - Modal behavior options.
   * @param {boolean} [opts.dismissable=true] - Whether the modal can be dismissed via Cancel, Escape, or backdrop click.
   * @returns {Promise<boolean>} True if authentication succeeded, or false if dismissed.
   */
  function ensureAuth({ dismissable = true } = {}) {
    if (authPromptPromise) return authPromptPromise;

    authPromptPromise = new Promise((resolve) => {
      let overlay = element("auth-modal");
      if (!overlay) {
        overlay = document.createElement("div");
        overlay.id = "auth-modal";
        overlay.className = "modal-overlay";
        overlay.setAttribute("role", "dialog");
        overlay.setAttribute("aria-modal", "true");
        overlay.innerHTML = `<div class="modal" role="document">
          <header>Authentication</header>
          <div class="modal-body">
            <label for="auth-username">Username</label>
            <input id="auth-username" type="text" autocomplete="username" placeholder="Enter your Shoko username" />
            <label for="auth-password">Password</label>
            <input id="auth-password" type="password" autocomplete="current-password" placeholder="Enter your Shoko password" />
            <small>Sign in with a Shoko administrator account</small>
          </div>
          <footer>
            <button id="auth-cancel" class="cancel" type="button">Cancel</button>
            <button id="auth-submit" type="button">Authenticate</button>
          </footer>
        </div>`;
        document.body.appendChild(overlay);
      }

      const usernameInput = element("auth-username");
      const passwordInput = element("auth-password");
      const submitBtn = element("auth-submit");
      const cancelBtn = element("auth-cancel");
      usernameInput.value = "";
      passwordInput.value = "";
      cancelBtn.hidden = !dismissable;

      let close = () => {};
      /**
       * Completes the authentication prompt and resolves the pending promise.
       * @param {boolean} isOk - True if login succeeded, false if dismissed.
       * @param {boolean} [fromModalClose=false] - Whether invoked from the modal close callback.
       * @returns {void}
       */
      const finish = (isOk, fromModalClose = false) => {
        if (!authPromptPromise) return;
        authPromptPromise = null;
        if (isOk) authGeneration++;
        if (!fromModalClose) close();
        resolve(isOk);
      };
      close = openModal(overlay, () => finish(false, true), dismissable);
      setTimeout(() => usernameInput.focus(), 15);

      cancelBtn.onclick = () => dismissable && finish(false);
      submitBtn.onclick = async () => {
        const username = usernameInput.value.trim();
        const password = passwordInput.value;
        if (!username) {
          usernameInput.focus();
          return;
        }
        submitBtn.disabled = true;
        try {
          const check = await fetch(`${base}/auth/login`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ username, password }),
          });
          if (check.ok) {
            finish(true);
          } else {
            const errMsg = check.status === 403 ? "Administrator privileges required." : "Invalid username or password.";
            showToast(errMsg, "error", TOAST_MS);
            passwordInput.focus();
          }
        } catch {
          showToast("Failed to authenticate.", "error", TOAST_MS);
        } finally {
          submitBtn.disabled = false;
        }
      };

      const onKeyDown = (event) => {
        if (event.key === "Enter") submitBtn.click();
      };
      usernameInput.onkeydown = onKeyDown;
      passwordInput.onkeydown = onKeyDown;
    });

    return authPromptPromise;
  }

  /**
   * Verifies initial page authentication state, locking controls and prompting non-dismissably if required.
   * @returns {Promise<boolean>} True once the session is authorized or auth is not required.
   */
  async function checkInitialAuth() {
    const isPlayer = !!element("player");
    if (!isPlayer) setAuthLocked(true);
    try {
      const response = await fetch(`${base}/auth/verify${isPlayer ? "?player=true" : ""}`);
      if (response.ok) {
        const data = await response.json().catch(() => ({}));
        window._sr.authState = {
          authenticated: !!data.authenticated,
          required: !!data.required,
          allowPlayerAccess: !!data.allowPlayerAccess,
        };
        if (!isPlayer) setAuthLocked(false);
        return true;
      }
      if (response.status === 401 || response.status === 403) {
        const isOk = await ensureAuth({ dismissable: false });
        if (isOk) {
          window._sr.authState = { authenticated: true, required: true, allowPlayerAccess: false };
          if (!isPlayer) setAuthLocked(false);
          return true;
        }
      }
    } catch {}
    if (!isPlayer) setAuthLocked(false);
    return true;
  }
  // #endregion

  // #region Network Helpers
  /**
   * Fetch a URL and parse the response as JSON, returning a normalized result object.
   * @param {string} url - The URL to fetch.
   * @param {RequestInit} [opts] - Optional fetch options.
   * @param {boolean} [allowPrompt=true] - Whether to prompt for authentication on 401/403 responses.
   * @returns {Promise<{isOk: boolean, status: number, data: *}>} The normalized fetch response object.
   */
  async function fetchJson(url, opts = {}, allowPrompt = true) {
    try {
      const requestGen = authGeneration;
      const response = await fetch(url, opts);
      if ((response.status === 401 || response.status === 403) && allowPrompt) {
        if (authGeneration !== requestGen) return fetchJson(url, opts, false);
        setAuthLocked(true);
        const isOk = await ensureAuth({ dismissable: false });
        if (isOk) {
          setAuthLocked(false);
          return fetchJson(url, opts, false);
        }
      }
      const text = await response.text();
      try {
        return { isOk: response.ok, status: response.status, data: JSON.parse(text) };
      } catch {
        return { isOk: response.ok, status: response.status, data: text };
      }
    } catch (error) {
      console.error("fetchJson error for", url, error);
      return { isOk: false, status: 0, data: { error: String(error), message: error?.message ?? String(error), url } };
    }
  }

  /**
   * Extracts the inner data object from a standardized server response envelope.
   * @param {Object} response - The normalized fetch response object.
   * @returns {*} The actual result data.
   */
  function getData(response) {
    return response?.data?.Data ?? response?.data?.data ?? response?.data;
  }
  // #endregion

  // #region Helpers
  /**
   * Build a human-readable summary string and error count from common API response fields.
   * @param {Object} response - The normalized fetch response object.
   * @returns {{text: string, errorCount: number}} Summarized details.
   */
  function summarizeResult(response) {
    const resData = getData(response);
    if (!resData) return { text: "", errorCount: 0 };
    if (resData.errors && typeof resData.errors === "object" && !Array.isArray(resData.errors)) {
      const msgs = Object.values(resData.errors).flat();
      if (msgs.length > 0) return { text: msgs.join(", "), errorCount: msgs.length };
    }
    if (response.isOk === false && resData.title && !resData.message) return { text: resData.title, errorCount: 1 };

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
      const match = aliases.find((key) => resData[key] !== undefined && resData[key] !== null);
      if (match === undefined) return;
      const value = resData[match];
      const count = label === "errors" ? (Array.isArray(value) ? value.length : Number(value) || 0) : value;
      if (label === "errors") errorCount = count;
      if (label !== "errors" || count > 0) parts.push(`${label}: ${count}`);
    });
    const text = parts.length ? parts.join(", ") : typeof resData === "string" ? (resData.length > 200 ? resData.substring(0, 200) + "..." : resData) : "";
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
    const container = element("toast-container");
    if (!container) return null;
    const toast = document.createElement("div");
    toast.className = `toast ${type || "info"}`;
    toast.tabIndex = 0;
    toast.innerHTML = `<span class="toast-message">${message}</span>`;
    if (timeout > 0) {
      const bar = document.createElement("div");
      bar.className = "toast-progress-bar";
      bar.style.animationDuration = `${timeout}ms`;
      toast.appendChild(bar);
    }
    container.appendChild(toast);
    requestAnimationFrame(() => toast.classList.add("visible"));
    const dismiss = () => {
      toast.classList.remove("visible");
      setTimeout(() => toast.remove(), 300);
    };
    let timer = timeout > 0 ? setTimeout(dismiss, timeout) : null;
    toast.onclick = () => {
      clearTimeout(timer);
      dismiss();
    };
    return toast;
  }

  /**
   * Show success/error toasts for HTTP responses with log-link injection and summary.
   * @param {Object} response - The normalized fetch response object.
   * @param {string} label - Identifies the operation.
   * @param {Object} [opts] - Display options.
   * @returns {void}
   */
  function toastOperation(response, label, opts = {}) {
    const { summary, hideOnSucceed, type } = opts;
    const { text, errorCount } = summarizeResult(response);
    const logUrl = response.data?.logUrl || response.data?.LogUrl;
    const logLink = logUrl ? `[<a href="${esc(logUrl)}" target="_blank" rel="noopener noreferrer" class="log-link">view log</a>]` : "";

    if (response.isOk) {
      const display = summary || response.data?.message || response.data?.Message || (response.data?.status !== "ok" ? response.data?.status : null) || text || "Complete";
      const toastType = type || (errorCount > 0 ? "error" : "success");
      const persistence = errorCount > 0 ? 0 : (hideOnSucceed ?? (logUrl ? 0 : TOAST_MS)); // Always persist if there are errors. Otherwise, respect the hideOnSucceed override.
      showToast(`${esc(label)}: ${esc(display)} ${logLink}`, toastType, persistence);
    } else {
      const display = summary || response.data?.message || response.data?.Message || text || (typeof response.data === "string" ? response.data : JSON.stringify(response.data));
      showToast(`${esc(label)} Failed: ${esc(display)} ${logLink}`, "error", 0);
    }
  }

  /**
   * Opens a modal and attaches automated close handlers.
   * @param {HTMLElement} modal - The modal element.
   * @param {Function|null} [onClose=null] - Optional callback invoked when the modal closes.
   * @param {boolean} [dismissable=true] - Whether clicking outside or pressing Escape can close the modal.
   * @returns {Function} Close function.
   */
  function openModal(modal, onClose = null, dismissable = true) {
    if (!modal) return () => {};
    modal.setAttribute("aria-hidden", "false");
    modal.classList.add("open");
    document.body.style.overflow = "hidden";
    const close = () => {
      modal.setAttribute("aria-hidden", "true");
      modal.classList.remove("open");
      document.body.style.overflow = "";
      if (dismissable) {
        modal.removeEventListener("click", onOverlay);
        document.removeEventListener("keydown", onKey);
      }
      onClose?.();
    };
    const onOverlay = (event) => event.target === modal && close();
    const onKey = (event) => event.key === "Escape" && close();
    if (dismissable) {
      modal.addEventListener("click", onOverlay);
      document.addEventListener("keydown", onKey);
    }
    const firstInput = modal.querySelector("button:not([hidden]), textarea, input:not([type='hidden'])");
    setTimeout(() => firstInput?.focus(), 10);
    return close;
  }

  /**
   * Updates element title from data-mode or data-state for tooltips.
   * @param {HTMLElement} targetElement - The target element.
   * @returns {void}
   */
  function updatePlaybackTooltip(targetElement) {
    if (!targetElement) return;
    const key = targetElement.getAttribute("data-mode") || targetElement.getAttribute("data-state");
    const label = PLAYBACK_LABELS[key];
    if (label) {
      if (targetElement.dataset.tooltipText !== undefined) targetElement.dataset.tooltipText = label;
      else targetElement.title = label;
    }
  }

  /** @type {Promise<*>} Tracks the latest in-flight settings persistence promise. */
  let pendingSettingsSave = Promise.resolve();

  /**
   * Persists the plugin configuration to the server.
   * @param {Object} config - The configuration object to save.
   * @returns {Promise<Object>} The server response.
   */
  function saveSettings(config) {
    const run = pendingSettingsSave.then(async () => {
      const cleanCfg = JSON.parse(JSON.stringify(config));
      delete cleanCfg.PlexLibrary;
      delete cleanCfg.PlexAuth;
      const response = await fetchJson(configUrl, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(cleanCfg),
      });
      if (!response.isOk) toastOperation(response, "Settings Save");
      return response;
    });
    pendingSettingsSave = run.catch(() => {});
    return run;
  }

  /**
   * Waits for any pending configuration saves to complete before running dependent actions.
   * @returns {Promise<void>}
   */
  async function flushSaves() {
    await pendingSettingsSave;
  }
  // #endregion

  // #region Global Object
  const authReady = checkInitialAuth();

  // Populate shared global object IMMEDIATELY so feature scripts can destructure it
  window._sr = Object.assign(window._sr || {}, {
    base,
    shokoBase,
    configUrl,
    authReady,
    element,
    esc,
    TOAST_MS,
    ensureAuth,
    fetchJson,
    showToast,
    toastOperation,
    openModal,
    updatePlaybackTooltip,
    getData,
    saveSettings,
    flushSaves,
    summarizeResult,
    actions: {},
    /**
     * Merges a freshly fetched configuration payload into the shared in-memory configuration instance.
     * @param {Object} fresh - The unwrapped configuration object retrieved from the server.
     * @returns {Object} The shared configuration object reference.
     */
    syncConfig: (fresh) => {
      if (!sharedConfig) sharedConfig = fresh || {};
      else if (fresh) Object.assign(sharedConfig, fresh);
      window.relaySettings = sharedConfig;
      return sharedConfig;
    },
    /**
     * Unwraps a configuration payload envelope if present.
     * @param {Object} data - The raw response payload.
     * @returns {Object} The unwrapped configuration object.
     */
    unwrapConfig: (data) => (data?.payload !== undefined ? data.payload || {} : data || {}),
    /**
     * Retrieves a nested property value using a dot-delimited path.
     * @param {Object} objectValue - The source object.
     * @param {string} path - The dot-delimited property path.
     * @returns {*} The resolved property value, or undefined.
     */
    getValueByPath: (objectValue, path) => path.split(".").reduce((curr, key) => curr?.[key], objectValue),
    /**
     * Sets a nested property value using a dot-delimited path, creating intermediate objects as needed.
     * @param {Object} objectValue - The target object.
     * @param {string} path - The dot-delimited property path.
     * @param {*} value - The value to assign.
     * @returns {void}
     */
    setValueByPath: (objectValue, path, value) => {
      const parts = path.split(".");
      const last = parts.pop();
      const target = parts.reduce((curr, key) => ((curr[key] ??= {}), curr[key]), objectValue);
      target[last] = value;
    },
    /**
     * Binds an HTML form control to a nested configuration path and persists changes.
     * @param {HTMLElement|string} target - The DOM element or its ID.
     * @param {string} path - The dot-delimited configuration path.
     * @param {Object} config - The shared configuration object.
     * @param {Function} persistFn - Async function called to persist changes.
     * @param {"text"|"number"|"check"} [type="text"] - Input value parsing mode.
     * @returns {void}
     */
    bindConfig: (target, path, config, persistFn, type = "text") => {
      const targetElement = typeof target === "string" ? element(target) : target;
      if (!targetElement) return;
      const value = window._sr.getValueByPath(config, path);
      if (type === "check") targetElement.checked = !!value;
      else targetElement.value = value ?? "";
      targetElement.onchange = async () => {
        let newValue = type === "check" ? targetElement.checked : type === "number" ? Number(targetElement.value) || 0 : targetElement.value;
        window._sr.setValueByPath(config, path, newValue);
        await persistFn(config);
      };
    },
    /**
     * Appends a parameter to URLSearchParams when its value is non-null and non-empty.
     * @param {URLSearchParams} params - The target search parameters collection.
     * @param {string} key - The parameter name.
     * @param {*} value - The parameter value.
     * @returns {void}
     */
    setIfNotEmpty: (params, key, value) => {
      if (value != null && String(value) !== "") params.set(key, String(value));
    },
  });
  // #endregion

  // #region Feature Logic
  /**
   * Initializes the custom hover tooltip overlay and configures secure external link target behaviors.
   * @returns {void}
   */
  function initTooltips() {
    if (element("shoko-tooltip")) return;
    const tooltipElement = document.createElement("div");
    tooltipElement.id = "shoko-tooltip";
    tooltipElement.className = "tooltip-core tooltip-box tooltip-dark tooltip-place-top";
    tooltipElement.setAttribute("role", "status");
    tooltipElement.setAttribute("aria-hidden", "true");
    tooltipElement.innerHTML = '<div class="tooltip-arrow"></div><div class="rt-content"></div>';
    document.body.appendChild(tooltipElement);
    const content = tooltipElement.querySelector(".rt-content");
    let showTimer;

    /**
     * Displays the hover tooltip positioned relative to the target element.
     * @param {HTMLElement} target - The hovered element providing tooltip text.
     * @returns {void}
     */
    const show = (target) => {
      const text = target.dataset.tooltipText || target.getAttribute("data-tooltip") || target.getAttribute("title");
      if (target.disabled || !text) return;
      if (target.dataset.tooltipOverflowOnly === "true" && target.scrollWidth <= target.clientWidth) return;
      content.textContent = text;
      tooltipElement.className = "tooltip-core tooltip-box tooltip-dark tooltip-show";
      tooltipElement.setAttribute("aria-hidden", "false");
      const disabledChild = target.tagName !== "LABEL" ? target.querySelector(":disabled, [disabled]") : null;
      const bounds = disabledChild ? disabledChild.getBoundingClientRect() : target.getBoundingClientRect();
      const viewportWidth = document.documentElement.clientWidth;
      const viewportHeight = document.documentElement.clientHeight;
      const margin = 10;
      let place = "top";
      let top = bounds.top - tooltipElement.offsetHeight - margin;
      let left = bounds.left + bounds.width / 2 - tooltipElement.offsetWidth / 2;
      if (top < margin) {
        const spaceBelow = viewportHeight - bounds.bottom;
        if (spaceBelow > tooltipElement.offsetHeight + margin) {
          top = bounds.bottom + margin;
          place = "bottom";
        }
      }
      const minLeft = margin;
      const maxLeft = viewportWidth - tooltipElement.offsetWidth - margin;
      const clampedLeft = Math.max(minLeft, Math.min(left, maxLeft));
      const arrow = tooltipElement.querySelector(".tooltip-arrow");
      if (arrow) arrow.style.marginLeft = `${left - clampedLeft}px`;
      tooltipElement.classList.add(`tooltip-place-${place}`);
      tooltipElement.style.left = `${Math.round(clampedLeft + window.scrollX)}px`;
      tooltipElement.style.top = `${Math.round(top + window.scrollY)}px`;
      target.setAttribute("aria-describedby", "shoko-tooltip");
    };

    /**
     * Hides the hover tooltip and cancels any pending display timer.
     * @returns {void}
     */
    const hide = () => {
      clearTimeout(showTimer);
      tooltipElement.classList.remove("tooltip-show");
      tooltipElement.classList.add("tooltip-closing");
      tooltipElement.setAttribute("aria-hidden", "true");
      setTimeout(() => tooltipElement.classList.remove("tooltip-closing"), 150);
    };

    window.addEventListener("blur", hide);
    document.addEventListener("mouseleave", hide);

    /**
     * Attaches custom tooltip handlers and external-link security attributes to an element.
     * @param {HTMLElement} target - The element to inspect and bind.
     * @returns {void}
     */
    const attach = (target) => {
      // Tooltip logic
      if (target.title) {
        target.dataset.tooltipText = target.title;
        target.removeAttribute("title");
        if (!target.dataset.tooltipAttached) {
          target.dataset.tooltipAttached = "true";
          target.addEventListener("mouseenter", () => {
            showTimer = setTimeout(() => {
              if (target.matches(":hover")) show(target);
            }, 100);
          });
          target.addEventListener("mouseleave", hide);
          target.addEventListener("mousedown", hide);
        }
      }
      // Automated link behavior: Force new tab and security headers for all links
      if (target.tagName === "A" && target.hasAttribute("href")) {
        const href = target.getAttribute("href");
        if (href && !href.startsWith("#") && !href.startsWith("javascript:")) {
          target.target = "_blank";
          target.rel = "noopener noreferrer";
        }
      }
    };

    document.querySelectorAll("[title], a[href]").forEach(attach);
    new MutationObserver((mutations) => {
      mutations.forEach((mutation) => {
        mutation.addedNodes.forEach((node) => {
          if (node.nodeType === 1) {
            attach(node);
            node.querySelectorAll("[title], a[href]").forEach(attach);
          }
        });
        if (mutation.type === "attributes" && (mutation.attributeName === "title" || mutation.attributeName === "href") && mutation.target.nodeType === 1) attach(mutation.target);
      });
    }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["title", "href"] });
  }
  // #endregion

  // #region Lifecycle Execution
  initTooltips();
  // #endregion
})();
