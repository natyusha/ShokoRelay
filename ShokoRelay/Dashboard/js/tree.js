/**
 * @file tree.js
 * @description Shared utilities for tree-based UI components including search interactions and debouncing.
 */
(() => {
  /**
   * Prevents rapid, consecutive execution of expensive callback operations by introducing a time buffer.
   * @param {Function} fn - The target callback function to debounce.
   * @param {number} ms - The debounce cooldown delay in milliseconds.
   * @returns {Function} A debounced wrapper function.
   */
  window._sr.debounce = (fn, ms) => {
    let timeout;
    return (...args) => {
      clearTimeout(timeout);
      timeout = setTimeout(() => fn.apply(this, args), ms);
    };
  };

  /**
   * Attaches real-time search, clear actions, and slash hotkey bindings to a target filter element.
   * @param {HTMLInputElement} input - The text input element serving as the filter.
   * @param {HTMLElement} clearBtn - The button used to clear input text.
   * @param {Function} renderFn - The draw callback invoked when filters change.
   * @returns {void}
   */
  window._sr.initSearchInteractions = (input, clearBtn, renderFn) => {
    if (!input) return;
    const debouncedRender = window._sr.debounce(renderFn, 250);

    input.oninput = () => {
      if (clearBtn) clearBtn.hidden = !input.value;
      debouncedRender();
    };

    input.onkeydown = (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        input.blur();
      } else if (e.key === "Escape") {
        e.preventDefault();
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
    window.addEventListener("keydown", (e) => {
      if (document.activeElement.tagName === "INPUT" || document.activeElement.tagName === "TEXTAREA") return;
      if (e.key === "/") {
        e.preventDefault();
        input.focus();
      }
    });
  };
})();
