/*
 * Passes task-reporter.js's reports to the Prompt Advisor task board, which
 * listens on this PC only, at 127.0.0.1:47823. This is the one address the
 * extension is allowed to contact. A page cannot reach it itself: the site's
 * own security rules block it, and the board refuses anything without the
 * X-Prompt-Advisor header.
 */
chrome.runtime.onMessage.addListener((msg) => {
  if (!msg || msg.type !== 'task' || !msg.body) return;
  fetch('http://127.0.0.1:47823/event', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Prompt-Advisor': '1' },
    body: JSON.stringify(msg.body),
  }).catch(() => { /* the board is not running */ });
});
