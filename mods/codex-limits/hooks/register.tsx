import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { CodexView, CodexWindow } from '../types'

// Codex writes your plan's usage into its session files as it works: each
// token count carries rate_limits, the 5-hour window as `primary` and the
// weekly one as `secondary`, each with the percentage used and when it
// resets. This mod reads the newest of those files once a minute and draws a
// small ring at the right end of the row above the prompt: the outer ring is
// the 5-hour limit, the inner one the week. Hovering it shows both in words.
// It reads nothing else and sends nothing.

type Window = { used_percent?: number; window_minutes?: number; resets_at?: number }
type Limits = { primary?: Window; secondary?: Window }
type Rollout = { path: string; mtimeMs: number; size: number }

// Change the tag when CodexView changes: it declines what an older version
// stored, which outlives a reload.
const shown = atom({ plugin: 'codex-limits', key: 'view' } as const, null as CodexView, { shape: 'view-1' })
const besideCache = atom({ plugin: 'codex-limits', key: 'besideCache' } as const, false)
const replied = atom({ plugin: 'codex-limits', key: 'replied' } as const, false)

const EVERY_MS = 60_000
// Files bigger than this are skipped: a read is held to 4 MiB.
const MAX_BYTES = 4 * 1024 * 1024 - 1
// How many of the newest files to look through for a reading.
const LOOK_BACK = 6
// The ring's size on screen, in CSS pixels.
const RING_PX = 20

// The numbered folders in a folder, highest first.
async function numberedDirs($: EngineInterface, path: string): Promise<string[]> {
  const entries = await $.fs.list(path).catch(() => [])
  return entries
    .filter(d => d.kind === 'dir' && /^\d+$/.test(d.name))
    .map(d => d.name)
    .sort()
    .reverse()
}

// The newest rollout files under <root>/YYYY/MM/DD, newest first.
async function newestFiles($: EngineInterface, root: string): Promise<Rollout[]> {
  const found: Rollout[] = []
  for (const year of await numberedDirs($, root)) {
    for (const month of await numberedDirs($, `${root}/${year}`)) {
      for (const day of await numberedDirs($, `${root}/${year}/${month}`)) {
        const folder = `${root}/${year}/${month}/${day}`
        const entries = await $.fs.list(folder).catch(() => [])
        for (const f of entries)
          if (f.kind === 'file' && f.name.startsWith('rollout-') && f.name.endsWith('.jsonl'))
            found.push({ path: `${folder}/${f.name}`, mtimeMs: f.mtimeMs, size: f.size })
        if (found.length >= LOOK_BACK) return found.sort((a, b) => b.mtimeMs - a.mtimeMs).slice(0, LOOK_BACK)
      }
    }
  }
  return found.sort((a, b) => b.mtimeMs - a.mtimeMs).slice(0, LOOK_BACK)
}

function find(value: unknown): Limits | undefined {
  if (!value || typeof value !== 'object') return undefined
  const o = value as Record<string, unknown>
  if (o.rate_limits && typeof o.rate_limits === 'object') return o.rate_limits as Limits
  for (const v of Object.values(o)) {
    const hit = find(v)
    if (hit) return hit
  }
  return undefined
}

// The last rate_limits a file holds, or undefined.
export function lastLimits(text: string): Limits | undefined {
  let at = text.lastIndexOf('"rate_limits"')
  while (at >= 0) {
    const start = text.lastIndexOf('\n', at) + 1
    const end = text.indexOf('\n', at)
    try {
      const limits = find(JSON.parse(text.slice(start, end < 0 ? undefined : end)))
      if (limits && (limits.primary || limits.secondary)) return limits
    } catch {
      // a line cut short while Codex writes it: try the one before
    }
    at = start > 0 ? text.lastIndexOf('"rate_limits"', start - 1) : -1
  }
  return undefined
}

