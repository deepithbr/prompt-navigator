// Runs the prompt advisor's scoring rules against sample prompts.
// node tests/advisor.test.js
const fs = require('fs');
const path = require('path');
const src = fs.readFileSync(path.join(__dirname, '..', 'prompt-advisor.user.js'), 'utf8');
const from = src.indexOf('  // Kind of thinking, lightest first.');
const to = src.indexOf('  /* The button reads');
const { audit, pick, NAMES, EFFORTS } = new Function(src.slice(from, to) + '\nreturn { audit, pick, NAMES, EFFORTS };')();

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
console.log(fail ? `\n${fail} failed` : '\nall passed');
process.exit(fail ? 1 : 0);
