#!/usr/bin/env node
/*
 * Lists a mod folder in CLAUDE_CODE_PLUGIN_DIRS, in the env block of a
 * Claude Code settings.json, so every new chat loads that mod; or takes it
 * out again. Everything else in the file stays as it was.
 *
 *   node mods.js <settings.json> add <folder>      prints added | unchanged
 *   node mods.js <settings.json> remove <folder>   prints removed | unchanged
 *   node mods.js <settings.json> check <folder>    prints present | missing
 *
 * The variable holds folders separated by the platform's list separator
 * (; on Windows). A folder already listed is not listed twice. The file is
 * backed up before it is changed.
 */
'use strict';
const fs = require('fs');
const path = require('path');

const [file, action, folder] = process.argv.slice(2);
if (!file || !folder || !['add', 'remove', 'check'].includes(action)) {
  console.error('usage: node mods.js <settings.json> add|remove|check <folder>');
  process.exit(2);
}

const KEY = 'CLAUDE_CODE_PLUGIN_DIRS';
const want = path.resolve(folder);
const same = (a) => path.resolve(a).toLowerCase() === want.toLowerCase();

let text = '';
try { text = fs.readFileSync(file, 'utf8'); } catch (e) { if (e.code !== 'ENOENT') throw e; }
const bom = text.charCodeAt(0) === 0xfeff;
const settings = text.trim() ? JSON.parse(bom ? text.slice(1) : text) : {};
const env = settings.env || {};
const listed = String(env[KEY] || '').split(path.delimiter).map((s) => s.trim()).filter(Boolean);
const has = listed.some(same);

if (action === 'check') { console.log(has ? 'present' : 'missing'); process.exit(0); }
if ((action === 'add') === has) { console.log('unchanged'); process.exit(0); }

const next = action === 'add' ? [...listed, want] : listed.filter((d) => !same(d));
if (next.length) env[KEY] = next.join(path.delimiter); else delete env[KEY];
if (Object.keys(env).length) settings.env = env; else delete settings.env;

if (text) fs.copyFileSync(file, file + '.bak-prompt-advisor');
const eol = text.includes('\r\n') ? '\r\n' : '\n';
fs.writeFileSync(file, (bom ? '﻿' : '') + JSON.stringify(settings, null, 2).replace(/\n/g, eol) + eol);
console.log(action === 'add' ? 'added' : 'removed');
