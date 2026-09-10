import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { mkdtemp, mkdir, writeFile, rm, utimes } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import {
  parseAllSessions,
  clearSessionCache,
  withColdFirstPaintFloor,
  shouldDeferToBackgroundFill,
  filesParsedFromSourceCount,
  isSessionHydrationComplete,
  FIRST_PAINT_MTIME_MARGIN_MS,
} from '../src/parser.js'
import { clearLoadCacheMemo, isColdCacheOnDisk } from '../src/session-cache.js'
import { readCacheOnDisk } from './fixtures/session-cache-io.js'

const DAY_MS = 24 * 60 * 60 * 1000

let tmpDir: string

beforeEach(async () => {
  clearSessionCache()
  clearLoadCacheMemo()
  tmpDir = await mkdtemp(join(tmpdir(), 'progressive-cold-'))
  process.env['CLAUDE_CONFIG_DIR'] = tmpDir
  process.env['CODEBURN_CACHE_DIR'] = join(tmpDir, 'cache')
  process.env['CODEBURN_DESKTOP_SESSIONS_DIR'] = join(tmpDir, 'desktop-sessions')
})

afterEach(async () => {
  clearSessionCache()
  clearLoadCacheMemo()
  await rm(tmpDir, { recursive: true, force: true })
})

/** One Claude session file whose only turn is `ageDays` old, with an mtime to
 *  match — the shape the mtime argument is about. */
async function writeSession(name: string, ageDays: number): Promise<string> {
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
  return path
}

function lastWeek(): Date {
  return new Date(Date.now() - 7 * DAY_MS)
}

async function cachedClaudePaths(): Promise<string[]> {
  const raw = await readCacheOnDisk()
  return Object.keys(raw.providers['claude']?.files ?? {}).sort()
}

describe('first-paint deferral predicate', () => {
  const floor = 1_000_000

  it('defers only files strictly below the floor', () => {
    expect(shouldDeferToBackgroundFill({ mtimeMs: floor - 1 }, undefined, floor)).toBe(true)
    expect(shouldDeferToBackgroundFill({ mtimeMs: floor }, undefined, floor)).toBe(false)
    expect(shouldDeferToBackgroundFill({ mtimeMs: floor + 1 }, undefined, floor)).toBe(false)
  })

  it('keeps a file the clock-skew margin rescues', () => {
    // The floor the dashboard passes is rangeStart - MARGIN, so a file stamped
    // up to MARGIN before the range start is still parsed for the first paint.
    const rangeStart = 10 * DAY_MS
    const skewed = rangeStart - FIRST_PAINT_MTIME_MARGIN_MS + 1
    expect(shouldDeferToBackgroundFill({ mtimeMs: skewed }, undefined, rangeStart - FIRST_PAINT_MTIME_MARGIN_MS)).toBe(false)
    expect(shouldDeferToBackgroundFill({ mtimeMs: skewed - 2 }, undefined, rangeStart - FIRST_PAINT_MTIME_MARGIN_MS)).toBe(true)
  })

  it('never defers a file that has a cache entry, or any file outside the scope', () => {
    const cached = { fingerprint: { dev: 0, ino: 0, mtimeMs: 0, sizeBytes: 0 }, mcpInventory: [], turns: [] }
    expect(shouldDeferToBackgroundFill({ mtimeMs: floor - 1 }, cached, floor)).toBe(false)
    // floor === null is every warm run and every one-shot command.
    expect(shouldDeferToBackgroundFill({ mtimeMs: floor - 1 }, undefined, null)).toBe(false)
  })

  it('never defers a network source', () => {
    // Network providers have no on-disk file; they enter the parse with a
    // synthetic fingerprint stamped `Date.now()` (and skip this check entirely),
    // so no floor a dated view can produce ever holds them back.
    expect(shouldDeferToBackgroundFill({ mtimeMs: Date.now() }, undefined, lastWeek().getTime())).toBe(false)
  })
})

describe('progressive cold start', () => {
  it('paints from the recent files and leaves the rest to the fill', async () => {
    await writeSession('recent', 1)
    await writeSession('old', 200)

    const { result, deferredFiles } = await withColdFirstPaintFloor(lastWeek(), () => parseAllSessions())
    expect(deferredFiles).toBe(1)
    expect(result.flatMap(p => p.sessions).map(s => s.sessionId)).toEqual(['recent'])
    // A pass that deferred files is a partial hydration: it must not stamp the
    // session cache complete (the next launch has to come back cold) and must
    // not let the daily backfill finalize history off it.
    expect(isSessionHydrationComplete()).toBe(false)
    expect(await isColdCacheOnDisk()).toBe(true)
    // Durable partial progress: the file it DID parse is already cached.
    expect(await cachedClaudePaths()).toEqual([join(tmpDir, 'projects', 'proj', 'recent.jsonl')])
  })

  it('converges on the full-cold-parse state, without re-parsing what pass 1 did', async () => {
    await writeSession('recent', 1)
    await writeSession('old', 200)

    await withColdFirstPaintFloor(lastWeek(), () => parseAllSessions())
    clearSessionCache()

    // The background fill is an ordinary unscoped parse in the same process.
    const parsedBefore = filesParsedFromSourceCount()
    const filled = await parseAllSessions()
    // Exactly one file parsed: the deferred one. The pass-1 file is served from
    // its cache entry, so it is neither re-read nor counted twice.
    expect(filesParsedFromSourceCount() - parsedBefore).toBe(1)
    expect(filled.flatMap(p => p.sessions).map(s => s.sessionId).sort()).toEqual(['old', 'recent'])
    expect(isSessionHydrationComplete()).toBe(true)
    expect(await isColdCacheOnDisk()).toBe(false)

    const progressive = await readCacheOnDisk()

    // Same corpus, a plain full cold parse, in a cache dir of its own.
    process.env['CODEBURN_CACHE_DIR'] = join(tmpDir, 'cache-baseline')
    clearSessionCache()
    clearLoadCacheMemo()
    await parseAllSessions()
    const baseline = await readCacheOnDisk()

    expect(progressive.providers['claude']?.files).toEqual(baseline.providers['claude']?.files)
    expect(progressive.complete).toBe(baseline.complete)
  })

  it('resumes after a fill that never ran', async () => {
    await writeSession('recent', 1)
    await writeSession('old', 200)

    // Pass 1 only, then the process dies: what survives on disk is the recent
    // file's entry and an INCOMPLETE marker.
    await withColdFirstPaintFloor(lastWeek(), () => parseAllSessions())
    expect(await isColdCacheOnDisk()).toBe(true)

    // Next launch. The deferred file has no cache entry, so it is still
    // discovered as changed and parsed; nothing is stranded behind a "seen" mark.
    clearSessionCache()
    clearLoadCacheMemo()
    const resumed = await parseAllSessions()
    expect(resumed.flatMap(p => p.sessions).map(s => s.sessionId).sort()).toEqual(['old', 'recent'])
    expect(await isColdCacheOnDisk()).toBe(false)
  })

  it('is a no-op when every file is recent enough to paint', async () => {
    await writeSession('recent', 1)

    const { deferredFiles } = await withColdFirstPaintFloor(lastWeek(), () => parseAllSessions())
    // Nothing deferred means this pass saw the whole corpus, so it keeps the
    // ordinary completeness stamp instead of owing a fill.
    expect(deferredFiles).toBe(0)
    expect(isSessionHydrationComplete()).toBe(true)
    expect(await isColdCacheOnDisk()).toBe(false)
  })
})