function span(seconds: number): string {
  const m = Math.max(0, Math.round(seconds / 60))
  if (m < 60) return `${m}m`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ${String(m % 60).padStart(2, '0')}m`
  return `${Math.floor(h / 24)}d ${h % 24}h`
}

// A window as shown; one whose reset time has passed is fresh again.
function windowOf(w: Window | undefined, nowS: number): CodexWindow | null {
  if (!w || typeof w.used_percent !== 'number') return null
  if (typeof w.resets_at === 'number' && w.resets_at <= nowS) return { used: 0, resets: 'just reset' }
  const resets = typeof w.resets_at === 'number' ? `resets in ${span(w.resets_at - nowS)}` : ''
  return { used: Math.max(0, Math.min(100, Math.round(w.used_percent))), resets }
}

export function viewOf(limits: Limits | undefined, nowS: number): CodexView {
  if (!limits) return null
  const view = { fiveHour: windowOf(limits.primary, nowS), week: windowOf(limits.secondary, nowS) }
  return view.fiveHour || view.week ? view : null
}

// The words on hover, one line each.
export function words(view: NonNullable<CodexView>): string {
  const line = (name: string, w: CodexWindow | null) =>
    w ? `${name}: ${w.used}% used${w.resets ? `, ${w.resets}` : ''}` : `${name}: no reading`
  return ['Codex usage', line('5-hour', view.fiveHour), line('Weekly', view.week)].join('\n')
}

// The overlay on hover: the app's own tooltip, which floats over the window
// and moves nothing. Each limit gets its name and percentage, a filling bar
// and its reset, as Claude's own usage card lays them out.
const BAR_BLOCKS = 24
export function overlay(view: NonNullable<CodexView>): string {
  const block = (name: string, w: CodexWindow | null) => {
    if (!w) return [`${name}`, 'No reading yet']
    const lit = w.used <= 0 ? 0 : Math.max(1, Math.round((Math.min(100, w.used) / 100) * BAR_BLOCKS))
    const reset = w.resets ? w.resets[0]!.toUpperCase() + w.resets.slice(1) : ''
    return [`${name}  ·  ${w.used}%`, '█'.repeat(lit) + '░'.repeat(BAR_BLOCKS - lit), reset].filter(Boolean)
  }
  return ['Codex usage limits', '', ...block('5-hour limit', view.fiveHour), '', ...block('Weekly', view.week)].join('\n')
}

// Green while there is room, amber past 70%, red past 90%.
function tone(used: number): string {
  return used >= 90 ? '#E24B4A' : used >= 70 ? '#EF9F27' : '#5DCAA5'
}

function arc(r: number, used: number, width: number): string {
  const c = 2 * Math.PI * r
  const lit = (Math.max(0, Math.min(100, used)) / 100) * c
  return (
    `<circle cx="12" cy="12" r="${r}" fill="none" stroke="#8884" stroke-width="${width}"/>` +
    `<circle cx="12" cy="12" r="${r}" fill="none" stroke="${tone(used)}" stroke-width="${width}" stroke-linecap="round" ` +
    `stroke-dasharray="${lit.toFixed(2)} ${c.toFixed(2)}" transform="rotate(-90 12 12)"/>`
  )
}

// The ring: the 5-hour limit outside, the week inside, and the overlay as
// its tooltip. Escaped for the markup.
export function ring(view: NonNullable<CodexView>): string {
  const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="${RING_PX}" height="${RING_PX}">` +
    `<title>${esc(overlay(view))}</title>` +
    arc(9.5, view.fiveHour?.used ?? 0, 3) +
    arc(4.5, view.week?.used ?? 0, 3) +
    `<rect width="24" height="24" fill="transparent"/>` +
    `</svg>`
  )
}

async function refresh($: EngineInterface): Promise<void> {
  const own = await $.env.get('CODEX_HOME')
  const profile = (await $.env.get('USERPROFILE')) ?? (await $.env.get('HOME')) ?? ''
  const home = own ?? `${profile}/.codex`
  let limits: Limits | undefined
  for (const f of await newestFiles($, `${home}/sessions`)) {
    if (f.size > MAX_BYTES) continue
    const text = await $.fs.read(f.path).catch(() => '')
    limits = typeof text === 'string' ? lastLimits(text) : undefined
    if (limits) break
  }
  const view = viewOf(limits, (await $.clock.now()) / 1000)
  await update($, shown, () => view)
}

async function readJson($: EngineInterface, path: string): Promise<Record<string, unknown>> {
  try {
    const text = await $.fs.read(path)
    const json = JSON.parse(typeof text === 'string' ? text.replace(/^﻿/, '') : '{}') as unknown
    return json && typeof json === 'object' && !Array.isArray(json) ? (json as Record<string, unknown>) : {}
  } catch {
    return {}
  }
}

