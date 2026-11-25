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
   * @param {HTMLElement} button - The button element to modify.
   * @param {boolean} isLoading - Whether to enable or disable the loading state.
   * @returns {void}
   */
  function setButtonLoading(button, isLoading) {
    if (!button) return;
    button.classList.toggle("loading", isLoading);
    isLoading ? button.setAttribute("disabled", "") : button.removeAttribute("disabled");
    if (isLoading && !button.querySelector(".button-spinner")) {
      const spinner = document.createElement("span");
      spinner.className = "button-spinner";
      spinner.innerHTML = '<svg class="icon-svg"><use href="img/icons.svg#loading"></use></svg>';
      button.appendChild(spinner);
    } else if (!isLoading) button.querySelector(".button-spinner")?.remove();
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

      MANAGED_TASK_IDS.forEach((taskId) => {
        const button = element(taskId);
        if (!button) return;

        if (isAutoRunning && [window._sr.tasks.plexCollectionsBuild, window._sr.tasks.plexRatingsApply, window._sr.tasks.plexImagesSync].includes(taskId)) {
          setButtonLoading(button, false);
          button.disabled = true;
        } else {
          setButtonLoading(button, activeTasks.includes(taskId) || button.classList.contains("clicking"));
          if (button.classList.contains("plex-auth") && !isPlexLinked) button.disabled = true;
        }
      });

      const completeResponse = await fetchJson(base + "/tasks/completed", {}, false);
      const completeData = getData(completeResponse);
      if (completeResponse.isOk && completeData) {
        let hasNewReports = false;
        for (const [taskName, result] of Object.entries(completeData)) {
          const button = element(taskName);
          if (button?.classList.contains("clicking")) continue;
          const isOk = (result.status || result.Status || "").toLowerCase() === "ok";
          const filterInput = button?.dataset.relayPersistIfEmpty ? document.querySelector(button.dataset.relayPersistIfEmpty) : null;

          toastOperation({ isOk: isOk, data: result }, taskName.replace(/-/g, " "), { hideOnSucceed: filterInput?.value?.trim() ? TOAST_MS : 0 });
          await fetchJson(base + `/tasks/clear/${taskName}`, { method: "POST" }, false);
          hasNewReports = true;
          if (taskName === window._sr.tasks.plexAuthRefresh) window._sr.refreshPlexState?.();
        }
        if (hasNewReports) loadReports();
      }
    } finally {
      isTaskSyncing = false;
    }
  }

  /**
   * Fetches the list of generated task reports from the server and renders them as clickable links.
   * @returns {Promise<void>}
   */
  async function loadReports() {
    const reportsList = element("reports-list");
    if (!reportsList) return;

    const response = await fetchJson(base + "/reports/list");
    if (response.isOk) {
      const files = getData(response) || [];
      if (files.length > 0) {
        reportsList.innerHTML = "";
        files.forEach((file) => {
          const listItem = document.createElement("li");
          listItem.className = "report-item";

          const link = document.createElement("a");
          link.href = `${base}/reports/${file.name}`;
          link.target = "_blank";
          link.rel = "noopener noreferrer";
          link.textContent = file.friendlyName;
          link.className = "report-link";

          const small = document.createElement("small");
          small.textContent = ` (${file.name})`;

          listItem.append(link, small);
          reportsList.appendChild(listItem);
        });
      } else {
        reportsList.innerHTML = '<li class="placeholder">No reports generated yet. Click any "Generate" or automation button on the dashboard to start a task.</li>';
      }
    } else {
      reportsList.innerHTML = '<li class="placeholder">Failed to load task reports list from server.</li>';
    }
  }

  /**
   * Core logic to wrap an async action with loading states and task management.
   * @param {HTMLElement} button - The button element trigger.
   * @param {Function} handler - The async function to execute.
   * @returns {Promise<void>}
   */
  async function runAction(button, handler) {
    if (!button || button.classList.contains("clicking")) return;
    button.classList.add("clicking");
    setButtonLoading(button, true);
    const taskId = button.id;
    try {
      await handler(button);
      if (taskId && MANAGED_TASK_IDS.includes(taskId)) await fetchJson(base + `/tasks/clear/${taskId}`, { method: "POST" }, false);
    } finally {
      button.classList.remove("clicking");
      if (taskId && !MANAGED_TASK_IDS.includes(taskId)) setTimeout(() => setButtonLoading(button, false), TOAST_MS);
      else syncActiveTasks();
      loadReports();
    }
  }

  /**
   * Initialize a button as an aria-pressed toggle with a click handler.
   * @param {HTMLElement|string} button - The button element or its DOM id.
   * @param {boolean} [defaultState=false] - Initial pressed state.
   * @returns {HTMLElement|null} The resolved button element.
   */
  function initToggle(button, defaultState = false) {
    const buttonElement = typeof button === "string" ? element(button) : button;
    if (!buttonElement) return null;
    if (!buttonElement.hasAttribute("aria-pressed")) buttonElement.setAttribute("aria-pressed", String(!!defaultState));
    buttonElement.onclick = () => buttonElement.setAttribute("aria-pressed", buttonElement.getAttribute("aria-pressed") === "true" ? "false" : "true");
    return buttonElement;
  }
  // #endregion

  // #region Global Exports
  Object.assign(window._sr, {
    runAction,
    initToggle,
    setButtonLoading,
    /**
     * Binds an async click handler to a button element wrapped with loading state management.
     * @param {HTMLElement|string} button - The button element or its DOM id.
     * @param {Function} handler - The async action handler to execute on click.
     * @returns {void}
     */
    withButtonAction: (button, handler) => {
      const buttonElement = typeof button === "string" ? element(button) : button;
      if (buttonElement) buttonElement.onclick = () => runAction(buttonElement, handler);
    },
  });
  // #endregion

  // #region Global Dispatcher
  document.addEventListener("click", (event) => {
    const target = event.target.closest("[data-relay-endpoint], [data-relay-action]");
    if (!target) return;

    /**
     * Dispatches a configured endpoint request or registered action for a clicked control.
     * @param {HTMLElement} button - The triggered button element.
     * @param {boolean} [forceDryRun=false] - Whether to rewrite dryRun=false to dryRun=true.
     * @returns {Promise<void>}
     */
    const action = async (button, forceDryRun = false) => {
      let endpoint = button.dataset.relayEndpoint;
      const actionKey = button.dataset.relayAction;
      const label = button.dataset.relayLabel;
      const paramFnName = button.dataset.relayParams;
      const method = button.dataset.relayMethod || "GET";
      const persistAttr = button.dataset.relayPersist === "true";
      const persistIfEmptySelector = button.dataset.relayPersistIfEmpty;

      if (endpoint) {
        if (forceDryRun) endpoint = endpoint.replace(/dryRun=false/i, "dryRun=true");
        let url = base + endpoint;
        if (paramFnName && typeof window._sr[paramFnName] === "function") {
          const paramString = window._sr[paramFnName]().toString();
          if (paramString) url += (url.includes("?") ? "&" : "?") + paramString;
        }
        let hideOnSucceed = persistAttr || forceDryRun ? 0 : TOAST_MS;
        if (persistIfEmptySelector) {
          const input = document.querySelector(persistIfEmptySelector);
          if (input && !input.value.trim()) hideOnSucceed = 0;
        }
        showToast(`${label}${forceDryRun ? " (Dry Run)" : ""}: Processing...`, "info", TOAST_MS);
        const response = await fetchJson(url, { method });
        toastOperation(response, label, { hideOnSucceed });
        if (button.id === window._sr.tasks.plexAuthRefresh && response.isOk) window._sr.refreshPlexState?.();
      } else if (actionKey) {
        const handler = window._sr.actions[actionKey];
        if (handler) await handler(button);
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
        runAction(target, (button) => action(button, false));
      };
      dryBtn.onclick = () => {
        close();
        runAction(target, (button) => action(button, true));
      };
    } else runAction(target, action);
  });
  // #endregion

  // #region Lifecycle Execution
  (async () => {
    await window._sr.authReady;
    setInterval(syncActiveTasks, 3000);
    syncActiveTasks();
    loadReports();
  })();
  // #endregion
})();
