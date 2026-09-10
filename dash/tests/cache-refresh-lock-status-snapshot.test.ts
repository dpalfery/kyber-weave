import { afterEach, describe, expect, it } from 'vitest'
import { spawn, type ChildProcess } from 'child_process'
import { createHash } from 'crypto'
import { existsSync } from 'fs'
import { mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'

import { saveStatusSnapshot } from '../src/session-cache.js'

const roots: string[] = []

async function waitFor(path: string, timeoutMs = 10_000): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (!existsSync(path)) {
    if (Date.now() >= deadline) throw new Error(`timed out waiting for ${path}`)
    await new Promise(resolve => { setTimeout(resolve, 5) })
  }
}

function waitForExit(child: ChildProcess): Promise<void> {
  if (child.exitCode !== null) {
    return child.exitCode === 0 ? Promise.resolve() : Promise.reject(new Error(`worker exited ${child.exitCode}`))
  }
  return new Promise((resolve, reject) => {
    let stderr = ''
    child.stderr?.on('data', chunk => { stderr += String(chunk) })
    child.once('error', reject)
    child.once('exit', code => code === 0 ? resolve() : reject(new Error(`worker exited ${code}: ${stderr}`)))
  })
}

function worker(cacheDir: string, barriers: string, role: 'fresh' | 'stale', rounds: number): ChildProcess {
  return spawn(process.execPath, [
    '--import',
    'tsx',
    join(process.cwd(), 'tests/fixtures/status-snapshot-writer.ts'),
    cacheDir,
    barriers,
    role,
    String(rounds),
  ], { cwd: process.cwd(), stdio: ['ignore', 'ignore', 'pipe'] })
}

function recordPath(cacheDir: string, queryKey: string): string {
  const hash = createHash('sha256').update(queryKey).digest('hex').slice(0, 16)
  return join(cacheDir, `status-snapshot.${hash}.json`)
}

afterEach(async () => {
  delete process.env['CODEBURN_CACHE_DIR']
  await Promise.all(roots.splice(0).map(root => rm(root, { recursive: true, force: true })))
})

describe('status snapshot child-process write lock', () => {
  it('never lets the older observation win a same-query publication race', async () => {
    const rounds = 20
    const root = await mkdtemp(join(tmpdir(), 'cb-status-snapshot-lock-'))
    roots.push(root)
    const cacheDir = join(root, 'cache')
    const barriers = join(root, 'barriers')
    await mkdir(cacheDir, { recursive: true })
    await mkdir(barriers, { recursive: true })
    process.env['CODEBURN_CACHE_DIR'] = cacheDir

    for (let round = 0; round < rounds; round++) {
      await saveStatusSnapshot(
        `baseline-${round}`,
        1_000,
        1_000,
        `child-query-${round}`,
        'child-process-render-v1',
        { role: 'baseline', round },
      )
    }

    const fresh = worker(cacheDir, barriers, 'fresh', rounds)
    const stale = worker(cacheDir, barriers, 'stale', rounds)
    for (let round = 0; round < rounds; round++) {
      await Promise.all([
        waitFor(join(barriers, `fresh.${round}.ready`)),
        waitFor(join(barriers, `stale.${round}.ready`)),
      ])
      await writeFile(join(barriers, `${round}.go`), '')
      await Promise.all([
        waitFor(join(barriers, `fresh.${round}.done`)),
        waitFor(join(barriers, `stale.${round}.done`)),
      ])
    }
    await Promise.all([waitForExit(fresh), waitForExit(stale)])

    for (let round = 0; round < rounds; round++) {
      const record = JSON.parse(await readFile(recordPath(cacheDir, `child-query-${round}`), 'utf-8')) as {
        corpusFingerprint: string
        payload: { role: string; round: number }
      }
      expect(record).toMatchObject({
        corpusFingerprint: `fresh-${round}`,
        payload: { role: 'fresh', round },
      })
    }
    expect((await readdir(cacheDir)).filter(name => name.endsWith('.tmp') || name.endsWith('.lock'))).toEqual([])
  })
})
