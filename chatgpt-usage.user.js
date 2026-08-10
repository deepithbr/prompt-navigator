// ==UserScript==
// @name         ChatGPT Usage Meter
// @namespace    local.deepith
// @version      1.2.0
// @description  Shows your ChatGPT plan usage as a bar with a window-elapsed marker, the same reading as the Claude Prompt Navigator header. Companion script — ChatGPT is a different origin, so this cannot live inside the claude.ai one.
// @author       deepith
// @copyright    2026 Deepith Kundar. All rights reserved. Personal use only —
//               see LICENSE. Not open source, not for redistribution.
// @match        https://chatgpt.com/*
// @match        https://chat.openai.com/*
// @run-at       document-idle
// @grant        none
// ==/UserScript==

/*
 * What this reads, and what it does not.
 *
 * ChatGPT has no general usage endpoint. /backend-api/usage, /rate_limits and
 * /conversation_limit all 404, and the model list carries no quota fields at
 * all — checked on a Plus account. The message caps you hit in ordinary chat
 * are surfaced reactively, only once you have already hit them.
 *
 * What does exist is /backend-api/codex/usage, which returns the agent and task
 * usage window: plan type, percent used, window length, and reset time. That is
 * the surface behind Codex and agent tasks. So this meter answers "how much of
 * my agent allowance is gone", not "how many GPT-5 messages do I have left".
 *
 * It needs the bearer token from /api/auth/session, which is why this cannot be
 * folded into the claude.ai script: that token is readable only from a page on
 * chatgpt.com.
 */

