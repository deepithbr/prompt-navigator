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
 * Every 30 seconds, the shortest an alarm allows, this reads the ten most
 * recent conversations with your claude.ai sign-in and tells the board what
 * changed: each conversation's name, state and address, nothing from inside
 * it. Nothing happens while Brave is closed or you are signed out.
 */
const CLAUDE = 'https://claude.ai';
const JSON_ONLY = { accept: 'application/json' };

chrome.alarms.create('claude-account', { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener((a) => { if (a.name === 'claude-account') pollClaude(); });
chrome.runtime.onStartup.addListener(pollClaude);
chrome.runtime.onInstalled.addListener(pollClaude);

async function claudeOrg() {
  const kept = await chrome.storage.session.get('org');
  if (kept.org) return kept.org;
  const r = await fetch(`${CLAUDE}/api/organizations`, { headers: JSON_ONLY, credentials: 'include' });
  if (!r.ok) return null;
  const orgs = await r.json();
  const org = ((orgs || []).find((o) => (o.capabilities || []).includes('chat')) || (orgs || [])[0] || {}).uuid;
  if (org) await chrome.storage.session.set({ org });
  return org || null;
}

let polling = false;
async function pollClaude() {
  if (polling) return;
  polling = true;
  try {
    const org = await claudeOrg();
    if (!org) return;
    const r = await fetch(`${CLAUDE}/api/organizations/${org}/chat_conversations?limit=10`,
      { headers: JSON_ONLY, credentials: 'include' });
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
    } else if (settled && settled !== was.settled && Date.parse(settled) > primedAt) {
      // Started and finished between two looks.
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
