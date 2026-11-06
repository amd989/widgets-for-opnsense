// Live demo board: the shipped widget pages in demo mode, with size and theme switches.
(() => {
  "use strict";

  const board = document.querySelector(".board");
  const tiles = [...board.querySelectorAll(".tile")];
  const state = { size: board.dataset.size, theme: board.dataset.theme };

  function render() {
    board.dataset.size = state.size;
    board.dataset.theme = state.theme;
    for (const tile of tiles) {
      const id = tile.dataset.widget;
      const title = tile.dataset.title;
      // Same path layout as the app: widgets/<Name>/Web/index.html loads ../../Shared/Web/widget.js
      const src = `widgets/${id}/Web/index.html?size=${state.size}&theme=${state.theme}`;
      let frame = tile.querySelector("iframe");
      if (!tile.querySelector(".titlebar")) {
        tile.insertAdjacentHTML("afterbegin",
          `<div class="titlebar"><span class="name"><img src="assets/logo.svg" alt=""><span>${title}</span></span>` +
          `<span class="more">&middot;&middot;&middot;</span></div>`);
      }
      if (!frame) {
        frame = document.createElement("iframe");
        frame.title = `${title} widget, live demo with sample data`;
        frame.loading = "lazy";
        tile.appendChild(frame);
      }
      if (frame.getAttribute("src") !== src) frame.setAttribute("src", src);
    }
  }

  for (const group of document.querySelectorAll(".seg")) {
    group.addEventListener("click", (e) => {
      const btn = e.target.closest("button");
      if (!btn) return;
      for (const b of group.querySelectorAll("button")) b.setAttribute("aria-checked", String(b === btn));
      if (btn.dataset.size) state.size = btn.dataset.size;
      if (btn.dataset.theme) state.theme = btn.dataset.theme;
      render();
    });
  }

  render();

  // Reveal sections as they scroll in
  const io = "IntersectionObserver" in window && new IntersectionObserver((entries) => {
    for (const en of entries) if (en.isIntersecting) { en.target.classList.add("in"); io.unobserve(en.target); }
  }, { rootMargin: "0px 0px -10% 0px" });
  for (const el of document.querySelectorAll(".reveal-on-scroll")) io ? io.observe(el) : el.classList.add("in");
})();
