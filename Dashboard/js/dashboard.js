/**
 * @file dashboard.js
 * @description Dashboard-exclusive task synchronization, lockouts, and endpoint routing.
 */
(() => {
  if (window.self === window.top) document.documentElement.style.scrollbarGutter = "stable";

  const { base, element, TOAST_MS, fetchJson, showToast, toastOperation, getData, openModal } = window._sr;

  /** @type {string[]} Array of all managed task identifiers configured on the server. */
  const MANAGED_TASK_IDS = Object.values(window._sr?.tasks || {});

  // #region Helpers
  /**
   * Toggle a button's loading state by adding/removing a spinner overlay.
   * @param {HTMLElement} btn - The button element to modify.
   * @param {boolean} isLoading - Whether to enable or disable the loading state.
   * @returns {void}
   */
  function setButtonLoading(btn, isLoading) {
    if (!btn) return;
    btn.classList.toggle("loading", isLoading);
    isLoading ? btn.setAttribute("disabled", "") : btn.removeAttribute("disabled");
    if (isLoading && !btn.querySelector(".button-spinner")) {
      const spinner = document.createElement("span");
      spinner.className = "button-spinner";
      spinner.innerHTML = '<svg class="icon-svg"><use href="img/icons.svg#loading"></use></svg>';
      btn.appendChild(spinner);
    } else if (!isLoading) btn.querySelector(".button-spinner")?.remove();
  }

  let isTaskSyncing = false;

  /**
   * Polls the server for active tasks and completed results, synchronizing the UI button states and disabling sub-tasks during automation runs.
   * @returns {Promise<void>}
   */
  async function syncActiveTasks() {
    if (isTaskSyncing) return;
    isTaskSyncing = true;
    try {
      const response = await fetchJson(base + "/tasks/active", {}, false);
      if (!response.isOk) return;
      const activeTasks = getData(response) || [];
      const isAutoRunning = activeTasks.includes(window._sr.tasks.plexAutomationRun);
      const isPlexLinked = !!window._sr.isPlexLinked;

      MANAGED_TASK_IDS.forEach((id) => {
        const btn = element(id);
        if (!btn) return;

        if (isAutoRunning && [window._sr.tasks.plexCollectionsBuild, window._sr.tasks.plexRatingsApply, window._sr.tasks.plexImagesSync].includes(id)) {
          setButtonLoading(btn, false);
          btn.disabled = true;
        } else {
          setButtonLoading(btn, activeTasks.includes(id) || btn.classList.contains("clicking"));
          if (btn.classList.contains("plex-auth") && !isPlexLinked) btn.disabled = true;
        }
      });

      const completeRes = await fetchJson(base + "/tasks/completed", {}, false);
      const completeData = getData(completeRes);
      if (completeRes.isOk && completeData) {
        let hasNewLogs = false;
        for (const [taskName, result] of Object.entries(completeData)) {
          const btn = element(taskName);
          if (btn?.classList.contains("clicking")) continue;
          const isOk = (result.status || result.Status || "").toLowerCase() === "ok";
          const filterInput = btn?.dataset.relayPersistIfEmpty ? document.querySelector(btn.dataset.relayPersistIfEmpty) : null;

          toastOperation({ isOk: isOk, data: result }, taskName.replace(/-/g, " "), { hideOnSucceed: filterInput?.value?.trim() ? TOAST_MS : 0 });
          await fetchJson(base + `/tasks/clear/${taskName}`, { method: "POST" }, false);
          hasNewLogs = true;
          if (taskName === window._sr.tasks.plexAuthRefresh) window._sr.refreshPlexState?.();
        }
        if (hasNewLogs) loadLogs();
      }
    } finally {
      isTaskSyncing = false;
    }
  }

  /**
   * Fetches the list of generated task logs from the server and renders them as clickable links.
   * @returns {Promise<void>}
   */
  async function loadLogs() {
    const logsList = element("logs-list");
    if (!logsList) return;

    const response = await fetchJson(base + "/logs/list");
    if (response.isOk) {
      const files = getData(response) || [];
      if (files.length > 0) {
        logsList.innerHTML = "";
        files.forEach((file) => {
          const listItem = document.createElement("li");
          listItem.className = "log-item";

          const link = document.createElement("a");
          link.href = `${base}/logs/${file.name}`;
          link.target = "_blank";
          link.rel = "noopener noreferrer";
          link.textContent = file.friendlyName;
          link.className = "log-link";

          const small = document.createElement("small");
          small.textContent = ` (${file.name})`;

          listItem.append(link, small);
          logsList.appendChild(listItem);
        });
      } else {
        logsList.innerHTML = '<li class="placeholder">No log reports generated yet. Click any "Generate" or automation button on the dashboard to start a task.</li>';
      }
    } else {
      logsList.innerHTML = '<li class="placeholder">Failed to load task logs list from server.</li>';
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
    btn.classList.add("clicking");
    setButtonLoading(btn, true);
    const taskId = btn.id;
    try {
      await handler(btn);
      if (taskId && MANAGED_TASK_IDS.includes(taskId)) await fetchJson(base + `/tasks/clear/${taskId}`, { method: "POST" }, false);
    } finally {
      btn.classList.remove("clicking");
      if (taskId && !MANAGED_TASK_IDS.includes(taskId)) setTimeout(() => setButtonLoading(btn, false), TOAST_MS);
      else syncActiveTasks();
      loadLogs();
    }
  }

  /**
   * Initialize a button as an aria-pressed toggle with a click handler.
   * @param {HTMLElement|string} btn - The button element or its DOM id.
   * @param {boolean} [defaultState=false] - Initial pressed state.
   * @returns {HTMLElement|null} The resolved button element.
   */
  function initToggle(btn, defaultState = false) {
    const elBtn = typeof btn === "string" ? element(btn) : btn;
    if (!elBtn) return null;
    if (!elBtn.hasAttribute("aria-pressed")) elBtn.setAttribute("aria-pressed", String(!!defaultState));
    elBtn.onclick = () => elBtn.setAttribute("aria-pressed", elBtn.getAttribute("aria-pressed") === "true" ? "false" : "true");
    return elBtn;
  }
  // #endregion

  // #region Global Exports
  Object.assign(window._sr, {
    runAction,
    initToggle,
    setButtonLoading,
    /**
     * Binds an async click handler to a button element wrapped with loading state management.
     * @param {HTMLElement|string} btn - The button element or its DOM id.
     * @param {Function} handler - The async action handler to execute on click.
     * @returns {void}
     */
    withButtonAction: (btn, handler) => {
      const elBtn = typeof btn === "string" ? element(btn) : btn;
      if (elBtn) elBtn.onclick = () => runAction(elBtn, handler);
    },
  });
  // #endregion

  // #region Global Dispatcher
  document.addEventListener("click", (event) => {
    const target = event.target.closest("[data-relay-endpoint], [data-relay-action]");
    if (!target) return;

    const action = async (btn, forceDryRun = false) => {
      let endpoint = btn.dataset.relayEndpoint;
      const actionKey = btn.dataset.relayAction;
      const label = btn.dataset.relayLabel;
      const paramFnName = btn.dataset.relayParams;
      const method = btn.dataset.relayMethod || "GET";
      const persistAttr = btn.dataset.relayPersist === "true";
      const persistIfEmptySelector = btn.dataset.relayPersistIfEmpty;

      if (endpoint) {
        if (forceDryRun) endpoint = endpoint.replace(/dryRun=false/i, "dryRun=true");
        let url = base + endpoint;
        if (paramFnName && typeof window._sr[paramFnName] === "function") {
          const params = window._sr[paramFnName]();
          url += (url.includes("?") ? "&" : "?") + params.toString();
        }
        let hideOnSucceed = persistAttr || forceDryRun ? 0 : TOAST_MS;
        if (persistIfEmptySelector) {
          const input = document.querySelector(persistIfEmptySelector);
          if (input && !input.value.trim()) hideOnSucceed = 0;
        }
        showToast(`${label}${forceDryRun ? " (Dry Run)" : ""}: Processing...`, "info", TOAST_MS);
        const response = await fetchJson(url, { method });
        toastOperation(response, label, { hideOnSucceed });
        if (btn.id === window._sr.tasks.plexAuthRefresh && response.isOk) window._sr.refreshPlexState?.();
      } else if (actionKey) {
        const handler = window._sr.actions[actionKey];
        if (handler) await handler(btn);
      }
    };

    event.preventDefault();
    const confirmMsg = target.dataset.relayConfirm;
    if (confirmMsg) {
      const modal = element("confirm-modal");
      const message = element("confirm-message");
      const execBtn = element("confirm-exec");
      const dryBtn = element("confirm-dry");
      message.innerHTML = confirmMsg;
      execBtn.textContent = target.dataset.relayConfirmButton || "Confirm";
      dryBtn.style.display = target.dataset.relayConfirmDry === "true" ? "" : "none"; // Dynamically display the Dry Run button only if explicitly supported by the action
      const close = openModal(modal);
      element("confirm-cancel").onclick = close;
      execBtn.onclick = () => {
        close();
        runAction(target, (btn) => action(btn, false));
      };
      dryBtn.onclick = () => {
        close();
        runAction(target, (btn) => action(btn, true));
      };
    } else runAction(target, action);
  });
  // #endregion

  // #region Lifecycle Execution
  (async () => {
    await window._sr.authReady;
    setInterval(syncActiveTasks, 3000);
    syncActiveTasks();
    loadLogs();
  })();
  // #endregion
})();
