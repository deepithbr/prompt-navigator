# Prompt Navigator for Claude

A rail down the right edge of a Claude chat listing every question you asked, first to last, with the documents that thread produced marked in place. Click one to jump to it. The header shows which model and effort the chat is on, how big the conversation has grown, and how much of your plan you have used.

Ships with a companion meter for ChatGPT that shows your agent and task allowance.

Works as a Chrome extension or as two Tampermonkey userscripts. The files are the same either way.

---

## What it shows

**On `claude.ai`**

- Every question in the thread, numbered, read from Claude's own conversation API rather than the page — the page only ever holds three to five of them at a time
- Documents Claude wrote, placed after the question that produced them. Solid marker means the file is still downloadable, hollow means it was created and has since been cleared from the sandbox
- The model and effort the chat is running, read live, so switching either updates the header
- Conversation size in tokens, measured
- Session and weekly plan usage, with a marker showing how much of each window has elapsed

**On `chatgpt.com`**

- Plan type and the agent/task usage window, with the same elapsed-time marker

---

## Install as a Chrome extension

1. Download or clone this repository
2. Open `chrome://extensions`
3. Turn on **Developer mode**, top right
4. Click **Load unpacked** and choose the repository folder
5. Reload any open Claude or ChatGPT tab

No permissions are requested. The extension declares no `permissions` and no `host_permissions`; the two content scripts only run on the sites listed in the manifest.

## Install as userscripts instead

1. Install [Tampermonkey](https://www.tampermonkey.net/)
2. Open `chrome://extensions`, enable Developer mode, open Tampermonkey's **Details**, and turn on **Allow user scripts** — Chrome 138 and later refuse to inject without this, and Tampermonkey fails silently rather than warning you
3. Create a new script, paste in `claude-prompt-navigator.user.js`, save
4. Repeat for `chatgpt-usage.user.js`

---

## What it reads

Everything stays in your browser. Nothing is sent anywhere, and there is no server, no analytics and no storage beyond one `localStorage` flag remembering whether you pinned the rail open.

On `claude.ai` it calls the same endpoints the page itself calls, using your existing session:

| Endpoint | Why |
|---|---|
| `/api/organizations` | find your organization id |
| `/api/organizations/{org}/chat_conversations/{id}` | the full question list, model and effort |
| `/api/organizations/{org}/conversations/{id}/wiggle/list-files` | documents the thread produced |
| `/api/organizations/{org}/projects/{id}` | document and file counts, for a tooltip |
| `/api/organizations/{org}/usage` | session and weekly plan usage |

On `chatgpt.com`:

| Endpoint | Why |
|---|---|
| `/api/auth/session` | the bearer token the next call needs |
| `/backend-api/codex/usage` | plan type, percent used, window length, reset time |

**Read this before installing the ChatGPT half.** It reads your session access token in order to call the usage endpoint, exactly as the ChatGPT page does. The token never leaves the tab, but you should not take that on trust from a README — read `chatgpt-usage.user.js`, it is short and the two `fetch` calls are the only network activity in it. If you would rather not, delete that file and remove its entry from `manifest.json`; the Claude half is entirely independent.

---

## Known limits

These are deliberate. Earlier versions guessed at them and the guesses were wrong.

- **No context window percentage.** claude.ai publishes no usable context budget: no window field on the conversation, no compaction signal, no reachable models endpoint. The token count shown is a measurement of the conversation; it cannot see your system prompt, skills, tool definitions or project knowledge, so it is not expressed as a share of anything.
- **Jumping to an unloaded message is approximate.** Claude virtualises the message list and programmatic scrolling does not remount older messages. Clicking a question from an unloaded part of the thread shows you its full text and moves you to roughly the right place.
- **Cowork is partial.** `/cowork/` sessions are not served by the conversation API. The rail falls back to listing what is on screen and says `N on screen` so the count is not mistaken for the whole session.
- **ChatGPT message caps are not available.** `/backend-api/usage`, `/rate_limits` and `/conversation_limit` all return 404 and the model list carries no quota fields. Only the agent and task window is exposed, which is what the meter shows.
- **These are undocumented internal endpoints.** Anthropic and OpenAI can change them without notice, and when they do this breaks. There is a `MANUAL_SELECTOR` escape hatch at the top of the Claude script for the most likely breakage.

---

## Licence

Copyright (c) 2026 Deepith Kundar. All rights reserved. **This is not open source.**

The source is public for one reason: this software reads your logged-in session
on `claude.ai` and `chatgpt.com`, and nobody should install something like that
without being able to read it first.

You may install and run it on your own browsers, and modify your own copy. You
may not redistribute it, publish it to an extension store, fold it into another
product, or use it commercially. See `LICENSE` for the exact terms.

Not affiliated with, endorsed by, or connected to Anthropic or OpenAI.