// Whether the cache-clock mod is showing its one collapsed line: it is
// turned on in settings, not hidden, not opened out (it keeps both switches
// in ~/.claude/mods/switches.json), and a turn has ended, which it waits for.
async function noteCache($: EngineInterface): Promise<void> {
  const profile = (await $.env.get('USERPROFILE')) ?? (await $.env.get('HOME')) ?? ''
  const settings = await readJson($, `${profile}/.claude/settings.json`)
  const enabled = (settings.enabledPlugins ?? {}) as Record<string, unknown>
  const on = Object.keys(enabled).some(k => k.startsWith('cache-clock@') && enabled[k] === true)
  const switches = await readJson($, `${profile}/.claude/mods/switches.json`)
  const collapsed = switches['cache-clock:hidden'] !== true && switches['cache-clock:expanded'] !== true
  // A reply has ended if one ended since this code loaded, or if the session
  // already holds a conversation, as after a reload partway through.
  const hasReplied =
    (await read($, replied)) || ((await $.session.usage().catch(() => undefined))?.context.tokens ?? 0) > 0
  const want = on && collapsed && hasReplied
  if ((await read($, besideCache)) !== want) await update($, besideCache, () => want)
}

// The element table a surface draws with; only what the ring uses.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type Elements = { Svg?: any }

// The ring. Drawn in its own frame so the app shows its tooltip, the
// overlay, when the pointer rests on it; nothing around it moves.
function ringButton(el: Elements, view: NonNullable<CodexView>) {
  const { Svg } = el
  return <Svg source={ring(view)} alt={words(view)} width={RING_PX} height={RING_PX} isInteractive />
}

// cache-clock draws its line only while no turn runs and no survey holds
// the band; the ring joins that line only while it is there to join.
async function sharesLine($: EngineInterface, band: { isWorking: boolean; hasSurvey: boolean }): Promise<boolean> {
  return !band.isWorking && !band.hasSurvey && (await read($, besideCache))
}

export const register: Register = on => {
  let timer: { cancel: () => void } | undefined

  on('session.start', async ($, e, next) => {
    const result = await next(e)
    // An earlier version pinned a status line; this one draws the ring.
    $.ui.status(undefined)
    await refresh($).catch(() => undefined)
    await noteCache($).catch(() => undefined)
    timer?.cancel()
    timer = $.clock.every(EVERY_MS, () => {
      void refresh($).catch(() => undefined)
      void noteCache($).catch(() => undefined)
    })
    return result
  })

  // A turn may have run Codex (a skill, a subagent): look again at its end.
  on('turn.complete', async ($, e, next) => {
    const result = await next(e)
    await update($, replied, () => true)
    await refresh($).catch(() => undefined)
    await noteCache($).catch(() => undefined)
    return result
  })

  // A press on cache-clock's line opens it out or folds it: follow at once.
  on('ui.press', { plugin: 'cache-clock' }, async ($, e, next) => {
    const result = await next(e)
    await noteCache($).catch(() => undefined)
    return result
  })

  // The ring at the right end of the row above the prompt. It gives way to a
  // survey, and with no reading there is nothing. The terminal draws no
  // pictures, so there it is two figures instead.
  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    const view = await read($, shown)
    if (e.props.hasSurvey || !view) return next(e)
    if (e.surface === 'terminal') {
      const { Box, Text } = $.ui.resolve(e)
      const short = `Codex ${view.fiveHour?.used ?? '-'}% · week ${view.week?.used ?? '-'}%`
      return (
        <Box justifyContent="flex-end" width="100%">
          <Text dimColor>{short}</Text>
        </Box>
      )
    }
    // Beside cache-clock's folded line the ring rides at that line's end
    // (the hook below), so this mod draws no row of its own. Opened out, or
    // with no such line, the ring keeps a row of its own.
    if (await sharesLine($, e.props)) return next(e)
    const el = $.ui.resolve(e)
    const { Box } = el
    return (
      <Box flexDirection="column" alignItems="flex-end" width="100%">
        {ringButton(el, view)}
      </Box>
    )
  })

  // cache-clock's own row: while it is folded to one line, its line keeps
  // the room it needs, its ▾ at its right end, and the ring goes after it,
  // at the far right of the band. Its tree is carried as it drew it.
  on('ui.render', { component: 'AbovePrompt', plugin: 'cache-clock' }, async ($, e, next) => {
    const theirs = await next(e)
    if (!theirs || e.surface === 'terminal' || !(await sharesLine($, e.props))) return theirs
    const view = await read($, shown)
    if (!view) return theirs
    // Their tree holds the engine's own node, and no Box above that node may
    // be given a size, so the line and the ring simply sit side by side, with
    // the card, hidden until the ring is hovered, above them.
    const el = $.ui.resolve(e)
    const { Box } = el
    return (
      <Box flexDirection="row" alignItems="flex-end" gap={1}>
        {theirs}
        {ringButton(el, view)}
      </Box>
    )
  })

  on('session.end', async ($, e, next) => {
    timer?.cancel()
    timer = undefined
    return next(e)
  })
}
