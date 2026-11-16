// What the codex-limits mod keeps for its ring above the prompt.

/** One limit window as shown: percentage used, and the words for its reset. */
export type CodexWindow = { used: number; resets: string }

/** The two windows, or null when Codex has no reading on this PC. */
export type CodexView = { fiveHour: CodexWindow | null; week: CodexWindow | null } | null

// Shaped is the engine's own, in scope inside its module.
declare module 'claude-code' {
  interface PluginState {
    // view: what the ring shows. besideCache: the cache-clock mod shows its
    // one collapsed line, so the ring sits at that line's right end instead
    // of taking a row of its own. replied: a turn has ended this session,
    // which cache-clock waits for before it draws.
    'codex-limits': { view: Shaped<CodexView>; besideCache: boolean; replied: boolean }
  }
}
