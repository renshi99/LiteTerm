(() => {
  'use strict';

  const terminal = new Terminal({
    cursorBlink: true,
    cursorStyle: 'block',
    fontFamily: 'Cascadia Mono, Consolas, monospace',
    fontSize: 14,
    lineHeight: 1.15,
    scrollback: 5000,
    convertEol: false,
    allowProposedApi: false,
    theme: {
      background: '#0b1020',
      foreground: '#d7e0ea',
      cursor: '#93c5fd',
      selectionBackground: '#334155'
    }
  });

  const fitAddon = new FitAddon.FitAddon();
  const decoder = new TextDecoder('utf-8');
  terminal.loadAddon(fitAddon);
  terminal.open(document.getElementById('terminal'));

  let resizeFrame = 0;
  const fit = () => {
    cancelAnimationFrame(resizeFrame);
    resizeFrame = requestAnimationFrame(() => {
      fitAddon.fit();
      window.chrome.webview.postMessage({ type: 'resize', columns: terminal.cols, rows: terminal.rows });
    });
  };

  terminal.onData(data => window.chrome.webview.postMessage({ type: 'input', data }));

  window.chrome.webview.addEventListener('message', event => {
    const message = event.data;
    if (!message || message.type !== 'output' || typeof message.data !== 'string') return;

    const binary = atob(message.data);
    const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
    terminal.write(decoder.decode(bytes, { stream: true }));
  });

  new ResizeObserver(fit).observe(document.getElementById('terminal'));
  fit();
  terminal.focus();
  window.chrome.webview.postMessage({ type: 'ready' });
})();
