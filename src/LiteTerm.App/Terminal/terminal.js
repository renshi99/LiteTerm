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
  const terminalElement = document.getElementById('terminal');
  const searchElement = document.getElementById('terminal-search');
  const searchInput = document.getElementById('terminal-search-input');
  const searchStatus = document.getElementById('terminal-search-status');
  const menuElement = document.getElementById('terminal-menu');
  const copyButton = document.getElementById('terminal-copy');
  let lastSearchLine = -1;
  terminal.loadAddon(fitAddon);
  terminal.open(terminalElement);

  let resizeFrame = 0;
  const fit = () => {
    cancelAnimationFrame(resizeFrame);
    resizeFrame = requestAnimationFrame(() => {
      fitAddon.fit();
      window.chrome.webview.postMessage({ type: 'resize', columns: terminal.cols, rows: terminal.rows });
    });
  };

  terminal.onData(data => window.chrome.webview.postMessage({ type: 'input', data }));

  const hideMenu = () => { menuElement.hidden = true; };
  const closeSearch = () => {
    searchElement.hidden = true;
    searchStatus.textContent = '';
    terminal.clearSelection();
    terminal.focus();
  };

  const find = direction => {
    const query = searchInput.value.trim();
    if (!query) {
      searchStatus.textContent = '请输入内容';
      return;
    }

    const buffer = terminal.buffer.active;
    const normalizedQuery = query.toLocaleLowerCase();
    const lineCount = buffer.length;
    const start = lastSearchLine < 0
      ? (direction > 0 ? 0 : lineCount - 1)
      : (lastSearchLine + direction + lineCount) % lineCount;

    for (let offset = 0; offset < lineCount; offset += 1) {
      const lineIndex = (start + (offset * direction) + lineCount) % lineCount;
      const line = buffer.getLine(lineIndex);
      if (!line || !line.translateToString(true).toLocaleLowerCase().includes(normalizedQuery)) continue;

      lastSearchLine = lineIndex;
      terminal.scrollToLine(lineIndex);
      terminal.select(0, lineIndex, terminal.cols);
      searchStatus.textContent = `第 ${lineIndex + 1} 行`;
      return;
    }

    lastSearchLine = -1;
    terminal.clearSelection();
    searchStatus.textContent = '未找到';
  };

  const openSearch = () => {
    hideMenu();
    searchElement.hidden = false;
    searchInput.focus();
    searchInput.select();
  };

  document.getElementById('terminal-search-next').addEventListener('click', () => find(1));
  document.getElementById('terminal-search-previous').addEventListener('click', () => find(-1));
  document.getElementById('terminal-search-close').addEventListener('click', closeSearch);
  searchInput.addEventListener('input', () => { lastSearchLine = -1; });
  searchInput.addEventListener('keydown', event => {
    if (event.key === 'Enter') {
      event.preventDefault();
      find(event.shiftKey ? -1 : 1);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      closeSearch();
    }
  });

  terminalElement.addEventListener('contextmenu', event => {
    event.preventDefault();
    copyButton.disabled = !terminal.hasSelection();
    menuElement.style.left = `${event.offsetX}px`;
    menuElement.style.top = `${event.offsetY}px`;
    menuElement.hidden = false;
  });

  copyButton.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(terminal.getSelection());
    } catch {
      // Clipboard permissions can be denied by the embedded browser.
    }
    hideMenu();
    terminal.focus();
  });

  document.getElementById('terminal-paste').addEventListener('click', async () => {
    try {
      const text = await navigator.clipboard.readText();
      if (text) terminal.paste(text);
    } catch {
      // Clipboard permissions can be denied by the embedded browser.
    }
    hideMenu();
    terminal.focus();
  });

  document.getElementById('terminal-clear').addEventListener('click', () => {
    terminal.clear();
    hideMenu();
    terminal.focus();
  });

  document.addEventListener('pointerdown', event => {
    if (!menuElement.contains(event.target)) hideMenu();
  });
  document.addEventListener('keydown', event => {
    if (event.ctrlKey && event.key.toLowerCase() === 'f') {
      event.preventDefault();
      openSearch();
    } else if (event.key === 'Escape' && !searchElement.hidden) {
      event.preventDefault();
      closeSearch();
    }
  });

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
