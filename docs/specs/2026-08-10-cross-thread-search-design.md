# Cross-thread search, burn rate and handoff

Design specification, 10 August 2026.
Target: `claude-prompt-navigator.user.js`, currently v3.3.0.

## What this adds

Three features go into the rail. They share one idea: everything the rail
already fetches during normal use is worth keeping, and almost nothing new
needs to be fetched to make these work.

- **Cross-thread question search.** An `Alt+K` palette that searches your
  questions across every conversation on the account, not only the open one,
  and jumps to a match in another thread.
- **Weekly burn rate.** The rail already polls plan usage every 60 seconds.
  Storing successive samples turns a static percentage into a rate, which
  answers "at this pace, when do I run out".
- **Handoff generator.** A button that assembles a paste-ready summary of the
  current thread from data the rail has already loaded, so moving work into a
  new thread stops being a manual copy job.

The rail's existing behaviour does not change. All three additions sit beside
it and degrade to nothing if their data is unavailable.

## Measured constraints

Everything below was measured live against the real account before any code
was designed. The numbers decide the architecture, so they are stated first.

Conversations on the account:

    544

Conversations with a populated `summary` field:

    537 (99%)

Those summaries are substantial prose rather than one-line titles, which
matters because it makes tier 1 of the index genuinely searchable rather than
a list of names.

One call to `/api/organizations/{org}/chat_conversations` returns all 544
conversations with name, summary, timestamps and project association.

    1.77 MB, 809 ms

That call does **not** include message bodies. Question text is only available
per conversation.

Fetching a single conversation's messages, averaged:

    630 ms, 75 KB

Which sets the cost of indexing everything up front:

    544 × 630 ms  =  5.7 minutes
    544 × 75 KB   =  40 MB

That is the option this design rejects. A userscript that spends 5.7 minutes
of network time and 40 MB of storage before its search box works is not
something anyone would leave installed, and the crawl would have to run again
as threads change. Rejected on cost, not on difficulty.

There is no server-side search endpoint to fall back on. `/search`,
`/chat_conversations/search` and the obvious variants all return 404 or 400.
Searching message text therefore has to happen in the browser, over whatever
the browser has already seen.

## The two-tier index

The index lives in IndexedDB and has two tiers that are populated by
completely different mechanisms.

**Tier 1, every thread, shallow.** One call to the conversation list gives
name and summary for all 544 threads at a known cost of 1.77 MB and 809 ms.
This is written to the index whole. It is refreshed only when the stored copy
is older than an hour, so a normal browsing session pays for it once.

Tier 1 makes every conversation findable by title and by the summary Claude
wrote for it. With 99% coverage, that is close to complete recall at the
thread level.

**Tier 2, opened threads, deep.** The rail already fetches every question in a
conversation when you open it. Those questions are written into the index as a
byproduct of that fetch. No extra request is made, no extra latency is
introduced, and the write happens after the rail has rendered.

Tier 2 makes threads you have actually opened searchable word for word. The
index therefore grows in exactly the shape of your usage: threads you return
to get deep coverage, threads you have never opened stay at title and summary
level. No upfront crawl ever runs, and none is offered.

Storage shape, one object store per tier:

    threads   { uuid, name, summary, updatedAt, projectUuid, searchText }
    questions { uuid, convUuid, n, text, at, searchText }

`searchText` is the lowercased concatenation written once at index time. The
reason for that is in the next section.

Why not index on a schedule in the background? Because a background crawl has
the same 5.7 minute and 40 MB cost as a foreground one, and it spends it on
threads you may never search for. Tying indexing to opening a thread spends
the cost only where it has already been proven useful.

Why not use `localStorage`? A 1.77 MB list plus question text for a few
hundred threads passes the practical 5 MB ceiling quickly, and `localStorage`
is synchronous, which conflicts directly with the responsiveness requirement.
IndexedDB is asynchronous and has no ceiling worth worrying about here.

## Responsiveness

This was called out explicitly as a requirement, so it is specified as one
rather than left as an implementation detail. The palette must never wait on
the network. Every rule below exists to hold that line.

