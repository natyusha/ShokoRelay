/**
 * @file tree.js
 * @description Shared utilities for tree-based UI components including search interactions, debouncing, and lazy loading.
 */
(() => {
  /**
   * Prevents rapid execution of expensive functions like tree rendering.
   * @param {Function} fn - The function to debounce.
   * @param {number} ms - Delay in milliseconds.
   * @returns {Function}
   */
  window._sr.debounce = (fn, ms) => {
    let timeout;
    return (...args) => {
      clearTimeout(timeout);
      timeout = setTimeout(() => fn.apply(this, args), ms);
    };
  };

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

  /**
   * Constructs a standard folder details node with deferred child rendering, wrapping the title in a flex-safe span.
   * @param {string} name - The display text or HTML for the folder summary.
   * @param {HTMLUListElement} ul - The container element to append items to.
   * @param {Function} renderFn - The callback function that generates and appends children.
   * @param {boolean} isOpen - If true, forces children to render immediately.
   * @returns {HTMLDetailsElement} The completed folder details node.
   */
  window._sr.createLazyDetails = (name, ul, renderFn, isOpen) => {
    const det = document.createElement("details");
    const sum = document.createElement("summary");
    det.open = isOpen;
    sum.title = name.replace(/<[^>]*>/g, ""); // strip HTML tags for tooltip title attribute
    sum.dataset.tooltipOverflowOnly = "true";
    const titleSpan = name ? `<span class="vfs-title" data-tooltip-overflow-only="true">${name}</span>` : "";
    sum.innerHTML = `<span class="tree-icon expand"></span><span class="tree-icon collapse"></span>${titleSpan}`;
    det.appendChild(sum);

    if (isOpen) {
      renderFn(ul);
      det.appendChild(ul);
    } else {
      det.ontoggle = () => {
        if (det.open && !det.dataset.rendered) {
          det.dataset.rendered = "true";
          renderFn(ul);
          det.appendChild(ul);
        }
      };
    }
    return det;
  };
})();
