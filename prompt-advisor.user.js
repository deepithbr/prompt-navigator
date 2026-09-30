// ==UserScript==
// @name         Prompt Advisor
// @namespace    local.deepith
// @version      1.5.0
// @description  Reads the prompt you are typing on claude.ai or chatgpt.com and says which model and effort level it needs, when that differs from what you have picked.
// @author       deepith
// @copyright    2026 Deepith Kundar. All rights reserved. Personal use only —
//               see LICENSE. Not open source, not for redistribution.
// @match        https://claude.ai/*
// @match        https://chatgpt.com/*
// @match        https://chat.openai.com/*
// @run-at       document-idle
// @grant        none
// ==/UserScript==

/*
 * What this does, and what it deliberately does not.
 *
 * It reads the text box as you type, scores the prompt on two separate axes,
 * and shows one line above the box when the model or effort you have picked
 * is heavier or lighter than the prompt needs. It never switches anything for
 * you and never sends the prompt anywhere. Everything below is local rules.
 *
 * The two axes are kept apart because they buy different things.
 *
 *   Kind of thinking picks the MODEL. A rewrite, a summary or an extraction
 *   needs no judgment, so Haiku does it. Writing, code and analysis sit with
 *   Sonnet. A call with trade-offs, stakes or no right answer goes to Opus.
 *   A bigger model knows more and weighs better. It does not by itself
 *   reason for longer.
 *
 *   Depth of reasoning picks the EFFORT. Effort buys thinking tokens before
 *   the answer: more steps that depend on each other, more constraints to hold
 *   at once, arithmetic to check, a bug to trace. A one-shot transform gains
 *   nothing from thinking, so high effort on it is paid for and thrown away.
 *
 * So "Opus 5.5 at Low" and "Sonnet 5.5 at High" are both sensible answers, to
 * different prompts. The grid in pick() is where the two axes meet.
 *
 * Why this cannot be exact. A prompt shows its task, not its difficulty.
 * "Now do the same for sem 4" looks trivial and leans on the whole thread.
 * So in an existing thread a follow-up borrows the reading of the question it
 * follows, and the hint reports the signals it acted on so you can overrule
 * it knowingly. It keeps a local count of how often you follow it. If you
 * overrule it on more than one prompt in four, the rules are too crude and a
 * model-based check would be worth its cost.
 */

