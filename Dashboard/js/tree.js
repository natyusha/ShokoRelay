/**
 * @file tree.js
 * @description Shared utilities for tree-based UI components including search interactions, debouncing, and lazy loading.
 */
(() => {
  // #region Helpers
  /**
   * Prevents rapid execution of expensive functions like tree rendering.
   * @param {Function} callback - The function to debounce.
   * @param {number} delayMs - Delay in milliseconds.
   * @returns {Function} A debounced wrapper around the supplied function.
   */
  window._sr.debounce = (callback, delayMs) => {
    let timeout;
    return (...args) => {
      clearTimeout(timeout);
      timeout = setTimeout(() => callback(...args), delayMs);
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

    input.onkeydown = (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        input.blur();
      } else if (event.key === "Escape") {
        event.preventDefault();
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
    window.addEventListener("keydown", (event) => {
      if (document.activeElement.tagName === "INPUT" || document.activeElement.tagName === "TEXTAREA" || document.querySelector(".modal-overlay.open")) return;
      if (event.key === "/") {
        event.preventDefault();
        input.focus();
      }
    });
  };

  /**
   * Constructs a standard folder details node with deferred child rendering, wrapping the title in a flex-safe span.
   * @param {string} name - The plain display text for the folder summary.
   * @param {HTMLUListElement} listElement - The container element to append items to.
   * @param {Function} renderFn - The callback function that generates and appends children.
   * @param {boolean} isOpen - If true, forces children to render immediately.
   * @param {string} [extraHtml=""] - Optional HTML appended after the escaped title span.
   * @returns {HTMLDetailsElement} The completed folder details node.
   */
  window._sr.createLazyDetails = (name, listElement, renderFn, isOpen, extraHtml = "") => {
    const details = document.createElement("details");
    const summary = document.createElement("summary");
    details.open = isOpen;
    summary.title = name || "";
    summary.dataset.tooltipOverflowOnly = "true";
    const titleSpan = name ? `<span class="vfs-title" data-tooltip-overflow-only="true">${window._sr.esc(name)}</span>` : "";
    summary.innerHTML = `<span class="tree-icon expand"></span><span class="tree-icon collapse"></span>${titleSpan}${extraHtml}`;
    summary.addEventListener("click", (event) => {
      if (event.target.closest(".vfs-link")) event.stopPropagation();
    });
    details.appendChild(summary);

    if (isOpen) {
      details.dataset.rendered = "true";
      renderFn(listElement);
      details.appendChild(listElement);
    } else {
      details.ontoggle = () => {
        if (details.open && !details.dataset.rendered) {
          details.dataset.rendered = "true";
          renderFn(listElement);
          details.appendChild(listElement);
        }
      };
    }
    return details;
  };
  // #endregion
})();