**Hydrate at startup.** The index is read out of IndexedDB into memory once,
when the script starts. Search then runs against in-memory arrays. Opening the
palette performs no I/O at all.

**Normalise once, at write time.** Search strings are lowercased when they go
into the index, never during a keystroke. Lowercasing thousands of strings per
keystroke is the obvious way to make a palette feel heavy, and it is entirely
avoidable.

**Filter with `indexOf`.** Matching is substring containment over the
pre-normalised strings. No regular expression compilation, no fuzzy scoring
pass over the full set. Ranking is applied only to the matches that survive
the filter.

**Cap results at 50 rows.** Beyond 50, more rows do not help you find
anything, and building them costs real layout time. The filter stops early
once the cap is reached.

**Paint in one animation frame.** Keystrokes schedule a single
`requestAnimationFrame` that rebuilds the result list. Rapid typing coalesces
into one paint per frame instead of one per keystroke.

**Prefetch the highlighted row.** Whichever result is currently highlighted
has its conversation fetched in the background. By the time you press Enter,
the data is usually already in hand and the jump has nothing left to wait for.
Moving the highlight cancels the previous prefetch.

**Navigate client side.** A cross-thread jump uses the app's own client-side
navigation rather than setting `location.href`. A full page load would cost a
cold start of claude.ai plus a fresh script boot, which is the slowest
possible way to move between two threads.

Search order within the palette is current thread first, then all other
threads. The thread you are in is the most likely target, and putting it first
means the palette also serves as the in-rail filter.

## Feature 1: cross-thread question search

**Trigger.** `Alt+K` opens the palette, as does a `⌕` button in the rail
header. `Ctrl+K` was the original choice and is not available: claude.ai binds
it to its own command palette, and taking it would have removed a first-party
feature. `Escape`
closes it. Arrow keys move the highlight, `Enter` opens the highlighted
result. The handler ignores the shortcut while focus is in the composer, so it
never steals a keystroke from a message you are writing.

**What it searches.** Three sources, in this order:

1. Questions in the current thread, already in memory from the rail.
2. Questions in other threads, from tier 2 of the index.
3. Thread names and summaries, from tier 1 of the index.

Results are grouped under those three headings so it is always clear whether a
hit is an exact question you asked or a thread that merely looks relevant.

**What a row shows.** The matched question text with the matching span marked,
the thread name it came from, and a relative timestamp. Tier 1 rows show the
thread name and the matching part of the summary.

**Opening a result.** A hit in the current thread reuses the rail's existing
`jumpTo` path with no navigation. A hit in another thread navigates client
side to that conversation, waits for the rail to load it, then runs the same
jump logic against the target question.

**Coverage, stated honestly in the UI.** The palette footer reports how many
threads are indexed at question level against the total, for example
"137 of 544 threads searched in full". Without that line, a user cannot tell
the difference between "that question does not exist" and "that thread has
never been opened". The line is not decoration, it is the only thing that
makes a null result interpretable.

## Feature 2: weekly burn rate

The rail already reads `/api/organizations/{org}/usage` every 60 seconds and
draws the weekly percentage as a bar. That percentage is a measurement from
Claude directly, and it is the only input this feature uses.

**Method.** Each poll appends `{ percent, at }` to a ring buffer held in
`localStorage`. Given two samples far enough apart, the rate is:

    rate  = (percent₂ − percent₁) / (hours between samples)
    hours = (100 − percent₂) / rate

The rail then reads, for example, "at this rate you hit the weekly limit in
19 hours". The existing bar keeps its clock marker, so the projection and the
marker are two readings of the same question from different directions.

**What it refuses to say.** With fewer than two samples, or with samples less
than 10 minutes apart, or with a rate at or below zero, the line is hidden
entirely. A projection built on two readings 40 seconds apart is noise
multiplied by a large number, and showing it would be worse than showing
nothing. Hidden is the correct default state on a fresh install.

**What it is.** A projection of the recent rate, and nothing more. It assumes
you carry on working exactly as you have been, which is the assumption most
likely to be wrong. If you stop for the evening the projection is meaningless,
and if you switch to a higher effort level it will underestimate. The tooltip
says this in those words. It is a pace indicator, not a guarantee.

