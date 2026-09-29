// ==UserScript==
// @name         Prompt Advisor
// @namespace    local.deepith
// @version      1.0.0
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
 * That is why the hint only speaks on a new chat, or on a long prompt in an
 * existing one, and why it reports the signals it acted on so you can overrule
 * it knowingly. It keeps a local count of how often you follow it. If you
 * overrule it on more than one prompt in four, the rules are too crude and a
 * model-based check would be worth its cost.
 */

(function () {
  'use strict';

  const SITE = /chatgpt\.com|chat\.openai\.com/.test(location.host) ? 'chatgpt' : 'claude';

  const CONFIG = {
    debounceMs: 400,
    minChars: 15,            // below this there is nothing to judge
    followUpMinChars: 600,   // an existing thread only hears from us on a big prompt
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
    ['should we', /\bshould (we|i|they|he|she|the)\b/],
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
   * Claude: the grid
   * ------------------------------------------------------------------ */

  const MODEL_RANK = { haiku: 0, sonnet: 1, opus: 2, fable: 3, mythos: 3 };
  const EFFORTS = ['Low', 'Medium', 'High', 'Extra', 'Max'];

  /*
   * Kind picks the model, depth picks the effort. Max never appears: the
   * picker itself warns it costs 5.5 times or more, and nothing a rule can
   * see in a prompt justifies that. Fable 5.1 never appears either: it is
   * built for long autonomous work, which a single prompt does not reveal.
   *
   *              depth 0          1                2               3
   *   transform  Haiku 4.5        Sonnet · Low     Sonnet · Medium Sonnet · High
   *   create     Sonnet · Low     Sonnet · Medium  Sonnet · High   Opus · High
   *   judgment   Opus · Medium    Opus · Medium    Opus · High     Opus · Extra
   */
  const GRID = [
    [['haiku', null], ['sonnet', 0], ['sonnet', 1], ['sonnet', 2]],
    [['sonnet', 0], ['sonnet', 1], ['sonnet', 2], ['opus', 2]],
    [['opus', 1], ['opus', 1], ['opus', 2], ['opus', 3]],
  ];
  const NAMES = { haiku: 'Haiku 4.5', sonnet: 'Sonnet 5.5', opus: 'Opus 5.5' };

  function pick(a) {
    let [model, effort] = GRID[a.kind][a.depth];
    // A transform on something the reader must get right goes to a model that
    // makes fewer slips, at the lowest effort.
    if (model === 'haiku' && a.stakes) { model = 'sonnet'; effort = 0; }
    // Haiku's window is 200K tokens [Claude Help Center, context windows on
    // paid plans]. Leave headroom for the reply.
    if (model === 'haiku' && a.tokens > 150000) { model = 'sonnet'; effort = 0; }
    return { model, effort };
  }

  /* The button reads e.g. "Opus 5.5 High", or "Haiku 4.5" with no effort. */
  function claudeCurrent() {
    const b = document.querySelector('[data-testid="model-selector-dropdown"]');
    if (!b) return null;
    const t = (b.textContent || '').replace(/\s+/g, ' ').trim();
    const m = t.match(/^(haiku|sonnet|opus|fable|mythos)\s*([\d.]+)?\s*(low|medium|high|extra|max)?/i);
    if (!m) return null;
    const family = m[1].toLowerCase();
    const effort = m[3] ? EFFORTS.findIndex((e) => e.toLowerCase() === m[3].toLowerCase()) : null;
    return { family, version: m[2] || '', effort, label: t };
  }

  function claudeAdvice(a) {
    const cur = claudeCurrent();
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
    return { agree: false, dir, label, cur: cur.label };
  }

  /* ------------------------------------------------------------------ *
   * ChatGPT: one model, three thinking levels
   *
   * Checked on 29 Sep 2026 on a Plus account: the picker offers GPT-5.6 Sol
   * (and GPT-5.5, marked as leaving on 14 October), and a thinking-effort
   * slider with three stops, 0 to 2, where 2 reads "High". So on ChatGPT only
   * effort is advised. The names of the lower stops are learned the first
   * time you open the slider on them, rather than guessed here.
   * ------------------------------------------------------------------ */

  const LEVEL_KEY = 'cpa-gpt-levels';
  function gptLevels() {
    try { return JSON.parse(localStorage.getItem(LEVEL_KEY)) || { 2: 'High' }; }
    catch (e) { return { 2: 'High' }; }
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
  function gptCurrent() {
    const b = document.querySelector('button[aria-label="Select ChatGPT model"]');
    if (!b) return null;
    // innerText, because the button also holds a hidden "Thinking effort"
    // measuring label that textContent would glue onto the front.
    const t = (b.innerText || '').replace(/thinking effort/ig, '').replace(/\s+/g, ' ').trim();
    if (!t) return null;
    const lv = gptLevels();
    for (const k of Object.keys(lv)) if (t === lv[k] || t.startsWith(lv[k])) return { level: +k, label: t };
    return null;
  }
  function gptAdvice(a) {
    const cur = gptCurrent();
    if (!cur) return null;
    const want = (a.kind === 0 && a.depth <= 1) ? 0 : (a.kind === 2 || a.depth >= 2) ? 2 : 1;
    if (want === cur.level) return { agree: true, cur: cur.label };
    // Upward only for real reasoning, same rule as on Claude.
    if (want > cur.level && a.depth < 2 && a.kind < 2) return { agree: true, cur: cur.label };
    const lv = gptLevels();
    const label = lv[want] ? lv[want] + ' thinking' : (want === 0 ? 'Lowest thinking' : want === 1 ? 'Middle thinking' : 'High thinking');
    return { agree: false, dir: want < cur.level ? -1 : 1, label, cur: cur.label };
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
  .cpa-hint .cpa-why { opacity: 0.6; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .cpa-hint button { all: unset; cursor: pointer; opacity: 0.5; padding: 0 4px; font-size: 14px; line-height: 1; }
  .cpa-hint button:hover { opacity: 1; }
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

  let hint = null, hintText = null, hintWhy = null, cardEl = null;
  let lastAudit = null, lastAdvice = null;
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
      dismissedFor = b ? b.innerText.trim() : '';
      hide();
    });
    hint.append(dot, hintText, hintWhy, x);
    cardEl = document.createElement('div');
    cardEl.className = 'cpa-card';
    hint.addEventListener('mouseenter', showCard);
    hint.addEventListener('mouseleave', () => cardEl.classList.remove('cpa-on'));
    document.body.append(hint, cardEl);
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
    const rows = [
      ['You picked', lastAdvice.cur],
      ['Suggested', lastAdvice.label],
      ['Kind', KIND[a.kind] + '. ' + a.why.join('. ')],
      ['Reasoning', DEPTH[a.depth] + '. ' + a.depthWhy.join(', ')],
      ['Size', '~' + a.tokens.toLocaleString() + ' tokens typed'
        + (a.attachments ? ', plus ' + a.attachments + ' attachment' + (a.attachments > 1 ? 's' : '') + ' of unknown size' : '')],
    ];
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
      + (existingThread() ? 'Switching model mid-thread probably means the new model rereads the whole thread uncached. ' : '')
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
    const b = box();
    if (!b || blocked()) { hide(); return; }
    const text = (b.innerText || '').trim();
    if (!text) { dismissedFor = null; draftHinted = false; }
    const c = card(b);
    const att = attachments(c);
    if (text.length < CONFIG.minChars && !att) { hide(); return; }
    if (existingThread() && text.length < CONFIG.followUpMinChars && !att) { hide(); return; }
    if (dismissedFor !== null && text.startsWith(dismissedFor.slice(0, 40))) { hide(); return; }

    const a = audit(text, { attachments: att });
    const adv = SITE === 'claude' ? claudeAdvice(a) : gptAdvice(a);
    lastAudit = a;
    lastAdvice = adv;
    if (!adv || adv.agree) { hide(); return; }

    hintText.textContent = '';
    const bold = document.createElement('b');
    bold.textContent = adv.label;
    hintText.append(bold, document.createTextNode(adv.dir < 0 ? ' would do this' : ' fits this better'));
    hintWhy.textContent = a.why[0];
    hint.classList.toggle('cpa-up', adv.dir > 0);
    hint.classList.add('cpa-on');
    draftHinted = true;
    place();
  }

  function schedule() {
    clearTimeout(timer);
    timer = setTimeout(evaluate, CONFIG.debounceMs);
  }

  // Counted at send time. Followed means a hint showed for this draft and your
  // pick agreed with it by the time you sent, which is the only way the hint
  // goes quiet without the close button.
  function onSend() {
    if (!draftHinted) return;
    const st = stats();
    st.hinted += 1;
    if (lastAdvice && lastAdvice.agree && dismissedFor === null) st.followed += 1;
    try { localStorage.setItem('cpa-stats', JSON.stringify(st)); } catch (e) {}
    draftHinted = false;
    dismissedFor = null;
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
        onSend();
      }
    }, true);
    document.addEventListener('click', (e) => {
      const s = e.target.closest && e.target.closest(
        'button[aria-label="Send message"],button[data-testid="send-button"],#composer-submit-button');
      if (s) { evaluate(); onSend(); }
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
