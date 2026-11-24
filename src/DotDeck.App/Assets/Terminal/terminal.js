// DotDeck terminal page: xterm.js bridged to the host (TerminalView) through WebView2 web messages.
// Host -> page: init {theme, fontFamily, fontSize, replay}, output {d}, theme {...}, notice {d}, stdin {enabled}, focus,
//   screen {id} (read the screen lines), probe {id, c, r, replay} (render recent output of a hidden terminal and read it).
// Page -> host: ready {c, r}, input {d}, binary {d}, resize {c, r}, copy {d}, screen {id, d}.
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
  // A probe page renders other terminals' output at their size: it never fits itself to the window.
  let probing = false;
  const refit = () => {
    if (probing) {
      return;
    }

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

  // The current screen (not the scrollback), top to bottom, after everything written so far has been parsed.
  const postScreen = (id) => {
    term.write('', () => {
      const buffer = term.buffer.active;
      const lines = [];
      for (let y = buffer.baseY; y < buffer.baseY + term.rows; y++) {
        const line = buffer.getLine(y);
        lines.push(line ? line.translateToString(true) : '');
      }
      post({ t: 'screen', id: typeof id === 'number' ? id : 0, d: lines.join('\n') });
    });
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
      case 'screen':
        postScreen(m.id);
        break;
      case 'probe':
        probing = true;
        term.options.disableStdin = true;
        term.reset();
        if (m.c > 0 && m.r > 0) {
          term.resize(m.c, m.r);
        }
        term.write(m.replay || '');
        postScreen(m.id);
        break;
    }
  });

  refit();
  post({ t: 'ready', c: term.cols, r: term.rows });
})();
