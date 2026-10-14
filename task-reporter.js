/*
 * Tells the Prompt Advisor task board on this PC when a Claude or ChatGPT
 * reply starts and finishes in this tab.
 *
 * A reply is running while the site shows its Stop button, and finished when
 * the button goes. Both sites redraw the page as a reply streams in, so a
 * MutationObserver sees the button go even in a background tab, where Chrome
 * slows ordinary timers to once a minute. A slow timer stays as a fallback.
 *
 * It sends the tab's title, address and the two states, nothing from the
 * conversation itself, to the extension's background script, which passes it
 * to 127.0.0.1. If the board is not running, nothing happens.
 */
(() => {
  'use strict';

  const SITE = /chatgpt\.com|chat\.openai\.com/.test(location.host) ? 'chatgpt-web' : 'claude-web';
  const STOP = /^stop( response| generating| streaming| answering)?$/i;
  const HEARTBEAT_MS = 30000;

  let run = null;
  let lastBeat = 0;
  let lastCheck = 0;

  function stopVisible() {
    const buttons = document.querySelectorAll('button[aria-label], button[data-testid="stop-button"]');
    for (const b of buttons) {
      const label = (b.getAttribute('aria-label') || '').trim();
      if (b.dataset.testid !== 'stop-button' && !STOP.test(label)) continue;
      if (b.getClientRects().length) return true;
    }
    return false;
  }

  function title() {
    const t = document.title.replace(/\s*[-|–]\s*(Claude|ChatGPT)\s*$/i, '').trim();
    return t || (SITE === 'claude-web' ? 'Claude' : 'ChatGPT');
  }

  function send(state) {
    try {
      chrome.runtime.sendMessage({
        type: 'task',
        body: {
          source: SITE,
          id: run.id,
          title: title(),
          state,
          url: location.href,
          // Looking at this tab right now: the board stays quiet.
          visible: document.visibilityState === 'visible' && document.hasFocus(),
        },
      });
    } catch (e) { /* extension reloaded underneath this tab */ }
  }

  function check() {
    const now = Date.now();
    if (now - lastCheck < 400) return;
    lastCheck = now;
    const on = stopVisible();
    if (on && !run) {
      run = { id: SITE + ':' + now + ':' + Math.random().toString(36).slice(2, 7) };
      lastBeat = now;
      send('working');
    } else if (on && now - lastBeat > HEARTBEAT_MS) {
      // The board marks a tab that goes silent as stopped reporting.
      lastBeat = now;
      send('working');
    } else if (!on && run) {
      send('done');
      run = null;
    }
  }

  new MutationObserver(check).observe(document.documentElement, {
    childList: true, subtree: true, attributes: true, attributeFilter: ['aria-label', 'data-testid'],
  });
  setInterval(check, 2000);
})();
