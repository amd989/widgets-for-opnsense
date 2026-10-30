// Shared bootstrap for the web widget pages (Widgets/<Name>/Web/index.html).
//
// Protocol with the provider (OPNsenseWidgetProvider):
//   page -> provider: {type:"ready", w, h, dpr} on load, while waiting for data, and after resizes
//                     {type:"refresh"} to fetch now
//   provider -> page: {type:"data", size:"small|medium|large", payload:{...}}
//
// Opened in a normal browser (no WebView2 bridge) a page runs on demo data instead;
// add ?size=small|medium|large, otherwise the size is guessed from the window.
(() => {
  "use strict";

  const bridge = window.chrome && window.chrome.webview;
  const preview = !bridge;
  const previewSize = preview ? new URLSearchParams(location.search).get("size") : null;

  let state = null;      // last data message
  let renderFn = null;
  let demoFn = null;

  function post(message) {
    if (bridge) bridge.postMessage(JSON.stringify(message));
  }

  function sayReady() {
    // Dimensions only go to the provider log, to confirm the real canvas sizes.
    post({ type: "ready", w: innerWidth, h: innerHeight, dpr: devicePixelRatio });
  }

  function sizeFromViewport() {
    if (innerHeight < 220) return "small";
    return innerHeight > 380 ? "large" : "medium";
  }

  function currentSize() {
    return (state && state.size) || previewSize || sizeFromViewport();
  }

  function draw(fresh) {
    if (!state || !renderFn) return;
    const size = currentSize();
    document.body.className = "size-" + size;
    renderFn(state.payload || {}, size, fresh);
  }

  function receive(message) {
    if (typeof message === "string") {
      try { message = JSON.parse(message); } catch { return; }
    }
    if (!message || message.type !== "data") return;
    state = message;
    draw(true);
  }

  function deliverDemo() {
    if (demoFn) receive({ type: "data", size: previewSize, payload: demoFn() });
  }

  /**
   * render(payload, size, fresh): fresh is true for new data, false for re-draws (resize, timers).
   * demo(): payload for browser preview. demoIntervalMs: re-generate demo data periodically.
   */
  function start({ render, demo, demoIntervalMs }) {
    renderFn = render;
    demoFn = demo;

    // A size change resizes this page instead of reloading it: re-draw now, then say "ready"
    // again once it settles so the provider resumes pushing data with the new size.
    let resizeTimer = 0;
    addEventListener("resize", () => {
      draw(false);
      clearTimeout(resizeTimer);
      resizeTimer = setTimeout(sayReady, 400);
    });

    if (bridge) {
      bridge.addEventListener("message", e => receive(e.data));
      // The provider may still be starting up (or restarting), so keep asking until data arrives.
      sayReady();
      const retry = setInterval(() => {
        if (state) clearInterval(retry);
        else sayReady();
      }, 3000);
    } else {
      document.documentElement.classList.add("preview");
      deliverDemo();
      if (demoIntervalMs) setInterval(deliverDemo, demoIntervalMs);
    }
  }

  function refresh() {
    post({ type: "refresh" });
    if (preview) setTimeout(deliverDemo, 600);
  }

  // ─── DOM helpers ───
  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  function svgEl(tag, attrs) {
    const node = document.createElementNS("http://www.w3.org/2000/svg", tag);
    if (attrs) for (const key in attrs) node.setAttribute(key, attrs[key]);
    return node;
  }

  // ─── Formatting ───
  function trimNumber(x) {
    if (x >= 100) return x.toFixed(0);
    if (x >= 10) return x.toFixed(1);
    return x.toFixed(2);
  }

  const fmt = {
    /** Bits per second split into value and unit: {value:"182", unit:"Mbps"} */
    bits(bps) {
      bps = Math.max(0, bps || 0);
      for (const [unit, div] of [["Gbps", 1e9], ["Mbps", 1e6], ["Kbps", 1e3]]) {
        if (bps >= div) return { value: trimNumber(bps / div), unit };
      }
      return { value: Math.round(bps).toString(), unit: "bps" };
    },
    bitsText(bps) {
      const b = fmt.bits(bps);
      return b.value + " " + b.unit;
    },
    bytes(n) {
      n = Math.max(0, n || 0);
      for (const [unit, div] of [["TB", 1024 ** 4], ["GB", 1024 ** 3], ["MB", 1024 ** 2], ["KB", 1024]]) {
        if (n >= div) return trimNumber(n / div) + " " + unit;
      }
      return Math.round(n) + " B";
    },
    /** 966269802 -> "966M" */
    compact(n) {
      n = n || 0;
      for (const [suffix, div] of [["T", 1e12], ["B", 1e9], ["M", 1e6], ["K", 1e3]]) {
        if (Math.abs(n) >= div) return trimNumber(n / div) + suffix;
      }
      return Math.round(n).toString();
    },
    number(n) {
      return Math.round(n || 0).toLocaleString();
    },
    time(ms) {
      return ms ? new Date(ms).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }) : "";
    },
    /** <60s "now", <60m "Xm", <24h "Xh", else "Xd" */
    relative(start, now) {
      if (start == null) return "";
      const s = Math.max(0, (now - start) / 1000);
      if (s < 60) return "now";
      if (s < 3600) return Math.floor(s / 60) + "m";
      if (s < 86400) return Math.floor(s / 3600) + "h";
      return Math.floor(s / 86400) + "d";
    }
  };

  // ─── Charts ───
  /** Smallest 1/2/5 x 10^n at or above value. */
  function niceCeil(value) {
    if (!(value > 0)) return 1;
    const pow = Math.pow(10, Math.floor(Math.log10(value)));
    for (const step of [1, 2, 5, 10]) if (value <= step * pow) return step * pow;
    return 10 * pow;
  }

  /**
   * Smooth path through points (Catmull-Rom as cubic Beziers). Control points are clamped to
   * [minY, maxY] and, horizontally, to each segment, so the curve never bends backwards when
   * points are unevenly spaced.
   */
  function smoothPath(points, minY, maxY) {
    if (points.length === 0) return "";
    const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
    let d = `M${points[0][0].toFixed(1)},${points[0][1].toFixed(1)}`;
    for (let i = 0; i < points.length - 1; i++) {
      const p0 = points[i - 1] || points[i];
      const p1 = points[i];
      const p2 = points[i + 1];
      const p3 = points[i + 2] || p2;
      const t = 0.18;
      const c1x = clamp(p1[0] + (p2[0] - p0[0]) * t, p1[0], p2[0]);
      const c2x = clamp(p2[0] - (p3[0] - p1[0]) * t, p1[0], p2[0]);
      const c1y = clamp(p1[1] + (p2[1] - p0[1]) * t, minY, maxY);
      const c2y = clamp(p2[1] - (p3[1] - p1[1]) * t, minY, maxY);
      d += ` C${c1x.toFixed(1)},${c1y.toFixed(1)} ${c2x.toFixed(1)},${c2y.toFixed(1)} ${p2[0].toFixed(1)},${p2[1].toFixed(1)}`;
    }
    return d;
  }

  window.Widget = {
    start, post, refresh, el, svgEl, fmt, niceCeil, smoothPath,
    rerender: () => draw(false),
    get size() { return currentSize(); },
    preview,
    // Browser preview only: feed a payload by hand, e.g. Widget.inject({...}) from the console.
    inject: payload => preview && receive({ type: "data", size: previewSize, payload })
  };
})();
