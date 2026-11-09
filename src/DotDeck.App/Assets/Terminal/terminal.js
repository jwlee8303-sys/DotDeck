// DotDeck terminal page: xterm.js bridged to the host (TerminalView) through WebView2 web messages.
// Host -> page: init {theme, fontFamily, fontSize, replay}, output {d}, theme {...}, notice {d}, stdin {enabled}, focus.
// Page -> host: ready {c, r}, input {d}, binary {d}, resize {c, r}, copy {d}.
(() => {
  'use strict';

  const host = window.chrome && window.chrome.webview;
  if (!host) {
    return;
  }

  const post = (message) => host.postMessage(message);

  const term = new Terminal({
    allowProposedApi: false,
    cursorBlink: true,
    scrollback: 5000,
    convertEol: false,
    rightClickSelectsWord: false,
    fontFamily: 'Consolas, monospace',
    fontSize: 13,
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(document.getElementById('terminal'));

  let lastCols = 0;
  let lastRows = 0;
  const refit = () => {
    try {
      fit.fit();
    } catch (e) {
      // Not laid out yet (hidden pane).
    }

    if (term.cols !== lastCols || term.rows !== lastRows) {
      lastCols = term.cols;
      lastRows = term.rows;
      post({ t: 'resize', c: term.cols, r: term.rows });
    }
  };

  let pending = 0;
  new ResizeObserver(() => {
    cancelAnimationFrame(pending);
    pending = requestAnimationFrame(refit);
  }).observe(document.body);

  term.onData((d) => post({ t: 'input', d }));
  term.onBinary((d) => post({ t: 'binary', d }));

  term.attachCustomKeyEventHandler((e) => {
    if (e.type !== 'keydown') {
      return true;
    }

    const ctrlOnly = e.ctrlKey && !e.altKey && !e.metaKey;
    const key = e.key.toLowerCase();

    // Ctrl+C copies when there is a selection, otherwise it is the interrupt key. Ctrl+Shift+C always copies.
    if (ctrlOnly && key === 'c' && (e.shiftKey || term.hasSelection())) {
      if (term.hasSelection()) {
        post({ t: 'copy', d: term.getSelection() });
        term.clearSelection();
      }
      e.preventDefault();
      return false;
    }

    // Ctrl+V / Ctrl+Shift+V / Shift+Insert: let the browser raise its paste event (xterm pastes it, bracketed
    // when the program asked for it) instead of sending ^V.
    if ((ctrlOnly && key === 'v') || (e.shiftKey && !e.ctrlKey && e.key === 'Insert')) {
      return false;
    }

    return true;
  });

  const applyAppearance = (m) => {
    if (m.theme) {
      term.options.theme = m.theme;
      document.documentElement.style.setProperty('--terminal-background', m.theme.background || 'transparent');
    }
    if (m.fontFamily) {
      term.options.fontFamily = m.fontFamily;
    }
    if (m.fontSize) {
      term.options.fontSize = m.fontSize;
    }
    refit();
  };

  host.addEventListener('message', (event) => {
    const m = event.data;
    if (!m || typeof m.t !== 'string') {
      return;
    }

    switch (m.t) {
      case 'init':
        applyAppearance(m);
        term.reset();
        if (m.replay) {
          term.write(m.replay);
        }
        term.focus();
        break;
      case 'output':
        term.write(m.d);
        break;
      case 'theme':
        applyAppearance(m);
        break;
      case 'notice':
        term.write(m.d);
        break;
      case 'stdin':
        term.options.disableStdin = !m.enabled;
        term.options.cursorBlink = !!m.enabled;
        break;
      case 'focus':
        term.focus();
        break;
    }
  });

  refit();
  post({ t: 'ready', c: term.cols, r: term.rows });
})();
