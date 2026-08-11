// ==UserScript==
// @name         Claude Prompt Navigator
// @namespace    local.deepith
// @version      3.8.0
// @description  Lists every question you asked in a Claude chat, first to last, and jumps to them. Reads the full list from Claude's own conversation API, so it is not limited to the handful of messages the page keeps loaded. On Cowork it reads the session event log for the same complete list, and shows the files that session produced.
// @author       deepith
// @copyright    2026 Deepith Kundar. All rights reserved. Personal use only —
//               see LICENSE. Not open source, not for redistribution.
// @match        https://claude.ai/*
// @run-at       document-start
// @grant        none
// ==/UserScript==

/*
 * Why v2 exists
 * -------------
 * v1 scraped [data-testid="user-message"] out of the DOM. That only ever
 * finds 3 to 5 questions, because claude.ai virtualises the message list:
 * everything outside the current window is replaced by one tall spacer div
 * and is genuinely absent from the document. Programmatic scrolling moves
 * the scrollbar but does not remount those messages, so no amount of
 * scrolling from script can recover them.
 *
 * v2 therefore builds the list from the same endpoint the app itself uses,
 * which returns the whole conversation at once. The DOM is still consulted,
 * but only to work out which questions happen to be on screen right now.
 *
 * Consequence worth knowing: clicking a question that is currently loaded
 * scrolls to it exactly. Clicking one from a part of the chat Claude has
 * unloaded moves you to roughly the right place and shows you the full
 * question text, because an exact scroll target does not exist in the page.
 */

