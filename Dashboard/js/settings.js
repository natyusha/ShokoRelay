/**
 * @file settings.js
 * @description Dedicated logic for building and persisting the Provider Settings form on the Shoko Relay dashboard.
 */
(() => {
  const { configUrl, element, esc, fetchJson, showToast, getValueByPath, setValueByPath, openModal, bindConfig, unwrapConfig, saveSettings } = window._sr;

  // #region Config Helpers
  /**
   * Attach a smooth open/close animation to a <details> element using the Web Animations API, persisting its open/closed state to localStorage.
   * @param {HTMLElement} details - The details element.
   * @param {HTMLElement} content - The inner content container.
   * @param {number} [duration=300] - Animation duration in ms.
   * @returns {void}
   */
  function initDetailsAnimation(details, content, duration = 300) {
    let anim = null;
    if (details.id) {
      const saved = localStorage.getItem(`details-state-${details.id}`);
      if (saved !== null) details.open = saved === "true";
    }
    details.querySelector("summary")?.addEventListener("click", (event) => {
      event.preventDefault();
      if (anim) anim.cancel();
      const isOpening = !details.open;
      if (isOpening) details.open = true;
      if (details.id) localStorage.setItem(`details-state-${details.id}`, String(isOpening));
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
   * Consolidates dynamic state updates for the dashboard. Evaluates Plex auth, Sync user setting, and TMDB episode numbering constraints to disable controls.
   * @param {Object} [config] - The optional active configuration object to evaluate.
   * @returns {void}
   */
  window._sr.updateControlStates = (config) => {
    const isPlexLinked = !!window._sr.isPlexLinked;
    const isSyncActive = element("sync-users")?.value !== "4";

    document.querySelectorAll(".plex-auth, .sync-user").forEach((elem) => {
      const reqPlex = elem.classList.contains("plex-auth");
      const reqSync = elem.classList.contains("sync-user");
      const isDisabled = (reqPlex && !isPlexLinked) || (reqSync && !isSyncActive);

      if (["INPUT", "BUTTON", "SELECT", "TEXTAREA"].includes(elem.tagName)) elem.disabled = isDisabled;

      const message = isDisabled ? (reqPlex && !isPlexLinked ? "Requires Plex Authentication" : "Requires Sync Users to be Selected") : "";
      const parent = elem.closest("label, .w100, .dsbld-wrap") || elem.parentElement;

      if (parent) {
        if (message) parent.title = message;
        else {
          delete parent.dataset.tooltipText;
          parent.removeAttribute("title");
          parent.removeAttribute("aria-describedby");
        }
      }
    });

    const overridesBtn = element("vfs-overrides");
    const cfg = config || window.relaySettings;
    if (overridesBtn && cfg) {
      const isDisabled = !(window._sr.getValueByPath(cfg, "Advanced.TmdbEpNumbering") || window._sr.getValueByPath(cfg, "Advanced.MergeTmdbSeries"));
      overridesBtn.disabled = isDisabled;

      const parent = overridesBtn.closest(".w100, div") || overridesBtn.parentElement;
      if (parent) {
        if (isDisabled) parent.title = "Requires TMDB Episode Numbering or Auto-Merge";
        else {
          delete parent.dataset.tooltipText;
          parent.removeAttribute("title");
          parent.removeAttribute("aria-describedby");
        }
      }
    }
  };

  /**
   * Parses a flags enum value (bitmask integer or comma-separated string) into a numeric bitmask.
   * @param {number|string} value - The raw value from the configuration payload.
   * @param {Array<{name: string, value: number}>} enumValues - The single-bit flag schema definitions.
   * @returns {number} The resolved integer bitmask.
   */
  function parseFlagsValue(value, enumValues) {
    if (typeof value === "number") return value;
    if (typeof value !== "string" || !value.trim()) return 0;
    const trimmed = value.trim();
    if (/^\d+$/.test(trimmed)) return Number(trimmed);
    if (trimmed.toLowerCase() === "all") return enumValues.reduce((acc, enumVal) => acc | enumVal.value, 0);
    if (trimmed.toLowerCase() === "none") return 0;
    return trimmed.split(",").reduce((acc, part) => {
      const match = enumValues.find((enumVal) => enumVal.name.toLowerCase() === part.trim().toLowerCase());
      return match ? acc | match.value : acc;
    }, 0);
  }

  /**
   * Generates a multiselect checkbox dropdown for Flags enum properties and binds it to the configuration state.
   * @param {HTMLElement} wrap - The container element to append to.
   * @param {Object} prop - The property schema object.
   * @param {Object} config - The active configuration object.
   * @param {number|string} value - The current property value.
   * @returns {void}
   */
  function createFlagsMultiselect(wrap, prop, config, value) {
    const label = document.createElement("label");
    label.innerHTML = `<span>${esc(prop.Display || prop.Path.split(".").pop())}</span>${prop.Description ? `<small>${esc(prop.Description)}</small>` : ""}`;
    wrap.appendChild(label);

    const enumValues = prop.EnumValues || [];
    let mask = parseFlagsValue(value, enumValues);

    const container = document.createElement("div");
    container.className = "multiselect";

    const trigger = document.createElement("button");
    trigger.type = "button";
    trigger.className = "multiselect-trigger";
    const textSpan = document.createElement("span");
    textSpan.className = "multiselect-text";
    trigger.appendChild(textSpan);

    const menu = document.createElement("div");
    menu.className = "multiselect-menu";

    const updateSummary = () => {
      const activeNames = enumValues.filter((enumVal) => (mask & enumVal.value) === enumVal.value).map((enumVal) => enumVal.name);
      textSpan.textContent = activeNames.length ? activeNames.join(", ") : "None";
    };

    enumValues.forEach((enumVal) => {
      const itemLabel = document.createElement("label");
      itemLabel.className = "shoko-checkbox";
      itemLabel.innerHTML = `<input type="checkbox" value="${esc(enumVal.value)}">
        <span class="shoko-checkbox-icon" aria-hidden="true"><svg class="unchecked"><use href="img/icons.svg#checkbox-blank-circle-outline"></use></svg><svg class="checked"><use href="img/icons.svg#checkbox-marked-circle-outline"></use></svg></span>
        <span class="shoko-checkbox-text"><span class="shoko-checkbox-title">${esc(enumVal.name)}</span></span>`;
      const checkbox = itemLabel.querySelector("input");
      checkbox.checked = (mask & enumVal.value) === enumVal.value;
      checkbox.onchange = async () => {
        mask = checkbox.checked ? mask | enumVal.value : mask & ~enumVal.value;
        updateSummary();
        setValueByPath(config, prop.Path, mask);
        await saveSettings(config);
      };
      menu.appendChild(itemLabel);
    });

    trigger.onclick = (clickEvent) => {
      clickEvent.stopPropagation();
      const willOpen = !container.classList.contains("open");
      document.querySelectorAll(".multiselect.open").forEach((openElement) => openElement.classList.remove("open"));
      if (willOpen) container.classList.add("open");
    };

    menu.onclick = (clickEvent) => clickEvent.stopPropagation();

    updateSummary();
    container.append(trigger, menu);
    wrap.appendChild(container);
  }

  document.addEventListener("click", () => document.querySelectorAll(".multiselect.open").forEach((openElement) => openElement.classList.remove("open")));

  /**
   * Generates a dual-textarea UI for mapping dictionaries and binds it to the configuration state.
   * @param {HTMLElement} wrap - The container element to append to.
   * @param {Object} prop - The property schema object.
   * @param {Object} config - The active configuration object.
   * @param {Object} value - The current dictionary value.
   * @param {string} leftHeader - The label for the left textarea.
   * @param {string} rightHeader - The label for the right textarea.
   * @param {boolean} keyOnLeft - If true, dictionary keys are placed on the left; otherwise, they are on the right.
   * @returns {void}
   */
  function createDualTextareaMapping(wrap, prop, config, value, leftHeader, rightHeader, keyOnLeft) {
    const label = document.createElement("label");
    label.innerHTML = `<span>${esc(prop.Display || prop.Path.split(".").pop())}</span>${prop.Description ? `<small>${esc(prop.Description)}</small>` : ""}`;
    wrap.appendChild(label);

    const mappingContainer = document.createElement("div");
    mappingContainer.innerHTML = `<div class="full"><div><small>${esc(leftHeader)}</small><textarea class="mapping-left"></textarea></div><div><small>${esc(rightHeader)}</small><textarea class="mapping-right"></textarea></div></div>`;
    wrap.appendChild(mappingContainer);

    const leftArea = mappingContainer.querySelector(".mapping-left");
    const rightArea = mappingContainer.querySelector(".mapping-right");
    const mappingDict = value || {};
    const keys = Object.keys(mappingDict); // Preserve user priority/order

    leftArea.value = keys.map((key) => (keyOnLeft ? key : mappingDict[key])).join("\n");
    rightArea.value = keys.map((key) => (keyOnLeft ? mappingDict[key] : key)).join("\n");

    leftArea.onchange = rightArea.onchange = async () => {
      const mappingValue = {};
      const leftLines = leftArea.value.split("\n");
      const rightLines = rightArea.value.split("\n");
      leftLines.forEach((leftVal, index) => {
        if (leftVal.trim() && rightLines[index]?.trim()) mappingValue[keyOnLeft ? leftVal.trim() : rightLines[index].trim()] = keyOnLeft ? rightLines[index].trim() : leftVal.trim();
      });
      setValueByPath(config, prop.Path, mappingValue);
      await saveSettings(config);
    };
  }
  // #endregion

  // #region Form Generation
  /**
   * Builds the configuration settings form dynamically based on the server schema.
   * @returns {Promise<void>}
   */
  async function loadConfig() {
    if (!element("config-form")) return;
    const [schemaRes, configRes] = await Promise.all([fetchJson(configUrl + "/schema"), fetchJson(configUrl)]);
    if (!schemaRes.isOk || !configRes.isOk) return showToast("Failed to Load Config", "error", 0);

    const schema = schemaRes.data.properties || [];
    const rawConfig = configRes.data || {};
    const config = window._sr.syncConfig(unwrapConfig(rawConfig));
    const overridesBtn = element("vfs-overrides");

    element("config-form").innerHTML = "";
    if (element("overrides-text")) element("overrides-text").value = rawConfig.overrides || "";

    const primarySection = document.createElement("details");
    const primaryContent = document.createElement("div");
    primarySection.id = "config-primary-settings";
    primarySection.className = "details-anim";
    primaryContent.className = "details-content";
    primarySection.open = true;
    primarySection.innerHTML = "<summary>Metadata</summary>";
    primaryContent.appendChild(document.createElement("hr"));

    const langSection = document.createElement("details");
    const langContent = document.createElement("div");
    langSection.id = "config-lang-settings";
    langSection.className = "details-anim";
    langContent.className = "details-content";
    langSection.innerHTML = "<summary>Language</summary>";
    langContent.appendChild(document.createElement("hr"));

    const advSection = document.createElement("details");
    const advContent = document.createElement("div");
    advSection.id = "config-adv-settings";
    advSection.className = "details-anim";
    advContent.className = "details-content";
    advSection.innerHTML = "<summary>Advanced</summary>";
    advContent.appendChild(document.createElement("hr"));

    schema.forEach((prop) => {
      const wrap = document.createElement("div");
      if (prop.Rebuild) wrap.classList.add("vfs-rebuild");
      const label = document.createElement("label");
      const value = getValueByPath(config, prop.Path);
      let input;

      if (prop.Path.endsWith("SelectedTheme")) {
        label.innerHTML = `<span>${esc(prop.Display || prop.Path.split(".").pop())}</span>${prop.Description ? `<small>${esc(prop.Description)}</small>` : ""}`;
        wrap.appendChild(label);
        input = document.createElement("select");
        input.add(new Option("Default", "default"));
        input.add(new Option("Shoko Gray", "shoko-gray"));

        (rawConfig.themes || []).forEach((theme) => {
          const opt = new Option(theme.name, theme.id);
          opt.selected = theme.id === value;
          input.add(opt);
        });
        wrap.appendChild(input);

        // Custom save handler to force-reload the dynamic theme stylesheet instantly on dropdown change
        const customSave = async (cfg) => {
          await saveSettings(cfg);
          const link = document.querySelector('link[href*="theme.css"]');
          if (link) link.href = `../theme.css?t=${new Date().getTime()}`;
        };

        bindConfig(input, prop.Path, config, customSave, "text");
      } else if (prop.Type === "bool") {
        wrap.innerHTML = `<label class="shoko-checkbox"><input type="checkbox">
          <span class="shoko-checkbox-icon" aria-hidden="true"><svg class="unchecked"><use href="img/icons.svg#checkbox-blank-circle-outline"></use></svg><svg class="checked"><use href="img/icons.svg#checkbox-marked-circle-outline"></use></svg></span>
          <span class="shoko-checkbox-text"><span class="shoko-checkbox-title">${esc(prop.Display || prop.Path)}</span><small class="shoko-checkbox-desc" style="display:block">${esc(prop.Description || "")}</small></span></label>`;
        input = wrap.querySelector("input");
        bindConfig(input, prop.Path, config, saveSettings, "check");
        // Re-evaluate the overrides button state when either relevant setting is toggled.
        if (overridesBtn && ["Advanced.TmdbEpNumbering", "Advanced.MergeTmdbSeries"].includes(prop.Path)) input.addEventListener("change", () => window._sr.updateControlStates(config));
      } else if (prop.Type === "flags") {
        createFlagsMultiselect(wrap, prop, config, value);
      } else if (prop.Path.endsWith("PathMappings")) {
        createDualTextareaMapping(wrap, prop, config, value, "Working Base Paths", "Shoko Base Paths", false);
      } else if (prop.Path.endsWith("SubtitleLanguageMappings")) {
        createDualTextareaMapping(wrap, prop, config, value, "Original Tags", "Mapped Tags", true);
      } else {
        label.innerHTML = `<span>${esc(prop.Display || prop.Path.split(".").pop())}</span>${prop.Description ? `<small>${esc(prop.Description)}</small>` : ""}`;
        wrap.appendChild(label);
        input = document.createElement(prop.Type === "enum" ? "select" : prop.Type === "json" || prop.Path.endsWith("TagOverrides") || prop.Path.endsWith("Exclusions") ? "textarea" : "input");
        if (prop.Path === "TagOverrides") input.id = "resizable";
        if (prop.Type === "enum") {
          (prop.EnumValues || []).forEach((enumVal) => {
            const opt = new Option(enumVal.name, enumVal.value);
            opt.selected = String(enumVal.value) === String(value);
            input.add(opt);
          });
        } else if (prop.Type === "number") {
          input.type = "number";
        } else if (input.tagName === "INPUT") {
          input.type = "text";
          if (prop.Path.endsWith("ShokoServerUrl")) input.placeholder = "e.g. http://localhost:8111";
          if (prop.Path.endsWith("PlexServerUrl")) input.placeholder = "e.g. http://localhost:32400";
        }
        wrap.appendChild(input);

        if (prop.Path === "Advanced.RequireAuth") {
          input.onchange = async () => {
            const nextVal = Number(input.value) || 0;
            if (nextVal !== 0) {
              const verifyRes = await fetchJson(`${window._sr.base}/auth/verify?force=true`, {}, false);
              if (!verifyRes.isOk) {
                const authed = await window._sr.ensureAuth?.({ dismissable: true });
                if (!authed) {
                  input.value = String(getValueByPath(config, prop.Path) ?? 0);
                  return;
                }
              }
            }
            setValueByPath(config, prop.Path, nextVal);
            await saveSettings(config);
          };
          // Custom validation for ShokoServerUrl and PlexServerUrl
        } else if (prop.Path.endsWith("ShokoServerUrl") || prop.Path.endsWith("PlexServerUrl")) {
          input.value = value ?? "";
          input.onchange = async () => {
            const urlRegex = /^https?:\/\/([a-zA-Z0-9.-]+|\[[a-fA-F0-9:]+\])(:\d+)?$/;
            const cleanVal = input.value.trim().replace(/\/+$/, "");
            if (cleanVal && !urlRegex.test(cleanVal)) {
              showToast(`Invalid ${prop.Path.endsWith("ShokoServerUrl") ? "Shoko" : "Plex"} URL. Use http(s)://HOST:PORT`, "error", 5000);
              input.value = getValueByPath(config, prop.Path) || "";
              return;
            }
            input.value = cleanVal;
            setValueByPath(config, prop.Path, cleanVal);
            await saveSettings(config);
          };
        } else if (prop.Path === "Advanced.FolderExclusions") {
          input.value = value ?? "";
          input.onchange = async () => {
            const cleanVal = input.value
              .split(/\r?\n/)
              .map((line) => line.trim().replace(/^[\\/]+|[\\/]+$/g, ""))
              .filter((line) => line.length > 0)
              .join("\n");
            input.value = cleanVal;
            setValueByPath(config, prop.Path, cleanVal);
            await saveSettings(config);
          };
        } else bindConfig(input, prop.Path, config, saveSettings, prop.Type === "number" ? "number" : "text");
      }
      (prop.Advanced ? advContent : prop.Language ? langContent : primaryContent).appendChild(wrap);
    });

    if (primaryContent.children.length > 1) {
      element("config-form").appendChild(primarySection);
      primarySection.appendChild(primaryContent);
      initDetailsAnimation(primarySection, primaryContent);
    }

    if (langContent.children.length > 1) {
      element("config-form").appendChild(langSection);
      langSection.appendChild(langContent);
      initDetailsAnimation(langSection, langContent);
    }

    if (advContent.children.length > 1) {
      element("config-form").appendChild(advSection);
      advSection.appendChild(advContent);
      initDetailsAnimation(advSection, advContent);
    }

    const bindField = (id, path, type) => bindConfig(id, path, config, saveSettings, type);
    const autoMap = {
      "shoko-utc-offset": "UtcOffsetHours",
      "shoko-import-frequency": "ShokoImportFrequencyHours",
      "shoko-sync-frequency": "ShokoSyncWatchedFrequencyHours",
      "plex-auto-frequency": "PlexAutomationFrequencyHours",
    };
    for (const [id, path] of Object.entries(autoMap)) {
      bindField(id, "Automation." + path, "number");
      const inputEl = element(id);
      if (inputEl) {
        inputEl.oninput = () => {
          inputEl.value = inputEl.value.replace(id === "shoko-utc-offset" ? /[^-0-9]/g : /[^0-9]/g, "");
        };
      }
    }

    bindField("sync-ratings", "Automation.ShokoSyncWatchedIncludeRatings", "check");
    bindField("sync-progress", "Automation.ShokoSyncWatchedIncludeProgress", "check");
    bindField("sync-users", "Automation.ShokoSyncWatchedUserType", "number");
    bindField("sync-user-mappings", "Automation.AdvancedUserMappings", "text");
    window._sr.initAtConfig?.(config, saveSettings);
    window._sr.updateControlStates(config);
  }
  // #endregion

  // #region Initialization
  // Help Modal Logic
  const helpBtn = element("settings-help-open");
  if (helpBtn)
    helpBtn.onclick = () => {
      const modal = element("settings-help-modal");
      openModal(modal);
    };

  (async () => {
    await window._sr.authReady;
    loadConfig();
  })();

  const logsDetails = document.querySelector("#logs-list")?.closest(".details-anim");
  const logsContent = logsDetails?.querySelector(".details-content");
  if (logsDetails && logsContent) initDetailsAnimation(logsDetails, logsContent);
  // #endregion
})();
