/**
 * @file settings.js
 * @description Dedicated logic for building and persisting the Provider Settings form on the Shoko Relay dashboard.
 */
(() => {
  const { configUrl, el, fetchJson, showToast, getValueByPath, setValueByPath, openModal, bindConfig, unwrapConfig, saveSettings } = window._sr;

  // #region Config Helpers
  /**
   * Attach a smooth open/close animation to a <details> element using the Web Animations API.
   * @param {HTMLElement} details - The details element.
   * @param {HTMLElement} content - The inner content container.
   * @param {number} [duration=300] - Animation duration in ms.
   * @returns {void}
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
   * Consolidates dynamic state updates for the dashboard. Evaluates Plex auth, Sync user setting, and TMDB episode numbering constraints to disable controls.
   * @param {Object} [config] - The optional active configuration object to evaluate.
   * @returns {void}
   */
  window._sr.updateControlStates = (config) => {
    const isPlexLinked = !!window._sr.isPlexLinked;
    const isSyncActive = el("sync-users")?.value !== "3";

    document.querySelectorAll(".plex-auth, .sync-user").forEach((elem) => {
      const reqPlex = elem.classList.contains("plex-auth");
      const reqSync = elem.classList.contains("sync-user");
      const isDisabled = (reqPlex && !isPlexLinked) || (reqSync && !isSyncActive);

      if (["INPUT", "BUTTON", "SELECT", "TEXTAREA"].includes(elem.tagName)) elem.disabled = isDisabled;

      const msg = isDisabled ? (reqPlex && !isPlexLinked ? "Requires Plex Authentication" : "Requires Sync Users to be Selected") : "";
      const parent = elem.closest("label, .w100, div") || elem.parentElement;
      if (parent) {
        if (msg) parent.title = msg;
        else {
          delete parent.dataset.tooltipText;
          parent.removeAttribute("title");
          parent.removeAttribute("aria-describedby");
        }
      }
    });

    const overridesBtn = el("vfs-overrides");
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
  // #endregion

  // #region Form Generation
  /**
   * Builds the configuration settings form dynamically based on the server schema.
   * @returns {Promise<void>}
   */
  async function loadConfig() {
    if (!el("config-form")) return;
    const [schemaRes, configRes] = await Promise.all([fetchJson(configUrl + "/schema"), fetchJson(configUrl)]);
    if (!schemaRes.ok || !configRes.ok) return showToast("Failed To Load Config", "error", 0);

    const schema = schemaRes.data.properties || [];
    const rawCfg = configRes.data || {};
    const config = unwrapConfig(rawCfg);
    const overridesBtn = el("vfs-overrides");

    el("config-form").innerHTML = "";
    if (el("overrides-text")) el("overrides-text").value = rawCfg.overrides || "";

    const advSection = document.createElement("details");
    const advContent = document.createElement("div");
    advSection.className = "details-anim";
    advContent.className = "details-content";
    advSection.innerHTML = "<summary>Advanced Settings</summary>";
    advContent.appendChild(document.createElement("hr"));

    schema.forEach((p) => {
      const wrap = document.createElement("div");
      if (p.Rebuild) wrap.classList.add("vfs-rebuild");
      const label = document.createElement("label");
      const value = getValueByPath(config, p.Path);
      let input;

      if (p.Path.endsWith("SelectedTheme")) {
        label.innerHTML = `<span>${p.Display || p.Path.split(".").pop()}</span>${p.Description ? `<small>${p.Description}</small>` : ""}`;
        wrap.appendChild(label);
        input = document.createElement("select");
        input.add(new Option("Default", "default"));
        input.add(new Option("Shoko Gray", "shoko-gray"));

        (rawCfg.themes || []).forEach((t) => {
          const opt = new Option(t.name, t.id);
          opt.selected = t.id === value;
          input.add(opt);
        });
        wrap.appendChild(input);

        // Custom save handler to force-reload the dynamic theme stylesheet instantly on dropdown change
        const customSave = async (cfg) => {
          await saveSettings(cfg);
          const link = document.querySelector('link[href*="theme.css"]');
          if (link) link.href = `../theme.css?t=${new Date().getTime()}`;
        };

        bindConfig(input, p.Path, config, customSave, "text");
      } else if (p.Type === "bool") {
        wrap.innerHTML = `<label class="shoko-checkbox"><input type="checkbox">
          <span class="shoko-checkbox-icon" aria-hidden="true"><svg class="unchecked"><use href="img/icons.svg#checkbox-blank-circle-outline"></use></svg><svg class="checked"><use href="img/icons.svg#checkbox-marked-circle-outline"></use></svg></span>
          <span class="shoko-checkbox-text"><span class="shoko-checkbox-title">${p.Display || p.Path}</span><small class="shoko-checkbox-desc" style="display:block">${p.Description || ""}</small></span></label>`;
        input = wrap.querySelector("input");
        bindConfig(input, p.Path, config, saveSettings, "check");

        // Re-evaluate the overrides button state when either relevant setting is toggled.
        if (overridesBtn && ["Advanced.TmdbEpNumbering", "Advanced.MergeTmdbSeries"].includes(p.Path)) input.addEventListener("change", () => window._sr.updateControlStates(config));
      } else if (p.Path.endsWith("PathMappings")) {
        label.innerHTML = `<span>${p.Display || p.Path.split(".").pop()}</span>${p.Description ? `<small>${p.Description}</small>` : ""}`;
        wrap.appendChild(label);
        const mappingContainer = document.createElement("div");
        mappingContainer.innerHTML = `<div class="full"><div><small>Plex Base Paths</small><textarea id="path-mappings-left"></textarea></div><div><small>Shoko Base Paths</small><textarea id="path-mappings-right"></textarea></div></div>`;
        wrap.appendChild(mappingContainer);
        const l = mappingContainer.querySelector("#path-mappings-left");
        const r = mappingContainer.querySelector("#path-mappings-right");
        const m = value || {};
        const keys = Object.keys(m).sort();
        l.value = keys.map((k) => m[k]).join("\n");
        r.value = keys.join("\n");
        l.onchange = r.onchange = async () => {
          const val = {};
          const lLines = l.value.split("\n");
          const rLines = r.value.split("\n");
          lLines.forEach((lv, idx) => {
            if (lv.trim() && rLines[idx]?.trim()) val[rLines[idx].trim()] = lv.trim();
          });
          setValueByPath(config, p.Path, val);
          await saveSettings(config);
        };
      } else {
        label.innerHTML = `<span>${p.Display || p.Path.split(".").pop()}</span>${p.Description ? `<small>${p.Description}</small>` : ""}`;
        wrap.appendChild(label);
        input = document.createElement(p.Type === "enum" ? "select" : p.Type === "json" || p.Path.endsWith("TagBlacklist") || p.Path.endsWith("FolderExclusions") ? "textarea" : "input");
        if (p.Type === "enum") {
          (p.EnumValues || []).forEach((ev) => {
            const opt = new Option(ev.name, ev.value);
            opt.selected = String(ev.value) === String(value);
            input.add(opt);
          });
        } else if (p.Type === "number") {
          input.type = "number";
        } else {
          input.type = "text";
          if (p.Path.endsWith("ShokoServerUrl")) input.placeholder = "e.g. http://localhost:8111";
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
            await saveSettings(config);
          };
        } else bindConfig(input, p.Path, config, saveSettings, p.Type === "bool" ? "check" : p.Type === "number" ? "number" : "text");
      }
      (p.Advanced ? advContent : el("config-form")).appendChild(wrap);
    });

    if (advContent.children.length > 1) {
      el("config-form").appendChild(advSection);
      advSection.appendChild(advContent);
      initDetailsAnimation(advSection, advContent);
    }

    const b = (id, path, type) => bindConfig(id, path, config, saveSettings, type);
    const autoMap = {
      "shoko-utc-offset": "UtcOffsetHours",
      "shoko-import-frequency": "ShokoImportFrequencyHours",
      "shoko-sync-frequency": "ShokoSyncWatchedFrequencyHours",
      "plex-auto-frequency": "PlexAutomationFrequencyHours",
    };
    for (const [id, path] of Object.entries(autoMap)) b(id, "Automation." + path, "number");

    b("sync-ratings", "Automation.ShokoSyncWatchedIncludeRatings", "check");
    b("sync-users", "Automation.ShokoSyncWatchedUserType", "number");
    b("plex-scrobble", "Automation.AutoScrobble", "check");
    window._sr.initAtConfig?.(config, saveSettings);
    window._sr.updateControlStates(config);
  }
  // #endregion

  // #region Initialization
  // Help Modal Logic
  const helpBtn = el("settings-help-open");
  if (helpBtn)
    helpBtn.onclick = () => {
      const modal = el("settings-help-modal");
      openModal(modal);
    };

  loadConfig();
  // #endregion
})();
