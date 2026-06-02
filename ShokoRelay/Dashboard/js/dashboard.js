/**
 * @file dashboard.js
 * @description Dashboard-exclusive task synchronization, lockouts, and endpoint routing.
 */
(() => {
  if (window.self === window.top) document.documentElement.style.scrollbarGutter = "stable"; // prevent layout shifts when scrollbars appear/disappear on the dashboard

  const { base, configUrl, el, TOAST_MS, fetchJson, showToast, toastOperation, saveSettings, getData, openModal } = window._sr;

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
      const s = document.createElement("span");
      s.className = "button-spinner";
      s.innerHTML = '<svg class="icon-svg"><use href="img/icons.svg#loading"></use></svg>';
      btn.appendChild(s);
    } else if (!isLoading) btn.querySelector(".button-spinner")?.remove();
  }

  /**
   * Polls the server for active tasks and completed results, synchronizing the UI button states and disabling sub-tasks during automation runs.
   * @returns {Promise<void>}
   */
  async function syncActiveTasks() {
    const res = await fetchJson(base + "/tasks/active");
    if (!res.ok) return;
    const activeTasks = getData(res) || [];
    const isAutoRunning = activeTasks.includes(window._sr.tasks.plexAutomationRun);
    const isPlexLinked = !!window._sr.isPlexLinked;

    MANAGED_TASK_IDS.forEach((id) => {
      const btn = el(id);
      if (!btn) return;

      if (isAutoRunning && [window._sr.tasks.plexCollectionsBuild, window._sr.tasks.plexRatingsApply, window._sr.tasks.plexImagesSync].includes(id)) {
        setButtonLoading(btn, false);
        btn.disabled = true;
      } else {
        setButtonLoading(btn, activeTasks.includes(id) || btn.classList.contains("clicking"));
        if (btn.classList.contains("plex-auth") && !isPlexLinked) {
          btn.disabled = true;
        }
      }
    });

    const completeRes = await fetchJson(base + "/tasks/completed");
    const completeData = getData(completeRes);
    if (completeRes.ok && completeData) {
      for (const [taskName, result] of Object.entries(completeData)) {
        const btn = el(taskName);
        if (btn?.classList.contains("clicking")) continue;
        const isOk = (result.status || result.Status || "").toLowerCase() === "ok";
        const fInput = btn?.dataset.relayPersistIfEmpty ? document.querySelector(btn.dataset.relayPersistIfEmpty) : null;

        toastOperation({ ok: isOk, data: result }, taskName.replace(/-/g, " "), { hideOnSucceed: fInput?.value?.trim() ? TOAST_MS : 0 });
        await fetch(base + `/tasks/clear/${taskName}`, { method: "POST" });
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
    btn.classList.add("clicking");
    setButtonLoading(btn, true);
    const taskId = btn.id;
    try {
      await handler(btn);
      if (taskId && MANAGED_TASK_IDS.includes(taskId)) await fetch(base + `/tasks/clear/${taskId}`, { method: "POST" });
    } finally {
      btn.classList.remove("clicking");
      if (taskId && !MANAGED_TASK_IDS.includes(taskId)) setTimeout(() => setButtonLoading(btn, false), TOAST_MS);
      else syncActiveTasks();
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
  // #endregion

  // #region Global Exports
  Object.assign(window._sr, {
    runAction,
    initToggle,
    setButtonLoading,
    withButtonAction: (btn, handler) => {
      const elBtn = typeof btn === "string" ? el(btn) : btn;
      if (elBtn) elBtn.onclick = () => runAction(elBtn, handler);
    },
  });
  // #endregion

  // #region Global Dispatcher
  document.addEventListener("click", (e) => {
    const target = e.target.closest("[data-relay-endpoint], [data-relay-action]");
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
          const ps = window._sr[paramFnName]();
          url += (url.includes("?") ? "&" : "?") + ps.toString();
        }
        let hideOnSucceed = persistAttr || forceDryRun ? 0 : TOAST_MS;
        if (persistIfEmptySelector) {
          const input = document.querySelector(persistIfEmptySelector);
          if (input && !input.value.trim()) hideOnSucceed = 0;
        }
        showToast(`${label}${forceDryRun ? " (Dry Run)" : ""}: Processing...`, "info", TOAST_MS);
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
      const modal = el("confirm-modal");
      const msg = el("confirm-message");
      const execBtn = el("confirm-exec");
      const dryBtn = el("confirm-dry");
      msg.innerHTML = confirmMsg;
      execBtn.textContent = target.dataset.relayConfirmButton || "Confirm";
      dryBtn.style.display = target.dataset.relayConfirmDry === "true" ? "" : "none"; // Dynamically display the Dry Run button only if explicitly supported by the action
      const close = openModal(modal);
      el("confirm-cancel").onclick = close;
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

  // Lifecycle Execution
  setInterval(syncActiveTasks, 3000);
  syncActiveTasks();
})();
