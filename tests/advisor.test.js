// Runs the prompt advisor's scoring rules against sample prompts.
// node tests/advisor.test.js
const fs = require('fs');
const path = require('path');
const src = fs.readFileSync(path.join(__dirname, '..', 'prompt-advisor.user.js'), 'utf8');
const from = src.indexOf('  // Kind of thinking, lightest first.');
const to = src.indexOf('  /* The button reads');
// The ChatGPT section sits after the Claude grid; pull workEffort out on its own.
const workSrc = src.slice(src.indexOf('  function workEffort'), src.indexOf('  function gptCurrent'));
const { audit, pick, NAMES, EFFORTS, inherit, handoffWhy, longThreadDepth, workEffort } = new Function(
  'const CONFIG = { rereadTokens: 20000 };\n' + src.slice(from, to)
  + workSrc + '\nreturn { audit, pick, NAMES, EFFORTS, inherit, handoffWhy, longThreadDepth, workEffort };')();

const cases = [
  // [prompt, expected model, expected effort or null]
  ['Fix the typos in this paragraph: Teh students will recieve there marks on monday.', 'haiku', null],
  ['Summarise this circular in three bullet points for the staff group.', 'haiku', null],
  ['Translate this notice into Kannada.', 'haiku', null],
  ['Make this email more formal.', 'haiku', null],
  ['Fix the typos in the fee notice before it goes to parents.', 'sonnet', 0],
  ['What is a credit hour?', 'haiku', null],
  ['Write a lesson plan for a 50 minute session on photosynthesis for class 8.', 'sonnet', 0],
  ['Draft a question paper for BCA semester 3 data structures. It must have exactly 5 units, each unit must carry 14 marks, no more than 2 questions per unit can be numerical, and every question must map to a course outcome.', 'sonnet', 2],
  ['Should we raise the BCA fee to 1.2 lakh next year?', 'opus', 1],
  ['Critique my plan to split the BCA into two sections and tell me the weakest assumption.', 'opus', 1],
  ['Why does this function return undefined?\n```js\nfunction f(a){ a.map(x => x*2) }\n```', 'sonnet', 1],
  ['Calculate the break-even enrolment for the new programme if fixed costs are 42 lakh, the fee is 1.1 lakh, and each student must be allocated at least 2 lab hours, with scenarios for 40, 60 and 80 students, and a sensitivity check on the fee.', 'sonnet', 2],
  ['Design the timetable for 6 sections and 14 faculty. Each faculty must teach at most 18 hours, no section can have more than 2 labs a day, labs must be in the afternoon, and every section needs exactly 1 free period daily. Check all combinations for clashes.', 'opus', 2],
  ['Should we move to a trimester system? Weigh the trade-offs for placements, faculty load and accreditation, and forecast the cost over 3 years across every scenario.', 'opus', 2],
  ['now do the same for sem 4', 'haiku', null],
  ['Brainstorm 10 names for the college fest.', 'sonnet', 0],
  ['Explain how prompt caching works.', 'sonnet', 0],
  ['Convert this table into CSV.', 'haiku', null],
  ['Explain the pros and cons of Python and Java for a first-year course.', 'sonnet', 0],
  ['What are the trade-offs for us in moving the BCA to Aloysius timings?', 'opus', 1],
  ['Write a mobile phone policy for students.', 'sonnet', 0],
  ['Review this contract clause and tell me if we are exposed.', 'opus', 1],
  ['Evaluate this essay against the rubric and give a mark out of 20.', 'sonnet', 0],
  ['hi', 'haiku', null],
  ['Shud we follow this in our current CI for outputs..', 'opus', 1],
];

let fail = 0;
for (const [p, m, e] of cases) {
  const a = audit(p, {});
  const w = pick(a);
  const got = NAMES[w.model] + (w.effort == null ? '' : ' · ' + EFFORTS[w.effort]);
  const ok = w.model === m && w.effort === e;
  if (!ok) fail++;
  console.log((ok ? 'ok   ' : 'FAIL ') + got.padEnd(20) + ` k${a.kind} d${a.depth}  ` + p.slice(0, 70).replace(/\n/g, ' '));
  if (!ok) console.log('       want ' + NAMES[m] + (e == null ? '' : ' · ' + EFFORTS[e]) + ' | ' + a.why.join('; ') + ' | ' + a.depthWhy.join('; '));
}
// Follow-ups in an existing thread: [asked so far, draft, expected model, effort]
const PAPER = cases[7][0];
const FEE = 'Should we raise the BCA fee to 1.2 lakh next year?';
const threads = [
  [[PAPER], 'now do the same for sem 4', 'sonnet', 2],
  [[PAPER, 'yes go ahead'], 'same for sem 5 as well', 'sonnet', 2],
  [[FEE], 'ok, and the MCA fee too?', 'opus', 1],
  [[FEE], 'Make this email more formal: hi all, lab shut tomorrow', 'haiku', null],
];
console.log('');
for (const [asked, p, m, e] of threads) {
  const a = inherit(audit(p, {}), p, asked);
  const w = pick(a);
  const got = NAMES[w.model] + (w.effort == null ? '' : ' · ' + EFFORTS[w.effort]);
  const ok = w.model === m && w.effort === e;
  if (!ok) fail++;
  console.log((ok ? 'ok   ' : 'FAIL ') + got.padEnd(20) + ' follow-up  ' + p);
}
{
  const p = 'now do the same for sem 4';
  const ok = inherit(audit(p, {}), p, []).unjudged === true;
  if (!ok) fail++;
  console.log((ok ? 'ok   ' : 'FAIL ') + 'silent'.padEnd(20) + ' follow-up with no thread to follow');
}

