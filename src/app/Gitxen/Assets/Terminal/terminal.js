// SPDX-License-Identifier: GPL-3.0-or-later
// Wires xterm.js to the host. See index.html for the message protocol.
(function () {
  "use strict";

  const host = window.chrome && window.chrome.webview;
  const send = (message) => host && host.postMessage(message);

  let term = null;
  let fit = null;
  const pending = [];

  function start(options) {
    term = new Terminal({
      fontFamily: options.font,
      fontSize: options.fontSize,
      theme: options.theme,
      cursorStyle: options.cursor || "block",
      cursorBlink: true,
      scrollback: 10000,
      allowProposedApi: true,
      macOptionIsMeta: true
    });

    fit = new FitAddon.FitAddon();
    term.loadAddon(fit);
    term.open(document.getElementById("terminal"));

    term.onData((data) => send({ t: "in", d: data }));
    term.onResize(({ cols, rows }) => send({ t: "size", cols, rows }));

    // Copy with a selection, interrupt without one: what Windows Terminal does with Ctrl+C.
    term.attachCustomKeyEventHandler((e) => {
      if (e.type !== "keydown") {
        return true;
      }

      const ctrl = e.ctrlKey && !e.altKey && !e.metaKey;

      if (ctrl && (e.key === "c" || e.key === "C") && term.hasSelection()) {
        send({ t: "copy", d: term.getSelection() });
        term.clearSelection();
        return false;
      }

      if (ctrl && (e.key === "v" || e.key === "V")) {
        send({ t: "paste" });
        e.preventDefault();
        return false;
      }

      if (ctrl && e.key === "`") {
        send({ t: "hide" });
        return false;
      }

      return true;
    });

    // Right-click copies a selection or pastes, as in Windows Terminal.
    document.addEventListener("contextmenu", (e) => {
      e.preventDefault();

      if (term.hasSelection()) {
        send({ t: "copy", d: term.getSelection() });
        term.clearSelection();
      } else {
        send({ t: "paste" });
      }
    });

    new ResizeObserver(() => {
      try {
        fit.fit();
      } catch (_) {
        // Not laid out yet; the next resize fits it.
      }
    }).observe(document.getElementById("terminal"));

    fit.fit();
    send({ t: "size", cols: term.cols, rows: term.rows });

    for (const chunk of pending.splice(0)) {
      term.write(chunk);
    }

    term.focus();
  }

  if (host) {
    host.addEventListener("message", (event) => {
      const m = event.data;

      switch (m && m.t) {
        case "init":
          if (!term) {
            start(m);
          }
          break;
        case "out":
          if (term) {
            term.write(m.d);
          } else {
            pending.push(m.d);
          }
          break;
        case "paste":
          if (term && m.d) {
            term.paste(m.d);
          }
          break;
        case "focus":
          if (term) {
            term.focus();
          }
          break;
      }
    });
  }

  send({ t: "ready" });
})();
