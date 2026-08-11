# Launch drafts

Ready to paste. Each one leads with the finding rather than the tool, because
"I built a thing" reads as an advert and "your chat only holds four messages"
reads as news.

Send these from your own account. A first post from an account with no history
in a subreddit is the one most likely to be filtered, so comment in the thread
for a day or two beforehand if you have not been active lately.

---

## r/ClaudeAI

**Title**

> claude.ai only keeps three or four of your messages in the page. I built a rail that lists all of them.

**Body**

> I kept losing my place in long threads, so I went looking for why scrolling back is so bad. It turns out claude.ai virtualises the message list: at any moment the page holds three to five of your messages and everything else has been thrown away and replaced with a spacer. Ctrl+F genuinely cannot find what you asked an hour ago, because it is not there.
>
> So this reads Claude's own conversation API instead and puts every question you asked down the right edge, first to last. Click one to jump to it. Hover one to read the whole prompt.
>
> It also marks the files a thread produced next to the question that produced them, and clicking a file opens it in Claude's viewer. Cowork sessions work too, which took a different API entirely.
>
> There is a usage header as well, session and weekly, with a marker showing how far through each window you are, so the gap between the fill and the marker tells you whether you are pacing under the limit.
>
> A few things it deliberately does not do, because I could not make them honest: no context window percentage, since claude.ai publishes no usable context budget, and no "you have N messages left", since that number does not exist anywhere I could find.
>
> Source, install steps and the list of endpoints it reads: https://github.com/deepithbr/prompt-navigator
>
> It reads your logged-in session, so read it before you install it. That is the main reason the source is public.

---

## r/userscripts

**Title**

> Userscript: navigate a Claude thread by your own questions, reading the API instead of the DOM

**Body**

> The interesting part for this sub is that the DOM approach does not work at all here. claude.ai virtualises its transcript, so `querySelectorAll` on the message selector returns three to five nodes no matter how long the thread is, and programmatic scrolling does not remount the rest.
>
> So it walks the conversation API, follows `current_leaf_message_uuid` back through `parent_message_uuid` so edited-away branches are excluded, and builds the list from that.
>
> Two things worth knowing if you write scripts for this site:
>
> - Chrome 138+ needs "Allow user scripts" toggled in Tampermonkey's own extension details, separately from Developer mode. Without it Tampermonkey fails silently
> - `scrollIntoView({behavior:'smooth'})` is a no-op on claude.ai's scroll container. Instant `{block:'center'}` works
>
> https://github.com/deepithbr/prompt-navigator

---

## Hacker News

**Title**

> Show HN: Claude's web app only keeps four of your messages in the DOM

**First comment** (post this yourself right after submitting)

> Author here. This started as an annoyance and turned into a small study of how the app works.
>
> claude.ai virtualises the message list, so scrolling back through a long thread is not slow, it is impossible: the messages are gone from the document. The fix was to stop reading the page and read the conversation API the page itself calls.
>
> The part I found most interesting is what is not exposed. There is no context budget on the conversation object, no compaction signal, and no reachable models endpoint, so an honest context-window percentage cannot be built from the client. An earlier version guessed and was wrong by a factor of five. Cowork sessions turned out to use a completely separate API that answers 400 without an `anthropic-version` header, which is why it looked like it did not exist.
>
> It is not open source. The source is public because it reads your logged-in session and nobody should install that without reading it first.

---

## X / LinkedIn

> Scrolling back through a long Claude thread does not work, and it is not your browser.
>
> claude.ai keeps three or four of your messages in the page and throws the rest away. Ctrl+F cannot find what you asked an hour ago because it is not there.
>
> So I built a rail that reads the conversation API instead and lists every question in the thread.
>
> github.com/deepithbr/prompt-navigator

---

## Chrome Web Store listing

The only route to a one-click install. A one-off 5 dollar developer fee and a
review of a few days. Nothing in the manifest should trouble review: it
requests no permissions and declares no host permissions.

**Name**

> Prompt Navigator for Claude

**Short description** (132 characters max)

> Lists every question in a Claude thread down the side, jumps to any of them, marks the files it produced, and tracks plan usage.

**Category**: Workflow & Planning

**Detailed description**

> Scrolling back through a long Claude conversation does not work well, because claude.ai keeps only three or four of your messages in the page at a time.
>
> Prompt Navigator reads Claude's own conversation data and puts every question you asked down the right edge of the chat, first to last.
>
> • Click a question to jump to it, hover to read the whole prompt
> • See the files the thread produced, marked next to the question that produced them, and click one to open it
> • Search your questions across every thread with Alt+K
> • Read the model, effort level and your session and weekly plan usage from the header
> • Works in Cowork sessions as well as ordinary chats
> • Adds a usage meter to ChatGPT
>
> No permissions are requested. Nothing is sent anywhere. Everything stays in your browser.
>
> This uses undocumented internal endpoints and can break when those change. Not affiliated with Anthropic or OpenAI.

**Assets still needed**

- 128×128 icon
- At least one 1280×800 or 640×400 screenshot
- A privacy policy URL, required because the extension touches site data. The
  README's "What it reads" section is the substance; it needs a stable URL,
  which GitHub Pages on this repo would give for free
