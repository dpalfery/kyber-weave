import { describe, it, expect, beforeAll, afterAll } from 'vitest'
import { spawn, type ChildProcess } from 'child_process'
import { mkdtemp, mkdir, writeFile, rm, utimes } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'

import { parseAllSessions, clearSessionCache } from '../src/parser.js'
import { clearLoadCacheMemo } from '../src/session-cache.js'
import { readCacheOnDisk } from './fixtures/session-cache-io.js'

const DAY_MS = 24 * 60 * 60 * 1000

// End-to-end for the progressive cold start of the resident serve child
// (#1110): a cold cache is answered from the files the requested period can
// show, the answer says so in-band, and the background fill converges the
// on-disk cache to exactly what a full cold parse would have written.
describe('codeburn serve --stdio progressive cold start', () => {
  let home: string
  let child: ChildProcess
  let buffer = ''
  const waiters = new Map<number, (msg: Record<string, unknown>) => void>()
  let readyResolve: () => void
  const ready = new Promise<void>(resolve => { readyResolve = resolve })

  function request(id: number, args: string[]): Promise<Record<string, unknown>> {
    return new Promise(resolve => {
      waiters.set(id, resolve)
      child.stdin!.write(JSON.stringify({ id, args }) + '\n')
    })
  }

  async function session(name: string, ageDays: number): Promise<void> {
    const dir = join(home, '.claude', 'projects', 'proj')
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

  const PAYLOAD_ARGS = ['status', '--format', 'menubar-json', '--period', 'week', '--no-timeline', '--no-optimize']

  beforeAll(async () => {
    home = await mkdtemp(join(tmpdir(), 'serve-progressive-'))
    await session('recent', 1)
    await session('old', 200)

    child = spawn(process.execPath, ['--import', 'tsx', join(__dirname, '..', 'src', 'cli.ts'), 'serve', '--stdio'], {
      stdio: ['pipe', 'pipe', 'ignore'],
      env: {
        ...process.env,
        HOME: home,
        CLAUDE_CONFIG_DIR: join(home, '.claude'),
        CODEBURN_CACHE_DIR: join(home, 'cache'),
        CODEBURN_DESKTOP_SESSIONS_DIR: join(home, 'desktop-sessions'),
        // The desktop app's opt-in: only a client that renders the indexing
        // indicator may be answered partially.
        CODEBURN_SERVE_PROGRESSIVE: '1',
        // The fill normally waits for the client's opening burst to land.
        CODEBURN_SERVE_FILL_DELAY_MS: '200',
      },
    })
    child.stdout!.setEncoding('utf8')
    child.stdout!.on('data', (chunk: string) => {
      buffer += chunk
      let idx: number
      while ((idx = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, idx).trim()
        buffer = buffer.slice(idx + 1)
        if (!line) continue
        const msg = JSON.parse(line) as Record<string, unknown>
        if (msg['ready']) { readyResolve(); continue }
        // Progress frames keep a waiting client's watchdog armed; they are not
        // the answer.
        if (typeof msg['progress'] === 'string') continue
        const waiter = waiters.get(msg['id'] as number)
        if (waiter) { waiters.delete(msg['id'] as number); waiter(msg) }
      }
    })
    await ready
  }, 60_000)

  afterAll(async () => {
    child?.kill('SIGKILL')
    await rm(home, { recursive: true, force: true })
  })

  it('answers cold with labelled partial data, then converges to complete', async () => {
    const first = await request(1, PAYLOAD_ARGS)
    expect(first['ok']).toBe(true)
    const partial = JSON.parse(first['output'] as string) as {
      hydration?: { complete: boolean; indexedFiles: number; totalFiles: number }
      stale?: boolean
    }
    // Explicit partiality: a consumer can tell this apart from a final answer
    // programmatically, and it is NOT the unrelated `stale` claim.
    expect(partial.hydration?.complete).toBe(false)
    expect(partial.hydration!.totalFiles).toBeGreaterThan(partial.hydration!.indexedFiles)
    expect(partial.stale).toBeUndefined()

    // Poll the way every consumer does. The partial answer is never memoized,
    // so a later poll re-derives (or picks up the fill's converged payload).
    let converged: { hydration?: { complete: boolean; indexedFiles: number; totalFiles: number } } | null = null
    for (let id = 2; id < 40; id++) {
      const response = await request(id, PAYLOAD_ARGS)
      const payload = JSON.parse(response['output'] as string)
      // Convergence = the hydration block disappears (absence means complete).
      if (payload.hydration === undefined) { converged = payload; break }
      await new Promise(resolve => setTimeout(resolve, 250))
    }
    expect(converged).not.toBeNull()
    expect(converged!.hydration).toBeUndefined()
  }, 60_000)

  it('leaves the on-disk cache exactly where a full cold parse would', async () => {
    process.env['CODEBURN_CACHE_DIR'] = join(home, 'cache')
    clearSessionCache()
    clearLoadCacheMemo()
    const converged = await readCacheOnDisk()
    expect(converged.complete).toBe(true)

    // Same corpus, one plain cold parse, in a cache dir of its own.
    process.env['CLAUDE_CONFIG_DIR'] = join(home, '.claude')
    process.env['CODEBURN_CACHE_DIR'] = join(home, 'cache-baseline')
    process.env['CODEBURN_DESKTOP_SESSIONS_DIR'] = join(home, 'desktop-sessions')
    clearSessionCache()
    clearLoadCacheMemo()
    await parseAllSessions()
    const baseline = await readCacheOnDisk()

    expect(converged.providers['claude']?.files).toEqual(baseline.providers['claude']?.files)
    expect(converged.complete).toBe(baseline.complete)
  }, 60_000)
})
