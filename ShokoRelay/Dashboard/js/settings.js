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
      const parent = elem.closest("label, .w100, .dsbld-wrap") || elem.parentElement;

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

  // #region Subtitle Rules
  /**
   * Builds an ordered two-column editor that automatically saves valid changes and previews a series' sidecars.
   * @param {HTMLElement} wrap - Setting container.
   * @param {Object} property - Server configuration schema entry.
   * @param {Object} config - Shared saved configuration.
   * @returns {void}
   */
  function buildSubtitleRules(wrap, property, config) {
    const path = property.Path;
    const rules = (getValueByPath(config, path) || []).map((rule) => ({ ...rule }));
    let savedRules = rules.map((rule) => ({ ...rule }));
    wrap.classList.add("subtitle-rules");
    wrap.innerHTML = `<label><span>${property.Display}</span><small>${property.Description}</small></label>
      <div class="subtitle-rule-head"><span>Original suffix</span><span>Final suffix</span><span>Order / Remove</span></div>
      <div class="subtitle-rule-list"></div>
      <div class="full"><button type="button" class="subtitle-rule-add">Add rule</button><button type="button" class="subtitle-rule-retry" hidden>Retry saving</button></div>
      <small>Enter suffixes without surrounding dots, for example chs → zh-Hans. Valid changes save automatically when you leave a field; reordering and removal save immediately.</small>
      <small class="subtitle-rule-status" role="status"></small>
      <details><summary>Preview a series</summary>
        <p>Inspect these rules on a Shoko series before refreshing the VFS. The preview shows the suffix after the VFS video name.</p>
        <div class="full"><input type="number" min="1" max="2147483647" step="1" placeholder="Shoko series ID" aria-label="Shoko series ID"><button type="button" class="subtitle-rule-preview">Preview</button></div>
        <div class="subtitle-preview-results" aria-live="polite"></div>
      </details>`;
    const list = wrap.querySelector(".subtitle-rule-list");
    const retry = wrap.querySelector(".subtitle-rule-retry");
    const status = wrap.querySelector(".subtitle-rule-status");
    const preview = wrap.querySelector(".subtitle-rule-preview");
    const results = wrap.querySelector(".subtitle-preview-results");
    const seriesInput = wrap.querySelector('input[type="number"]');
    let revision = 0;
    let editRevision = 0;
    let saveRevision = 0;

    seriesInput.oninput = () => {
      revision++;
      results.replaceChildren();
    };

    const changed = () => {
      revision++;
      editRevision++;
      retry.hidden = true;
      status.textContent = "Finish editing both suffixes, then leave the field to save automatically.";
      results.replaceChildren();
    };

    const render = () => {
      list.replaceChildren();
      if (!rules.length) {
        const empty = document.createElement("p");
        empty.className = "placeholder";
        empty.textContent = "No rules. Subtitle names will be kept.";
        list.appendChild(empty);
      }
      rules.forEach((rule, index) => {
        const row = document.createElement("div");
        row.className = "subtitle-rule-row";
        ["OriginalSuffix", "FinalSuffix"].forEach((key) => {
          const input = document.createElement("input");
          input.type = "text";
          input.placeholder = key === "OriginalSuffix" ? "Original suffix" : "Final suffix";
          input.setAttribute("aria-label", `${input.placeholder}, rule ${index + 1}`);
          input.autocomplete = "off";
          input.spellcheck = false;
          bindConfig(input, key, rule, persistRules);
          input.oninput = () => {
            rule[key] = input.value;
            input.setCustomValidity("");
            input.removeAttribute("aria-invalid");
            changed();
          };
          row.appendChild(input);
        });
        const actions = document.createElement("div");
        actions.className = "subtitle-rule-actions";
        [
          ["↑", "Move up", -1],
          ["↓", "Move down", 1],
          ["×", "Remove", 0],
        ].forEach(([text, label, direction]) => {
          const button = document.createElement("button");
          button.type = "button";
          button.textContent = text;
          button.setAttribute("aria-label", `${label}, rule ${index + 1}`);
          button.title = `${label}, rule ${index + 1}`;
          button.disabled = (direction === -1 && index === 0) || (direction === 1 && index === rules.length - 1);
          button.onclick = () => {
            if (direction) [rules[index], rules[index + direction]] = [rules[index + direction], rules[index]];
            else rules.splice(index, 1);
            changed();
            render();
            list.querySelectorAll(".subtitle-rule-row")[Math.min(index + direction, rules.length - 1)]?.querySelector("input")?.focus();
            void persistRules();
          };
          actions.appendChild(button);
        });
        row.appendChild(actions);
        list.appendChild(row);
      });
    };

    const readRules = (report = false) => {
      const normalized = [];
      for (const [index, rule] of rules.entries()) {
        const original = rule.OriginalSuffix.trim();
        const final = rule.FinalSuffix.trim();
        if (!original && !final) continue;
        for (const [column, value] of [original, final].entries()) {
          if (!value || value.startsWith(".") || value.endsWith(".") || /[<>:"/\\|?*\u0000-\u001f\u007f-\u009f]/.test(value)) {
            const input = list.querySelectorAll(".subtitle-rule-row")[index].querySelectorAll("input")[column];
            const message = value ? "Use suffixes without surrounding dots, path separators, or invalid filename characters." : "Complete both suffixes.";
            input.setCustomValidity(message);
            input.setAttribute("aria-invalid", "true");
            status.textContent = `Rule ${index + 1}: ${message} These edits have not been saved.`;
            if (report) input.reportValidity();
            return null;
          }
        }
        normalized.push({ OriginalSuffix: original, FinalSuffix: final });
      }
      return normalized;
    };

    wrap.querySelector(".subtitle-rule-add").onclick = () => {
      rules.push({ OriginalSuffix: "", FinalSuffix: "" });
      changed();
      render();
      list.lastElementChild.querySelector("input").focus();
    };
    const persistRules = async () => {
      const normalized = readRules();
      if (!normalized) return;
      const serialized = JSON.stringify(normalized);
      if (serialized === JSON.stringify(getValueByPath(config, path) || [])) {
        status.textContent = serialized === JSON.stringify(savedRules) ? "Rules saved. Refresh the VFS to apply changes." : "Saving rules…";
        return;
      }
      const requestedEdit = editRevision;
      const requestedSave = ++saveRevision;
      status.textContent = "Saving rules…";
      retry.hidden = true;
      setValueByPath(config, path, normalized);
      try {
        const res = await saveSettings(config);
        if (res.ok) {
          savedRules = normalized;
          if (requestedEdit === editRevision) status.textContent = "Rules saved. Refresh the VFS to apply changes.";
        } else if (requestedSave === saveRevision) saveFailed();
      } catch {
        if (requestedSave === saveRevision) saveFailed();
      }

      function saveFailed() {
        setValueByPath(config, path, savedRules);
        status.textContent = "Could not save rules. Your edits are kept here; retry saving.";
        retry.hidden = false;
      }
    };
    retry.onclick = () => void persistRules();
    preview.onclick = async () => {
      const normalized = readRules(true);
      if (!normalized) return;
      if (!seriesInput.value || !seriesInput.reportValidity()) {
        seriesInput.focus();
        return;
      }
      const requestedRevision = revision;
      preview.disabled = true;
      results.textContent = "Reading subtitles…";
      try {
        const res = await fetchJson(window._sr.base + "/vfs/subtitles/preview", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ SeriesId: Number(seriesInput.value), Rules: normalized }),
        });
        if (requestedRevision !== revision) return;
        results.replaceChildren();
        if (!res.ok) {
          results.textContent = res.data?.message || "Unable to preview this series.";
          return;
        }
        const files = res.data.files || [];
        const summary = document.createElement("p");
        summary.textContent = files.length ? `${files.filter((file) => file.suffix).length} subtitle links planned. Sources are unchanged.` : "No eligible subtitles found for this series.";
        results.appendChild(summary);
        if (files.length) {
          const table = document.createElement("table");
          table.innerHTML = "<thead><tr><th>Source subtitle</th><th>VFS suffix</th><th>Selection</th></tr></thead>";
          const body = table.createTBody();
          files.forEach((file) => {
            const row = body.insertRow();
            [file.source, file.suffix || "—", file.reason].forEach((text) => {
              row.insertCell().textContent = text;
            });
          });
          results.appendChild(table);
        }
        (res.data.errors || []).forEach((error) => {
          const message = document.createElement("p");
          message.textContent = error;
          results.appendChild(message);
        });
      } finally {
        preview.disabled = false;
      }
    };
    render();
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

      if (p.Type === "subtitleRules") {
        buildSubtitleRules(wrap, p, config);
      } else if (p.Path.endsWith("SelectedTheme")) {
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
        mappingContainer.innerHTML = `<div class="full"><div><small>Working Base Paths</small><textarea id="path-mappings-left"></textarea></div><div><small>Shoko Base Paths</small><textarea id="path-mappings-right"></textarea></div></div>`;
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
    for (const [id, path] of Object.entries(autoMap)) {
      b(id, "Automation." + path, "number");
      const inputEl = el(id);
      if (inputEl) {
        inputEl.oninput = () => {
          inputEl.value = inputEl.value.replace(id === "shoko-utc-offset" ? /[^-0-9]/g : /[^0-9]/g, "");
        };
      }
    }

    b("sync-ratings", "Automation.ShokoSyncWatchedIncludeRatings", "check");
    b("sync-progress", "Automation.ShokoSyncWatchedIncludeProgress", "check");
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

  const logsDetails = document.querySelector("#logs-list")?.closest(".details-anim");
  const logsContent = logsDetails?.querySelector(".details-content");
  if (logsDetails && logsContent) initDetailsAnimation(logsDetails, logsContent);
  // #endregion
})();
