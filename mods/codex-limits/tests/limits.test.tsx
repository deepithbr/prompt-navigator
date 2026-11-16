import type { On } from 'claude-code'
import { expect, mock, test } from 'claude-code/testing'

// A pretend ~/.codex: one day's folder with two sessions, the newer one
// holding two readings, of which the later counts.
const NOW_S = 1_794_800_000
const reading = (used5h: number, usedWeek: number) =>
  JSON.stringify({
    type: 'event_msg',
    payload: {
      type: 'token_count',
      rate_limits: {
        limit_id: 'codex',
        primary: { used_percent: used5h, window_minutes: 300, resets_at: NOW_S + 2 * 3600 + 5 * 60 },
        secondary: { used_percent: usedWeek, window_minutes: 10080, resets_at: NOW_S + 3 * 86400 + 20 * 3600 },
      },
    },
  })

const ROOT = 'C:/Users/me/.codex/sessions'
const FILES: Record<string, string> = {
  [`${ROOT}/2026/11/16/rollout-a.jsonl`]: [reading(10, 5), '{"type":"other"}'].join('\n') + '\n',
  [`${ROOT}/2026/11/16/rollout-b.jsonl`]: [reading(30, 20), reading(36, 26), '{"type":"msg","text":"half writ'].join('\n'),
}

function codexFolder(on: On) {
  // The engine hands Windows paths over with backslashes.
  const norm = (p: string) => p.split('\\').join('/')
  on('fs.list', (_, e) => {
    const path = norm(e.path)
    const dirs: Record<string, string[]> = { [ROOT]: ['2026'], [`${ROOT}/2026`]: ['11'], [`${ROOT}/2026/11`]: ['16'] }
    if (dirs[path]) return { value: dirs[path]!.map(name => ({ name, kind: 'dir' as const, size: 0, mtimeMs: 0, isLink: false })) }
    if (path === `${ROOT}/2026/11/16`)
      return {
        value: [
          { name: 'rollout-a.jsonl', kind: 'file' as const, size: 100, mtimeMs: 1, isLink: false },
          { name: 'rollout-b.jsonl', kind: 'file' as const, size: 200, mtimeMs: 2, isLink: false },
        ],
      }
    return { value: [] }
  })
  on('fs.read', (_, e) => ({ value: FILES[norm(e.path)] ?? '' }))
}

const BAND = {
  plugin: 'codex-limits',
  component: 'AbovePrompt',
  props: { hasSurvey: false, isCollapsed: false, isWorking: false, maxRows: 10, bodyColumns: 120, scroll: { offset: 0, bodyRows: 10 }, view: {} },
} as const

const WORDS = 'Codex usage\n5-hour: 36% used, resets in 2h 05m\nWeekly: 26% used, resets in 3d 20h'

