import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it, vi } from 'vitest'

const copyFileSync = vi.hoisted(() =>
  vi.fn((source: string, destination: string) => {
    void source
    void destination
  }),
)

vi.mock('node:fs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:fs')>()
  return {
    ...actual,
    copyFileSync: (source: string, destination: string) => copyFileSync(source, destination),
  }
})

import { copyFileBestEffort } from './sqlite.js'

describe('copyFileBestEffort (issue #194 Warp EPERM)', () => {
  let root = ''

  afterEach(() => {
    copyFileSync.mockReset()
    if (root !== '') rmSync(root, { recursive: true, force: true })
  })

  it('falls back to read/write when copyfile returns EPERM', () => {
    root = mkdtempSync(join(tmpdir(), 'kyberdash-eperm-copy-'))
    const src = join(root, 'warp.sqlite')
    const dest = join(root, 'warp-copy.sqlite')
    writeFileSync(src, 'warp-bytes')
    copyFileSync.mockImplementation(() => {
      const err = new Error('EPERM: copyfile') as NodeJS.ErrnoException
      err.code = 'EPERM'
      throw err
    })

    copyFileBestEffort(src, dest)

    expect(readFileSync(dest, 'utf8')).toBe('warp-bytes')
    expect(copyFileSync).toHaveBeenCalledWith(src, dest)
  })
})
