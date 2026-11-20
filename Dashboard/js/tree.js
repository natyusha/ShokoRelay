/**
 * @file tree.js
 * @description Shared utilities for tree-based UI components including search interactions, debouncing, and lazy loading.
 */
(() => {
  // #region Helpers
  /**
   * Prevents rapid execution of expensive functions like tree rendering.
   * @param {Function} fn - The function to debounce.
   * @param {number} ms - Delay in milliseconds.
   * @returns {Function} A debounced wrapper around the supplied function.
   */
  window._sr.debounce = (fn, ms) => {
    let timeout;
    return (...args) => {
      clearTimeout(timeout);
      timeout = setTimeout(() => fn(...args), ms);
    };
  };
  // #endregion

  // #region Search & Tree
  /**
   * Initializes common search bar interactions including global hotkeys and performance-optimized rendering.
   * @param {HTMLInputElement} input - The filter input element.
   * @param {HTMLElement} clearBtn - The button to clear the input.
   * @param {Function} renderFn - The function to call when the filter changes.
   * @returns {void}
   */
  window._sr.initSearchInteractions = (input, clearBtn, renderFn) => {
    if (!input) return;
    const debouncedRender = window._sr.debounce(renderFn, 250);

    // Set the initial visibility of the clear button on page load (handles browser form autofill/recovery)
    if (clearBtn) clearBtn.hidden = !input.value;

    input.oninput = () => {
      if (clearBtn) clearBtn.hidden = !input.value;
      debouncedRender();
    };

    input.onkeydown = (ev) => {
      if (ev.key === "Enter") {
        ev.preventDefault();
        input.blur();
      } else if (ev.key === "Escape") {
        ev.preventDefault();
        input.value = "";
        if (clearBtn) clearBtn.hidden = true;
        renderFn();
        input.blur();
      }
    };

    if (clearBtn) {
      clearBtn.onclick = () => {
        input.value = "";
        clearBtn.hidden = true;
        input.focus();
        setTimeout(renderFn, 10); // Defer to reduce perceived lag during input focus
      };
    }

    // Global Key Listener for search focus (shared across browser and player)
    window.addEventListener("keydown", (ev) => {
      if (document.activeElement.tagName === "INPUT" || document.activeElement.tagName === "TEXTAREA" || document.querySelector(".modal-overlay.open")) return;
      if (ev.key === "/") {
        ev.preventDefault();
        input.focus();
      }
    });
  };

  /**
   * Constructs a standard folder details node with deferred child rendering, wrapping the title in a flex-safe span.
   * @param {string} name - The plain display text for the folder summary.
   * @param {HTMLUListElement} listEl - The container element to append items to.
   * @param {Function} renderFn - The callback function that generates and appends children.
   * @param {boolean} isOpen - If true, forces children to render immediately.
   * @param {string} [extraHtml=""] - Optional HTML appended after the escaped title span.
   * @returns {HTMLDetailsElement} The completed folder details node.
   */
  window._sr.createLazyDetails = (name, listEl, renderFn, isOpen, extraHtml = "") => {
    const details = document.createElement("details");
    const summary = document.createElement("summary");
    details.open = isOpen;
    summary.title = name || "";
    summary.dataset.tooltipOverflowOnly = "true";
    const titleSpan = name ? `<span class="vfs-title" data-tooltip-overflow-only="true">${window._sr.esc(name)}</span>` : "";
    summary.innerHTML = `<span class="tree-icon expand"></span><span class="tree-icon collapse"></span>${titleSpan}${extraHtml}`;
    summary.addEventListener("click", (ev) => {
      if (ev.target.closest(".vfs-link")) ev.stopPropagation();
    });
    details.appendChild(summary);

    if (isOpen) {
      details.dataset.rendered = "true";
      renderFn(listEl);
      details.appendChild(listEl);
    } else {
      details.ontoggle = () => {
        if (details.open && !details.dataset.rendered) {
          details.dataset.rendered = "true";
          renderFn(listEl);
          details.appendChild(listEl);
        }
      };
    }
    return details;
  };
  // #endregion
})();
