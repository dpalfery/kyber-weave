// The codex result cache is a single (often hundreds-of-MB) JSON file, memoized
// in memory only once the read + parse resolves. Discovery now asks for it from
// many concurrent callers, so without a shared in-flight promise every one of
// them re-read and re-parsed the whole file.

import { mkdtemp, rm, writeFile, mkdir } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'

const readSpy = vi.hoisted(() => vi.fn())

vi.mock('../src/cache-dir.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../src/cache-dir.js')>()
  return {
    ...actual,
    readExistingTextFile: (path: string) => {
      readSpy(path)
      return actual.readExistingTextFile(path)
    },
  }
})

const { CODEX_CACHE_VERSION, clearCodexMemCaches, codexCacheFileName, getCachedCodexProject, withCodexCacheDirectory } =
  await import('../src/codex-cache.js')

let cacheDir: string
let sessionDir: string

beforeEach(async () => {
  readSpy.mockClear()
  clearCodexMemCaches()
  const root = await mkdtemp(join(tmpdir(), 'codeburn-codex-cache-'))
  cacheDir = join(root, 'cache')
  sessionDir = join(root, 'sessions')
  await mkdir(cacheDir, { recursive: true })
  await mkdir(sessionDir, { recursive: true })
})

afterEach(async () => {
  clearCodexMemCaches()
  await rm(join(cacheDir, '..'), { recursive: true, force: true })
})

describe('codex result cache under concurrent readers', () => {
  it('reads the cache file once and answers every caller correctly', async () => {
    const paths: string[] = []
    const files: Record<string, unknown> = {}
    for (let i = 0; i < 24; i++) {
      const p = join(sessionDir, `rollout-${i}.jsonl`)
      await writeFile(p, '{}\n')
      paths.push(p)
      const { statSync } = await import('fs')
      const s = statSync(p)
      files[p] = { dev: s.dev, ino: s.ino, mtimeMs: s.mtimeMs, sizeBytes: s.size, project: `proj-${i}`, calls: [] }
    }
    await writeFile(join(cacheDir, codexCacheFileName()), JSON.stringify({ version: CODEX_CACHE_VERSION, files }))

    const projects = await withCodexCacheDirectory(cacheDir, () =>
      Promise.all(paths.map(p => getCachedCodexProject(p))))

    expect(projects).toEqual(paths.map((_, i) => `proj-${i}`))
    expect(readSpy.mock.calls.filter(([p]) => String(p).includes('codex-results'))).toHaveLength(1)
  })
})