(function () {
  'use strict';

  // The extension and the userscript can both be installed. One hint is enough.
  if (window.__promptAdvisor) return;
  window.__promptAdvisor = true;

  const SITE = /chatgpt\.com|chat\.openai\.com/.test(location.host) ? 'chatgpt' : 'claude';

  const CONFIG = {
    debounceMs: 400,
    minChars: 15,            // below this there is nothing to judge
    showAgree: true,         // a dim "High fits this" line when your pick is right
    rereadTokens: 20000,     // past this, a mid-thread switch costs more than a summary
  };

  /* ------------------------------------------------------------------ *
   * The signals
   *
   * Every rule is a named pattern, so the audit card can say which ones fired.
   * Patterns run on lower-cased text.
   * ------------------------------------------------------------------ */

  // Kind of thinking, lightest first.
  const TRANSFORM = [
    ['fix typos or grammar', /\b(fix|correct|check|clean up)\b[^.\n]{0,20}\b(typos?|grammar|spelling|punctuation)\b/],
    ['proofread', /\bproof-?read/],
    ['rephrase', /\b(re-?phrase|re-?word|paraphrase|rewrite (this|it|these|the following|that))\b/],
    ['shorten', /\b(shorten|condense|trim (this|it)|cut (this|it) down)\b/],
    ['summarise', /\b(summar(y|ise|ize|ising|izing)|tl;?dr|key points|gist)\b/],
    ['translate', /\btranslat(e|ion)\b/],
    ['change tone', /\bmake (it|this|these|them|(this|the|my|that) \w+) (more |less |a bit |slightly )?(formal|polite|concise|shorter|professional|friendly|casual|crisp|simple|simpler|clear|clearer)\b/],
    ['reformat', /\b((re)?format|convert)\b|\b(into|as) (a )?(table|bullet(s| points)?|list|csv|json|markdown)\b/],
    ['extract', /\b(extract|pull out|list (all|every|the))\b/],
    ['title or subject line', /\b(title|subject line|caption|headline|name)s? for\b/],
    ['sort or tidy', /\b(sort|alphabeti[sz]e|capitali[sz]e|dedupe|de-duplicate)\b/],
    ['look up a meaning', /\b(define|definition of|meaning of|what does [^?]{1,40} mean)\b/],
  ];

  const CREATE = [
    ['write or draft', /\b(write|draft|compose|prepare)\b/],
    ['create or generate', /\b(create|generate|produce|come up with|brainstorm)\b/],
    ['explain', /\b(explain|teach|walk me through|help me understand)\b/],
    ['analyse or review', /\b(analy[sz]e|analysis|review|examine|interpret)\b/],
    ['build or code', /\b(build|implement|refactor|code|script|function|component|query|regex|api)\b/],
    ['plan or outline', /\b(plan|outline|structure|syllabus|lesson|curriculum|rubric|question paper|timetable)\b/],
    ['document', /\b(report|proposal|email|letter|memo|slides?|deck|spreadsheet|document|brief|circular|notice)\b/],
  ];

  // Judgment: a decision with trade-offs, or a view that has to be defended.
  const JUDGMENT = [
    // Spelled the way people type when they are in a hurry, too.
    ['should we', /\b(should|shud|shld|shd) (we|i|they|he|she|the|u|you)\b/],
    ['trade-offs', /\b(trade-?offs?|pros and cons|weigh (up )?|dilemma|downsides?)\b/],
    ['strategy', /\b(strategy|strategic|positioning|roadmap|long[- ]term|go-to-market)\b/],
    ['decide or recommend', /\b(decide|decision|recommend(ation)?|which (one|option|approach|path|of these) (is|should|would|to))\b/],
    ['critique', /\b(critique|evaluate|assess|audit|stress[- ]test|poke holes|devil'?s advocate|counter-?argument|weakest|blind spots?)\b/],
    ['negotiate', /\bnegotiat\w*\b/],
    ['root cause', /\b(root cause|investigate|diagnos(e|is))\b/],
    ['architecture', /\b(architecture|system design|design the system|data model)\b/],
    ['research synthesis', /\b(research|literature|state of the art|compare (the )?(approaches|options|vendors|models))\b/],
  ];

  const SOFT_JUDGMENT = ['trade-offs', 'decide or recommend', 'research synthesis', 'critique'];

  // Stakes: nothing here is hard, but a wrong answer costs money or standing.
  const STAKES = [
    ['fees or money', /\b(fees?|pricing|price|budget|salary|salaries|payment|revenue|cost structure)\b/],
    ['legal or compliance', /\b(legal|contract|clause|compliance|regulat\w*|liability)\b/],
    ['hiring', /\b(hiring|hire|recruit\w*|appraisal|termination)\b/],
    ['accreditation', /\b(accreditation|naac|nba|ugc|aicte|nirf|affiliation)\b/],
  ];

  // Depth of reasoning.
  const STEPS = [
    ['step by step', /\bstep[- ]by[- ]step\b/],
    ['arithmetic', /\b(calculate|compute|work out|how (much|many)|percentage|per ?cent|ratio|average|total|sum of)\b/],
    ['forecast or model', /\b(forecast|projection|estimate|scenario|sensitivity|break-?even|model the)\b/],
    ['proof or derivation', /\b(prove|proof|derive|derivation|theorem|solve for)\b/],
    ['optimise or schedule', /\b(optimi[sz]e|optimal|schedul\w*|timetabl\w*|assign \w+ to)\b/],
    ['debug', /\b(debug|traceback|stack trace|exception|segfault|race condition|deadlock|memory leak|why (does|is|isn'?t|doesn'?t|did|won'?t)|not working|fails?|failing|broken)\b/],
    ['every case', /\b(edge cases?|corner cases?|all (the )?(cases|combinations|permutations|scenarios|possibilities)|every (combination|case|scenario|permutation)|permutations)\b/],
    ['compare', /\b(compare|comparison|versus|vs\.?)\b/],
    ['multi-part plan', /\b(phases?|milestones?|dependencies|sequence|critical path)\b/],
  ];

  // Steps where an error is a slip in the working rather than a poor call.
  const REASONING_LED = ['arithmetic', 'forecast or model', 'proof or derivation', 'optimise or schedule', 'debug'];

  const CONSTRAINT = /\b(must|mustn'?t|must not|without|at least|at most|no more than|no less than|exactly|only if|unless|except|each|every|constraint|requirement|cannot|can'?t exceed|within)\b/g;

  const hits = (text, rules) => rules.filter(([, re]) => re.test(text)).map(([name]) => name);

  /* ------------------------------------------------------------------ *
   * The audit
   * ------------------------------------------------------------------ */

  /*
   * Score a prompt. Returns the two tiers and the evidence for each.
   *
   *   kind  0 transform   1 create or analyse   2 judgment
   *   depth 0 one shot    1 some steps          2 many dependent steps   3 hard
   */
  function audit(raw, extra) {
    const text = raw.toLowerCase();
    const chars = raw.length + (extra.attachChars || 0);
    const tokens = Math.round(chars / 4);

    const transform = hits(text, TRANSFORM);
    const create = hits(text, CREATE);
    const judgment = hits(text, JUDGMENT);
    const stakes = hits(text, STAKES);
    const steps = hits(text, STEPS);
    const constraints = (text.match(CONSTRAINT) || []).length;
    const listLines = (raw.match(/^\s*(\d+[.)]|[-*•])\s+/gm) || []).length;
    const hasCode = /```|^\s{4}\S|[{};]\s*$/m.test(raw) && /[{}();=]/.test(raw);
    const codeLines = hasCode ? raw.split(/\r?\n/).length : 0;

    /*
     * Some judgment words are only judgment when the decision is yours.
     * "Pros and cons of Python and Java" is an explainer, and Sonnet writes it
     * well. "The trade-offs for us" is a call someone has to make.
     */
    const personal = /\b(we|our|us|my|i|me|mine)\b/.test(text);
    const judgmentFor = judgment.filter((j) => personal || !SOFT_JUDGMENT.includes(j));

    // Kind of thinking.
    let kind;
    const why = [];
    if (judgmentFor.length) {
      kind = 2;
      why.push('judgment: ' + judgmentFor.join(', '));
    } else if (transform.length && !create.some((c) => c !== 'document')) {
      kind = 0;
      why.push('transform: ' + transform.join(', '));
    } else if (create.length) {
      kind = 1;
      why.push('task: ' + create.join(', '));
    } else if (raw.length <= 120) {
      kind = 0;
      why.push('short question, no task words');
    } else {
      kind = 1;
      why.push('open request');
    }

    // Depth of reasoning.
    let score = steps.length;
    const depthWhy = [];
    if (steps.length) depthWhy.push(steps.join(', '));
    if (constraints >= 3) { score += Math.floor(constraints / 3); depthWhy.push(constraints + ' constraints'); }
    if (listLines >= 5) { score += 1; depthWhy.push(listLines + ' listed requirements'); }
    // A fault in a few lines is found on sight. One in a long listing has to be
    // traced through it, which is what effort buys.
    if (codeLines > 30 && steps.includes('debug')) { score += 1; depthWhy.push(codeLines + ' lines of code to trace'); }
    if (raw.length > 1500 && kind > 0) { score = Math.max(score, 1); depthWhy.push('long brief'); }
    let depth = score === 0 ? 0 : score === 1 ? 1 : score <= 3 ? 2 : 3;

    /*
     * Stakes mean a wrong answer costs money or standing, and the fix depends
     * on where the error would come from. In writing and analysis it comes
     * from judgment, how a fee rise reads to parents, so stakes lift the
     * model. In a sum it comes from a slipped step, which a bigger model does
     * not prevent and more thinking does, so stakes lift the effort instead.
     * A transform is left alone: fixing typos in a fee notice is still fixing
     * typos, and pick() moves it off Haiku for that case.
     */
    const reasoningLed = steps.some((x) => REASONING_LED.includes(x));
    if (stakes.length) {
      if (kind === 1 && !reasoningLed) { kind = 2; why.push('stakes lift the model: ' + stakes.join(', ')); }
      else if (kind === 1) { depth = Math.max(depth, 2); depthWhy.push('stakes on a calculation lift the effort: ' + stakes.join(', ')); }
      else why.push('stakes: ' + stakes.join(', '));
    }

    return {
      kind, depth, tokens, why,
      depthWhy: depthWhy.length ? depthWhy : ['no multi-step reasoning found'],
      stakes: stakes.length > 0,
      attachments: extra.attachments || 0,
    };
  }

  /* ------------------------------------------------------------------ *
   * The thread
   *
   * Filled from window.__promptThread, which the Prompt Navigator and the
   * ChatGPT meter leave there. Without them a follow-up cannot be judged and
   * the advisor says nothing about it rather than guess.
   * ------------------------------------------------------------------ */

  // Words that point back at the thread rather than stating a task.
  const BACKREF = /\b(the same|same (for|with|way|thing|format|again)|again|above|earlier|previous|continue|carry on|go ahead|proceed|as before|like before|similar(ly)?|likewise|next one|another one|redo|now do|do it|do that|do this|the rest|remaining|as well|too)\b|^(yes|yeah|yep|ok(ay)?|sure|great|perfect|thanks|good|fine|right|no|nope)\b/;

  function isFollowUp(text) {
    return text.length < 300 && BACKREF.test(text.toLowerCase());
  }

  /*
   * A follow-up is judged by the question it follows. "Same for sem 4" after a
   * question paper with seven constraints is that question paper again. Walk
   * back past earlier follow-ups to the last question that stated a task, and
   * take the heavier reading on each axis.
   */
  function inherit(a, text, asked) {
    if (!isFollowUp(text)) return a;
    const list = asked || [];
    for (let i = list.length - 1, n = 0; i >= 0 && n < 6; i--, n++) {
      const q = list[i];
      if (!q || isFollowUp(q)) continue;
      const p = audit(q, {});
      const snip = q.length > 60 ? q.slice(0, 57) + '…' : q;
      return {
        ...a,
        kind: Math.max(a.kind, p.kind),
        depth: Math.max(a.depth, p.depth),
        stakes: a.stakes || p.stakes,
        why: [`follows up on "${snip}"`, ...p.why],
        depthWhy: p.depthWhy,
        followUp: true,
      };
    }
    return { ...a, followUp: true, unjudged: true };
  }

  function fmtK(n) {
    if (n >= 1000000) return (n / 1000000).toFixed(1).replace(/\.0$/, '') + 'M';
    if (n >= 1000) return Math.round(n / 1000) + 'K';
    return String(n);
  }

  /*
   * When a new chat beats carrying on.
   *
   * Three cases. Near compaction, Claude is about to summarise earlier turns
   * on its own terms, so do it on yours. On a thread past the "getting long"
   * band, a prompt that is not a follow-up is a new task paying to reread a
   * conversation it does not need. And any change of model or effort mid-thread
   * rereads the whole thread without the cache: changing effort invalidates the
   * prompt cache the same way changing model does [Claude Platform Docs, Prompt
   * caching]. Past a modest size, a short summary in a new chat is cheaper
   * than that reread.
   *
   * ChatGPT publishes no context window, so there the only signal is ChatGPT's
   * own flag that it trimmed earlier turns.
   */
  /*
   * Returns { text, soft } or null. A soft one is the wrap-up warning: the
   * thread is long but this prompt belongs to it, so finish here and move for
   * the next task. It shows once per thread per band, not on every prompt.
   */
  function handoffWhy(adv, a, thread) {
    if (!thread) return null;
    if (thread.site === 'chatgpt') {
      return thread.trimmed ? { text: 'ChatGPT has trimmed earlier turns' } : null;
    }
    if (thread.band === 'near compaction') return { text: 'thread is near compaction' };
    // Work that draws on the whole thread has to stay in it. A summary would
    // drop the detail the work needs, so it takes the switch and pays the reread.
    if (a.spansThread) return null;
    if (thread.band === 'getting long' && !a.followUp) {
      return { text: 'long thread, and this reads as a new task' };
    }
    if (adv && !adv.agree && thread.tokens >= CONFIG.rereadTokens) {
      return { text: 'switching mid-thread rereads ~' + fmtK(thread.tokens) + ' tokens' };
    }
    if (thread.band === 'getting long') {
      return { text: 'thread getting long, finish this task here and start fresh for the next', soft: true };
    }
    return null;
  }

  // Asking for work that spans the thread. On a long thread that is many
  // dependent steps whatever the wording, so it earns a step more effort.
  const SPANS_THREAD = /\b(everything (above|so far|we('ve| have) (done|discussed|decided))|all of (the )?above|whole (thread|conversation|chat)|consolidat\w*|combine (all|everything|them)|merge (all|everything|them)|final version|pull (it|this|everything) together|across (all|every) )/;

  function longThreadDepth(a, text, thread) {
    if (!thread || !SPANS_THREAD.test(text.toLowerCase())) return a;
    const long = thread.band ? thread.band !== 'plenty of room' : thread.tokens >= 50000;
    if (!long) return { ...a, spansThread: true };
    return {
      ...a,
      spansThread: true,
      depth: Math.min(3, a.depth + 1),
      depthWhy: [...a.depthWhy.filter((d) => d !== 'no multi-step reasoning found'),
        'draws on a ' + fmtK(thread.tokens) + '-token thread'],
    };
  }

  /* ------------------------------------------------------------------ *
   * Claude: the grid
   * ------------------------------------------------------------------ */

  const MODEL_RANK = { haiku: 0, sonnet: 1, opus: 2, fable: 3, mythos: 3 };
  const EFFORTS = ['Low', 'Medium', 'High', 'Extra', 'Max'];

  /*
   * Kind picks the model, depth picks the effort. Checked against Anthropic's
   * own guidance on 30 Sep 2026 [Claude docs: Effort, Choosing a model,
   * Prompting Claude Opus 5.5; Claude Help Center: effort settings]:
   *
   *   Low "Skips thinking for simple tasks where speed matters most".
   *   Medium is "the default on Claude Opus 5.5", and Sonnet 5.5's in the apps.
   *   High is for "Complex reasoning, difficult coding problems".
   *   Extra is "designed for long-running coding and agentic tasks", which a
   *   chat prompt is not, so it is never suggested here. Nor is Max: Anthropic
   *   says to reserve both "for work where you've measured a quality gain".
   *   Fable 5.1 is for "Problems you've tested with Opus and it struggled",
   *   which one prompt cannot show, and on Pro it draws on usage credits.
   *
   *              depth 0          1                2               3
   *   transform  Haiku 4.5        Sonnet · Low     Sonnet · Medium Sonnet · High
   *   create     Sonnet · Low     Sonnet · Medium  Sonnet · High   Opus · High
   *   judgment   Opus · Medium    Opus · Medium    Opus · High     Opus · High
   */
  const GRID = [
    [['haiku', null], ['sonnet', 0], ['sonnet', 1], ['sonnet', 2]],
    [['sonnet', 0], ['sonnet', 1], ['sonnet', 2], ['opus', 2]],
    [['opus', 1], ['opus', 1], ['opus', 2], ['opus', 2]],
  ];
  const NAMES = { haiku: 'Haiku 4.5', sonnet: 'Sonnet 5.5', opus: 'Opus 5.5' };

  function pick(a) {
    let [model, effort] = GRID[a.kind][a.depth];
    // A transform on something the reader must get right goes to a model that
    // makes fewer slips, at the lowest effort.
    if (model === 'haiku' && a.stakes) { model = 'sonnet'; effort = 0; }
    // Haiku's window is 200K tokens [Claude Help Center, context windows on
    // paid plans]. Leave headroom for the reply.
    if (model === 'haiku' && a.tokens + (a.threadTokens || 0) > 150000) { model = 'sonnet'; effort = 0; }
    return { model, effort };
  }

  /* The button reads e.g. "Opus 5.5 High", or "Haiku 4.5" with no effort. */
  function claudeCurrent() {
    const b = document.querySelector('[data-testid="model-selector-dropdown"]');
    return b ? parseClaudeLabel(b.textContent || '') : null;
  }

  /*
   * Pure, so the desktop companion can call it with the button's accessible
   * name, which carries a "Model: " prefix the page text does not.
   */
  function parseClaudeLabel(raw) {
    const t = raw.replace(/^\s*model:\s*/i, '').replace(/\s+/g, ' ').trim();
    const m = t.match(/^(haiku|sonnet|opus|fable|mythos)\s*([\d.]+)?\s*(low|medium|high|extra|max)?/i);
    if (!m) return null;
    const family = m[1].toLowerCase();
    const effort = m[3] ? EFFORTS.findIndex((e) => e.toLowerCase() === m[3].toLowerCase()) : null;
    return { family, version: m[2] || '', effort, label: t };
  }

  function claudeAdvice(a, cur = claudeCurrent()) {
    if (!cur) return null;
    const want = pick(a);
    const cm = MODEL_RANK[cur.family], wm = MODEL_RANK[want.model];
    const ce = cur.effort == null ? 0 : cur.effort, we = want.effort == null ? 0 : want.effort;
    let dir = 0;
    if (cm > wm) dir = -1;
    else if (cm < wm) dir = 1;
    else if (want.effort != null && ce > we) dir = -1;
    // Asking for more effort on the same model only when the prompt really is
    // a chain of steps. One level short on a medium prompt is not worth a nag.
    else if (want.effort != null && we > ce && a.depth >= 2) dir = 1;
    if (!dir) return { agree: true, cur: cur.label };
    const label = NAMES[want.model] + (want.effort == null ? '' : ' · ' + EFFORTS[want.effort]);
    // "Tuning effort is often a better lever than switching models" [Claude
    // docs, Choosing a model]. So a step down in model also names the same
    // step down in effort on the model you already have. Haiku has no effort.
    let alt = null;
    if (cm > wm && cur.family !== 'haiku') {
      const name = cur.family[0].toUpperCase() + cur.family.slice(1) + (cur.version ? ' ' + cur.version : '');
      const e = want.effort == null ? 0 : want.effort;
      if (cur.effort == null || e < cur.effort) alt = name + ' · ' + EFFORTS[e];
    }
    return { agree: false, dir, label, alt, cur: cur.label };
  }

  /* ------------------------------------------------------------------ *
   * ChatGPT: two modes, effort only
   *
   * Checked on 29 Sep 2026 on a Plus account. ChatGPT has a Chat | Work switch
   * above the composer, and the two use different pickers.
   *
   *   Chat runs GPT-5.6 Sol with a thinking-effort slider of three stops,
   *   Instant, Medium and High. The button shows only the current stop's name.
   *
   *   Work runs GPT-6 Astra with a nine-step ladder: None, Minimal, Light,
   *   Medium, High, Extra High, Max, Ultra, Persistent. The button carries the
   *   whole ladder in order, plus the current step in a screen-reader label.
   *
   * There is one model per mode, so only effort is advised on ChatGPT.
   * ------------------------------------------------------------------ */

  // The names ChatGPT uses, lightest first, read off the Work ladder.
  const GPT_NAMES = ['none', 'minimal', 'light', 'medium', 'high', 'extra high', 'max', 'ultra', 'persistent'];

  const LEVEL_KEY = 'cpa-gpt-levels';
  function gptLevels() {
    // Chat mode's three stops, as learned on a Plus account on 29 Sep 2026.
    // Anything learned since overrides them.
    const known = { 0: 'Instant', 1: 'Medium', 2: 'High' };
    try { return { ...known, ...(JSON.parse(localStorage.getItem(LEVEL_KEY)) || {}) }; }
    catch (e) { return known; }
  }
  function learnGptLevel() {
    const s = document.querySelector('[role="slider"][aria-valuemax="2"]');
    const b = document.querySelector('button[aria-label="Select ChatGPT model"]');
    if (!s || !b) return;
    const pop = s.closest('[role="dialog"],[role="menu"],[data-radix-popper-content-wrapper]') || s.parentElement.parentElement;
    const name = ((pop && pop.innerText) || '').split('\n').map((x) => x.replace(/[›>]/g, '').trim()).find((x) => x && x.length < 20);
    const now = s.getAttribute('aria-valuenow');
    if (!name || now == null) return;
    const lv = gptLevels();
    if (lv[now] !== name) { lv[now] = name; try { localStorage.setItem(LEVEL_KEY, JSON.stringify(lv)); } catch (e) {} }
  }

  /*
   * Work mode advises model as well as effort. Checked against OpenAI's own
   * guidance on 30 Sep 2026 [OpenAI: Codex models, Introducing GPT-6 Sol and
   * Luna, Introducing GPT-6.1 Sol]:
   *
   *   Luna is "Our most efficient model for focused, high-volume tasks,
   *   including summarization, extraction". OpenAI says to start it at High.
   *   Sol "can take on difficult work tasks" with higher usage limits, and
   *   GPT-6.1 Sol, out 29 Sep, "nearly matches GPT-6 Astra". Work and Codex
   *   only, not Chat.
   *   Astra is for "when a task needs the strongest capability across steps
   *   and tools". OpenAI says to start it at Light.
   *   Light "suits quick, well-scoped tasks", Medium is for "tasks that need
   *   more planning", High and Extra High for "difficult work with multiple
   *   steps, sources, or tradeoffs", and "Most tasks do not need Max or Ultra".
   *
   * The button on this account also lists None, Minimal and Persistent, which
   * no OpenAI page describes, so none of those is ever suggested.
   */
  const GPT_MODELS = ['GPT-6 Luna', 'GPT-6.1 Sol', 'GPT-6 Astra'];
  function gptRank(model) {
    return /luna/i.test(model) ? 0 : /astra|\bpro\b/i.test(model) ? 2 : 1;
  }
  function workPick(a) {
    if (a.kind === 0 && a.depth <= 1) return { model: 0, step: 'High' };
    if (a.depth >= 3) return { model: 2, step: 'Light' };
    if (a.depth === 2) return { model: 1, step: 'High' };
    if (a.kind === 2 || a.depth === 1) return { model: 1, step: 'Medium' };
    return { model: 1, step: 'Light' };
  }

  function gptCurrent() {
    const b = document.querySelector('button[aria-label="Select ChatGPT model"]');
    if (!b) return null;

    // Work mode: the button lists its ladder.
    const steps = [...b.querySelectorAll('[class*="EffortText"]')].map((e) => e.textContent.trim()).filter(Boolean);
    if (steps.length) {
      const modelEl = b.querySelector('[class*="TriggerModel"]');
      const curEl = b.querySelector('.sr-only');
      const cur = curEl ? curEl.textContent.trim() : '';
      const level = steps.findIndex((x) => x.toLowerCase() === cur.toLowerCase());
      if (level < 0) return null;
      const model = modelEl ? modelEl.textContent.trim() : '';
      return { work: true, model, level, steps, label: (model ? model + ' · ' : '') + cur };
    }

    // Chat mode: innerText, because the button also holds a hidden "Thinking
    // effort" measuring label that textContent would glue onto the front.
    const t = (b.innerText || '').replace(/thinking effort/ig, '').replace(/\s+/g, ' ').trim();
    if (!t) return null;
    const lv = gptLevels();
    for (const k of Object.keys(lv)) if (t === lv[k] || t.startsWith(lv[k])) return { level: +k, label: t };
    // A stop you have not opened the slider on yet. Place it by name: below
    // Medium is the bottom stop, Medium the middle, and High or above the top.
    const rank = GPT_NAMES.indexOf(t.toLowerCase());
    if (rank < 0) return null;
    return { level: rank < 3 ? 0 : rank === 3 ? 1 : 2, label: t };
  }

  /*
   * The desktop app's button is one accessible name, such as "GPT-5.6 Sol
   * Medium" or "GPT-6 Astra Light", so the effort is the trailing step name.
   * work says the app is in Work or Codex mode, which uses Work's ladder
   * whatever the model. The GPT-6 family only runs there.
   */
  const WORK_STEPS = ['None', 'Minimal', 'Light', 'Medium', 'High', 'Extra High', 'Max', 'Ultra', 'Persistent'];
  function parseGptButton(raw, work) {
    const t = raw.replace(/\s+/g, ' ').trim();
    const m = t.match(/^(.*?)\s+(none|minimal|instant|light|low|medium|high|extra high|max|ultra|persistent)$/i);
    if (!m) return null;
    const model = m[1], step = m[2];
    if (work || /gpt-6/i.test(model)) {
      const level = WORK_STEPS.findIndex((x) => x.toLowerCase() === step.toLowerCase());
      return level < 0 ? null : { work: true, model, level, steps: WORK_STEPS, label: model + ' · ' + WORK_STEPS[level] };
    }
    const lv = gptLevels();
    for (const k of Object.keys(lv)) if (lv[k].toLowerCase() === step.toLowerCase()) return { level: +k, label: model + ' · ' + lv[k] };
    const rank = GPT_NAMES.indexOf(step.toLowerCase());
    return { level: rank < 3 ? 0 : rank === 3 ? 1 : 2, label: model + ' · ' + step };
  }

  function gptAdvice(a, cur = gptCurrent()) {
    if (!cur) return null;
    // Upward only for real reasoning or a real judgment call, as on Claude.
    const worthRaising = a.depth >= 2 || a.kind === 2;

    if (cur.work) {
      const want = workPick(a);
      const cm = gptRank(cur.model || ''), wm = want.model;
      const ws = cur.steps.findIndex((x) => x.toLowerCase() === want.step.toLowerCase());
      let dir = 0;
      if (cm > wm) dir = -1;
      else if (cm < wm) dir = 1;
      else if (ws >= 0 && cur.level > ws) dir = -1;
      else if (ws >= 0 && ws > cur.level && worthRaising) dir = 1;
      if (!dir) return { agree: true, work: true, cur: cur.label };
      const model = cm === wm && cur.model ? cur.model : GPT_MODELS[wm];
      return { agree: false, work: true, dir, label: model + ' · ' + want.step, cur: cur.label };
    }

    const want = (a.kind === 0 && a.depth <= 1) ? 0 : worthRaising ? 2 : 1;
    if (want === cur.level) return { agree: true, cur: cur.label };
    if (want > cur.level && !worthRaising) return { agree: true, cur: cur.label };
    const lv = gptLevels();
    const label = lv[want] ? lv[want] + ' thinking' : (want === 0 ? 'Lowest thinking' : want === 1 ? 'Middle thinking' : 'High thinking');
    return { agree: false, dir: want < cur.level ? -1 : 1, label, cur: cur.label };
  }

  /*
   * What an effort step is for, in the makers' words, for the hover card.
   * [Claude docs: Effort, Steering thinking; Claude Help Center: Change the
   * model, effort and thinking settings; OpenAI Help Center: GPT-5.6 Sol;
   * OpenAI: Codex models. All read 30 Sep 2026.]
   */
  const EFFORT_NOTES = {
    claude: {
      low: 'Low skips thinking for simple tasks where speed matters most, and stretches your usage furthest.',
      medium: 'Medium is the default on Opus 5.5, and on Sonnet 5.5 in the apps. It may skip thinking for simple queries.',
      high: 'High gives deep reasoning on complex tasks. Anthropic calls it the best overall balance of quality and speed.',
      extra: 'Extra is designed for long-running coding and agentic tasks, not chat.',
      max: 'Max is the most thorough and uses your limits fastest. Anthropic reserves it for work where it has measured a gain.',
    },
    chat: {
      instant: 'Instant gives fast responses for everyday questions.',
      medium: 'Medium is standard thinking.',
      high: 'High is extended thinking, and uses your limits faster.',
    },
    work: {
      light: 'Light suits quick, well-scoped tasks.',
      medium: 'Medium balances speed and depth for tasks that need more planning.',
      high: 'High suits difficult work with multiple steps, sources or trade-offs.',
      'extra high': 'Extra High suits difficult work with multiple steps, sources or trade-offs.',
      max: 'Most tasks do not need Max, says OpenAI.',
      ultra: 'Most tasks do not need Ultra, says OpenAI.',
    },
  };
  function effortNote(site, label, work) {
    const t = String(label || '').toLowerCase();
    if (site === 'claude' && /haiku/.test(t)) return 'Haiku 4.5 has no effort setting.';
    // OpenAI names a starting step per Work model, which is not the same as
    // that step's general meaning.
    if (work && /luna/.test(t) && /\bhigh\b/.test(t) && !/extra high/.test(t)) {
      return "OpenAI says to start Luna at High. It is Luna's usual step for summaries and extraction, not a sign of hard work.";
    }
    if (work && /astra/.test(t) && /\blight\b/.test(t)) {
      return 'OpenAI says to start Astra at Light. Astra brings the capability, so it rarely needs more.';
    }
    const table = site === 'claude' ? EFFORT_NOTES.claude : work ? EFFORT_NOTES.work : EFFORT_NOTES.chat;
    const hit = Object.keys(table)
      .filter((k) => new RegExp('\\b' + k + '\\b').test(t))
      .sort((x, y) => y.length - x.length)[0];
    return hit ? table[hit] : null;
  }

  /* ------------------------------------------------------------------ *
   * The page
   * ------------------------------------------------------------------ */

  function box() {
    if (SITE === 'claude') return document.querySelector('[data-testid="chat-input"]');
    return document.querySelector('form [contenteditable="true"][role="textbox"]')
      || document.querySelector('[contenteditable="true"][role="textbox"]');
  }

  // The composer card: the first ancestor tall enough to hold the toolbar row.
  // ChatGPT's composer is a single row, so there it is simply the form.
  function card(b) {
    if (SITE === 'chatgpt' && b.closest('form')) return b.closest('form');
    const h = b.getBoundingClientRect().height;
    let el = b.parentElement;
    for (let i = 0; el && i < 8; i++, el = el.parentElement) {
      if (el.getBoundingClientRect().height >= h + 30) return el;
    }
    return b.parentElement;
  }

  // Long pastes on claude.ai turn into attachment chips, so the text box alone
  // undercounts. Their size is not shown, so they only count as present.
  function attachments(c) {
    if (!c) return 0;
    return c.querySelectorAll('[data-testid*="file"],[data-testid*="attachment"],[data-testid*="thumbnail"]').length;
  }

  function existingThread() {
    return SITE === 'claude' ? /\/chat\/[0-9a-f-]{36}/i.test(location.pathname)
      : /\/c\/[0-9a-z-]+/i.test(location.pathname);
  }

  function threadId() {
    const m = SITE === 'claude' ? location.pathname.match(/\/chat\/([0-9a-f-]{36})/i)
      : location.pathname.match(/\/c\/([0-9a-z-]+)/i);
    return m ? m[1] : null;
  }

  function threadFor() {
    const t = window.__promptThread;
    const id = threadId();
    return t && id && t.id === id ? t : null;
  }

  function blocked() {
    // Cowork runs its own agent models, not the chat picker.
    return SITE === 'claude' && /\/cowork\//.test(location.pathname);
  }

  /* ------------------------------------------------------------------ *
   * The hint
   * ------------------------------------------------------------------ */

  const CSS = `
  .cpa-hint { position: fixed; z-index: 2147483000; display: none; align-items: center; gap: 8px;
    font: 12px/1.3 ui-sans-serif, system-ui, "Segoe UI", sans-serif; color: #e6e4df;
    background: #262524; border: 1px solid rgba(140,135,125,0.35); border-radius: 8px;
    padding: 4px 6px 4px 9px; box-shadow: 0 2px 10px rgba(0,0,0,0.25); cursor: default; max-width: 560px; }
  .cpa-hint.cpa-on { display: flex; }
  .cpa-hint b { font-weight: 600; }
  .cpa-hint .cpa-dot { width: 7px; height: 7px; border-radius: 50%; flex: none; background: #d97757; }
  .cpa-hint.cpa-up .cpa-dot { background: #5b9dbb; }
  .cpa-hint.cpa-ok { opacity: 0.7; box-shadow: none; }
  .cpa-hint.cpa-ok .cpa-dot { background: #7fa37f; }
  .cpa-hint .cpa-why { opacity: 0.6; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .cpa-hint button { all: unset; cursor: pointer; opacity: 0.5; padding: 0 4px; font-size: 14px; line-height: 1; }
  .cpa-hint button:hover { opacity: 1; }
  .cpa-hint button.cpa-act { opacity: 1; font-size: 12px; padding: 2px 8px; border-radius: 6px;
    background: rgba(217,119,87,0.18); color: inherit; }
  .cpa-hint button.cpa-act:hover { background: rgba(217,119,87,0.32); }
  .cpa-hint button.cpa-act[hidden] { display: none; }
  .cpa-card { position: fixed; z-index: 2147483001; display: none; width: 340px;
    font: 12px/1.45 ui-sans-serif, system-ui, "Segoe UI", sans-serif; color: #e6e4df;
    background: #201f1e; border: 1px solid rgba(140,135,125,0.35); border-radius: 10px;
    padding: 10px 12px; box-shadow: 0 6px 24px rgba(0,0,0,0.35); }
  .cpa-card.cpa-on { display: block; }
  .cpa-card h4 { margin: 0 0 6px; font-size: 12px; font-weight: 600; }
  .cpa-card dl { margin: 0; display: grid; grid-template-columns: 78px 1fr; gap: 3px 8px; }
  .cpa-card dt { opacity: 0.55; }
  .cpa-card dd { margin: 0; }
  .cpa-card p { margin: 8px 0 0; opacity: 0.6; font-size: 11px; }
  @media (prefers-color-scheme: light) {
    .cpa-hint, .cpa-card { background: #faf8f4; color: #2b2925; border-color: rgba(60,55,45,0.2); }
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

  let hint = null, hintText = null, hintWhy = null, hintAct = null, cardEl = null;
  let lastAudit = null, lastAdvice = null, lastThread = null, lastHandoff = null;
  let noteFor = null;         // the start of the summary request we put in the box
  let lastSoft = false;       // the current hint is the wrap-up warning
  let lastText = '';          // the draft the last reading was of
  const wrapSeen = new Set(); // thread|band pairs whose wrap-up warning you have seen
  let summarised = false;     // you took the new-chat route for this draft
  let dismissedFor = null;    // the draft text you closed the hint on
  let draftHinted = false;    // a hint showed at some point for this draft
  let timer = null;

  function build() {
    hint = document.createElement('div');
    hint.className = 'cpa-hint';
    const dot = document.createElement('span');
    dot.className = 'cpa-dot';
    hintText = document.createElement('span');
    hintWhy = document.createElement('span');
    hintWhy.className = 'cpa-why';
    const x = document.createElement('button');
    x.textContent = '×';
    x.title = 'Hide for this prompt';
    x.addEventListener('click', () => {
      const b = box();
      if (lastSoft && lastThread) wrapSeen.add(lastThread.id + '|' + lastThread.band);
      dismissedFor = b ? b.innerText.trim() : '';
      hide();
    });
    hintAct = document.createElement('button');
    hintAct.className = 'cpa-act';
    hintAct.textContent = 'Summarise';
    hintAct.title = 'Put a request for a short handover into the box, with your prompt kept at the end';
    hintAct.hidden = true;
    hintAct.addEventListener('click', summarise);
    hint.append(dot, hintText, hintWhy, hintAct, x);
    cardEl = document.createElement('div');
    cardEl.className = 'cpa-card';
    hint.addEventListener('mouseenter', showCard);
    hint.addEventListener('mouseleave', () => cardEl.classList.remove('cpa-on'));
    document.body.append(hint, cardEl);
  }

  const HANDOFF_ASK = 'Before I move to a new chat, write a handover I can paste into it. '
    + 'Keep it under 400 words. Include the goal, the decisions we made and why, the current '
    + 'state of every document or piece of code we produced, the constraints I set, and the '
    + 'open questions. Leave out anything settled that the next chat will not need.';

  /*
   * Swap the draft for a summary request that carries the draft at its end,
   * so nothing you typed is lost. The summary runs on this thread, where the
   * cache makes rereading it cheapest, and its reply is what you paste into
   * the new chat.
   */
  function summarise() {
    const b = box();
    if (!b) return;
    const draft = (b.innerText || '').trim();
    const ask = HANDOFF_ASK + (draft
      ? '\n\nAfter the handover, repeat my next request exactly as written:\n\n' + draft : '');
    const target = lastAdvice && !lastAdvice.agree ? lastAdvice.label : null;
    b.focus();
    document.execCommand('selectAll', false, null);
    document.execCommand('insertText', false, ask);
    noteFor = ask.slice(0, 60);
    summarised = true;
    hintText.textContent = 'Send this, then paste the reply into a new chat'
      + (target ? ' on ' + target : '') + '.';
    hintWhy.textContent = '';
    hintAct.hidden = true;
    cardEl.classList.remove('cpa-on');
    place();
  }

  function hide() {
    if (hint) hint.classList.remove('cpa-on');
    if (cardEl) cardEl.classList.remove('cpa-on');
  }

  function place() {
    const b = box();
    if (!b || !hint.classList.contains('cpa-on')) return;
    const r = card(b).getBoundingClientRect();
    const h = hint.getBoundingClientRect().height || 26;
    hint.style.left = Math.round(r.left) + 'px';
    hint.style.top = Math.round(r.top - h - 6) + 'px';
  }

  function stats() {
    try { return JSON.parse(localStorage.getItem('cpa-stats')) || { hinted: 0, followed: 0 }; }
    catch (e) { return { hinted: 0, followed: 0 }; }
  }

  function showCard() {
    if (!lastAudit || !lastAdvice) return;
    const a = lastAudit;
    const KIND = ['Transform', 'Create or analyse', 'Judgment'];
    const DEPTH = ['One shot', 'Some steps', 'Many dependent steps', 'Hard reasoning'];
    const st = stats();
    const t = lastThread;
    const rows = [
      ['You picked', lastAdvice.cur],
      ['Suggested', lastAdvice.agree ? 'Keep it' : lastAdvice.label],
      ['Kind', KIND[a.kind] + '. ' + a.why.join('. ')],
      ['Reasoning', DEPTH[a.depth] + '. ' + a.depthWhy.join(', ')],
      ['Size', '~' + a.tokens.toLocaleString() + ' tokens typed'
        + (a.attachments ? ', plus ' + a.attachments + ' attachment' + (a.attachments > 1 ? 's' : '') + ' of unknown size' : '')],
    ];
    if (existingThread()) {
      rows.push(['Thread', !t ? 'not read yet'
        : '~' + fmtK(t.tokens) + ' tokens' + (t.window ? ' of a ' + fmtK(t.window) + ' window, ' + t.band : '')
          + (t.trimmed ? ', earlier turns trimmed' : '')]);
    }
    const note = effortNote(SITE, lastAdvice.agree ? lastAdvice.cur : lastAdvice.label, lastAdvice.work);
    if (note) rows.push(['Effort', note]);
    if (lastAdvice.alt) {
      rows.push(['Or', lastAdvice.alt + ' on the model you have. Anthropic says tuning effort '
        + 'is often a better lever than switching models.']);
    }
    const KINDS = ['transform', 'create or analyse', 'judgment'];
    const record = recordFor(a.kind);
    rows.push(['Your record', record
      ? `On ${KINDS[a.kind]} prompts: ${record}.`
      : `Nothing judged yet on ${KINDS[a.kind]} prompts. It fills in as you use Claude and ChatGPT.`]);
    if (lastHandoff) {
      rows.push(['New chat', lastHandoff + '. Summarise puts a handover request in the box, '
        + 'with your prompt kept at the end.']);
    }
    cardEl.textContent = '';
    const h = document.createElement('h4');
    h.textContent = SITE === 'claude'
      ? 'Model picks the kind of thinking. Effort picks how many steps.'
      : 'Effort decides how long it thinks before answering.';
    const dl = document.createElement('dl');
    for (const [k, v] of rows) {
      const dt = document.createElement('dt'); dt.textContent = k;
      const dd = document.createElement('dd'); dd.textContent = v;
      dl.append(dt, dd);
    }
    const p = document.createElement('p');
    p.textContent = (SITE === 'claude'
      ? 'Per token, Opus 5.5 costs twice Sonnet 5.5 and four times Haiku 4.5 on the API. '
      : '')
      + 'Read from the words only, so it cannot see how hard the thread behind them is. '
      + (existingThread() ? "Changing model or effort mid-thread rereads the thread without the cache, per Claude's prompt caching docs. " : '')
      + (st.hinted ? `You have followed ${st.followed} of ${st.hinted} hints here.` : '');
    cardEl.append(h, dl, p);
    cardEl.classList.add('cpa-on');
    const r = hint.getBoundingClientRect();
    const ch = cardEl.getBoundingClientRect().height;
    cardEl.style.left = Math.round(r.left) + 'px';
    cardEl.style.top = Math.max(8, Math.round(r.top - ch - 6)) + 'px';
  }

  function evaluate() {
    if (!hint) return;
    lastAudit = null;
    lastText = '';
    const b = box();
    if (!b || blocked()) { hide(); return; }
    const text = (b.innerText || '').trim();
    if (!text) { dismissedFor = null; draftHinted = false; noteFor = null; summarised = false; }
    // Leave the "send this, then paste" note up while the summary request sits
    // in the box.
    if (noteFor && text.startsWith(noteFor)) { place(); return; }
    const c = card(b);
    const att = attachments(c);
    if (text.length < CONFIG.minChars && !att) { hide(); return; }
    const suppressed = dismissedFor !== null && text.startsWith(dismissedFor.slice(0, 40));

    const thread = existingThread() ? threadFor() : null;
    let a = audit(text, { attachments: att });
    if (existingThread()) {
      a = inherit(a, text, thread && thread.questions);
      // A follow-up with nothing to follow: silence beats a guess from its words.
      if (a.unjudged) { hide(); return; }
    }
    a = longThreadDepth(a, text, thread);
    a.threadTokens = thread ? thread.tokens : 0;
    const adv = SITE === 'claude' ? claudeAdvice(a) : gptAdvice(a);
    let move = handoffWhy(adv, a, thread);
    // The wrap-up warning has said its piece once you send a prompt past it.
    if (move && move.soft && (!adv || adv.agree) && wrapSeen.has(thread.id + '|' + thread.band)) move = null;
    const why = move && move.text;
    lastAudit = a;
    lastAdvice = adv;
    lastThread = thread;
    lastHandoff = why;
    lastSoft = !!(move && move.soft);
    lastText = text;
    if (suppressed) { hide(); return; }
    if (!why && !adv) { hide(); return; }
    if (!why && adv.agree) {
      if (!CONFIG.showAgree) { hide(); return; }
      // Your pick already fits. Say so quietly, so silence never has to mean
      // either "agreed" or "not working".
      hintText.textContent = '';
      const ok = document.createElement('b');
      ok.textContent = adv.cur;
      hintText.append(ok, document.createTextNode(' fits this'));
      hintWhy.textContent = a.why[0];
      hintAct.hidden = true;
      hint.classList.remove('cpa-up');
      hint.classList.add('cpa-ok', 'cpa-on');
      place();
      return;
    }
    hint.classList.remove('cpa-ok');

    hintText.textContent = '';
    const bold = document.createElement('b');
    if (lastSoft && adv && !adv.agree) {
      bold.textContent = adv.label;
      hintText.append(bold, document.createTextNode(adv.dir < 0 ? ' would do this' : ' fits this better'));
    } else if (lastSoft) {
      bold.textContent = 'Wrap up soon';
    } else if (why && adv && !adv.agree) {
      bold.textContent = adv.label;
      hintText.append(bold, document.createTextNode(' in a new chat'));
    } else if (why) {
      bold.textContent = 'A new chat';
      hintText.append(bold, document.createTextNode(' would serve this better'));
    } else {
      bold.textContent = adv.label;
      hintText.append(bold, document.createTextNode(adv.dir < 0 ? ' would do this' : ' fits this better'));
    }
    hintWhy.textContent = why || a.why[0];
    hintAct.hidden = !why;
    hint.classList.toggle('cpa-up', !why && !!adv && adv.dir > 0);
    hint.classList.add('cpa-on');
    draftHinted = true;
    place();
  }

  function schedule() {
    clearTimeout(timer);
    timer = setTimeout(evaluate, CONFIG.debounceMs);
  }

  /*
   * Your record. Every send notes the prompt's reading and your pick in
   * cpa-log. The Prompt Navigator and the ChatGPT meter mark each question
   * kept or missed in cpa-outcomes as they read threads: missed means you
   * regenerated, stopped, edited or corrected the answer. Joining the two
   * shows how often each pick had to be redone on prompts of the same kind,
   * which is the evidence the grid above should be tuned against. Both stay
   * in this browser.
   */
  function outcomeKey(t) {
    return String(t || '').toLowerCase().replace(/\s+/g, ' ').trim().slice(0, 80);
  }
  function readJson(key, fallback) {
    try { return JSON.parse(localStorage.getItem(key)) || fallback; } catch (e) { return fallback; }
  }
  function logSend() {
    if (!lastAudit || !lastAdvice || !lastText) return;
    const log = readJson('cpa-log', []);
    log.push({
      t: Date.now(), site: SITE, k: outcomeKey(lastText),
      kind: lastAudit.kind, depth: lastAudit.depth,
      pick: lastAdvice.cur, suggested: lastAdvice.agree ? null : lastAdvice.label,
    });
    try { localStorage.setItem('cpa-log', JSON.stringify(log.slice(-1500))); } catch (e) {}
  }
  function recordFor(kind) {
    const outcomes = readJson('cpa-outcomes', {});
    const by = {};
    for (const e of readJson('cpa-log', [])) {
      const o = outcomes[e.k];
      if (e.site !== SITE || e.kind !== kind || !o) continue;
      const r = by[e.pick] || (by[e.pick] = { n: 0, missed: 0 });
      r.n += 1;
      if (o[0] === 'missed') r.missed += 1;
    }
    const picks = Object.keys(by).sort((x, y) => by[y].n - by[x].n).slice(0, 3);
    return picks.length ? picks.map((p) => `${p} redone ${by[p].missed} of ${by[p].n}`).join('. ') : null;
  }

  // Counted at send time. Followed means a hint showed for this draft and your
  // pick agreed with it by the time you sent, which is the only way the hint
  // goes quiet without the close button.
  function onSend() {
    if (lastSoft && lastThread) wrapSeen.add(lastThread.id + '|' + lastThread.band);
    if (!draftHinted) return;
    const st = stats();
    st.hinted += 1;
    if (summarised || (lastAdvice && lastAdvice.agree && !lastHandoff && dismissedFor === null)) st.followed += 1;
    try { localStorage.setItem('cpa-stats', JSON.stringify(st)); } catch (e) {}
    draftHinted = false;
    dismissedFor = null;
    summarised = false;
    noteFor = null;
    hide();
  }

  function start() {
    injectStyles();
    build();

    document.addEventListener('input', (e) => {
      const b = box();
      if (b && (e.target === b || b.contains(e.target))) schedule();
    }, true);
    document.addEventListener('keydown', (e) => {
      const b = box();
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing && b && b.contains(e.target)) {
        evaluate();
        logSend();
        onSend();
      }
    }, true);
    document.addEventListener('click', (e) => {
      const s = e.target.closest && e.target.closest(
        'button[aria-label="Send message"],button[data-testid="send-button"],#composer-submit-button');
      if (s) { evaluate(); logSend(); onSend(); }
    }, true);

    // The picker changes without touching the text box, so it is polled. This
    // is two querySelector calls a second and nothing else.
    let lastPick = '';
    setInterval(() => {
      if (SITE === 'chatgpt') learnGptLevel();
      const cur = SITE === 'claude' ? claudeCurrent() : gptCurrent();
      const key = (cur ? cur.label : '') + location.pathname;
      if (key !== lastPick) { lastPick = key; evaluate(); }
      place();
    }, 1000);
    window.addEventListener('resize', place);
  }

  if (document.body) start();
  else document.addEventListener('DOMContentLoaded', start);
})();
