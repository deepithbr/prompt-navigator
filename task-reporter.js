/*
 * Tells the Prompt Advisor task board on this PC when a Claude or ChatGPT
 * reply starts and finishes in this tab.
 *
 * A reply is running while the site shows its Stop button, and finished when
 * the button goes. Both sites redraw the page as a reply streams in, so a
 * MutationObserver sees the button go even in a background tab, where Chrome
 * slows ordinary timers to once a minute. A slow timer stays as a fallback.
 *
 * Claude Code on the web is the exception: it stops redrawing a tab you are
 * not looking at, so its Stop button can stay up long after the agent has
 * finished. On a Claude Code page, while a run is open, the script asks
 * claude.ai itself every 20 seconds (once a minute in a background tab)
 * whether the agent behind the page is still working, and reports the
 * finish, or a request for approval, from that.
 *
 * It sends the tab's title, address and the states, nothing from the
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

  // Claude Code on the web: which agent a page belongs to, and its state.
  const API = { credentials: 'include', headers: { 'anthropic-version': '2023-06-01' } };
  const ASK_MS = 20000;
  const agentOf = {};      // project (chan_...) -> its agent's id
  let lastAsk = 0;
  let asking = false;
  // Reported finished from claude.ai while the page still shows Stop: no
  // new run starts until the page catches up and the button goes.
  let stale = false;

  function onCodePage() {
    return SITE === 'claude-web' && location.pathname.split('/')[1] === 'code';
  }

  async function agentId() {
    const parts = location.pathname.split('/');
    const own = parts.find((p) => /^(cse|session)_[A-Za-z0-9]+$/.test(p));
    if (own) return own;
    const i = parts.indexOf('project');
    const chan = i > 0 ? parts[i + 1] : '';
    if (!/^chan_[A-Za-z0-9]+$/.test(chan)) return null;
    if (agentOf[chan]) return agentOf[chan];
    const r = await fetch('/v1/code/channels?scope=joined&limit=200', API);
    if (!r.ok) return null;
    const found = ((await r.json()).data || []).find((c) => c.id === chan);
    return (agentOf[chan] = (found && found.channel_session_id) || null);
  }

  // 'working', 'done' or 'needs', by claude.ai's account; null when unsure.
  async function agentState(since) {
    const id = await agentId();
    if (!id) return null;
    const r = await fetch('/v1/code/sessions/' + encodeURIComponent(id), API);
    if (!r.ok) return null;
    const j = await r.json();
    const a = j.response_shape || j.session || j;
    if ((a.requires_action_details_list || []).length) return 'needs';
    const last = Date.parse(a.last_event_at || '') || 0;
    // Idle, with something new since this run began: the agent is done.
    return a.worker_status === 'idle' && last > since ? 'done' : 'working';
  }

  function askAgent(now) {
    if (asking || now - lastAsk < ASK_MS) return;
    lastAsk = now;
    asking = true;
    const mine = run;
    agentState(mine.since)
      .then((state) => {
        if (run !== mine || !state || state === (mine.told || 'working')) return;
        // Back to work after a request for approval, or done, or waiting.
        mine.told = state === 'working' ? null : state;
        send(state);
        if (state === 'done') { run = null; stale = true; }
      })
      .catch(() => {})
      .then(() => { asking = false; });
  }

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
    if (stale) {
      if (on) return;
      stale = false;
    }
    if (on && !run) {
      run = { id: SITE + ':' + now + ':' + Math.random().toString(36).slice(2, 7), since: now - 10000, code: onCodePage() };
      lastBeat = now;
      send('working');
    } else if (on && now - lastBeat > HEARTBEAT_MS && !run.told) {
      // The board marks a tab that goes silent as stopped reporting. While
      // the agent waits on you, the request already stands.
      lastBeat = now;
      send('working');
    } else if (!on && run) {
      send('done');
      run = null;
    }
    if (on && run && run.code) askAgent(now);
  }

  new MutationObserver(check).observe(document.documentElement, {
    childList: true, subtree: true, attributes: true, attributeFilter: ['aria-label', 'data-testid'],
  });
  setInterval(check, 2000);
})();
