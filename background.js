/*
 * Passes task-reporter.js's reports to the Prompt Advisor task board, which
 * listens on this PC only, at 127.0.0.1:47823. A page cannot reach it itself:
 * the site's own security rules block it, and the board refuses anything
 * without the X-Prompt-Advisor header.
 */
const BOARD = 'http://127.0.0.1:47823/event';

function toBoard(body) {
  return fetch(BOARD, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Prompt-Advisor': '1' },
    body: JSON.stringify(body),
  }).catch(() => { /* the board is not running */ });
}

chrome.runtime.onMessage.addListener((msg) => {
  if (!msg || msg.type !== 'task' || !msg.body) return;
  toBoard(msg.body);
});

/*
 * Claude chats and Cowork tasks, wherever they run.
 *
 * The Claude desktop app freezes its screen while minimized and posts no
 * notification then either, so the board cannot see a chat there finish.
 * claude.ai can. Each conversation in the account's list carries live_status
 * while a reply is being written, needs_input while it waits on you, and
 * last_settled_at once it has finished. That covers a chat sent from the
 * desktop app, a Brave tab or a phone alike, and Cowork tasks, which sit in
 * the same list.
 *
 * It reads the ten most recent conversations with your claude.ai sign-in and
 * tells the board what changed: each conversation's name, state and address,
 * nothing from inside it. How often depends on what the board says first:
 * every 30 seconds, the shortest an alarm allows, while a Claude run is going
 * or out of sight; every two minutes while the Claude app is open and quiet;
 * not at all while it is closed and nothing runs, or the board is not
 * running. Nothing happens while Brave is closed or you are signed out.
 */
const QUIET_CHECK_MS = 115000;

async function claudeState() {
  try {
    const r = await fetch('http://127.0.0.1:47823/claude-state', { headers: { 'X-Prompt-Advisor': '1' } });
    return r.ok ? await r.json() : null;
  } catch (e) {
    return null;
  }
}
const CLAUDE = 'https://claude.ai';
const JSON_ONLY = { accept: 'application/json' };

chrome.alarms.create('claude-account', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener((a) => { if (a.name === 'claude-account') pollClaude(); });
chrome.runtime.onStartup.addListener(pollClaude);
chrome.runtime.onInstalled.addListener(pollClaude);

async function claudeOrg() {
  const kept = await chrome.storage.session.get('org');
  if (kept.org) return kept.org;
  const r = await fetch(`${CLAUDE}/api/organizations`, { headers: JSON_ONLY, credentials: 'include', signal: AbortSignal.timeout(10000) });
  if (!r.ok) return null;
  const orgs = await r.json();
  const org = ((orgs || []).find((o) => (o.capabilities || []).includes('chat')) || (orgs || [])[0] || {}).uuid;
  if (org) await chrome.storage.session.set({ org });
  return org || null;
}

// While a run is going, a second look 15 seconds after each alarm halves
// the wait for its finish; an alarm cannot fire more often than every 30.
const BUSY_RECHECK_MS = 15000;

let polling = false;
async function pollClaude() {
  if (polling) return;
  polling = true;
  let again = false;
  try {
    const state = await claudeState();
    if (!state) return;
    if (!state.busy) {
      if (!state.app) return;
      const kept = await chrome.storage.session.get('claudeCheckedAt');
      if (Date.now() - (kept.claudeCheckedAt || 0) < QUIET_CHECK_MS) return;
    }
    await chrome.storage.session.set({ claudeCheckedAt: Date.now() });
    const org = await claudeOrg();
    if (!org) return;
    const r = await fetch(`${CLAUDE}/api/organizations/${org}/chat_conversations?limit=10`,
      { headers: JSON_ONLY, credentials: 'include', signal: AbortSignal.timeout(10000) });
    again = state.busy;
    // Signed out, or switched account: find the organisation again next time.
    if (r.status === 401 || r.status === 403) { await chrome.storage.session.remove('org'); return; }
    if (!r.ok) return;
    const list = await r.json();
    if (Array.isArray(list)) await compare(list);
  } catch (e) {
    /* offline, or claude.ai changed its answer; try again next time */
  } finally {
    polling = false;
  }
  if (again) setTimeout(pollClaude, BUSY_RECHECK_MS);
}

/*
 * What was seen last time is kept in extension storage, because the
 * background script is stopped between alarms. The first pass after
 * installing only takes stock: chats that finished before it are not news,
 * though one still running is.
 */
async function compare(list) {
  const kept = await chrome.storage.local.get(['claudeSeen', 'claudePrimedAt']);
  const seen = kept.claudeSeen || {};
  const primedAt = kept.claudePrimedAt || 0;
  const next = {};

  for (const c of list) {
    const was = seen[c.uuid] || {};
    const state = c.needs_input ? 'needs' : c.live_status ? 'working' : 'done';
    const settled = c.last_settled_at || '';
    const busy = was.state === 'working' || was.state === 'needs';
    // One run of a conversation keeps one id from start to finish.
    const run = busy && was.run ? was.run : (c.live_status_started_at || settled || c.updated_at || '');
    next[c.uuid] = { state, settled, run };

    if (!primedAt) {
      if (state !== 'done') report(c, state, run);
    } else if (state !== 'done') {
      if (state !== was.state) report(c, state, run);
    } else if (busy) {
      report(c, 'done', run);
    } else if (settled && settled !== was.settled && Date.parse(settled) > Math.max(primedAt, Date.now() - 180000)) {
      // Started and finished between two looks. After a long pause in
      // checking, only a finish from the last three minutes is still news.
      report(c, 'done', settled);
    }
  }
  await chrome.storage.local.set({ claudeSeen: next, claudePrimedAt: primedAt || Date.now() });
}

function report(c, state, run) {
  toBoard({
    source: 'claude-account',
    id: c.uuid + ':' + run,
    title: c.name || 'New chat',
    state,
    url: `${CLAUDE}/chat/${c.uuid}`,
    visible: false,
  });
}
