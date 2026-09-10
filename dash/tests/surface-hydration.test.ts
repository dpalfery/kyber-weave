import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { mkdtemp, mkdir, writeFile, rm, utimes } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import { parseAllSessions, clearSessionCache, withColdFirstPaintFloor } from '../src/parser.js'
import { clearLoadCacheMemo, isColdCacheOnDisk } from '../src/session-cache.js'
import { buildMenubarPayloadForRange, SERVE_HYDRATION_ENV } from '../src/usage-aggregator.js'
import { getDateRange } from '../src/cli-date.js'
import { coldFirstPaintRangeStart, SERVE_PROGRESSIVE_ENV } from '../src/serve.js'

const DAY_MS = 24 * 60 * 60 * 1000

let tmpDir: string

beforeEach(async () => {
  clearSessionCache()
  clearLoadCacheMemo()
  tmpDir = await mkdtemp(join(tmpdir(), 'surface-hydration-'))
  process.env['CLAUDE_CONFIG_DIR'] = tmpDir
  process.env['CODEBURN_CACHE_DIR'] = join(tmpDir, 'cache')
  process.env['CODEBURN_DESKTOP_SESSIONS_DIR'] = join(tmpDir, 'desktop-sessions')
  delete process.env[SERVE_HYDRATION_ENV]
  process.env[SERVE_PROGRESSIVE_ENV] = '1'
})

afterEach(async () => {
  clearSessionCache()
  clearLoadCacheMemo()
  delete process.env[SERVE_HYDRATION_ENV]
  delete process.env[SERVE_PROGRESSIVE_ENV]
  await rm(tmpDir, { recursive: true, force: true })
})

async function writeSession(name: string, ageDays: number): Promise<void> {
  const dir = join(tmpDir, 'projects', 'proj')
  await mkdir(dir, { recursive: true })
  const at = new Date(Date.now() - ageDays * DAY_MS)
  const path = join(dir, `${name}.jsonl`)
  await writeFile(path, JSON.stringify({
    type: 'assistant',
    sessionId: name,
    timestamp: at.toISOString(),
    cwd: '/tmp/proj',
    message: {
      id: `msg-${name}`, type: 'message', role: 'assistant', model: 'claude-sonnet-4-5',
      content: [], usage: { input_tokens: 100, output_tokens: 50 },
    },
  }) + '\n')
  await utimes(path, at, at)
}

const weekPayload = () => buildMenubarPayloadForRange(getDateRange('week'), { optimize: false, timeline: false })

describe('serve first-paint gate', () => {
  const start = (args: string[]) => coldFirstPaintRangeStart(args, getDateRange)

  it('accepts only the menubar payload, the one served output that carries the marker', () => {
    expect(start(['status', '--format', 'menubar-json'])).toBeInstanceOf(Date)
    expect(start(['status', '--format=menubar-json', '--no-timeline'])).toBeInstanceOf(Date)
    // Every other served command is a one-shot shape: its consumer has no
    // in-band way to tell a partial answer from a final one.
    expect(start(['status', '--format', 'json'])).toBeNull()
    expect(start(['status'])).toBeNull()
    expect(start(['models', '--format', 'json'])).toBeNull()
    expect(start(['sessions', '--format', 'json'])).toBeNull()
  })

  it('floors to the requested period and defaults to the CLI default', () => {
    const week = start(['status', '--format', 'menubar-json', '--period', 'week'])
    expect(week?.getTime()).toBe(getDateRange('week').range.start.getTime())
    expect(start(['status', '--format', 'menubar-json'])?.getTime())
      .toBe(getDateRange('today').range.start.getTime())
    expect(start(['status', '--format', 'menubar-json', '--period', 'nonsense'])).toBeNull()
  })

  it('declines every request when the client did not opt in', () => {
    // Each surface holds its own serve child. A client that cannot render the
    // indexing indicator (the Swift menubar, GNOME, Windows) never gets a
    // partial answer, whatever it asks for.
    delete process.env[SERVE_PROGRESSIVE_ENV]
    expect(start(['status', '--format', 'menubar-json'])).toBeNull()
  })

  it('declines an explicit range, which is a question that deserves a real answer', () => {
    expect(start(['status', '--format', 'menubar-json', '--day', '2026-01-01'])).toBeNull()
    expect(start(['status', '--format', 'menubar-json', '--from', '2026-01-01'])).toBeNull()
    expect(start(['status', '--format', 'menubar-json', '--to', '2026-01-01'])).toBeNull()
    expect(start(['status', '--format', 'menubar-json', '--days', '2026-01-01,2026-01-02'])).toBeNull()
  })
})

