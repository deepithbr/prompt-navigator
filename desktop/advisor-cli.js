// Prompt Advisor rules, served to the desktop companion.
//
// The companion (PromptAdvisor.exe) reads the text box and model button of the
// Claude and ChatGPT desktop apps through Windows accessibility, and asks this
// process what to say. The rules are not copied here: they are loaded from
// prompt-advisor.user.js itself, so a fix to the browser rules is a fix to the
// desktop ones too.
//
// Protocol: one JSON object per line on stdin, one per line on stdout.
//   in   { "app": "claude" | "chatgpt", "text": "...", "button": "Model: Opus 5.5 High",
//          "mode": "chat" | "work" }   (ChatGPT only: Codex and Work use one ladder)
//   out  { "show": true, "tone": "down" | "up" | "ok", "label": "...", "verb": "...",
//          "why": "...", "cur": "...", "detail": "..." }
//
// Nothing here touches the network. node advisor-cli.js --selftest checks it.

'use strict';

const fs = require('fs');
const path = require('path');
const readline = require('readline');

function loadRules() {
  const file = path.join(__dirname, '..', 'prompt-advisor.user.js');
  const src = fs.readFileSync(file, 'utf8');
  const from = src.indexOf('  // Kind of thinking, lightest first.');
  const to = src.indexOf('   * The page');
  if (from < 0 || to < 0) throw new Error('prompt-advisor.user.js has moved its markers');
  const body = src.slice(from, src.lastIndexOf('/*', to));
  // The rules run in the page normally. Here there is no page, so the few
  // globals they touch are stood in for. localStorage is read inside a try.
  const make = new Function(
    'const CONFIG = { rereadTokens: 20000 };\n' + body
    + '\nreturn { audit, pick, parseClaudeLabel, claudeAdvice, parseGptButton, gptAdvice, effortNote };');
  return make();
}

const R = loadRules();
const KIND = ['Transform', 'Create or analyse', 'Judgment'];
const DEPTH = ['One shot', 'Some steps', 'Many dependent steps', 'Hard reasoning'];

function effortLine(app, adv) {
  const note = R.effortNote(app, adv.agree ? adv.cur : adv.label, adv.work);
  return note ? 'Effort: ' + note : '';
}

function answer(req) {
  const text = String(req.text || '').trim();
  if (text.length < 15) return { show: false };
  const a = R.audit(text, {});
  const cur = req.app === 'chatgpt' ? R.parseGptButton(String(req.button || ''), req.mode === 'work')
    : R.parseClaudeLabel(String(req.button || ''));
  if (!cur) return { show: false, reason: 'could not read the model button' };
  const adv = req.app === 'chatgpt' ? R.gptAdvice(a, cur) : R.claudeAdvice(a, cur);
  if (!adv) return { show: false };
  const detail = [
    'Kind: ' + KIND[a.kind] + '. ' + a.why.join('. '),
    'Reasoning: ' + DEPTH[a.depth] + '. ' + a.depthWhy.join(', '),
    'Size: ~' + a.tokens + ' tokens typed',
    effortLine(req.app, adv),
    adv.alt ? 'Or ' + adv.alt + ' on the model you have. Anthropic says tuning effort is often a better lever than switching models.' : '',
    req.app === 'claude' && cur.effort == null
      ? 'Your effort is not shown on this button, so only the model is compared.' : '',
  ].filter(Boolean).join('\n');
  if (adv.agree) {
    return { show: true, tone: 'ok', label: adv.cur, verb: ' fits this', why: a.why[0], cur: adv.cur, detail };
  }
  return {
    show: true,
    tone: adv.dir < 0 ? 'down' : 'up',
    label: adv.label,
    verb: adv.dir < 0 ? ' would do this' : ' fits this better',
    why: a.why[0],
    cur: adv.cur,
    detail,
  };
}

if (process.argv.includes('--selftest')) {
  const cases = [
    { app: 'claude', button: 'Model: Opus 5.5 High', text: 'Make this email more formal: hi all, lab shut tomorrow' },
    { app: 'claude', button: 'Model: Opus 5.5', text: 'Write a lesson plan for a 50 minute session on photosynthesis for class 8.' },
    { app: 'claude', button: 'Model: Opus 5.5 Medium', text: 'Write a lesson plan for a 50 minute session on photosynthesis for class 8.' },
    { app: 'claude', button: 'Model: Opus 5.5 High', text: 'Should we move the BCA to a trimester system? Weigh the trade-offs and forecast every scenario.' },
    { app: 'chatgpt', button: 'GPT-5.6 Sol Medium', text: 'Fix the typos in this paragraph: Teh students will recieve there marks on monday.' },
    { app: 'chatgpt', button: 'GPT-5.6 Sol Medium', text: 'i need a strategy for marketing a product..let me know how to do it' },
    { app: 'chatgpt', button: 'GPT-6 Astra Light', text: 'i need a strategy for marketing a product..let me know how to do it' },
    { app: 'chatgpt', mode: 'work', button: 'GPT-5.6 Sol Medium', text: 'Summarise this circular in three bullet points for the staff group.' },
  ];
  for (const c of cases) {
    const r = answer(c);
    console.log(`${c.app.padEnd(8)} ${(c.mode || '').padEnd(5)} ${c.button.padEnd(22)} -> ${r.show ? r.label + r.verb : '(silent)'}`);
  }
  process.exit(0);
}

const rl = readline.createInterface({ input: process.stdin });
rl.on('line', (line) => {
  let out;
  try { out = answer(JSON.parse(line)); }
  catch (e) { out = { show: false, error: String(e && e.message || e) }; }
  process.stdout.write(JSON.stringify(out) + '\n');
});
// The companion closing, or being killed, closes this pipe. Leave with it.
rl.on('close', () => process.exit(0));