Samples are discarded when the weekly window resets, since a percentage that
has just dropped to zero would otherwise produce a large negative rate. A drop
of more than five points is treated as a reset. Samples older than six hours
are also dropped, because the projection is meant to read recent pace and a
reading from yesterday morning dilutes it toward nothing.

## Feature 3: handoff generator

Long threads get retired and their work moves to a new one. That handoff is
currently written by hand, which is where a file such as
`C08_Cowork_Handover.md` came from. Everything in such a document already
exists in the rail's memory at the moment you need it.

**Trigger.** A small button in the rail header, visible when the rail is open.
One click assembles the block and writes it to the clipboard via
`navigator.clipboard.writeText`, with the button confirming in place.

**What it assembles.** Only data already loaded. No new network call is made.

- Thread name and a link to it.
- Model and effort level, from `convModel` and `convEffort`.
- The numbered list of questions, from `questions`.
- Documents the thread produced, from `documents`, with the ones no longer in
  the sandbox marked as such, since a handoff that lists a file you can no
  longer download is actively misleading.
- The measured conversation size, carried over with the same caveat the rail
  already attaches to it.

**Output.** Markdown, because the destination is a Claude message or a `.md`
file. Long question lists are truncated per item rather than dropped, so the
shape of the thread survives even on a 60 question conversation.

The block is a starting point for a handoff, not a finished one. It carries
what happened. It cannot carry why, and the generated text says so at the top
rather than pretending to be complete.

## Deliberate exclusions

Two features were considered in detail and dropped. Both are recorded here so
they do not get proposed again without new reasoning.

**Download all documents.** The rail already knows every document a thread
produced, so a bulk download button is a short piece of code. It was dropped
because claude.ai ships a "Download all" button in its own Artifacts panel. A
second button doing the same job would sit next to the first one, would break
whenever the file API changed, and would offer the user a choice between two
identical actions. Reimplementing something the host app already does well is
a cost with no matching benefit.

**Type to filter the rail.** An inline filter box in the rail header was the
original plan. It was folded into the palette instead, because the
palette searches the current thread first and then everything else. Building
both would put two search boxes on screen with overlapping scope, and a user
would have to learn which one to reach for. One entry point that widens its
scope as you go is a smaller thing to learn than two boxes with a boundary
between them.

## Known limitations

**Jumps into unrendered parts of a thread land approximately.** This is the
same limitation the rail already documents, and it is inherited by
cross-thread search rather than introduced by it. claude.ai virtualises its
message list: messages outside the current window are replaced by a spacer and
are genuinely absent from the document. Programmatic scrolling moves the
scrollbar without remounting them. So a jump to a question in an unrendered
region gives you the full question text on a card and a landing near the right
part of the thread, rather than an exact scroll to the message. A small amount
of real scrolling brings it in. There is no fix available from a userscript.

**Question-level search only covers threads you have opened.** By design, and
reported in the palette footer. A question in a thread you have never opened
in this browser is findable through its thread's name and summary, and not by
its own text.

**Tier 1 can be up to an hour stale.** A conversation created in the last hour
may be missing from the palette until the list refreshes. The alternative,
refetching 1.77 MB more often, costs more than the staleness does.

**The burn rate needs two spaced samples.** On a fresh install, after a
browser restart, or right after a weekly reset, the line is absent. That is
correct behaviour and not a failure state.

**Clipboard access requires a user gesture.** The handoff button works because
a click is a gesture. The same code called from the console or a timer will be
rejected by the browser.

**The most likely wrong assumption in this design** is that client-side
navigation to another conversation can be driven reliably from a userscript
without a full page load. The smallest check that settles it: on a live
claude.ai tab, call `history.pushState` to another `/chat/<uuid>` and confirm
the app repaints the conversation rather than leaving the previous one on
screen. If it does not, the fallback is a normal navigation with the target
question handed over in `sessionStorage` and picked up by the rail on boot,
which costs a page load but changes nothing else in this design.
