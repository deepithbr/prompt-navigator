#!/usr/bin/env node
/*
 * Adds the task board's three Claude Code hooks to a settings.json, or takes
 * them out again, leaving everything else in the file as it was.
 *
 *   node hooks.js <settings.json> add      prints added | unchanged
 *   node hooks.js <settings.json> remove   prints removed | unchanged
 *   node hooks.js <settings.json> check    prints present | missing
 *
 * A hook is the board's if its command posts to 127.0.0.1:47823. Running
 * add twice leaves one copy. The file is backed up before it is changed.
 */
'use strict';
const fs = require('fs');

const [file, action] = process.argv.slice(2);
if (!file || !['add', 'remove', 'check'].includes(action)) {
  console.error('usage: node hooks.js <settings.json> add|remove|check');
  process.exit(2);
}

const BOARD = '127.0.0.1:47823';
const post = (path) => `curl -s -m 2 -o /dev/null -H 'X-Prompt-Advisor: 1' --data-binary @- http://${BOARD}/hook/claude-code/${path} || true`;
const OURS = {
  UserPromptSubmit: { hooks: [{ type: 'command', async: true, command: post('start') }] },
  Stop: { hooks: [{ type: 'command', async: true, command: post('stop') }] },
  Notification: { matcher: 'permission_prompt|elicitation_dialog', hooks: [{ type: 'command', async: true, command: post('needs') }] },
};

const isOurs = (group) => (group.hooks || []).some((h) => String(h.command || '').includes(BOARD));

let text = '';
try { text = fs.readFileSync(file, 'utf8'); } catch (e) { if (e.code !== 'ENOENT') throw e; }
const bom = text.charCodeAt(0) === 0xfeff;
const settings = text.trim() ? JSON.parse(bom ? text.slice(1) : text) : {};
const before = JSON.stringify(settings);

if (action === 'check') {
  const hooks = settings.hooks || {};
  const all = Object.keys(OURS).every((event) => (hooks[event] || []).some(isOurs));
  console.log(all ? 'present' : 'missing');
  process.exit(0);
}

const hooks = settings.hooks || {};
const mine = (h) => String(h.command || '').includes(BOARD);

// Already there, exactly as they should be, once each: leave the file alone.
if (action === 'add' && Object.keys(OURS).every((event) => {
  const found = (hooks[event] || []).flatMap((g) => (g.hooks || []).filter(mine));
  return found.length === 1 && found[0].command === OURS[event].hooks[0].command;
})) { console.log('unchanged'); process.exit(0); }
for (const event of Object.keys(OURS)) {
  // Only the board's own commands go; anything sharing a group with them stays.
  const kept = [];
  for (const group of hooks[event] || []) {
    if (!isOurs(group)) { kept.push(group); continue; }
    const rest = (group.hooks || []).filter((h) => !mine(h));
    if (rest.length) kept.push({ ...group, hooks: rest });
  }
  if (action === 'add') kept.push(OURS[event]);
  if (kept.length) hooks[event] = kept; else delete hooks[event];
}
if (Object.keys(hooks).length) settings.hooks = hooks; else delete settings.hooks;

if (JSON.stringify(settings) === before) { console.log('unchanged'); process.exit(0); }
if (text) fs.copyFileSync(file, file + '.bak-prompt-advisor');
const eol = text.includes('\r\n') ? '\r\n' : '\n';
fs.writeFileSync(file, (bom ? '﻿' : '') + JSON.stringify(settings, null, 2).replace(/\n/g, eol) + eol);
console.log(action === 'add' ? 'added' : 'removed');