(function () {
  'use strict';

  /*
   * Where this sits, and why.
   *
   * Bottom left was wrong: on a collapsed sidebar it lands on top of the
   * account avatar, and at low opacity the result reads as a smudge rather
   * than a readout.
   *
   * Probing the layout found two genuinely empty regions in both the new-chat
   * and conversation views. The far top right corner is not one of them — in a
   * conversation it holds the share and overflow buttons. The strip directly
   * below those buttons is empty, and so is the bottom right. The strip wins:
   * it is where the eye already goes for controls, it never meets the
   * composer, and nothing else claims it.
   */
  const CONFIG = {
    refreshMs: 60000,
    position: 'below-header',   // below-header | bottom-right | bottom-left
    gap: 16,                    // distance from the edges
  };

  const CSS = `
  .cgu-pill {
    position: fixed; z-index: 2147483000;
    display: flex; flex-direction: column; gap: 4px;
    padding: 8px 10px; min-width: 210px;
    border-radius: 8px;
    background: rgba(32,31,29,0.92);
    color: #ececec;
    font: 11px/1.35 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
    border: 1px solid rgba(140,135,125,0.22);
    box-shadow: 0 4px 20px rgba(0,0,0,0.35);
    /* Fully opaque at rest. The old .35 made it look like a rendering
       artefact, and .82 still read as faint against a dark page. The
       translucent background is what keeps it unobtrusive, not the opacity. */
    opacity: 1; transition: right 180ms ease, opacity 120ms ease;
    cursor: default; user-select: none;
  }
  .cgu-pill:hover { opacity: 1; }
  /* Gets out of the way while a menu or dialog is open over it. The pill sits
     at a very high z-index so it stays readable over ordinary page content,
     which means it would otherwise draw on top of ChatGPT's own menus. */
  .cgu-pill.cgu-hidden { opacity: 0; pointer-events: none; }
  @media (prefers-color-scheme: light) {
    .cgu-pill { background: rgba(252,251,249,0.97); color: #2b2924; }
  }

  .cgu-head {
    display: flex; align-items: center; justify-content: space-between; gap: 8px;
    font-size: 10px; letter-spacing: .04em; text-transform: uppercase; opacity: .6;
  }
  .cgu-row { display: flex; align-items: center; gap: 6px; font-size: 10.5px; }
  .cgu-label {
    flex: 0 0 78px; opacity: .65; white-space: nowrap;
    overflow: hidden; text-overflow: ellipsis;
  }
  .cgu-right { flex: 0 0 56px; opacity: .4; white-space: nowrap; text-align: right; }
  .cgu-bar {
    position: relative; flex: 1 1 auto; min-width: 56px; height: 9px;
    border: 1px solid #787877; border-radius: 5px; overflow: hidden;
  }
  .cgu-fill {
    position: absolute; top: 0; bottom: 0; left: 0; width: 0;
    background: #2c84db; transition: width 220ms ease;
  }
  /* Same convention as the Claude rail: the marker is the clock, not the usage.
     Fill behind the marker means you are pacing under the limit. */
  .cgu-mark {
    position: absolute; top: 0; bottom: 0; width: 2px; left: 0;
    background: #ffffff; transform: translateX(-1px); transition: left 220ms ease;
  }
  .cgu-row.cgu-warn .cgu-fill { background: #d97757; }
  .cgu-row.cgu-warn .cgu-label { color: #d97757; opacity: .95; }
  .cgu-note { font-size: 10px; opacity: .45; }
  @media (prefers-color-scheme: light) {
    .cgu-bar { border-color: #bfbfbf; }
    .cgu-fill { background: #5aa6ff; }
    .cgu-mark { background: #111111; }
  }
  `;

  function injectStyles() {
    try {
      const s = new CSSStyleSheet();
      s.replaceSync(CSS);
      document.adoptedStyleSheets = [...document.adoptedStyleSheets, s];
      return;
    } catch (e) { /* fall through */ }
    const t = document.createElement('style');
    t.textContent = CSS;
    (document.head || document.documentElement).appendChild(t);
  }

  /* ------------------------------------------------------------------ *
   * Data
   * ------------------------------------------------------------------ */
  let token = null, tokenAt = 0;

  async function getToken() {
    if (token && Date.now() - tokenAt < 600000) return token;
    const r = await fetch('/api/auth/session', { headers: { accept: 'application/json' } });
    if (!r.ok) return null;
    const j = await r.json();
    token = j && j.accessToken ? j.accessToken : null;
    tokenAt = Date.now();
    return token;
  }

  async function fetchUsage() {
    const t = await getToken();
    if (!t) return null;
    const r = await fetch('/backend-api/codex/usage', {
      headers: { accept: 'application/json', authorization: 'Bearer ' + t },
    });
    if (!r.ok) return null;
    return r.json();
  }

  /* ------------------------------------------------------------------ *
   * Rendering
   * ------------------------------------------------------------------ */
  let pill = null, head = null, rows = [], note = null;

  function makeRow() {
    const row = document.createElement('div');
    row.className = 'cgu-row';
    const label = document.createElement('span');
    label.className = 'cgu-label';
    const bar = document.createElement('div');
    bar.className = 'cgu-bar';
    const fill = document.createElement('div');
    fill.className = 'cgu-fill';
    const mark = document.createElement('div');
    mark.className = 'cgu-mark';
    bar.append(fill, mark);
    const right = document.createElement('span');
    right.className = 'cgu-right';
    row.append(label, bar, right);
    return { row, label, fill, mark, right };
  }

  function ensurePill() {
    if (pill && document.body.contains(pill)) return pill;
    pill = document.createElement('div');
    pill.className = 'cgu-pill';
    const g = CONFIG.gap + 'px';
    if (CONFIG.position === 'bottom-left') {
      Object.assign(pill.style, { bottom: g, left: g });
    } else if (CONFIG.position === 'bottom-right') {
      Object.assign(pill.style, { bottom: g, right: g });
    } else {
      // Clear of the share and overflow buttons that sit in the header row.
      Object.assign(pill.style, { top: '56px', right: g });
    }

    head = document.createElement('div');
    head.className = 'cgu-head';
    pill.appendChild(head);

    rows = [makeRow(), makeRow()];
    rows.forEach((r) => pill.appendChild(r.row));

    note = document.createElement('div');
    note.className = 'cgu-note';
    note.textContent = 'agent and task allowance';
    note.title = 'ChatGPT exposes no endpoint for ordinary message caps — those '
      + 'surface only once you hit them. This is the agent and task window from '
      + '/backend-api/codex/usage, which is what Codex and scheduled tasks draw on.';
    pill.appendChild(note);

    document.body.appendChild(pill);
    syncOffset();
    return pill;
  }

  /*
   * Step aside for a right-docked panel, the same trick the Claude rail uses:
   * find a tall block flush with the right edge that starts well inside the
   * viewport, and move out by its width. Covers canvas and any side panel
   * without depending on a class name that will be renamed.
   */
  /*
   * Yield to ChatGPT's own menus.
   *
   * The overflow menu opens directly beneath its button, which is the same
   * strip this pill lives in. Measured: the menu occupies a 204 by 217 box
   * starting at y 48, and the pill's box overlaps it, so the pill was covering
   * the first two menu items.
   *
   * Rather than move to a corner that some other popup will eventually reach,
   * this fades the pill out whenever an open menu, dialog or listbox actually
   * intersects it, and brings it back when that closes. Overlap is tested by
   * rectangle, so a menu opening somewhere else on the page changes nothing.
   */
  const POPUP_SEL = '[role="menu"],[role="dialog"],[role="listbox"],[role="tooltip"],'
    + '[data-radix-popper-content-wrapper]';

  function overlapsPopup() {
    if (!pill) return false;
    const p = pill.getBoundingClientRect();
    for (const el of document.querySelectorAll(POPUP_SEL)) {
      if (el === pill || pill.contains(el) || el.contains(pill)) continue;
      const state = el.getAttribute('data-state');
      if (state && state !== 'open') continue;      // closed popovers stay in the DOM
      const r = el.getBoundingClientRect();
      if (r.width < 40 || r.height < 30) continue;
      if (r.right > p.left && r.left < p.right && r.bottom > p.top && r.top < p.bottom) {
        return true;
      }
    }
    return false;
  }

  let lastHidden = null;
  function syncVisibility() {
    if (!pill) return;
    const hide = overlapsPopup();
    if (hide === lastHidden) return;
    lastHidden = hide;
    pill.classList.toggle('cgu-hidden', hide);
  }

  let lastOffset = -1;
  function syncOffset() {
    if (!pill || CONFIG.position === 'bottom-left') return;
    const vw = window.innerWidth, vh = window.innerHeight;
    let edge = vw;
    for (const el of document.querySelectorAll('div,aside,section')) {
      if (el === pill || pill.contains(el)) continue;
      const r = el.getBoundingClientRect();
      if (r.width < 200 || r.width > vw * 0.8) continue;
      if (r.height < vh * 0.35) continue;
      if (Math.abs(r.right - vw) > 10) continue;
      if (r.left <= vw * 0.15) continue;
      if (r.left < edge) edge = r.left;
    }
    const offset = Math.max(0, Math.round(vw - edge)) + CONFIG.gap;
    if (offset === lastOffset) return;
    lastOffset = offset;
    pill.style.right = offset + 'px';
  }

  function fmtLeft(seconds) {
    if (!isFinite(seconds) || seconds <= 0) return 'due';
    const m = Math.round(seconds / 60);
    if (m < 60) return `${m}m`;
    const h = Math.floor(m / 60);
    if (h < 24) return `${h}h ${m % 60}m`;
    return `${Math.floor(h / 24)}d ${h % 24}h`;
  }

  function windowName(seconds) {
    if (!seconds) return 'Usage';
    const h = seconds / 3600;
    if (h <= 1.5) return 'Hour';
    if (h <= 6) return 'Session';
    if (h <= 26) return 'Day';
    if (h <= 24 * 8) return 'Week';
    return 'Month';
  }

  function drawWindow(r, w, planNote) {
    if (!w) { r.row.style.display = 'none'; return; }
    r.row.style.display = '';
    const used = Math.max(0, Math.min(100, Number(w.used_percent) || 0));
    const total = Number(w.limit_window_seconds) || 0;
    const left = Number(w.reset_after_seconds);
    const elapsed = total && isFinite(left)
      ? Math.max(0, Math.min(100, ((total - left) / total) * 100))
      : null;

    r.label.textContent = `${windowName(total)} ${Math.round(used)}%`;
    r.right.textContent = fmtLeft(left);
    r.fill.style.width = used + '%';
    if (elapsed == null) {
      r.mark.style.display = 'none';
    } else {
      r.mark.style.display = '';
      r.mark.style.left = elapsed + '%';
    }
    r.row.classList.toggle('cgu-warn', used >= 80);

    const resetAt = w.reset_at ? new Date(Number(w.reset_at) * 1000).toLocaleString() : null;
    r.row.title = `${Math.round(used)} per cent of a `
      + `${Math.round((total || 0) / 3600)} hour window used.`
      + (resetAt ? `\nResets ${resetAt}.` : '')
      + (elapsed == null ? '' :
        `\n\nThe white marker is the clock: ${Math.round(elapsed)} per cent of the `
        + 'window has passed. Fill behind the marker means you are pacing under '
        + 'the limit; fill ahead of it means you will run out before it resets.')
      + (planNote ? `\n\n${planNote}` : '');
  }

  async function refresh() {
    let data;
    try { data = await fetchUsage(); }
    catch (e) { data = null; }

    if (!data || !data.rate_limit) {
      if (pill) pill.style.display = 'none';
      return;
    }
    ensurePill();
    pill.style.display = '';

    const plan = (data.plan_type || 'chatgpt').toString();
    head.textContent = plan.charAt(0).toUpperCase() + plan.slice(1);

    const credits = data.credits || {};
    const planNote = credits.unlimited
      ? 'Credits: unlimited.'
      : (credits.has_credits ? `Credits balance ${credits.balance}.` : 'No credit balance.');

    drawWindow(rows[0], data.rate_limit.primary_window, planNote);
    drawWindow(rows[1], data.rate_limit.secondary_window, planNote);

    if (data.rate_limit.limit_reached) {
      head.textContent = plan + ' · limit reached';
    }

    window.cguDebug = () => ({
      plan: data.plan_type,
      allowed: data.rate_limit.allowed,
      limitReached: data.rate_limit.limit_reached,
      primary: data.rate_limit.primary_window,
      secondary: data.rate_limit.secondary_window,
      reachedType: data.rate_limit_reached_type,
    });
  }

  function throttle(fn, ms) {
    let waiting = false;
    return function () {
      if (waiting) return;
      waiting = true;
      setTimeout(() => { waiting = false; fn(); }, ms);
    };
  }

  function start() {
    injectStyles();
    refresh();
    setInterval(refresh, CONFIG.refreshMs);

    // A panel opening or the window resizing both move the right edge.
    new MutationObserver(throttle(() => {
      syncOffset();
      syncVisibility();
    }, 200)).observe(document.body, { childList: true, subtree: true });
    window.addEventListener('resize', throttle(() => {
      syncOffset();
      syncVisibility();
    }, 250));

    // A menu can open and close without a body mutation the observer sees, so
    // this also polls. Two rectangle tests, cheap enough to run often.
    setInterval(syncVisibility, 250);
    console.log('[ChatGPT Usage Meter] ready. Run cguDebug() for the raw window.');
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