describe('hydration in the menubar payload', () => {
  it('labels a floored first paint partial, and never as stale', async () => {
    await writeSession('recent', 1)
    await writeSession('old', 200)
    process.env[SERVE_HYDRATION_ENV] = '1'

    const { result: payload, deferredFiles } = await withColdFirstPaintFloor(
      getDateRange('week').range.start,
      weekPayload,
    )
    expect(deferredFiles).toBe(1)
    expect(payload.hydration).toEqual({ complete: false, indexedFiles: expect.any(Number), totalFiles: expect.any(Number) })
    expect(payload.hydration!.totalFiles).toBeGreaterThan(payload.hydration!.indexedFiles)
    // `stale` is a different claim (a read-only snapshot that could not see
    // real files) and must not ride along with a converging first paint.
    expect(payload.stale).toBeUndefined()
  })

  it('reports complete once the fill has run', async () => {
    await writeSession('recent', 1)
    await writeSession('old', 200)
    process.env[SERVE_HYDRATION_ENV] = '1'

    await withColdFirstPaintFloor(getDateRange('week').range.start, weekPayload)
    clearSessionCache()
    const payload = await weekPayload()

    // Complete payloads carry no hydration block at all: absence means
    // complete, and the warm serve payload stays byte-identical to a one-shot.
    expect(payload.hydration).toBeUndefined()
    expect(payload.stale).toBeUndefined()
    expect(await isColdCacheOnDisk()).toBe(false)
  })

  it('omits the field entirely outside the resident serve process', async () => {
    await writeSession('recent', 1)
    await writeSession('old', 200)

    // The one-shot shape: a full parse, and no field for a script to mistake
    // for a completeness claim either way.
    const oneShot = await weekPayload()
    expect(oneShot.hydration).toBeUndefined()
    expect(await isColdCacheOnDisk()).toBe(false)

    // Even under a floor, an unmarked process emits no hydration block — the
    // floor is only ever set by the serve child, which does set the marker.
    clearSessionCache()
    clearLoadCacheMemo()
    process.env['CODEBURN_CACHE_DIR'] = join(tmpDir, 'cache-2')
    const floored = await withColdFirstPaintFloor(getDateRange('week').range.start, weekPayload)
    expect(floored.deferredFiles).toBe(1)
    expect(floored.result.hydration).toBeUndefined()
  })

  it('reports complete for a floored run that had nothing to defer', async () => {
    await writeSession('recent', 1)
    process.env[SERVE_HYDRATION_ENV] = '1'

    const { result: payload, deferredFiles } = await withColdFirstPaintFloor(
      getDateRange('week').range.start,
      weekPayload,
    )
    expect(deferredFiles).toBe(0)
    expect(payload.hydration).toBeUndefined()
  })

  it('keeps the spawn-fallback path on a full parse', async () => {
    await writeSession('recent', 1)
    await writeSession('old', 200)
    process.env[SERVE_HYDRATION_ENV] = '1'

    // The spawn fallback runs the same command with no floor: it is a one-shot
    // that cannot converge, so it must see every file.
    const payload = await weekPayload()
    expect(payload.hydration).toBeUndefined()
    const sessions = (await parseAllSessions()).flatMap(p => p.sessions).map(s => s.sessionId).sort()
    expect(sessions).toEqual(['old', 'recent'])
  })
})