// When a new chat beats carrying on: [name, advice, audit flags, thread, expect a reason]
const AGREE = { agree: true }, CHANGE = { agree: false };
const handoffs = [
  ['near compaction', AGREE, {}, { site: 'claude', band: 'near compaction', tokens: 600000 }, true],
  ['long thread, new task', AGREE, {}, { site: 'claude', band: 'getting long', tokens: 400000 }, true],
  ['long thread, follow-up: wrap-up warning', AGREE, { followUp: true }, { site: 'claude', band: 'getting long', tokens: 400000 }, true],
  ['roomy thread, follow-up', AGREE, { followUp: true }, { site: 'claude', band: 'plenty of room', tokens: 90000 }, false],
  ['switch on a 48K thread', CHANGE, { followUp: true }, { site: 'claude', band: 'plenty of room', tokens: 48000 }, true],
  ['switch on a 5K thread', CHANGE, {}, { site: 'claude', band: 'plenty of room', tokens: 5000 }, false],
  ['ChatGPT trimmed', AGREE, {}, { site: 'chatgpt', trimmed: true, tokens: 9000 }, true],
  ['ChatGPT not trimmed', CHANGE, {}, { site: 'chatgpt', trimmed: false, tokens: 90000 }, false],
];
console.log('');
for (const [name, adv, a, th, want] of handoffs) {
  const why = handoffWhy(adv, a, th);
  const ok = !!why === want;
  if (!ok) fail++;
  console.log((ok ? 'ok   ' : 'FAIL ') + (why ? (why.soft ? 'soft: ' : '') + why.text : 'stay').slice(0, 60).padEnd(62) + ' ' + name);
}
// Work that spans a long thread earns a step more effort.
{
  const p = 'Write the final version of the syllabus, pulling everything above together.';
  const long = { band: 'getting long', tokens: 380000 }, roomy = { band: 'plenty of room', tokens: 30000 };
  const dl = longThreadDepth(audit(p, {}), p, long).depth, dr = longThreadDepth(audit(p, {}), p, roomy).depth;
  const ok = dl === dr + 1;
  if (!ok) fail++;
  console.log('\n' + (ok ? 'ok   ' : 'FAIL ') + `depth ${dr} on a roomy thread, ${dl} on a long one  ` + p);
}
// Work that spans the thread stays in it, even when a switch is suggested.
{
  const p = 'Consolidate everything above into the final version of the proposal';
  const th = { site: 'claude', band: 'getting long', tokens: 420000 };
  const a = longThreadDepth(inherit(audit(p, {}), p, [FEE]), p, th);
  const why = handoffWhy({ agree: false }, a, th);
  const ok = why === null;
  if (!ok) fail++;
  console.log((ok ? 'ok   ' : 'FAIL ') + 'stays in the thread'.padEnd(62) + ' ' + p);
}
// ChatGPT Work mode: the step each prompt should get on GPT-6 Astra's ladder.
const work = [
  ['Fix the typos in this paragraph: Teh students will recieve there marks on monday.', 'Minimal'],
  ['i need a strategy for marketing a product..let me know how to do it', 'Medium'],
  ['Write a lesson plan for a 50 minute session on photosynthesis for class 8.', 'Light'],
  [cases[7][0], 'High'],
  [cases[12][0], 'Extra High'],
];
console.log('');
for (const [p, want] of work) {
  const got = workEffort(audit(p, {}));
  const ok = got === want;
  if (!ok) fail++;
  console.log((ok ? 'ok   ' : 'FAIL ') + ('Work · ' + got).padEnd(20) + ' ' + p.slice(0, 70));
}
console.log(fail ? `\n${fail} failed` : '\nall passed');
process.exit(fail ? 1 : 0);
