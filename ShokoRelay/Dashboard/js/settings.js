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

    const schema = schemaRes.data.properties || [],
      rawCfg = configRes.data || {},
      config = unwrapConfig(rawCfg);
    const overridesBtn = el("vfs-overrides");
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

    schema.forEach((p) => {
      const wrap = document.createElement("div"),
        label = document.createElement("label");
      let input,
        value = getValueByPath(config, p.Path);

      if (p.Type === "bool") {
        wrap.innerHTML = `<label class="shoko-checkbox"><input type="checkbox">
          <span class="shoko-checkbox-icon" aria-hidden="true"><svg class="unchecked"><use href="img/icons.svg#checkbox-blank-circle-outline"></use></svg><svg class="checked"><use href="img/icons.svg#checkbox-marked-circle-outline"></use></svg></span>
          <span class="shoko-checkbox-text"><span class="shoko-checkbox-title">${p.Display || p.Path}</span><small class="shoko-checkbox-desc" style="display:block">${p.Description || ""}</small></span></label>`;
        input = wrap.querySelector("input");
        bindConfig(input, p.Path, config, saveSettings, "check");

        // UI Logic: Toggle dependent button states when TMDB Ep numbering changes
        if (overridesBtn && (p.Path === "TmdbEpNumbering" || p.Path === "Advanced.TmdbEpNumbering")) {
          input.addEventListener("change", (e) => {
            overridesBtn.disabled = !e.target.checked;
          });
        }
      } else if (p.Path.endsWith("PathMappings")) {
        label.innerHTML = `<span>${p.Display || p.Path.split(".").pop()}</span>${p.Description ? `<small>${p.Description}</small>` : ""}`;
        wrap.appendChild(label);
        const mappingContainer = document.createElement("div");
        mappingContainer.innerHTML = `<div class="full"><div><small>Plex Base Paths</small><textarea id="path-mappings-left"></textarea></div><div><small>Shoko Base Paths</small><textarea id="path-mappings-right"></textarea></div></div>`;
        wrap.appendChild(mappingContainer);
        const l = mappingContainer.querySelector("#path-mappings-left"),
          r = mappingContainer.querySelector("#path-mappings-right"),
          m = value || {};
        const keys = Object.keys(m).sort();
        l.value = keys.map((k) => m[k]).join("\n");
        r.value = keys.join("\n");
        const onMapChange = async () => {
          const val = {};
          const lLines = l.value.split("\n"),
            rLines = r.value.split("\n");
          lLines.forEach((lv, idx) => {
            if (lv.trim() && rLines[idx]?.trim()) val[rLines[idx].trim()] = lv.trim();
          });
          setValueByPath(config, p.Path, val);
          await saveSettings(config);
        };
        l.onchange = r.onchange = onMapChange;
      } else {
        label.innerHTML = `<span>${p.Display || p.Path.split(".").pop()}</span>${p.Description ? `<small>${p.Description}</small>` : ""}`;
        wrap.appendChild(label);
        input = document.createElement(p.Type === "enum" ? "select" : p.Type === "json" || p.Path.endsWith("TagBlacklist") || p.Path.endsWith("PathExclusions") ? "textarea" : "input");

        if (p.Type === "enum") {
          (p.EnumValues || []).forEach((ev) => {
            const opt = new Option(ev.name, ev.value);
            opt.selected = String(ev.value) === String(value);
            input.add(opt);
          });
        } else if (p.Type === "number") {
          input.type = "number";
        } else if (p.Type === "json") {
          input.placeholder = "JSON object";
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
        } else {
          bindConfig(input, p.Path, config, saveSettings, p.Type === "bool" ? "check" : p.Type === "number" ? "number" : "text");
        }
      }
      (p.Advanced ? advContent : el("config-form")).appendChild(wrap);
    });

    if (advContent.children.length > 1) {
      el("config-form").appendChild(advSection);
      advSection.appendChild(advContent);
      initDetailsAnimation(advSection, advContent);
    }

    const b = (id, path, type) => bindConfig(id, path, config, saveSettings, type);
    b("shoko-utc-offset", "Automation.UtcOffsetHours", "number");
    b("shoko-import-frequency", "Automation.ShokoImportFrequencyHours", "number");
    b("shoko-sync-frequency", "Automation.ShokoSyncWatchedFrequencyHours", "number");
    b("plex-auto-frequency", "Automation.PlexAutomationFrequencyHours", "number");
    b("sync-ratings", "Automation.ShokoSyncWatchedIncludeRatings", "check");
    b("sync-exclude-admin", "Automation.ShokoSyncWatchedExcludeAdmin", "check");
    b("plex-scrobble", "Automation.AutoScrobble", "check");

    window._sr.initAtConfig?.(config, saveSettings);
  }
  // #endregion

  // #region Initialization
  // Help Modal Logic
  const helpBtn = el("settings-help-open");
  if (helpBtn) {
    helpBtn.onclick = () => {
      const modal = el("settings-help-modal");
      openModal(modal);
    };
  }

  loadConfig();
  // #endregion
})();