(function () {
  'use strict';

  const CONFIG = {
    labelChars: 80,        // characters of each question shown in the list
    hotkeys: true,         // Alt+ArrowUp / Alt+ArrowDown
    flashMs: 1500,         // how long a jumped-to message stays outlined
    settleMs: 260,         // gap between checks after an approximate scroll
    settleTries: 6,        // how many times to check before giving up
    refetchMs: 1600,       // delay before refetching after you send a message

    // How long Anthropic keeps a conversation prompt-cached. This is an
    // assumption, not something the page reports. Anthropic runs both a five
    // minute and a one hour cache window and the browser cannot see which
    // applies, so claude-counter's hardcoded 5 is a guess like this one, and
    // is the likeliest reason its countdown reads wrong. Change it here if
    // yours behaves differently.
    cacheWindowMinutes: 5,

    // Fallback denominator for the context bar, used only when the chat runs a
    // model missing from CONTEXT_WINDOWS below. Deliberately the small, older
    // figure: an over-full bar makes you start a new chat sooner than needed,
    // which is the harmless direction to be wrong in.
    contextLimitTokens: 200000,
  };

  /*
   * Context window per model.
   *
   * The bar used to divide by a hardcoded 200,000, which claude-counter also
   * does. That is wrong by five times for every chat on this account: both
   * claude-opus-5 and claude-fable-5 carry a 1M window, so a thread reading
   * 9 per cent full was actually nearer 2.
   *
   * Figures are the documented API context windows. Whether claude.ai applies
   * its own tighter ceiling on top is not something the page reports, so treat
   * the denominator as the model's capability rather than a promise. (inference)
   */
  const CONTEXT_WINDOWS = {
    'claude-fable-5': 1000000,
    'claude-mythos-5': 1000000,
    'claude-opus-5': 1000000,
    'claude-opus-4-8': 1000000,
    'claude-opus-4-7': 1000000,
    'claude-opus-4-6': 1000000,
    'claude-sonnet-5': 1000000,
    'claude-sonnet-4-6': 1000000,
    'claude-haiku-4-5': 200000,
  };

  /* Short names, so the header reads as a label rather than an id. */
  const MODEL_LABELS = {
    'claude-fable-5': 'Fable 5',
    'claude-mythos-5': 'Mythos 5',
    'claude-opus-5': 'Opus 5',
    'claude-opus-4-8': 'Opus 4.8',
    'claude-opus-4-7': 'Opus 4.7',
    'claude-opus-4-6': 'Opus 4.6',
    'claude-sonnet-5': 'Sonnet 5',
    'claude-sonnet-4-6': 'Sonnet 4.6',
    'claude-haiku-4-5': 'Haiku 4.5',
  };

  // Verified live against claude.ai on 9 Aug 2026. Left first in the list.
  const MESSAGE_SELECTORS = [
    '[data-testid="user-message"]',
    'div.font-user-message',
    '[class*="font-user-message"]',
  ];

  const norm = (s) => (s || '').replace(/\s+/g, ' ').trim();
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const KEYLEN = 34;
  const keyOf = (s) => norm(s).slice(0, KEYLEN).toLowerCase();

  /* ------------------------------------------------------------------ *
   * Styles
   * ------------------------------------------------------------------ */
  const CSS = `
  /*
   * The rail itself never scrolls. It is a flex column holding a header that
   * stays put and a list that scrolls underneath it. Scrolling the whole rail
   * carried the header off the top of a long list, which is the one thing the
   * header exists to prevent.
   */
  /*
   * The right value here is only the pre-script default. syncRailOffset()
   * sets it inline from a live measurement, and an inline style beats this
   * rule, so changing the number below on its own does nothing. That is the
   * mistake this comment exists to stop being made twice.
   *
   * No backticks in this block. It sits inside a template literal, and a
   * backtick here ends the stylesheet and breaks the whole script.
   *
   * The offset is the same collapsed and open, deliberately. Sliding the rail
   * to the edge on hover moved the thing you were reaching for while you were
   * reaching for it, and it put the open panel back over the scrollbar. Since
   * it no longer sits flush, all four corners are rounded.
   */
  .cpn-rail {
    position: fixed; right: 24px; top: 50%; transform: translateY(-50%);
    z-index: 2147483000;
    display: flex; flex-direction: column;
    padding: 8px 6px; max-height: 78vh; overflow: hidden;
    font: 12px/1.35 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
    color: #e6e4df; background: transparent;
    border-radius: 10px;
    transition: background 140ms ease, box-shadow 140ms ease, right 140ms ease;
  }
  .cpn-rail:hover, .cpn-rail.cpn-pinned {
    background: rgba(32,31,29,0.95);
    box-shadow: 0 4px 24px rgba(0,0,0,0.45);
  }

  .cpn-list {
    display: flex; flex-direction: column; gap: 3px;
    overflow-y: auto; overflow-x: hidden;
    min-height: 0;   /* without this the flex child refuses to shrink */
    scrollbar-width: none;
  }
  /* Collapsed, the rail is only about 22px wide, so a scrollbar would eat the
     tick column. It appears once the rail is open. */
  .cpn-list::-webkit-scrollbar { width: 0; }
  .cpn-rail:hover .cpn-list, .cpn-rail.cpn-pinned .cpn-list { scrollbar-width: thin; }
  .cpn-rail:hover .cpn-list::-webkit-scrollbar,
  .cpn-rail.cpn-pinned .cpn-list::-webkit-scrollbar { width: 6px; }
  .cpn-list::-webkit-scrollbar-thumb { background: #5a5750; border-radius: 3px; }

  @media (prefers-color-scheme: light) {
    .cpn-rail { color: #2b2924; }
    .cpn-rail:hover, .cpn-rail.cpn-pinned { background: rgba(252,251,249,0.97); }
    .cpn-list::-webkit-scrollbar-thumb { background: #c4c0b8; }
  }

  /*
   * width: 0 matters as much as height: 0 here.
   *
   * Once the header gained meter rows with fixed 80px and 54px columns, its
   * intrinsic width became about 202px. A zero-height header still contributed
   * that width to the flex column, so the collapsed rail was 214px wide rather
   * than 18px — which is why it sat on top of the conversation whether or not
   * a panel was open. Collapsing both axes fixes it.
   */
  .cpn-head {
    flex: 0 0 auto;
    display: flex; flex-direction: column; align-items: stretch; gap: 3px;
    opacity: 0; height: 0; width: 0; min-width: 0; overflow: hidden;
  }
  .cpn-rail:hover .cpn-head, .cpn-rail.cpn-pinned .cpn-head {
    /* Only when open. A min-width on the collapsed header would widen the
       whole rail and destroy the 22px tick strip. */
    opacity: 1; height: auto; width: auto; min-width: 210px;
    padding-bottom: 6px; margin-bottom: 6px;
    border-bottom: 1px solid rgba(140,135,125,0.22);
  }
  .cpn-head-top {
    display: flex; align-items: center; justify-content: space-between; gap: 8px;
    font-size: 11px; letter-spacing: .04em; text-transform: uppercase; opacity: .55;
  }
  /* text-transform and text-align are stated because claude.ai's own rules
     otherwise reach in and uppercase and centre these lines. */
  .cpn-meter {
    font-size: 10.5px; opacity: .6; white-space: nowrap; cursor: default;
    text-transform: none; text-align: left; letter-spacing: 0;
  }
  .cpn-meter.cpn-warn { color: #d97757; opacity: .95; }

  /* A labelled track with a marker at the fill edge, so the position reads at
     a glance rather than having to be parsed out of a number. */
  .cpn-row {
    display: flex; align-items: center; gap: 6px;
    font-size: 10.5px; text-transform: none; letter-spacing: 0; cursor: default;
  }
  /* Fixed columns on both sides so the three bars start and end on the same
     x, instead of jittering with the width of "Context" vs "Session". */
  .cpn-row-label {
    flex: 0 0 80px; opacity: .65; white-space: nowrap;
    overflow: hidden; text-overflow: ellipsis;
  }
  .cpn-row-right {
    flex: 0 0 54px; opacity: .4; white-space: nowrap; text-align: right;
  }
  .cpn-bar {
    position: relative; flex: 1 1 auto; min-width: 56px; height: 9px;
    border: 1px solid #787877; border-radius: 5px; overflow: hidden;
  }
  .cpn-bar-fill {
    position: absolute; top: 0; bottom: 0; left: 0; width: 0;
    background: #2c84db; transition: width 220ms ease;
  }
  /* The marker is the clock, not the fill. It shows how far through the reset
     window you are, so its distance from the fill edge is the useful reading:
     fill behind the marker means you are pacing under the limit, fill ahead of
     it means you will run out before the window resets. */
  .cpn-bar-mark {
    position: absolute; top: 0; bottom: 0; width: 2px; left: 0;
    background: #ffffff; transform: translateX(-1px); transition: left 220ms ease;
  }
  .cpn-bar-mark.cpn-hidden { display: none; }
  .cpn-row.cpn-warn .cpn-bar-fill { background: #d97757; }
  .cpn-row.cpn-warn .cpn-row-label { color: #d97757; opacity: .95; }
  @media (prefers-color-scheme: light) {
    .cpn-bar { border-color: #bfbfbf; }
    .cpn-bar-fill { background: #5aa6ff; }
    .cpn-bar-mark { background: #111111; }
  }
  /* 12px glyphs in a text-sized button were hard to hit and harder to read.
     16px with a padded box gives them a 24px target without widening the
     header, since the row has spare height already. */
  .cpn-pin {
    cursor: pointer; border: 0; background: none; color: inherit;
    font-size: 16px; line-height: 1; opacity: .7;
    padding: 3px 4px; margin: -3px 0; border-radius: 5px;
    transition: opacity 120ms ease, background 120ms ease;
  }
  .cpn-pin:hover { opacity: 1; background: rgba(255,255,255,0.08); }
  @media (prefers-color-scheme: light) {
    .cpn-pin:hover { background: rgba(0,0,0,0.07); }
  }

  /* Collapsed, this is a tick strip and nothing else, so it is kept tight:
     18px wide and 3px between rows. On a 45 row thread that is roughly 225px
     of height instead of 315px. */
  .cpn-item {
    display: flex; align-items: center; gap: 8px;
    cursor: pointer; border-radius: 5px; padding: 1px 3px;
    max-width: 18px; transition: max-width 160ms ease, background 120ms ease;
  }
  .cpn-rail:hover .cpn-item, .cpn-rail.cpn-pinned .cpn-item { max-width: 360px; }
  .cpn-item:hover { background: rgba(140,135,125,0.18); }

  .cpn-tick {
    flex: 0 0 auto; height: 2px; width: 12px; border-radius: 2px;
    background: #8a857b; opacity: .5;
    transition: opacity 120ms, background 120ms, width 120ms;
  }
  /* solid tick = that message is loaded in the page and jumps exactly */
  .cpn-item.cpn-loaded .cpn-tick { opacity: .85; }
  .cpn-item:hover .cpn-tick { opacity: 1; width: 15px; }
  .cpn-item.cpn-active .cpn-tick { background: #d97757; opacity: 1; width: 17px; }

  .cpn-label { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; opacity: 0; transition: opacity 120ms ease; }
  .cpn-rail:hover .cpn-label, .cpn-rail.cpn-pinned .cpn-label { opacity: .92; }
  .cpn-item.cpn-active .cpn-label { color: #d97757; }
  .cpn-num { opacity: .45; margin-right: 5px; font-variant-numeric: tabular-nums; }

  /* Documents Claude created, marked distinctly from your questions. */
  .cpn-item.cpn-doc .cpn-tick { background: #5b9dbb; width: 7px; margin-left: 5px; opacity: .8; }
  .cpn-item.cpn-doc:hover .cpn-tick { width: 10px; opacity: 1; }
  .cpn-item.cpn-doc .cpn-label { opacity: 0; font-size: 11.5px; }
  .cpn-rail:hover .cpn-doc .cpn-label, .cpn-rail.cpn-pinned .cpn-doc .cpn-label { opacity: .72; }
  .cpn-doc-icon { color: #5b9dbb; margin-right: 5px; }
  /* Hollow marker: created in this chat, but the sandbox no longer holds it. */
  .cpn-item.cpn-gone .cpn-tick { background: transparent; box-shadow: inset 0 0 0 1px #5b9dbb; height: 4px; }
  .cpn-item.cpn-gone .cpn-label { font-style: italic; }
  .cpn-rail:hover .cpn-gone .cpn-label, .cpn-rail.cpn-pinned .cpn-gone .cpn-label { opacity: .55; }

  /*
   * While a right-hand panel is open there is no room for the open rail.
   * Measured with the Artifacts panel showing: the message column ends at 910
   * and the panel starts at 1118, a 208px gutter, while a pinned rail is 218px
   * wide — so a pinned rail sat on top of the download buttons. Pinning is
   * suspended for as long as the panel is open, leaving the tick strip, and
   * hovering still opens it deliberately.
   */
  .cpn-rail.cpn-panelled.cpn-pinned:not(:hover) { background: transparent; box-shadow: none; }
  .cpn-rail.cpn-panelled.cpn-pinned:not(:hover) .cpn-head {
    opacity: 0; height: 0; min-width: 0;
    padding: 0; margin: 0; border-bottom: 0;
  }
  .cpn-rail.cpn-panelled.cpn-pinned:not(:hover) .cpn-item { max-width: 18px; }
  .cpn-rail.cpn-panelled.cpn-pinned:not(:hover) .cpn-label { opacity: 0; }

  /* ---- command palette ---- */
  .cpn-pal {
    position: fixed; inset: 0; z-index: 2147483002;
    display: none; align-items: flex-start; justify-content: center;
    background: rgba(0,0,0,0.45); padding-top: 12vh;
    font: 13px/1.4 ui-sans-serif, system-ui, -apple-system, "Segoe UI", sans-serif;
  }
  .cpn-pal.cpn-pal-open { display: flex; }
  .cpn-pal-box {
    width: min(640px, 92vw); max-height: 70vh; display: flex; flex-direction: column;
    background: rgba(32,31,29,0.99); color: #e6e4df;
    border: 1px solid rgba(140,135,125,0.28); border-radius: 10px;
    box-shadow: 0 18px 60px rgba(0,0,0,0.55); overflow: hidden;
  }
  @media (prefers-color-scheme: light) {
    .cpn-pal-box { background: rgba(252,251,249,0.99); color: #2b2924; }
  }
  .cpn-pal-input {
    border: 0; outline: 0; background: transparent; color: inherit;
    font: inherit; font-size: 15px; padding: 14px 16px;
    border-bottom: 1px solid rgba(140,135,125,0.22);
  }
  .cpn-pal-list { overflow-y: auto; scrollbar-width: thin; }
  .cpn-pal-row {
    display: flex; align-items: baseline; gap: 9px;
    padding: 7px 16px; cursor: pointer;
  }
  .cpn-pal-row.cpn-pal-on { background: rgba(140,135,125,0.18); }
  .cpn-pal-icon { flex: 0 0 auto; opacity: .4; }
  .cpn-pal-main { flex: 1 1 auto; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .cpn-pal-sub { flex: 0 0 auto; max-width: 34%; opacity: .42; font-size: 11px;
                 overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .cpn-pal-empty { padding: 18px 16px; opacity: .5; }
  .cpn-pal-hint {
    padding: 8px 16px; font-size: 10.5px; opacity: .4;
    border-top: 1px solid rgba(140,135,125,0.22);
  }

  .cpn-flash { outline: 2px solid #d97757 !important; outline-offset: 4px; border-radius: 8px; transition: outline-color 600ms ease; }
  .cpn-flash-out { outline-color: transparent !important; }

  .cpn-peek {
    position: fixed; right: 400px; z-index: 2147483001;
    max-width: 420px; padding: 12px 14px;
    background: rgba(32,31,29,0.98); color: #e6e4df;
    border: 1px solid rgba(140,135,125,0.3); border-radius: 8px;
    box-shadow: 0 8px 32px rgba(0,0,0,0.5);
    font: 13px/1.5 ui-sans-serif, system-ui, sans-serif;
    white-space: pre-wrap;
  }
  @media (prefers-color-scheme: light) {
    .cpn-peek { background: rgba(252,251,249,0.99); color: #2b2924; }
  }
  .cpn-peek-note { margin-top: 9px; font-size: 11px; opacity: .6; }
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
   * Claude's own conversation API
   * ------------------------------------------------------------------ */
  let orgIds = null;
  let resolvedOrg = null;   // the org that actually owned the last conversation

  // Two surfaces, two data sources. Ordinary chats are served by the
  // conversation API and give the complete list. Cowork sessions are not:
  // their transcript is an agent event log at /v1/code/sessions/<id>/events,
  // which costs about 14 MB and 6 seconds to page through for one session,
  // and buries typed prompts among tool results, skill injections and
  // base64 screenshots. Not worth it, so Cowork reads the page instead and
  // says plainly that it is only showing what is on screen.
  function route() {
    const chat = location.pathname.match(/\/chat\/([0-9a-f-]{36})/i);
    if (chat) return { mode: 'chat', id: chat[1] };
    const cowork = location.pathname.match(/\/cowork\/(cse_[A-Za-z0-9]+)/);
    if (cowork) return { mode: 'cowork', id: cowork[1] };
    return null;
  }

  async function getOrgIds() {
    if (orgIds) return orgIds;
    const cached = localStorage.getItem('cpn-orgs');
    if (cached) { try { orgIds = JSON.parse(cached); return orgIds; } catch (e) {} }
    const r = await fetch('/api/organizations', { headers: { accept: 'application/json' } });
    orgIds = (await r.json()).map((o) => o.uuid);
    localStorage.setItem('cpn-orgs', JSON.stringify(orgIds));
    return orgIds;
  }

  function textOfMessage(m) {
    const direct = norm(m.text);
    if (direct) return direct;
    return norm((m.content || []).map((c) => c.text || '').join(' '));
  }

  async function fetchQuestions(convId) {
    const orgs = await getOrgIds();
    for (const org of orgs) {
      const url = `/api/organizations/${org}/chat_conversations/${convId}`
        + '?tree=True&rendering_mode=messages';
      let res;
      try { res = await fetch(url, { headers: { accept: 'application/json' } }); }
      catch (e) { continue; }
      if (!res.ok) continue;
      resolvedOrg = org;
      const data = await res.json();
      projectUuid = data.project_uuid || null;
      convModel = data.model || null;
      convEffort = (data.settings && data.settings.effort_level) || null;

      /*
       * Only the live branch counts.
       *
       * tree=True returns every message including ones you edited away, and
       * those are not in Claude's context any more. Walking back from
       * current_leaf_message_uuid through parent_message_uuid gives the branch
       * actually in play. On one thread checked here that was 42 messages out
       * of 44 in the tree. Without this the rail also listed questions you had
       * already replaced.
       */
      const everything = data.chat_messages || [];
      const byId = new Map(everything.map((m) => [m.uuid, m]));
      let msgs = [];
      let cur = byId.get(data.current_leaf_message_uuid);
      const walked = new Set();
      while (cur && !walked.has(cur.uuid)) {
        walked.add(cur.uuid);
        msgs.push(cur);
        cur = byId.get(cur.parent_message_uuid);
      }
      msgs.reverse();
      // If the leaf could not be resolved, fall back to the whole tree rather
      // than showing an empty rail.
      if (!msgs.length) msgs = everything.slice();
      msgs.sort((a, b) => (a.index ?? 0) - (b.index ?? 0));
      /*
       * Size the conversation from content blocks, counting text and the
       * readable part of attachments while skipping thinking.
       *
       * Skipping thinking is the correction. Measuring the raw message text
       * counted it, which on the thread this was tested against was 67,593
       * characters of thinking against 69,849 of actual reply, very nearly
       * doubling the figure. Thinking from earlier turns is not resent, so
       * counting it overstated the number by about half. claude-counter
       * excludes it too, and is right to.
       *
       * It also counts tool_use and tool_result payloads. Worth knowing that
       * this endpoint never returns those blocks: six conversations checked
       * here came back with text and thinking only, so that part of its logic
       * yields nothing in practice and there is nothing for us to match.
       */
      convChars = msgs.reduce((total, m) => {
        let c = 0;
        (m.content || []).forEach((b) => {
          if (b.type === 'text' && typeof b.text === 'string') c += b.text.length;
        });
        (m.attachments || []).forEach((a) => {
          if (typeof a.extracted_content === 'string') c += a.extracted_content.length;
        });
        return total + c;
      }, 0);

      /*
       * Kept for the export. The rail itself only needs your questions, but an
       * export of a thread without Claude's replies is a table of contents, not
       * a transcript, and re-fetching the conversation to get them back would
       * be a second download of something already in hand.
       */
      convTurns = msgs.map((m) => ({
        who: m.sender === 'human' ? 'you' : 'claude',
        text: textOfMessage(m),
        at: m.created_at || null,
      })).filter((t) => t.text);

      const humans = msgs.filter((m) => m.sender === 'human');
      return humans.map((m, i) => {
        const text = textOfMessage(m);
        const hasFiles = (m.attachments || []).length || (m.files || []).length;
        return {
          n: i + 1,
          uuid: m.uuid,
          text: text || (hasFiles ? '(attachment only)' : '(empty message)'),
          key: keyOf(text),
          at: Date.parse(m.created_at) || 0,
          mi: m.index ?? 0,
        };
      });
    }
    throw new Error('conversation fetch failed for every organization');
  }

  /*
   * Documents Claude produced during the conversation.
   *
   * The obvious source is the sandbox listing, and on its own it is wrong.
   * The sandbox is not permanent: files made early in a long thread get
   * evicted, so the listing shows only whatever still happens to exist.
   * Measured on a 27 question thread, it reported 11 files while the
   * conversation had actually produced 18. Everything from the first day,
   * including an xlsx and three docx files, had already gone.
   *
   * So two sources get merged. The listing is authoritative for files that
   * still exist and gives exact sizes and timestamps. The message text
   * recovers the ones that no longer do, and places them better besides,
   * because a filename first appearing in an assistant message pins the
   * document to that message rather than to a clock reading.
   *
   * Files you uploaded are excluded, as are a few conventional filenames
   * that get discussed constantly and created rarely.
   */
  const DENY = new Set([
    'skill.md', 'style.md', 'claude.md', 'agents.md', 'readme.md',
    'package.json', 'tsconfig.json', 'state.md', 'memory.md',
  ]);

  // Widened to match what the sandbox filter accepts. It was narrower to keep
  // code samples out of the list, but that also dropped real deliverables —
  // a build script or a json export is still something Claude made for you.
  // The denylist above is what keeps the noise down now.
  const TEXT_FILE_RE = new RegExp(
    '\\b[A-Za-z0-9][\\w.\\-]{1,70}\\.'
    + '(?:pdf|docx?|xlsx?|pptx?|html?|xml|csv|md|markdown|zip|txt|json|js|ts|py|css|svg|ya?ml)'
    + '\\b', 'g');

  async function listOutputFiles(convId) {
    const url = `/api/organizations/${resolvedOrg}/conversations/${convId}`
      + '/wiggle/list-files?prefix=';
    let res;
    try { res = await fetch(url, { headers: { accept: 'application/json' } }); }
    catch (e) { return { outputs: [], uploads: new Set() }; }
    if (!res.ok) return { outputs: [], uploads: new Set() };
    const meta = (await res.json()).files_metadata || [];
    const nameOf = (f) => f.path.split('/').pop();
    /*
     * Documents Claude wrote also went through the context window, and they
     * are often the bigger half. On the thread this was measured against, the
     * outputs came to 105,631 characters against 69,605 of conversation, which
     * moved the estimate from 18k to 46k.
     *
     * Only text-shaped outputs are counted. A pdf or docx is a rendering of
     * something already counted, usually the html or markdown sitting beside
     * it in the same folder, and its byte size says nothing about tokens. On
     * that thread the four skipped binaries were exactly the pdf, docx and zip
     * renders of html files already in the total.
     */
    const TEXTY = /\.(md|markdown|txt|html?|xml|csv|json|js|css|ts|py|svg|ya?ml)$/i;
    const outs = meta.filter((f) => f.path && f.path.includes('/outputs/'));
    docChars = outs.reduce((n, f) => (
      (TEXTY.test(f.path) || /^text\//.test(f.content_type || ''))
        ? n + (f.size || 0)
        : n
    ), 0);

    return {
      outputs: outs.map((f) => ({
        name: nameOf(f),
        size: f.size || 0,
        at: Date.parse(f.created_at) || 0,
        onDisk: true,
        msgIndex: null,
      })),
      uploads: new Set(meta.filter((f) => f.path && f.path.includes('/uploads/'))
        .map((f) => nameOf(f).toLowerCase())),
    };
  }

  async function scanMentions(convId, uploads) {
    const url = `/api/organizations/${resolvedOrg}/chat_conversations/${convId}`
      + '?tree=True&rendering_mode=raw';
    let res;
    try { res = await fetch(url, { headers: { accept: 'application/json' } }); }
    catch (e) { return new Map(); }
    if (!res.ok) return new Map();
    const msgs = ((await res.json()).chat_messages || []).slice()
      .sort((a, b) => (a.index ?? 0) - (b.index ?? 0));

    // Fallback only. If content blocks were unavailable the conversation went
    // unmeasured, and raw text beats showing nothing, even though it counts
    // thinking and so overstates.
    if (!convChars) convChars = msgs.reduce((n, m) => n + ((m.text || '').length), 0);

    /*
     * A filename you typed is not evidence Claude did not make it.
     *
     * This used to discard any name that had appeared in one of your messages
     * first. That is exactly backwards for the common case: you ask for
     * "BCom_Curriculum_Final.docx", Claude writes it, and the file then never
     * appears in the rail. Only files genuinely present under /uploads/ are
     * yours, and those are already excluded by the uploads set.
     */
    const first = new Map();
    msgs.forEach((m) => {
      if (m.sender !== 'assistant') return;
      const names = [...new Set((m.text || '').match(TEXT_FILE_RE) || [])];
      names.forEach((n) => {
        const k = n.toLowerCase();
        if (first.has(k) || uploads.has(k) || DENY.has(k)) return;
        first.set(k, {
          name: n,
          size: 0,
          at: Date.parse(m.created_at) || 0,
          onDisk: false,
          msgIndex: m.index ?? null,
        });
      });
    });
    return first;
  }

  /* Counts only, from the 5 KB project object. The docs themselves are a
     1.4 MB download and are not worth fetching just to caption a tooltip. */
  async function fetchProjectInfo() {
    projectInfo = null;
    if (!resolvedOrg || !projectUuid) return;
    try {
      const r = await fetch(`/api/organizations/${resolvedOrg}/projects/${projectUuid}`,
        { headers: { accept: 'application/json' } });
      if (!r.ok) return;
      const p = await r.json();
      projectInfo = { docs_count: p.docs_count ?? 0, files_count: p.files_count ?? 0 };
    } catch (e) { /* tooltip detail only, never worth failing over */ }
  }

  async function fetchDocuments(convId) {
    docChars = 0;
    if (!resolvedOrg) return [];
    const { outputs, uploads } = await listOutputFiles(convId);
    const mentions = await scanMentions(convId, uploads);

    const merged = new Map();
    mentions.forEach((v, k) => merged.set(k, v));
    outputs.forEach((f) => {
      const k = f.name.toLowerCase();
      const hit = merged.get(k);
      // Keep the message anchor from the text scan, take size and disk state
      // from the listing.
      if (hit) { hit.onDisk = true; hit.size = f.size; }
      else merged.set(k, f);
    });
    return [...merged.values()].sort((a, b) => (a.at || 0) - (b.at || 0));
  }

  /*
   * Interleaves documents into the question list. A document that was traced
   * to a message sits after the last question preceding that message, which
   * is exact. One known only from the sandbox falls back to its timestamp.
   */
  function buildEntries(qs, docs) {
    if (!qs.length) return [];
    if (!docs.length) return qs.map((q, i) => ({ kind: 'q', q, qi: i }));

    const anchorFor = (d) => {
      let a = -1;
      qs.forEach((q, i) => {
        const before = d.msgIndex != null ? (q.mi < d.msgIndex) : (q.at <= d.at);
        if (before) a = i;
      });
      return a === -1 ? 0 : a;
    };

    const byAnchor = new Map();
    docs.forEach((d) => {
      const a = anchorFor(d);
      if (!byAnchor.has(a)) byAnchor.set(a, []);
      byAnchor.get(a).push(d);
    });

    const out = [];
    qs.forEach((q, i) => {
      out.push({ kind: 'q', q, qi: i });
      (byAnchor.get(i) || []).forEach((d) => out.push({ kind: 'doc', doc: d, anchor: i }));
    });
    return out;
  }

  /* ------------------------------------------------------------------ *
   * Cowork outputs
   * ------------------------------------------------------------------ *
   *
   * Cowork has no file API. The endpoint chat uses,
   * `/conversations/{id}/wiggle/list-files`, wants a UUID and a session id is
   * `cse_…`, so it answers 400 — the app itself calls it and gets the same 400.
   * Nothing under `/cowork/sessions/{id}` serves files either.
   *
   * What cowork does have is an Outputs panel on the right, listing every file
   * the session produced, in creation order, accumulated across the whole
   * session rather than the last turn. That panel is the source here.
   *
   * It is anchored by its heading text rather than by class name. The classes
   * are generated and change; the word does not.
   */
  // Built from char codes rather than written as an escape, so the Private Use
  // Area range cannot be mangled by whatever edits this file next.
  const ICON_GLYPH = new RegExp(
    '[' + String.fromCharCode(0xE000) + '-' + String.fromCharCode(0xF8FF) + ']', 'g');

  /* ------------------------------------------------------------------ *
   * Opening a file through Claude's own viewer
   * ------------------------------------------------------------------ *
   *
   * Every artifact carries a `View <title>` button, both on its card in the
   * transcript and in the Artifacts panel. Those buttons stay in the document
   * whether the panel is open or shut, so nothing has to be opened first.
   *
   * The titles are prettified: `C08_Cowork_Handover.md` is listed as
   * `C08 cowork handover`. Reducing both sides to lowercase alphanumerics
   * makes them line up. Measured against a thread holding 11 files, all 11
   * matched a row.
   */
  const artifactKey = (s) => (s || '')
    .toLowerCase()
    .replace(/\.[a-z0-9]{1,5}$/, '')
    .replace(/[^a-z0-9]+/g, '');

  function artifactRows() {
    const rows = new Map();
    document.querySelectorAll('button[aria-label^="View "]').forEach((el) => {
      const title = el.getAttribute('aria-label').replace(/^View /, '');
      if (!title || title === 'all') return;          // "View all" is not a file

      /*
       * Two files can share a title and differ only by format: an assessment
       * plan exported as both .pdf and .html reduces to one key. The row's own
       * subtitle reads "Document · PDF" or "Code · HTML", so the format is
       * used to tell them apart. Without it, clicking the PDF could open the
       * HTML, which looks like a bug and is hard to explain.
       */
      let box = el;
      for (let i = 0; i < 5 && box.parentElement; i++) {
        box = box.parentElement;
        if ((box.innerText || '').trim()) break;
      }
      const fmt = (box.innerText || '').match(/·\s*([A-Za-z0-9]+)\s*$/);
      const k = artifactKey(title);
      if (!rows.has(k)) rows.set(k, []);
      rows.get(k).push({ ext: fmt ? fmt[1].toLowerCase() : null, el });
    });
    return rows;
  }

  /* Returns true only if a row was found and clicked. */
  function openArtifact(name) {
    const rows = artifactRows();
    const candidates = rows.get(artifactKey(name));
    if (!candidates || !candidates.length) return false;

    const ext = (name.match(/\.([a-z0-9]{1,5})$/i) || [])[1];
    const exact = ext && candidates.find((c) => c.ext === ext.toLowerCase());
    const pick = exact || candidates[0];
    if (!pick.el || !document.body.contains(pick.el)) return false;
    pick.el.click();
    return true;
  }

  function coworkOutputs() {
    let node = null;
    const walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let t = walk.nextNode(); t; t = walk.nextNode()) {
      if ((t.nodeValue || '').trim() === 'Outputs') { node = t; break; }
    }
    if (!node || !node.parentElement) return { files: [], collapsed: false, count: 0 };

    const header = node.parentElement.closest('[class*="section-header"]');
    if (!header || !header.parentElement) return { files: [], collapsed: false, count: 0 };

    // Collapsing the panel hides the rows visually but leaves them in the
    // document, so the list survives either way. The heading count is read as
    // a cross-check on what the rows produced, not as a fallback.
    const stated = (header.innerText || '').match(/Outputs\s*(\d+)/);
    const count = stated ? Number(stated[1]) : 0;
    const collapsed = header.getAttribute('aria-expanded') === 'false';

    /*
     * textContent, not innerText. The file-type icon is an icon-font glyph on
     * a CSS pseudo-element, so innerText prefixes every name with a Private
     * Use Area character that neither \s nor trim() removes. textContent does
     * not see pseudo-elements at all. The PUA strip is belt and braces in case
     * a future icon is a real child node.
     */
    const files = [...header.parentElement.querySelectorAll('button')]
      .filter((b) => !header.contains(b))
      .map((b) => ({ name: norm((b.textContent || '').replace(ICON_GLYPH, '')), el: b }))
      .filter((f) => f.name);

    return { files, collapsed, count: count || files.length };
  }

  /* ------------------------------------------------------------------ *
   * Cowork questions
   * ------------------------------------------------------------------ *
   *
   * Cowork does have an API. It is just not the one chat uses, which is why
   * earlier probing under /api/organizations/{org}/cowork/... found nothing
   * but 404s and this fell back to reading the page. The real surface is
   *
   *     /v1/code/sessions/{cse}/events
   *
   * and it needs an `anthropic-version` header or it answers 400. It pages
   * newest first, 500 events a page, and carries `next_cursor` until the
   * sequence numbers reach 1.
   *
   * Telling a human turn from a tool result matters here, because both are
   * `event_type: "user"`. The discriminator is `source`: `client` is you,
   * `worker` is a tool handing back its output. A human turn also carries its
   * text as a plain string, where a tool result carries an array of blocks.
   */
  const COWORK_HEADERS = {
    accept: 'application/json',
    'anthropic-version': '2023-06-01',
  };

  /*
   * Two things ride along inside a human turn that you did not type. The
   * timezone reminder the client injects is a whole message and is dropped.
   * An upload wrapper sits around a real message and is unwrapped, falling
   * back to the file names when the upload carried no words of its own.
   */
  function cleanCoworkText(raw) {
    let t = String(raw || '');
    if (/^\s*<system-reminder>/.test(t)) return '';
    t = t.replace(/<system-reminder>[\s\S]*?<\/system-reminder>/g, ' ');
    const uploads = t.match(/<file_path>([^<]+)<\/file_path>/g);
    t = t.replace(/<uploaded_files>[\s\S]*?<\/uploaded_files>/g, ' ');
    t = norm(t);
    if (!t && uploads) {
      const names = uploads
        .map((m) => m.replace(/<\/?file_path>/g, '').split(/[\\/]/).pop())
        .filter(Boolean);
      if (names.length) t = 'Uploaded ' + names.join(', ');
    }
    return t;
  }

  const MAX_EVENT_PAGES = 20;   // ~10k events; a backstop, not a real limit

  /*
   * onPage is called with the questions found so far after every page, so the
   * rail fills in as the walk proceeds rather than sitting empty until the end.
   * A long session is several megabytes of mostly tool output, and there is no
   * server-side filter for that: limit is honoured, event type filters are not.
   */
  async function fetchCoworkSession(sessionId, onPage) {
    const events = [];
    let cursor = null;
    for (let page = 0; page < MAX_EVENT_PAGES; page++) {
      const url = `/v1/code/sessions/${sessionId}/events?limit=500`
        + (cursor ? `&cursor=${encodeURIComponent(cursor)}` : '');
      const res = await fetch(url, { headers: COWORK_HEADERS });
      if (!res.ok) break;
      const body = await res.json();
      const batch = body && body.data ? body.data : [];
      events.push(...batch);
      if (onPage) onPage(coworkQuestionsFrom(events), coworkWritesFrom(events), events);
      if (!body.next_cursor || !batch.length) break;
      cursor = body.next_cursor;
    }
    return {
      questions: coworkQuestionsFrom(events),
      writes: coworkWritesFrom(events),
      events,
    };
  }

  /*
   * When each file was first written, by sequence number.
   *
   * This is what lets a cowork output sit next to the question that produced
   * it rather than in a heap at the end of the rail. Three tools put a file on
   * disk: Write and Edit name one in `file_path`, SendUserFile lists them in
   * `files`. The earliest sequence wins, because a file that is rewritten four
   * times belongs to the question that first asked for it, not the last one
   * that touched it.
   */
  function coworkWritesFrom(events) {
    const first = new Map();
    const note = (seq, path) => {
      const name = String(path || '').split(/[\\/]/).pop();
      if (!name || !/\.[A-Za-z0-9]{1,5}$/.test(name)) return;
      const k = artifactKey(name);
      if (!first.has(k) || seq < first.get(k)) first.set(k, seq);
    };
    events.forEach((e) => {
      if (e.event_type !== 'assistant') return;
      const c = e.payload && e.payload.message && e.payload.message.content;
      if (!Array.isArray(c)) return;
      const seq = Number(e.sequence_num);
      c.forEach((b) => {
        if (!b || b.type !== 'tool_use') return;
        const input = b.input || {};
        if (b.name === 'Write' || b.name === 'Edit') note(seq, input.file_path);
        if (b.name === 'SendUserFile' && Array.isArray(input.files)) {
          input.files.forEach((f) => note(seq, f));
        }
      });
    });
    return first;
  }

  function coworkQuestionsFrom(events) {
    const seen = new Set();
    return events
      .filter((e) => e.event_type === 'user' && e.source === 'client')
      .filter((e) => e.payload && e.payload.message
        && typeof e.payload.message.content === 'string')
      .sort((a, b) => Number(a.sequence_num) - Number(b.sequence_num))
      .map((e) => ({ seq: Number(e.sequence_num), text: cleanCoworkText(e.payload.message.content) }))
      .filter((q) => q.text && !seen.has(q.seq) && seen.add(q.seq))
      .map((q, i) => ({ n: i + 1, uuid: null, text: q.text, key: keyOf(q.text), seq: q.seq }));
  }

  /*
   * Cowork outputs, placed against the question that produced them.
   *
   * Both sides carry a sequence number now: questions from their own events,
   * files from the Write, Edit or SendUserFile call that first created them.
   * A file belongs after the last question whose sequence precedes its own.
   *
   * A file with no matching write still gets shown, at the end, rather than
   * dropped. That happens when the panel lists something produced by a route
   * these three tools do not cover, and losing it would be worse than putting
   * it in an honest heap.
   */
  function buildCoworkEntries(qs, docs) {
    const out = [];
    const placed = new Map();
    const orphans = [];

    docs.forEach((d) => {
      if (d.seq == null) { orphans.push(d); return; }
      let a = -1;
      qs.forEach((q, i) => { if (q.seq != null && q.seq <= d.seq) a = i; });
      if (a === -1) { orphans.push(d); return; }
      if (!placed.has(a)) placed.set(a, []);
      placed.get(a).push(d);
    });

    qs.forEach((q, i) => {
      out.push({ kind: 'q', q, qi: i });
      (placed.get(i) || []).forEach((d) => out.push({ kind: 'doc', doc: d, anchor: i }));
    });
    orphans.forEach((d) => out.push({ kind: 'doc', doc: d, anchor: null }));
    return out;
  }

  // Used only when the API is unreachable.
  function questionsFromDom() {
    for (const sel of MESSAGE_SELECTORS) {
      let nodes;
      try { nodes = [...document.querySelectorAll(sel)]; } catch (e) { continue; }
      nodes = nodes.filter((n) => !nodes.some((o) => o !== n && o.contains(n)));
      if (nodes.length) {
        return nodes.map((el, i) => {
          const text = norm(el.innerText || el.textContent);
          return { n: i + 1, uuid: null, text, key: keyOf(text) };
        });
      }
    }
    return [];
  }

  /* ------------------------------------------------------------------ *
   * Matching a question to whatever is on screen right now
   * ------------------------------------------------------------------ */
  function mountedNodes() {
    for (const sel of MESSAGE_SELECTORS) {
      let nodes;
      try { nodes = [...document.querySelectorAll(sel)]; } catch (e) { continue; }
      nodes = nodes.filter((n) => !nodes.some((o) => o !== n && o.contains(n)));
      if (nodes.length) return nodes;
    }
    return [];
  }

  function nodeFor(item, nodes) {
    if (!item.key) return null;
    return nodes.find((el) => {
      const k = keyOf(el.innerText || el.textContent);
      return k && (k === item.key || k.startsWith(item.key) || item.key.startsWith(k));
    }) || null;
  }

  /*
   * Step aside when claude.ai opens a right-hand panel.
   *
   * Opening the files panel, an artifact, or a preview docks a panel against
   * the right edge and the rail sits on top of it. Rather than guessing at a
   * class name that will be renamed, this looks for the geometry: a tall block
   * flush with the right edge that starts well inside the viewport. Measured
   * on the files panel it found exactly one candidate, 400px wide, and the
   * rail moves out by that width.
   */
  function panelOffset() {
    const vw = window.innerWidth, vh = window.innerHeight;
    let edge = vw;
    const nodes = document.querySelectorAll('div,aside,section');
    for (const el of nodes) {
      if (el === rail || (rail && rail.contains(el)) || el === peek) continue;
      const r = el.getBoundingClientRect();
      if (r.width < 200 || r.width > vw * 0.8) continue;
      if (r.height < vh * 0.35) continue;
      if (Math.abs(r.right - vw) > 10) continue;
      if (r.left <= vw * 0.15) continue;
      if (r.left < edge) edge = r.left;
    }
    return Math.max(0, Math.round(vw - edge));
  }

  /*
   * How far in the rail sits when no panel is open.
   *
   * It used to be zero, which put the ticks straight through Claude's own
   * scrollbar. Rather than hardcode a clearance that only holds at one window
   * size and zoom level, this measures the scrollbar: where the message
   * scroller's right edge is, and how wide its scrollbar is. On a 1526px
   * viewport that band runs from 18px to 8px in from the edge.
   *
   * The clamp covers the case where the scroller cannot be found at all, or
   * where an overlay scrollbar reports zero width.
   */
  const EDGE_AIR = 14;
  let gapCache = { vw: -1, px: 0 };

  function edgeGap() {
    const vw = window.innerWidth;
    // syncRailOffset runs on every DOM mutation and scroller() walks the
    // document, so this is measured once per window width and then reused.
    if (gapCache.vw === vw) return gapCache.px;
    let band = 18, measured = false;
    const sc = scroller();
    if (sc) {
      const r = sc.getBoundingClientRect();
      const bar = sc.offsetWidth - sc.clientWidth;
      const px = Math.round(vw - (r.right - bar));
      if (px >= 0 && px < 60) { band = px; measured = true; }
    }
    const gap = Math.min(48, Math.max(20, band + EDGE_AIR));
    // Only a real measurement is cached. At boot the scroller may not exist
    // yet, and caching the fallback would freeze it in place for the session.
    if (measured) gapCache = { vw, px: gap };
    return gap;
  }

  let lastOffset = -1;
  function syncRailOffset() {
    if (!rail || !document.body.contains(rail)) return;
    const raw = panelOffset();
    // A few pixels of air so the ticks do not touch the panel edge.
    const offset = raw > 0 ? raw + 6 : edgeGap();
    rail.classList.toggle('cpn-panelled', raw > 0);
    if (offset === lastOffset) return;
    lastOffset = offset;
    rail.style.right = offset + 'px';
    // Keep the peek card clear of both the rail and the panel.
    if (peek) peek.style.right = (offset + 400) + 'px';
  }

  function scroller() {
    const cands = [...document.querySelectorAll('div')].filter((e) => {
      if (e.scrollHeight <= e.clientHeight + 60) return false;
      const oy = getComputedStyle(e).overflowY;
      return oy === 'auto' || oy === 'scroll';
    });
    return cands.sort((a, b) => b.scrollHeight - a.scrollHeight)[0] || null;
  }

  /* ------------------------------------------------------------------ *
   * Size of the conversation, and how much of your plan you have used
   * ------------------------------------------------------------------ *
   *
   * Read this before trusting the number, because one of these two is a
   * measurement and the other is not.
   *
   * The session and weekly percentages are real. They come straight from
   * /api/organizations/<org>/usage, which is the same source the app uses for
   * the bars above the composer, and they are the limits that actually stop
   * you working.
   *
   * The token figure is an estimate of this conversation only, at roughly
   * 3.8 characters per token. Claude exposes no per-conversation context
   * measurement anywhere, so there is nothing better to read. Deliberately
   * shown as an absolute number and never as a percentage of a context
   * window, because the real context also carries your system prompt, your
   * skills, every enabled tool definition, and project knowledge. On the
   * thread this was built against, the conversation measured about 36k
   * tokens while the project behind it held 35 files worth roughly 350k,
   * which Claude retrieves from rather than loading whole. Any percentage
   * built on the conversation alone would read comfortably low at exactly
   * the moment you were in trouble. Watch it climb across a thread and learn
   * your own threshold instead.
   */
  let convChars = 0;
  let usage = null;
  let usageAt = 0;
  let liveLimits = null;      // exact figures pushed down the reply stream
  let docChars = 0;           // text-shaped documents Claude wrote in this chat
  let projectUuid = null;
  let convTurns = [];        // both sides of the thread, for the export
  let convModel = null;      // drives the context bar's denominator
  let convEffort = null;     // high / xhigh / max, drives how fast plan usage burns
  let projectInfo = null;     // counts only, the cheap end of the project API

  const fmtTokens = (n) => {
    if (n >= 1000000) return Math.round(n / 1000000) + 'M';   // 1M windows
    if (n >= 1000) return Math.round(n / 1000) + 'k';
    return String(n);
  };

  /* ------------------------------------------------------------------ *
   * Reading the exact limits out of the reply stream
   * ------------------------------------------------------------------ *
   *
   * /usage returns percentages already rounded to whole numbers. Claude also
   * pushes a message_limit event down the SSE stream while it replies, and
   * that one carries the raw fraction, so the figure is both exact and
   * arrives the instant a reply finishes rather than on the next poll.
   *
   * The shape of that event could not be verified here, because confirming it
   * means sending a message on the account and that is not mine to do. So
   * this hunts for the key anywhere in the payload instead of assuming a
   * path, accepts either a 0 to 1 fraction or a 0 to 100 percentage, and
   * logs the shape once so it can be tightened later. If it never matches,
   * nothing breaks and the polled figures stand.
   */
  let loggedLimitShape = false;

  function findMessageLimit(obj, depth) {
    if (!obj || typeof obj !== 'object' || (depth || 0) > 6) return null;
    if (obj.message_limit && typeof obj.message_limit === 'object') return obj.message_limit;
    for (const k of Object.keys(obj)) {
      const hit = findMessageLimit(obj[k], (depth || 0) + 1);
      if (hit) return hit;
    }
    return null;
  }

  function asPercent(v) {
    if (typeof v !== 'number' || !isFinite(v) || v < 0) return null;
    return v <= 1 ? v * 100 : v;   // 1.0 reads as full, which is the safe way round
  }

  function applyLiveLimits(payload) {
    const ml = findMessageLimit(payload, 0);
    if (!ml) return;
    if (!loggedLimitShape) {
      loggedLimitShape = true;
      console.log('[Claude Prompt Navigator] message_limit shape:', ml);
    }
    const pct = asPercent(ml.utilization) ?? asPercent(ml.percent) ?? asPercent(ml.used);
    if (pct == null) return;
    liveLimits = {
      percent: pct,
      resets_at: ml.resets_at || ml.resetsAt || null,
      type: ml.type || null,
      at: Date.now(),
    };
    renderMeters();
  }

  async function drainStream(res) {
    if (!res || !res.body || !res.body.getReader) return;
    const reader = res.body.getReader();
    const dec = new TextDecoder();
    let buf = '';
    try {
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += dec.decode(value, { stream: true });
        let nl;
        while ((nl = buf.indexOf('\n')) !== -1) {
          const line = buf.slice(0, nl).trim();
          buf = buf.slice(nl + 1);
          if (!line.startsWith('data:')) continue;
          const body = line.slice(5).trim();
          if (!body || body === '[DONE]') continue;
          let obj;
          try { obj = JSON.parse(body); } catch (e) { continue; }
          applyLiveLimits(obj);
        }
        if (buf.length > 500000) buf = buf.slice(-50000);   // never grow unbounded
      }
    } catch (e) { /* stream aborted, nothing to do */ }

    renderMeters();

    // Belt and braces. If the stream never carried a message_limit event, the
    // polled figures would otherwise sit stale for up to a minute after a
    // reply that just consumed quota. Expiring the cache forces a fresh read.
    usageAt = 0;
    fetchUsage().then(renderMeters).catch(() => {});
  }

  function installStreamWatcher() {
    const orig = window.fetch;
    if (typeof orig !== 'function' || orig.__cpnWrapped) return;
    const wrapped = function (...args) {
      const p = orig.apply(this, args);
      try {
        const a = args[0];
        const url = typeof a === 'string' ? a : (a && a.url) || '';
        if (/\/(retry_)?completion(\?|$)/.test(url)) {
          // Submitting is itself a signal: quota is about to move, so the
          // polled figures are stale from this instant. Refresh at the start
          // as well as at the end, so the header reacts when you press send
          // rather than only when the reply lands.
          usageAt = 0;
          fetchUsage().then(renderMeters).catch(() => {});
          p.then((res) => { try { drainStream(res.clone()); } catch (e) {} }).catch(() => {});
        }
      } catch (e) {}
      return p;
    };
    wrapped.__cpnWrapped = true;
    try { window.fetch = wrapped; } catch (e) { /* frozen, fall back to polling */ }
  }

  async function fetchUsage() {
    if (usage && Date.now() - usageAt < 60000) return usage;
    // Cowork never resolves an org through a conversation fetch, so fall back
    // to the first one. Plan usage is per account, not per conversation.
    let org = resolvedOrg;
    if (!org) { try { org = (await getOrgIds())[0]; } catch (e) { return null; } }
    if (!org) return null;
    let res;
    try {
      res = await fetch(`/api/organizations/${org}/usage`,
        { headers: { accept: 'application/json' } });
    } catch (e) { return usage; }
    if (!res.ok) return usage;
    const data = await res.json();
    const pick = (kind) => (data.limits || []).find((l) => l.kind === kind) || null;
    usage = { session: pick('session'), weekly: pick('weekly_all') };
    usageAt = Date.now();
    recordWeeklySample(usage.weekly);
    return usage;
  }

  /*
   * Weekly burn rate.
   *
   * The weekly percentage is polled once a minute, so successive samples give
   * a rate of change. Two samples at least ten minutes apart are required
   * before this says anything, otherwise a single idle minute reads as zero
   * burn and a single busy one reads as catastrophic. It is a projection of
   * recent pace, not a promise.
   */
  const SAMPLE_KEY = 'cpn-weekly-samples';

  /*
   * Held in localStorage, not just in memory. Ten minutes of history is the
   * entry price for this line, and an in-memory buffer pays it again after
   * every reload — which for a browser tab is often enough that the line
   * would rarely be there when wanted.
   */
  const SAMPLE_MAX_AGE = 6 * 3600000;

  let weeklySamples = (() => {
    try {
      const v = JSON.parse(localStorage.getItem(SAMPLE_KEY) || '[]');
      if (!Array.isArray(v)) return [];
      // Yesterday's pace is not this afternoon's. Anything older is dropped.
      const cut = Date.now() - SAMPLE_MAX_AGE;
      return v.filter((s) => s && typeof s.pct === 'number' && s.t > cut);
    } catch (e) { return []; }
  })();

  function saveWeeklySamples() {
    try { localStorage.setItem(SAMPLE_KEY, JSON.stringify(weeklySamples)); } catch (e) {}
  }

  function recordWeeklySample(w) {
    if (!w || typeof w.percent !== 'number') return;
    /*
     * A weekly reset drops the percentage to near zero. Kept alongside the
     * pre-reset samples that produces a large negative rate, and the line
     * would read flat for as long as the old readings survive in the buffer.
     * The window it was measuring is gone, so the readings go with it.
     */
    const last = weeklySamples[weeklySamples.length - 1];
    if (last && w.percent < last.pct - 5) {
      weeklySamples = [];
    } else if (last && last.pct === w.percent) {
      return;                                     // no movement, no new sample
    }
    weeklySamples.push({ t: Date.now(), pct: w.percent });
    const cut = Date.now() - SAMPLE_MAX_AGE;
    while (weeklySamples.length > 40 || (weeklySamples.length > 2 && weeklySamples[0].t < cut)) {
      weeklySamples.shift();
    }
    saveWeeklySamples();
  }

  function weeklyBurn(w) {
    if (!w || weeklySamples.length < 2) return null;
    const first = weeklySamples[0], last = weeklySamples[weeklySamples.length - 1];
    const hours = (last.t - first.t) / 3600000;
    if (hours < 0.16) return null;                // under ten minutes of history
    const perHour = (last.pct - first.pct) / hours;
    if (perHour <= 0.05) return { perHour, flat: true };
    const hoursToFull = (100 - last.pct) / perHour;
    const msToReset = Date.parse(w.resets_at) - Date.now();
    return {
      perHour,
      flat: false,
      hoursToFull,
      beatsReset: isFinite(msToReset) && hoursToFull * 3600000 < msToReset,
    };
  }

  function fmtUntil(iso) {
    if (!iso) return '';
    const ms = Date.parse(iso) - Date.now();
    if (isNaN(ms) || ms <= 0) return 'due';
    const m = Math.round(ms / 60000);
    if (m < 60) return `${m}m`;
    const h = Math.floor(m / 60);
    return h < 24 ? `${h}h ${m % 60}m` : `${Math.floor(h / 24)}d ${h % 24}h`;
  }

  /*
   * How far through a reset window we are.
   *
   * The bucket names in Claude's own usage payload give the durations away:
   * five_hour and seven_day. The weekly limit's resets_at matches seven_day's
   * exactly, which is the confirmation that the mapping is right.
   */
  const WINDOW_MS = {
    Session: 5 * 60 * 60 * 1000,
    Weekly: 7 * 24 * 60 * 60 * 1000,
  };

  function elapsedPct(resetsAt, windowMs) {
    if (!resetsAt || !windowMs) return null;
    const left = Date.parse(resetsAt) - Date.now();
    if (isNaN(left)) return null;
    return Math.max(0, Math.min(100, ((windowMs - left) / windowMs) * 100));
  }

  function setRow(r, pct, labelText, rightText, warn, title, timePct) {
    if (!r) return;
    if (pct == null) { r.row.style.display = 'none'; return; }
    r.row.style.display = '';
    r.fill.style.width = Math.max(0, Math.min(100, pct)) + '%';
    r.label.textContent = labelText;
    r.right.textContent = rightText || '';
    r.row.classList.toggle('cpn-warn', !!warn);
    if (title) r.row.title = title;

    // The marker rides the clock and is independent of the fill. A bar with no
    // reset window, such as the context one, simply has no marker.
    if (timePct == null) {
      r.mark.classList.add('cpn-hidden');
    } else {
      r.mark.classList.remove('cpn-hidden');
      r.mark.style.left = Math.max(0, Math.min(100, timePct)) + '%';
    }
  }

  /*
   * Which model and effort this chat is on.
   *
   * Both come from the conversation itself, so switching model or effort and
   * sending a message updates the header on the next refetch rather than
   * needing anything configured here. Effort is shown because it is the lever
   * that decides how fast the session and weekly bars move, but note it does
   * not change the context estimate: effort buys thinking, and thinking is not
   * resent on later turns.
   */
  function renderModelLine() {
    if (!modelLine) return;
    if (mode !== 'chat' || !convModel) { modelLine.style.display = 'none'; return; }
    modelLine.style.display = '';
    const label = MODEL_LABELS[convModel] || convModel;
    modelLine.textContent = convEffort ? `${label} · ${convEffort} effort` : label;
    const win = CONTEXT_WINDOWS[convModel];
    modelLine.title = `This chat runs ${convModel}`
      + (convEffort ? ` at ${convEffort} effort.` : '.')
      + (win ? `\n\nThat model's documented context window is ${fmtTokens(win)}, `
        + 'though claude.ai does not report how much of it a chat actually gets, '
        + 'which is why nothing here is shown as a percentage of it.' : '')
      + '\n\nEffort drives how quickly your session and weekly usage burn. It does '
      + 'not change the conversation size above, because it buys thinking and '
      + 'thinking is not resent on later turns.';
  }

  /*
   * Conversation size, stated as a measurement and nothing more.
   *
   * This used to be a percentage bar against the model's window, with a
   * projection of how many questions remained. Both are gone, because neither
   * could be made honest.
   *
   * The projection said things like 609 more questions. That is arithmetic on
   * a denominator nobody can justify: claude.ai reports no usable context
   * budget anywhere. There is no context window field on the conversation, no
   * compaction or threshold signal, and no reachable models endpoint. The 1M
   * figure is the model's documented API capability, not a promise about what
   * this product allots a chat, and the numerator ignored the system prompt,
   * skills, tool definitions and project knowledge, which on a project chat
   * are the larger share.
   *
   * A measured token count is defensible. A share of an unknown budget is not.
   * So this shows what was counted and lets the number speak by growing.
   */
  function renderContextLine(talk, docs) {
    if (!ctxLine) return;
    if (mode !== 'chat' || (!talk && !docs)) { ctxLine.style.display = 'none'; return; }
    ctxLine.style.display = '';
    ctxLine.textContent = docs
      ? `≈ ${fmtTokens(talk)} in messages · ${fmtTokens(docs)} in documents`
      : `≈ ${fmtTokens(talk)} in messages`;
    ctxLine.title = 'Measured from this conversation at about 3.8 characters per '
      + 'token, counting the live branch only, and skipping thinking because it '
      + 'is not resent.\n\n'
      + (docs ? 'The document figure is content that went through the window when '
        + 'each file was written. Whether it is still carried on later turns is '
        + 'not something the page reports.\n\n' : '')
      + 'Deliberately not shown as a percentage. claude.ai publishes no usable '
      + 'context budget, and this count cannot see your system prompt, skills, '
      + 'tool definitions'
      + (projectInfo
        ? `, or this project's ${projectInfo.docs_count} documents and `
          + `${projectInfo.files_count} files.`
        : '.')
      + '\n\nWatch it grow across a thread rather than reading it as a fill level.';
  }

  function renderMeters() {
    if (!sessionRow || !weeklyRow) return;
    renderModelLine();

    renderContextLine(Math.round(convChars / 3.8), Math.round(docChars / 3.8));


    if (!usage || (!usage.session && !usage.weekly)) {
      setRow(sessionRow, null);
      setRow(weeklyRow, null);
      return;
    }

    // A live figure is only trusted for a while, and only against whichever
    // limit Claude currently marks active, since the stream does not say
    // which bucket it belongs to.
    const live = liveLimits && Date.now() - liveLimits.at < 600000 ? liveLimits : null;
    const present = [usage.session, usage.weekly].filter(Boolean);
    const binding = present.find((l) => l.is_active)
      || present.slice().sort((a, b) => b.percent - a.percent)[0]
      || null;

    const draw = (r, l, name) => {
      if (!l) { setRow(r, null); return; }
      const exact = live && l === binding;
      const pct = exact ? live.percent : l.percent;
      const shown = exact ? pct.toFixed(1) : Math.round(pct);
      const timePct = elapsedPct(l.resets_at, WINDOW_MS[name]);
      const pace = (timePct == null) ? ''
        : (pct <= timePct
          ? '\n\nThe fill sits behind the marker, so you are using this window '
            + 'slower than it is running out.'
          : '\n\nThe fill has passed the marker, so at this rate you will hit '
            + 'the limit before the window resets.');
      setRow(r, pct, `${name} ${shown}%`, fmtUntil(l.resets_at),
        l.severity && l.severity !== 'normal',
        `${name} usage, read from Claude directly.\n`
        + `Resets ${l.resets_at ? new Date(l.resets_at).toLocaleString() : 'unknown'}\n`
        + (exact ? 'This figure came from the reply stream and is exact.'
                 : 'Polled every minute and rounded by Claude.')
        + `\n\nThe white marker is the clock, not your usage. It shows how much of `
        + `the ${name === 'Session' ? 'five hour' : 'seven day'} window has passed `
        + 'and reaches the end as the timer hits zero.' + pace,
        timePct);
    };

    draw(sessionRow, usage.session, 'Session');
    draw(weeklyRow, usage.weekly, 'Weekly');
    renderBurnLine(usage.weekly);
  }

  function renderBurnLine(w) {
    if (!burnLine) return;
    const b = weeklyBurn(w);
    if (!b) {
      // Says nothing until it has something measured to say.
      burnLine.style.display = 'none';
      return;
    }
    burnLine.style.display = '';
    if (b.flat) {
      burnLine.textContent = 'weekly usage flat';
      burnLine.classList.remove('cpn-warn');
    } else {
      const h = b.hoursToFull;
      const when = h < 1 ? `${Math.round(h * 60)}m` : `${h.toFixed(h < 10 ? 1 : 0)}h`;
      burnLine.textContent = b.beatsReset
        ? `at this rate, limit in ${when}`
        : `at this rate, resets before the limit`;
      burnLine.classList.toggle('cpn-warn', !!b.beatsReset);
    }
    burnLine.title = `Weekly usage has moved ${weeklyBurn(w).perHour.toFixed(2)} per cent `
      + `per hour across ${weeklySamples.length} samples this session.\n\n`
      + 'A projection of recent pace, not a promise. It stays quiet until there '
      + 'are at least two samples ten minutes apart, because one idle minute '
      + 'reads as zero burn and one busy minute reads as a crisis.';
  }

  /*
   * The cache countdown used to live here and has been removed.
   *
   * It counted down from the last reply against an invented five minute
   * constant. claude.ai reports nothing about prompt caching at all: searching
   * the conversation payload and the usage payload for cache, ttl or ephemeral
   * fields returns nothing, so there was no way to know the real duration or
   * even whether a cache existed. Anthropic runs more than one cache window,
   * so the constant was a coin flip.
   *
   * It also said almost nothing in practice. Any thread you return to is older
   * than five minutes, so the line read "Cache expired" nearly always — on the
   * thread this was removed against, the last reply was 2,183 minutes old. And
   * there was no action it enabled: on a plan you are bounded by the session
   * and weekly percentages, which are measured and still shown.
   */

  /* ------------------------------------------------------------------ *
   * Rail
   * ------------------------------------------------------------------ */
  let rail = null, railList = null, headCount = null, rows = [], activeIndex = -1;
  let modelLine = null, ctxLine = null, burnLine = null;
  let sessionRow = null, weeklyRow = null;

  function makeMeterRow() {
    const row = document.createElement('div');
    row.className = 'cpn-row';
    const label = document.createElement('span');
    label.className = 'cpn-row-label';
    const bar = document.createElement('div');
    bar.className = 'cpn-bar';
    const fill = document.createElement('div');
    fill.className = 'cpn-bar-fill';
    const mark = document.createElement('div');
    mark.className = 'cpn-bar-mark';
    bar.append(fill, mark);
    const right = document.createElement('span');
    right.className = 'cpn-row-right';
    row.append(label, bar, right);
    return { row, label, bar, fill, mark, right };
  }
  let questions = [], documents = [], entries = [];
  let coworkDocCount = 0;
  let peek = null;

  function ensureRail() {
    if (rail && document.body.contains(rail)) return rail;
    rail = document.createElement('div');
    rail.className = 'cpn-rail';
    if (localStorage.getItem('cpn-pinned') === '1') rail.classList.add('cpn-pinned');

    const head = document.createElement('div');
    head.className = 'cpn-head';

    const top = document.createElement('div');
    top.className = 'cpn-head-top';
    headCount = document.createElement('span');
    headCount.textContent = 'Your questions';
    const pin = document.createElement('button');
    pin.className = 'cpn-pin';
    pin.type = 'button';
    pin.title = 'Keep the list open';
    pin.textContent = '◉';
    pin.addEventListener('click', (e) => {
      e.stopPropagation();
      const on = rail.classList.toggle('cpn-pinned');
      localStorage.setItem('cpn-pinned', on ? '1' : '0');
    });
    /*
     * Handoff. You already write these by hand — C08_Cowork_Handover.md was
     * one — so this assembles the same block from data the rail has already
     * loaded, and puts it on the clipboard. No extra requests.
     */
    const hand = document.createElement('button');
    hand.className = 'cpn-pin';
    hand.type = 'button';
    hand.title = 'Copy a handoff block for starting a fresh thread';
    hand.textContent = '⎘';
    hand.addEventListener('click', (e) => {
      e.stopPropagation();
      const text = buildHandoff();
      navigator.clipboard.writeText(text).then(() => {
        const was = hand.textContent;
        hand.textContent = '✓';
        setTimeout(() => { hand.textContent = was; }, 1400);
      }).catch(() => {});
    });

    /*
     * The palette needs a visible way in. Alt+K is not a chord anyone guesses,
     * and Ctrl+K was not available — claude.ai binds it to its own palette.
     */
    const find = document.createElement('button');
    find.className = 'cpn-pin';
    find.type = 'button';
    find.title = 'Search your questions across every thread  (Alt+K)';
    find.textContent = '⌕';
    find.addEventListener('click', (e) => {
      e.stopPropagation();
      isPaletteOpen() ? closePalette() : openPalette();
    });

    const save = document.createElement('button');
    save.className = 'cpn-pin';
    save.type = 'button';
    save.title = 'Download the whole thread as markdown, both sides of it';
    save.textContent = '⤓';
    save.addEventListener('click', (e) => {
      e.stopPropagation();
      downloadExport();
      const was = save.textContent;
      save.textContent = '✓';
      setTimeout(() => { save.textContent = was; }, 1400);
    });

    top.append(headCount, find, save, hand, pin);

    modelLine = document.createElement('div');
    modelLine.className = 'cpn-meter';
    ctxLine = document.createElement('div');
    ctxLine.className = 'cpn-meter';
    sessionRow = makeMeterRow();
    weeklyRow = makeMeterRow();

    burnLine = document.createElement('div');
    burnLine.className = 'cpn-meter';
    burnLine.style.display = 'none';

    head.append(top, modelLine, ctxLine, sessionRow.row, weeklyRow.row, burnLine);
    rail.appendChild(head);

    railList = document.createElement('div');
    railList.className = 'cpn-list';
    rail.appendChild(railList);

    rail.addEventListener('mouseleave', hidePeek);
    document.body.appendChild(rail);
    lastOffset = -1;
    syncRailOffset();
    return rail;
  }

  /* ------------------------------------------------------------------ *
   * Export
   * ------------------------------------------------------------------ *
   *
   * The handoff button carries the shape of a thread: what was asked, what
   * came out. This carries the content, both sides of it, as a markdown file.
   *
   * Nothing extra is fetched. A chat thread was already downloaded whole to
   * draw the rail, and a cowork session's events were already walked for its
   * questions. Both are kept for exactly this.
   */
  function coworkTurns() {
    return coworkEvents
      .filter((e) => e.event_type === 'user' || e.event_type === 'assistant')
      .sort((a, b) => Number(a.sequence_num) - Number(b.sequence_num))
      .map((e) => {
        const msg = e.payload && e.payload.message;
        if (!msg) return null;
        if (e.event_type === 'user') {
          if (e.source !== 'client' || typeof msg.content !== 'string') return null;
          const text = cleanCoworkText(msg.content);
          return text ? { who: 'you', text } : null;
        }
        // Assistant turns are blocks; only the prose is wanted, not tool calls.
        const blocks = Array.isArray(msg.content) ? msg.content : [];
        const text = norm(blocks.filter((b) => b && b.type === 'text')
          .map((b) => b.text || '').join('\n'));
        return text ? { who: 'claude', text } : null;
      })
      .filter(Boolean);
  }

  function buildExport() {
    const title = document.title.replace(/ - Claude$/, '');
    const turns = mode === 'cowork' ? coworkTurns() : convTurns;
    const lines = [`# ${title}`, ''];

    lines.push(`Exported ${new Date().toLocaleString()}`);
    lines.push(`Source: ${location.href}`);
    if (convModel) lines.push(`Model: ${convModel}${convEffort ? ` (${convEffort} effort)` : ''}`);
    lines.push(`${questions.length} question${questions.length === 1 ? '' : 's'}`
      + (documents.length ? `, ${documents.length} file${documents.length === 1 ? '' : 's'}` : ''));
    lines.push('');

    if (documents.length) {
      lines.push('## Files produced', '');
      documents.forEach((d) => {
        lines.push(`- ${d.name}${d.onDisk ? '' : '  (no longer downloadable)'}`);
      });
      lines.push('');
    }

    lines.push('## Transcript', '');
    if (!turns.length) {
      lines.push('_The transcript was not available. This happens on a Cowork '
        + 'session whose event walk had not finished, or a chat the API declined._');
    }
    turns.forEach((t) => {
      lines.push(t.who === 'you' ? '### You' : '### Claude', '', t.text, '');
    });
    return lines.join('\n');
  }

  function downloadExport() {
    const title = document.title.replace(/ - Claude$/, '');
    const safe = (title || 'claude-thread').replace(/[^A-Za-z0-9]+/g, '_').slice(0, 60);
    const blob = new Blob([buildExport()], { type: 'text/markdown;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${safe}.md`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    // Revoked on a timer: revoking synchronously can cancel the download in
    // Chrome before it has read the blob.
    setTimeout(() => URL.revokeObjectURL(url), 30000);
  }

  function buildHandoff() {
    const title = document.title.replace(/ - Claude$/, '');
    const lines = [];
    lines.push(`Handoff from an earlier Claude thread: ${title}`);
    lines.push('');
    if (convModel) {
      lines.push(`That thread ran ${MODEL_LABELS[convModel] || convModel}`
        + (convEffort ? ` at ${convEffort} effort.` : '.'));
      lines.push('');
    }
    lines.push(`WHAT I ASKED (${questions.length} in order)`);
    questions.forEach((q) => {
      const t = q.text.replace(/\s+/g, ' ').trim();
      lines.push(`${q.n}. ${t.length > 160 ? t.slice(0, 160).trimEnd() + '…' : t}`);
    });
    if (documents.length) {
      lines.push('');
      lines.push(`WHAT IT PRODUCED (${documents.length})`);
      documents.forEach((d) => {
        lines.push(`- ${d.name}${d.onDisk ? '' : '  (no longer downloadable, would need regenerating)'}`);
      });
    }
    lines.push('');
    lines.push('WHAT I NEED FROM YOU');
    lines.push('Pick up from the last item above. Ask me for anything in that list '
      + 'you need the contents of, rather than assuming what it said.');
    return lines.join('\n');
  }

  function render() {
    ensureRail();
    rows.forEach((r) => r.el.remove());
    rows = [];

    entries = mode === 'cowork'
      ? buildCoworkEntries(questions, documents)
      : buildEntries(questions, documents);

    if (!entries.length) { rail.style.display = 'none'; return; }
    rail.style.display = '';

    if (mode === 'cowork') {
      const docs = coworkDocCount
        ? ` · ${coworkDocCount} output${coworkDocCount === 1 ? '' : 's'}`
        : '';
      // "on screen" is only honest while the walk has not landed. Once it has,
      // this is the whole session and saying otherwise undersells it.
      const full = coworkQuestions.length > 0;
      headCount.textContent = full
        ? `${questions.length} question${questions.length === 1 ? '' : 's'}${docs}`
        : `${questions.length} on screen${docs}`;
      headCount.title = full
        ? 'Every question in this session, read from its event log. The outputs '
          + 'come from the Outputs panel.'
        : 'Reading the session event log. Until it lands these are only the '
          + 'messages the page has mounted.';
    } else {
      const q = `${questions.length} question${questions.length === 1 ? '' : 's'}`;
      headCount.textContent = documents.length
        ? `${q} · ${documents.length} doc${documents.length === 1 ? '' : 's'}`
        : q;
    }

    entries.forEach((entry, i) => {
      const row = document.createElement('div');
      const tick = document.createElement('span');
      tick.className = 'cpn-tick';
      const label = document.createElement('span');
      label.className = 'cpn-label';

      if (entry.kind === 'doc') {
        const d = entry.doc;
        row.className = d.onDisk ? 'cpn-item cpn-doc' : 'cpn-item cpn-doc cpn-gone';
        if (d.el) {
          // Cowork. It has no byte count, so the tooltip does not invent one.
          row.title = d.name
            + (entry.anchor == null
              ? '\nProduced by this session.'
              : `\nCreated after question ${entry.anchor + 1}`)
            + '\nClick to open it';
        } else if (entry.anchor == null) {
          row.title = `${d.name}\nProduced by this session. Click to open it.`;
        } else {
          row.title = d.onDisk
            ? `${d.name}\n${Math.max(1, Math.round(d.size / 1024))} KB`
            + `\nCreated after question ${entry.anchor + 1}`
            + '\nClick to open it'
            : `${d.name}\nCreated after question ${entry.anchor + 1}, but no longer in the `
            + 'sandbox. Claude clears older files, so this one cannot be downloaded again '
            + 'without regenerating it.\nClick to go to the question that made it.';
        }
        const icon = document.createElement('span');
        icon.className = 'cpn-doc-icon';
        icon.textContent = d.onDisk ? '◆' : '◇';
        label.append(icon, document.createTextNode(d.name));
      } else {
        const item = entry.q;
        row.className = 'cpn-item';
        row.title = item.text.slice(0, 400);
        const short = item.text.length > CONFIG.labelChars
          ? item.text.slice(0, CONFIG.labelChars).trimEnd() + '…'
          : item.text;
        const num = document.createElement('span');
        num.className = 'cpn-num';
        num.textContent = String(item.n);
        label.append(num, document.createTextNode(short));
      }

      row.append(tick, label);
      row.addEventListener('click', () => jumpTo(i));
      railList.appendChild(row);
      rows.push({ el: row, entry });
    });

    renderMeters();
    syncState();
  }

  /* Marks which questions are currently loaded, and which one you are on. */
  function syncState() {
    if (!questions.length) return;
    const nodes = mountedNodes();
    let best = -1, bestTop = -Infinity;
    const line = window.innerHeight * 0.35;

    rows.forEach((r, i) => {
      if (r.entry.kind !== 'q') { r.node = null; return; }
      const node = nodeFor(r.entry.q, nodes);
      r.node = node;
      r.el.classList.toggle('cpn-loaded', !!node);
      if (node) {
        const top = node.getBoundingClientRect().top;
        if (top <= line && top > bestTop) { bestTop = top; best = i; }
      }
    });

    if (best === -1) {
      // Nothing above the line is loaded; fall back to the first loaded one.
      const first = rows.findIndex((r) => r.node);
      if (first !== -1) best = first;
    }
    setActive(best);
  }

  function setActive(i) {
    if (i === -1 || i === activeIndex) return;
    rows.forEach((r, n) => r.el.classList.toggle('cpn-active', n === i));
    activeIndex = i;
    const cur = rows[i];
    if (cur && railList && railList.scrollHeight > railList.clientHeight) {
      railList.scrollTo({ top: cur.el.offsetTop - railList.clientHeight / 2, behavior: 'smooth' });
    }
  }

  function reveal(node, i) {
    // Instant, not smooth. claude.ai's scroll container ignores smooth
    // programmatic scrolling entirely: the call returns, nothing moves, and
    // the message stays thousands of pixels off screen. Verified on 9 Aug 2026.
    node.scrollIntoView({ block: 'center' });
    node.classList.add('cpn-flash');
    setTimeout(() => node.classList.add('cpn-flash-out'), CONFIG.flashMs - 600);
    setTimeout(() => node.classList.remove('cpn-flash', 'cpn-flash-out'), CONFIG.flashMs);
    setActive(i);
  }

  async function jumpTo(i) {
    const entry = entries[i];
    if (!entry) return;

    /*
     * A row labelled with a filename opens that file. Scrolling to the
     * question that produced it was the old behaviour and it answered a
     * question nobody asked: the question rows are already right there if
     * context is what you wanted.
     *
     * The exception is a file the sandbox has cleared. There is nothing to
     * open, so where it was made is the only useful thing left, and that is
     * what the hollow marker does. Both are stated in the row's tooltip.
     */
    if (entry.kind === 'doc') {
      /*
       * Cowork rows carry their own button from the Outputs panel. Keyed off
       * the button, not off a missing anchor: cowork files are anchored now,
       * and testing for a null anchor would have quietly sent every one of
       * them down the chat path.
       */
      const own = entry.doc.el;
      if (own && document.body.contains(own)) { own.click(); return; }
      if (entry.anchor == null) return;
      if (entry.doc.onDisk && openArtifact(entry.doc.name)) return;
      const target = rows.findIndex((r) => r.entry.kind === 'q' && r.entry.qi === entry.anchor);
      if (target !== -1) return jumpTo(target);
      return;
    }

    const item = entry.q;
    hidePeek();

    let node = nodeFor(item, mountedNodes());
    if (node) { reveal(node, i); return; }

    // Not loaded into the page. Show the question immediately so the click
    // always does something, move to roughly the right part of the thread,
    // then watch briefly in case the app happens to mount it.
    //
    // Deliberately one scroll and no more. Each programmatic scroll makes the
    // virtualiser tear down and rebuild large chunks of the message tree, and
    // a loop that keeps scrolling to hunt for a message locks the tab up for
    // several seconds. Testing on a 14 question thread showed the target
    // almost never mounts from a scripted scroll anyway. Only real scrolling
    // reliably loads it, which is what the note on the card tells you.
    showPeek(i);

    const sc = scroller();
    if (sc && questions.length > 1) {
      const frac = entry.qi / (questions.length - 1);
      sc.scrollTop = Math.round(sc.scrollHeight * frac);
      for (let attempt = 0; attempt < CONFIG.settleTries; attempt++) {
        await sleep(CONFIG.settleMs);
        node = nodeFor(item, mountedNodes());
        if (node) { hidePeek(); reveal(node, i); return; }
      }
    }
  }

  function showPeek(i) {
    hidePeek();
    const item = entries[i] && entries[i].q;
    if (!item) return;
    peek = document.createElement('div');
    peek.className = 'cpn-peek';
    peek.textContent = item.text.slice(0, 1200);
    const note = document.createElement('div');
    note.className = 'cpn-peek-note';
    note.textContent = 'Claude has not loaded this part of the chat into the page, '
      + 'so there is nothing to scroll to yet. Scroll up a little and it will appear.';
    peek.appendChild(note);
    document.body.appendChild(peek);

    const row = rows[i] && rows[i].el.getBoundingClientRect();
    const top = row ? Math.min(Math.max(8, row.top - 20), window.innerHeight - 200) : 80;
    peek.style.top = top + 'px';
    setTimeout(hidePeek, 9000);
  }

  function hidePeek() {
    if (peek) { peek.remove(); peek = null; }
  }

  /* ------------------------------------------------------------------ *
   * Loading and staying in sync
   * ------------------------------------------------------------------ */
  let currentRoute = null;
  let mode = 'chat';
  let domSignature = '';
  let loading = false;

  /*
   * The event walk is per session and runs once. Several megabytes of mostly
   * tool output is not something to repeat on every DOM mutation, and the
   * questions in a session only ever grow at the end.
   */
  let coworkQuestions = [];
  let coworkWrites = new Map();
  let coworkEvents = [];
  let coworkFetchedFor = null;
  let coworkFetching = false;

  function loadCoworkQuestions(sessionId) {
    if (coworkFetching || coworkFetchedFor === sessionId) return;
    coworkFetching = true;
    fetchCoworkSession(sessionId, (partial, writes, events) => {
      // Still on the same session? A fast click away must not repaint the rail.
      if (currentRoute !== 'cowork:' + sessionId) return;
      coworkWrites = writes;
      coworkEvents = events;
      if (partial.length <= questions.length) return;
      coworkQuestions = partial;
      questions = partial;
      domSignature = '';           // force the next tick to rebuild cleanly
      render();
    })
      .then((all) => {
        if (currentRoute !== 'cowork:' + sessionId) return;
        coworkQuestions = all.questions;
        coworkWrites = all.writes;
        coworkEvents = all.events;
        coworkFetchedFor = sessionId;
        if (all.questions.length) {
          questions = all.questions;
          domSignature = '';
          render();
        }
      })
      .catch((e) => {
        console.warn('[Claude Prompt Navigator] Cowork event walk failed, '
          + 'falling back to the messages on screen:', e.message);
      })
      .finally(() => { coworkFetching = false; });
  }

  async function load(r) {
    if (r.mode === 'cowork') {
      // Whatever is mounted goes up straight away, so the rail is never blank
      // while the event walk runs. The walk then replaces it with the full list.
      questions = coworkQuestions.length ? coworkQuestions : questionsFromDom();
      const out = coworkOutputs();
      coworkDocCount = out.count;
      /*
       * No anchor is available. Cowork virtualises its transcript the same way
       * chat does, so at most a couple of messages are mounted and there is no
       * message index to tie a file to. Rather than guess at a position, these
       * sit together at the end of the rail in the order the panel lists them,
       * which is the order they were created.
       */
      documents = out.files.map((f) => ({
        name: f.name, onDisk: true, el: f.el,
        seq: coworkWrites.has(artifactKey(f.name)) ? coworkWrites.get(artifactKey(f.name)) : null,
      }));
      convChars = 0;
      domSignature = questions.map((q) => q.key).join('|')
        + '#' + out.count + '#' + out.files.map((f) => f.name).join('|');
      activeIndex = -1;
      await fetchUsage();
      render();
      loadCoworkQuestions(r.id);
      return;
    }
    if (loading) return;
    loading = true;
    try {
      questions = await fetchQuestions(r.id);
      documents = await fetchDocuments(r.id);
      await fetchProjectInfo();
      await fetchUsage();
    } catch (e) {
      console.warn('[Claude Prompt Navigator] API unavailable, falling back to the '
        + 'visible messages only:', e.message);
      questions = questionsFromDom();
      documents = [];
    } finally {
      loading = false;
    }
    activeIndex = -1;
    render();

    // Free tier-2 indexing: these questions were fetched to draw the rail.
    if (r.mode === 'chat' && questions.length) {
      recordQuestions(r.id, document.title.replace(/ - Claude$/, ''), questions);
    }
    consumePendingJump();
  }

  let refetchTimer = null;
  function maybeRefetch() {
    // A message on screen that is not in our list means you sent a new one.
    const nodes = mountedNodes();
    const unknown = nodes.some((el) => {
      const k = keyOf(el.innerText || el.textContent);
      return k && !questions.some((q) => q.key === k || k.startsWith(q.key) || q.key.startsWith(k));
    });
    if (!unknown) return;
    clearTimeout(refetchTimer);
    refetchTimer = setTimeout(() => {
      const r = route();
      if (r && r.mode === 'chat') load(r);
    }, CONFIG.refetchMs);
  }

  function tick() {
    const r = route();
    const key = r ? `${r.mode}:${r.id}` : null;

    if (key !== currentRoute) {
      currentRoute = key;
      mode = r ? r.mode : 'chat';
      questions = [];
      documents = [];
      if (coworkFetchedFor !== (r ? r.id : null)) coworkQuestions = [];
      convChars = 0;
      convModel = null;
      convEffort = null;
      domSignature = '';
      activeIndex = -1;
      render();
      if (r) load(r);
      return;
    }
    if (!r) return;

    if (r.mode === 'cowork') {
      // The rendered set changes as you scroll, so rebuild when it does. The
      // outputs move independently: Claude writes a file without you asking a
      // new question, so they carry their own part of the signature.
      const fresh = questionsFromDom();
      const out = coworkOutputs();
      const sig = fresh.map((q) => q.key).join('|')
        + '#' + out.count + '#' + out.files.map((f) => f.name).join('|');
      if (sig !== domSignature) {
        domSignature = sig;
        /*
         * Once the event walk has run, its list is the whole session and the
         * mounted messages are a two-item subset of it. Overwriting the full
         * list with that subset is exactly the bug this path used to have.
         * The DOM is still read, but only to work out what is on screen.
         */
        questions = coworkQuestions.length ? coworkQuestions : fresh;
        coworkDocCount = out.count;
        documents = out.files.map((f) => ({
          name: f.name, onDisk: true, el: f.el,
          seq: coworkWrites.has(artifactKey(f.name))
            ? coworkWrites.get(artifactKey(f.name)) : null,
        }));
        activeIndex = -1;
        render();
      } else if (questions.length) {
        syncState();
      }
      return;
    }

    if (questions.length) { syncState(); maybeRefetch(); }
  }

  function throttle(fn, ms) {
    let waiting = false;
    return function () {
      if (waiting) return;
      waiting = true;
      setTimeout(() => { waiting = false; fn(); }, ms);
    };
  }

  /* ================================================================== *
   * Cross-thread search
   * ================================================================== *
   *
   * Measured on the account this was built against:
   *
   *   conversations                       544
   *   with a populated summary            537
   *   one list call                       1.77 MB in 809 ms
   *   one conversation's messages         75 KB in 630 ms
   *   indexing all of them up front       5.7 minutes, 40 MB
   *
   * So there is no upfront crawl. Two tiers instead:
   *
   *   Tier 1  one list call gives name and summary for every thread.
   *   Tier 2  the rail already fetches every question when you open a
   *           thread, so those get written to the index for free. Threads
   *           you have worked in become searchable word for word.
   *
   * claude.ai has no search endpoint — /search, /chat_conversations/search
   * and the rest return 404 or 400 — so the matching happens locally.
   */
  const DB_NAME = 'cpn-index';
  const DB_VERSION = 1;
  let db = null;

  /* In-memory mirror. The palette reads only from here, never the network,
     and every searchable string is lowercased once on the way in. */
  let threadIndex = [];      // { id, name, summary, hay, updated }
  let questionIndex = [];    // { id, convId, convName, n, text, hay }
  let indexReady = false;

  function openDb() {
    return new Promise((resolve) => {
      let req;
      try { req = indexedDB.open(DB_NAME, DB_VERSION); }
      catch (e) { return resolve(null); }
      req.onupgradeneeded = () => {
        const d = req.result;
        if (!d.objectStoreNames.contains('threads')) d.createObjectStore('threads', { keyPath: 'id' });
        if (!d.objectStoreNames.contains('questions')) d.createObjectStore('questions', { keyPath: 'key' });
        if (!d.objectStoreNames.contains('meta')) d.createObjectStore('meta', { keyPath: 'k' });
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => resolve(null);
    });
  }

  function idbAll(store) {
    return new Promise((resolve) => {
      if (!db) return resolve([]);
      let tx;
      try { tx = db.transaction(store, 'readonly'); } catch (e) { return resolve([]); }
      const req = tx.objectStore(store).getAll();
      req.onsuccess = () => resolve(req.result || []);
      req.onerror = () => resolve([]);
    });
  }

  function idbPut(store, rows) {
    return new Promise((resolve) => {
      if (!db || !rows.length) return resolve();
      let tx;
      try { tx = db.transaction(store, 'readwrite'); } catch (e) { return resolve(); }
      const os = tx.objectStore(store);
      rows.forEach((r) => { try { os.put(r); } catch (e) {} });
      tx.oncomplete = () => resolve();
      tx.onerror = () => resolve();
    });
  }

  const lower = (s) => (s || '').toLowerCase();

  /* Tier 1. One call, and only when the cache has gone stale. */
  async function syncThreads(force) {
    const meta = await idbAll('meta');
    const last = (meta.find((m) => m.k === 'threadsSyncedAt') || {}).v || 0;
    if (!force && Date.now() - last < 3600000 && threadIndex.length) return;

    let org = resolvedOrg;
    if (!org) { try { org = (await getOrgIds())[0]; } catch (e) { return; } }
    if (!org) return;

    let list;
    try {
      const r = await fetch(`/api/organizations/${org}/chat_conversations`,
        { headers: { accept: 'application/json' } });
      if (!r.ok) return;
      list = await r.json();
    } catch (e) { return; }
    if (!Array.isArray(list)) return;

    const rows = list.map((c) => {
      const name = c.name || '(untitled)';
      const summary = c.summary || '';
      return {
        id: c.uuid, name, summary,
        updated: Date.parse(c.updated_at) || 0,
        hay: lower(name + ' ' + summary),
      };
    });
    threadIndex = rows.sort((a, b) => b.updated - a.updated);
    await idbPut('threads', rows);
    await idbPut('meta', [{ k: 'threadsSyncedAt', v: Date.now() }]);
  }

  /* Tier 2. Free: these questions were already fetched to draw the rail. */
  async function recordQuestions(convId, convName, qs) {
    if (!convId || !qs.length) return;
    const rows = qs.map((q) => ({
      key: convId + ':' + q.n,
      convId, convName, n: q.n, text: q.text,
      hay: lower(q.text),
    }));
    const byKey = new Map(questionIndex.map((r) => [r.key, r]));
    rows.forEach((r) => byKey.set(r.key, r));
    questionIndex = [...byKey.values()];
    await idbPut('questions', rows);
  }

  async function hydrateIndex() {
    db = await openDb();
    const [t, q] = await Promise.all([idbAll('threads'), idbAll('questions')]);
    if (t.length) threadIndex = t.sort((a, b) => b.updated - a.updated);
    if (q.length) questionIndex = q;
    indexReady = true;
  }

  /* ------------------------------------------------------------------ *
   * The palette
   * ------------------------------------------------------------------ */
  let palette = null, paletteInput = null, paletteList = null, paletteHint = null;
  let paletteRows = [], paletteSel = 0, prefetched = new Set();

  /*
   * Coverage. Tier 2 only holds threads you have opened, so a search that
   * finds nothing has two possible meanings: the question does not exist, or
   * the thread holding it has never been opened here. Without this line you
   * cannot tell those apart, which makes an empty result unreadable rather
   * than merely disappointing. It is the reason the number is on screen.
   */
  function coverage() {
    const deep = new Set();
    for (const r of questionIndex) deep.add(r.convId);
    return { deep: deep.size, total: threadIndex.length };
  }

  function coverageLine() {
    const c = coverage();
    if (!c.total) return 'Building the index…';
    return `${c.deep} of ${c.total} threads searched word for word · `
      + 'the rest by name and summary until you open them';
  }

  function buildPalette() {
    if (palette && document.body.contains(palette)) return palette;
    palette = document.createElement('div');
    palette.className = 'cpn-pal';
    const box = document.createElement('div');
    box.className = 'cpn-pal-box';
    paletteInput = document.createElement('input');
    paletteInput.className = 'cpn-pal-input';
    paletteInput.type = 'text';
    paletteInput.placeholder = 'Search your questions and threads…';
    paletteInput.spellcheck = false;
    paletteList = document.createElement('div');
    paletteList.className = 'cpn-pal-list';
    paletteHint = document.createElement('div');
    paletteHint.className = 'cpn-pal-hint';
    box.append(paletteInput, paletteList, paletteHint);
    palette.appendChild(box);

    palette.addEventListener('mousedown', (e) => { if (e.target === palette) closePalette(); });
    paletteInput.addEventListener('input', () => runSearch(paletteInput.value));
    paletteInput.addEventListener('keydown', onPaletteKey);
    document.body.appendChild(palette);
    return palette;
  }

  function openPalette() {
    buildPalette();
    palette.classList.add('cpn-pal-open');
    paletteInput.value = '';
    paletteInput.focus();
    runSearch('');
    // Refresh tier 1 quietly behind the open palette; it never blocks typing.
    syncThreads(false).then(() => { if (isPaletteOpen()) runSearch(paletteInput.value); });
  }

  function closePalette() {
    if (palette) palette.classList.remove('cpn-pal-open');
  }

  function isPaletteOpen() {
    return !!palette && palette.classList.contains('cpn-pal-open');
  }

  /*
   * Search runs against strings lowercased once at index time, caps the
   * rendered rows, and paints inside a single animation frame. Nothing here
   * touches the network, which is what keeps typing smooth.
   */
  const MAX_ROWS = 50;
  let searchFrame = null;

  function runSearch(raw) {
    const q = lower(raw.trim());
    const here = currentRoute && currentRoute.startsWith('chat:')
      ? currentRoute.slice(5) : null;
    const rows = [];

    if (!q) {
      questions.slice(0, 8).forEach((x) => rows.push(
        { kind: 'q', convId: here, label: `${x.n}. ${x.text}`, sub: 'this thread', n: x.n }));
      threadIndex.slice(0, MAX_ROWS - rows.length).forEach((t) => rows.push(
        { kind: 't', convId: t.id, label: t.name, sub: 'thread' }));
    } else {
      // Questions in the thread you are already looking at come first.
      questions.forEach((x) => {
        if (rows.length >= MAX_ROWS) return;
        if (lower(x.text).indexOf(q) !== -1) {
          rows.push({ kind: 'q', convId: here, label: `${x.n}. ${x.text}`, sub: 'this thread', n: x.n });
        }
      });
      for (const r of questionIndex) {
        if (rows.length >= MAX_ROWS) break;
        if (r.convId === here) continue;
        if (r.hay.indexOf(q) !== -1) {
          rows.push({ kind: 'q', convId: r.convId, label: `${r.n}. ${r.text}`, sub: r.convName, n: r.n });
        }
      }
      for (const t of threadIndex) {
        if (rows.length >= MAX_ROWS) break;
        if (t.hay.indexOf(q) !== -1 && !rows.some((x) => x.convId === t.id && x.kind === 't')) {
          rows.push({ kind: 't', convId: t.id, label: t.name, sub: 'thread' });
        }
      }
    }

    paletteRows = rows;
    paletteSel = 0;
    if (searchFrame) cancelAnimationFrame(searchFrame);
    searchFrame = requestAnimationFrame(paintPalette);
  }

  function paintPalette() {
    searchFrame = null;
    paletteList.textContent = '';
    paletteHint.textContent = 'Enter to open · Esc to close · ' + coverageLine();
    if (!paletteRows.length) {
      const empty = document.createElement('div');
      empty.className = 'cpn-pal-empty';
      empty.textContent = indexReady && threadIndex.length
        ? 'Nothing matched. Threads you have not opened are searchable by name only.'
        : 'Building the index…';
      paletteList.appendChild(empty);
      return;
    }
    paletteRows.forEach((r, i) => {
      const el = document.createElement('div');
      el.className = 'cpn-pal-row' + (i === paletteSel ? ' cpn-pal-on' : '');
      const icon = document.createElement('span');
      icon.className = 'cpn-pal-icon';
      icon.textContent = r.kind === 'q' ? '—' : '◇';
      const main = document.createElement('span');
      main.className = 'cpn-pal-main';
      main.textContent = r.label;
      const sub = document.createElement('span');
      sub.className = 'cpn-pal-sub';
      sub.textContent = r.sub;
      el.append(icon, main, sub);
      el.addEventListener('mouseenter', () => select(i));
      el.addEventListener('click', () => activate(i));
      paletteList.appendChild(el);
    });
    prefetchSelected();
  }

  function select(i) {
    if (i === paletteSel) return;
    paletteSel = Math.max(0, Math.min(paletteRows.length - 1, i));
    [...paletteList.children].forEach((el, n) =>
      el.classList.toggle('cpn-pal-on', n === paletteSel));
    const el = paletteList.children[paletteSel];
    if (el) el.scrollIntoView({ block: 'nearest' });
    prefetchSelected();
  }

  /* Warms the thread under the cursor so Enter has nothing left to wait on. */
  function prefetchSelected() {
    const r = paletteRows[paletteSel];
    if (!r || !r.convId || r.convId === (currentRoute || '').slice(5)) return;
    if (prefetched.has(r.convId)) return;
    prefetched.add(r.convId);
    if (!resolvedOrg) return;
    fetch(`/api/organizations/${resolvedOrg}/chat_conversations/${r.convId}`
      + '?tree=True&rendering_mode=messages', { headers: { accept: 'application/json' } })
      .then((res) => (res.ok ? res.json() : null))
      .then((data) => {
        if (!data) return;
        const msgs = (data.chat_messages || []).filter((m) => m.sender === 'human');
        recordQuestions(r.convId, data.name || '', msgs.map((m, i) => ({
          n: i + 1, text: textOfMessage(m) || '(attachment only)',
        })));
      })
      .catch(() => {});
  }

  function onPaletteKey(e) {
    if (e.key === 'Escape') { e.preventDefault(); closePalette(); return; }
    if (e.key === 'ArrowDown') { e.preventDefault(); select(paletteSel + 1); return; }
    if (e.key === 'ArrowUp') { e.preventDefault(); select(paletteSel - 1); return; }
    if (e.key === 'Enter') { e.preventDefault(); activate(paletteSel); }
  }

  function activate(i) {
    const r = paletteRows[i];
    if (!r) return;
    closePalette();
    const here = (currentRoute || '').startsWith('chat:') ? currentRoute.slice(5) : null;

    if (r.convId && r.convId !== here) {
      // Client-side navigation, so no page reload.
      history.pushState({}, '', `/chat/${r.convId}`);
      window.dispatchEvent(new PopStateEvent('popstate'));
      if (r.kind === 'q' && r.n) pendingJump = { convId: r.convId, n: r.n };
      tick();
      return;
    }
    if (r.kind === 'q' && r.n) {
      const idx = entries.findIndex((en) => en.kind === 'q' && en.q && en.q.n === r.n);
      if (idx !== -1) jumpTo(idx);
    }
  }

  /* Set when a jump crosses threads; consumed once the new thread loads. */
  let pendingJump = null;
  function consumePendingJump() {
    if (!pendingJump) return;
    const here = (currentRoute || '').startsWith('chat:') ? currentRoute.slice(5) : null;
    if (pendingJump.convId !== here) return;
    const want = pendingJump.n;
    pendingJump = null;
    const idx = entries.findIndex((en) => en.kind === 'q' && en.q && en.q.n === want);
    if (idx !== -1) jumpTo(idx);
  }

  function start() {
    injectStyles();
    installStreamWatcher();
    tick();

    // The cache figure counts down and the window markers creep, so the whole
    // meter block gets a second hand. It touches no network.
    setInterval(renderMeters, 1000);

    // The app is a single page app, so the URL changes without a reload.
    setInterval(tick, 900);

    // Plan usage moves on its own, independently of anything you do here.
    setInterval(async () => { await fetchUsage(); renderMeters(); }, 60000);

    new MutationObserver(throttle(() => {
      if (questions.length) syncState();
      syncRailOffset();      // a panel opening or closing is a DOM change
    }, 350)).observe(document.body, { childList: true, subtree: true });

    document.addEventListener('scroll', throttle(syncState, 140), true);
    window.addEventListener('resize', throttle(() => {
      syncState();
      syncRailOffset();
    }, 250));

    // Index: hydrate from disk first so the palette is usable immediately,
    // then refresh tier 1 once the page has settled.
    hydrateIndex().then(() => {
      if ('requestIdleCallback' in window) requestIdleCallback(() => syncThreads(false));
      else setTimeout(() => syncThreads(false), 3000);
    });

    /*
     * Alt+K, not Ctrl+K.
     *
     * Ctrl+K is claude.ai's own command palette — "Search or start a chat",
     * with quick actions and recents, and the site prints the shortcut in its
     * own footer. Binding it here would have swallowed a first-party feature.
     * Alt+K also matches the Alt+Arrow navigation already in this script.
     *
     * The two searches do different jobs: Claude's finds threads by title,
     * this one finds the questions inside them.
     */
    window.addEventListener('keydown', (e) => {
      if (!e.altKey || e.ctrlKey || e.metaKey) return;
      if (e.key.toLowerCase() !== 'k') return;
      e.preventDefault();
      isPaletteOpen() ? closePalette() : openPalette();
    }, true);

    if (CONFIG.hotkeys) {
      window.addEventListener('keydown', (e) => {
        if (!e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
        if (e.key === 'ArrowUp') { e.preventDefault(); jumpTo(Math.max(0, activeIndex - 1)); }
        // Clamp against entries, not questions: document rows sit in the same
        // list, so entries is the longer of the two.
        if (e.key === 'ArrowDown') { e.preventDefault(); jumpTo(Math.min(entries.length - 1, activeIndex + 1)); }
      });
    }

    window.cpnDebug = () => ({
      route: currentRoute,
      mode,
      complete: mode === 'chat',
      questions: questions.length,
      model: convModel,
      effort: convEffort,
      contextWindow: CONTEXT_WINDOWS[convModel] || CONFIG.contextLimitTokens,
      estTokens: convChars || docChars
        ? Math.round((convChars + docChars) / 3.8) : null,
      sessionPercent: usage && usage.session ? usage.session.percent : null,
      weeklyPercent: usage && usage.weekly ? usage.weekly.percent : null,
      documents: documents.length,
      stillOnDisk: documents.filter((d) => d.onDisk).length,
      documentNames: documents.map((d) => (d.onDisk ? '' : '(gone) ') + d.name),
      loadedInPage: rows.filter((r) => r.node).length,
      active: activeIndex + 1,
      first: questions[0] ? questions[0].text.slice(0, 60) : null,
      last: questions.length ? questions[questions.length - 1].text.slice(0, 60) : null,
    });
    console.log('[Claude Prompt Navigator] v2 ready. Run cpnDebug() for status.');
  }

  // The watcher has to be in place before the app takes its own reference to
  // fetch, which is why the script now runs at document-start. Everything that
  // touches the DOM still waits for the document.
  installStreamWatcher();

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start);
  } else {
    start();
  }
})();
