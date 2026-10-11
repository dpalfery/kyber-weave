import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, expect, it, vi } from 'vitest'

// A file that grows after the descriptor check: fstat reports it small, the read finds it large.
vi.mock('node:fs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:fs')>()
  return {
    ...actual,
    fstatSync: (fd: number) => {
      const info = actual.fstatSync(fd)
      return info.isFile() ? Object.assign(Object.create(info), { size: 10, isFile: () => true }) : info
    },
  }
})

import { antigravityHarness } from './antigravity.js'

const homes: string[] = []
afterEach(() => {
  for (const home of homes.splice(0)) rmSync(home, { recursive: true, force: true })
})

it.skipIf(process.platform === 'win32')('ignores a bridge script that outgrew the cap after the size check', () => {
  const home = mkdtempSync(join(tmpdir(), 'kyber-agy-cap-'))
  homes.push(home)
  const dir = join(home, '.gemini', 'antigravity-cli')
  mkdirSync(dir, { recursive: true })
  const script = join(dir, 'statusline.py')
  writeFileSync(script, `${'#'.repeat(1024 * 1024 + 100)}\nPORT = 4318\n`)
  writeFileSync(join(dir, 'settings.json'), JSON.stringify({ statusLine: { command: script } }))

  const lines = antigravityHarness.describeStatus!(home, { redact: true })
  expect(lines.some((line) => line.includes('does not name the receiver port'))).toBe(true)
})