test('the desktop app gets a ring at the right, with the limits on hover', async ($, on) => {
  mock.env(on, { USERPROFILE: 'C:/Users/me' })
  mock.clock(on, { now: NOW_S * 1000 })
  codexFolder(on)
  on('ui.status', () => ({ value: undefined }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  const svg = await ui.find({ type: 'Svg' })
  expect(svg?.props.alt).toBe(WORDS)
  // Both rings drawn, in the green that means there is room.
  expect(String(svg?.props.source).split('#5DCAA5').length - 1).toBe(2)

  // The overlay is the ring's own tooltip, so it floats and moves nothing:
  // drawn in a frame, and nothing hidden in the band to grow it.
  expect(svg?.props.isInteractive).toBe(true)
  expect((await ui.findAll({ type: 'Box' })).some(b => b.props.display === 'none')).toBe(false)
  // 36% of a 24-block bar is 9 blocks filled.
  const tip =
    'Codex usage limits\n\n' +
    '5-hour limit  ·  36%\n' + '█'.repeat(9) + '░'.repeat(15) + '\nResets in 2h 05m\n\n' +
    'Weekly  ·  26%\n' + '█'.repeat(6) + '░'.repeat(18) + '\nResets in 3d 20h'
  expect(String(svg?.props.source)).toContain(`<title>${tip}</title>`)
  await ui.unmount()
})

test('the terminal, which draws no pictures, gets the two figures', async ($, on) => {
  mock.env(on, { USERPROFILE: 'C:/Users/me' })
  mock.clock(on, { now: NOW_S * 1000 })
  codexFolder(on)
  on('ui.status', () => ({ value: undefined }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  await $.session.start({ cwd: '/', surface: 'terminal', isInteractive: true })

  const ui = await $.ui.mount({ ...BAND, surface: 'terminal' })
  expect((await ui.find({ type: 'Text' }))?.text).toBe('Codex 36% · week 26%')
  await ui.unmount()
})

test('it looks again every minute: a window that has passed shows as reset', async ($, on) => {
  mock.env(on, { USERPROFILE: 'C:/Users/me' })
  const clock = mock.clock(on, { now: NOW_S * 1000 })
  codexFolder(on)
  on('ui.status', () => ({ value: undefined }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })
  await clock.advance(3 * 3600 * 1000)

  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  expect((await ui.find({ type: 'Svg' }))?.props.alt).toBe(
    'Codex usage\n5-hour: 0% used, just reset\nWeekly: 26% used, resets in 3d 17h',
  )
  await ui.unmount()
})

test('no Codex on this PC: nothing is drawn', async ($, on) => {
  mock.env(on, { USERPROFILE: 'C:/Users/nobody' })
  mock.clock(on, { now: NOW_S * 1000 })
  on('fs.list', () => ({ value: [] }))
  on('ui.status', () => ({ value: undefined }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  // Stands for the app's own row, which the mod leaves alone here.
  on('ui.render', ($, e) => {
    const { Text } = $.ui.resolve(e)
    return <Text>app</Text>
  })
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  expect(await ui.find({ type: 'Svg' })).toBeUndefined()
  expect((await ui.find({ type: 'Text' }))?.text).toBe('app')
  await ui.unmount()
})

// cache-clock turned on in settings, and its switches as given; a turn has
// already ended this session.
function cacheClock(on: On, switches: Record<string, boolean>) {
  FILES['C:/Users/me/.claude/settings.json'] = JSON.stringify({ enabledPlugins: { 'cache-clock@benai-mods-win': true } })
  FILES['C:/Users/me/.claude/mods/switches.json'] = JSON.stringify(switches)
  on('state.get', (_, e, next) => (e.key === 'replied' ? { value: { value: true, version: 1 } } : next(e)))
}

// Stands for cache-clock beneath this mod: its folded line, ▾ at the end;
// and for the engine, which draws nothing for a row a mod passes on.
function cacheLine(on: On) {
  on('ui.render', ($, e) => {
    const { Box, Text } = $.ui.resolve(e)
    if (e.plugin === 'cache-clock') return <Text>Cache warm 40:57 · 758k tokens ▾</Text>
    return <Box />
  })
}

// (The kit draws rows as the mod under test's own, so the ring riding on
// cache-clock's row is checked on screen; here, this mod's side of it.)
test('cache-clock folded to one line: this mod draws no row of its own', async ($, on) => {
  mock.env(on, { USERPROFILE: 'C:/Users/me' })
  mock.clock(on, { now: NOW_S * 1000 })
  codexFolder(on)
  cacheClock(on, {})
  cacheLine(on)
  on('ui.status', () => ({ value: undefined }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const own = await $.ui.mount({ ...BAND, surface: 'desktop' })
  expect(await own.find({ type: 'Svg' })).toBeUndefined()
  await own.unmount()
})

test('reloaded partway through a conversation, the ring still finds the line', async ($, on) => {
  mock.env(on, { USERPROFILE: 'C:/Users/me' })
  mock.clock(on, { now: NOW_S * 1000 })
  codexFolder(on)
  FILES['C:/Users/me/.claude/settings.json'] = JSON.stringify({ enabledPlugins: { 'cache-clock@benai-mods-win': true } })
  FILES['C:/Users/me/.claude/mods/switches.json'] = '{}'
  // No turn has ended since this code loaded, but the session holds 752k tokens.
  on('session.usage', () => ({ value: { context: { tokens: 752_000, window: 1_000_000 } } as never }))
  cacheLine(on)
  on('ui.status', () => ({ value: undefined }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  // It counts as replied, so it steps aside for cache-clock's line.
  const own = await $.ui.mount({ ...BAND, surface: 'desktop' })
  expect(await own.find({ type: 'Svg' })).toBeUndefined()
  await own.unmount()
})

test('cache-clock opened out: the ring keeps a row of its own', async ($, on) => {
  mock.env(on, { USERPROFILE: 'C:/Users/me' })
  mock.clock(on, { now: NOW_S * 1000 })
  codexFolder(on)
  cacheClock(on, { 'cache-clock:expanded': true })
  on('ui.status', () => ({ value: undefined }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const ui = await $.ui.mount({ ...BAND, surface: 'desktop' })
  const boxes = await ui.findAll({ type: 'Box' })
  expect(boxes[0]?.props.height).toBeUndefined()
  expect(boxes[0]?.props.alignItems).toBe('flex-end')
  expect(await ui.find({ type: 'Svg' })).toBeDefined()
  await ui.unmount()
})

test('while a turn runs cache-clock hides its line, so the ring keeps its own row', async ($, on) => {
  mock.env(on, { USERPROFILE: 'C:/Users/me' })
  mock.clock(on, { now: NOW_S * 1000 })
  codexFolder(on)
  cacheClock(on, {})
  on('ui.status', () => ({ value: undefined }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  await $.session.start({ cwd: '/', surface: 'desktop', isInteractive: true })

  const ui = await $.ui.mount({ ...BAND, surface: 'desktop', props: { ...BAND.props, isWorking: true } })
  expect((await ui.findAll({ type: 'Box' }))[0]?.props.height).toBeUndefined()
  await ui.unmount()
})
