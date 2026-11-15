import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, expect, it } from 'vitest'

import { buildHarnessStatus } from '../status.js'
import { antigravityHarness } from './antigravity.js'

const homes: string[] = []
afterEach(() => {
  for (const home of homes.splice(0)) rmSync(home, { recursive: true, force: true })
})

it.skipIf(process.platform === 'win32')('structured status never echoes the configured command', () => {
  const home = mkdtempSync(join(tmpdir(), 'kyber-agy-safety-'))
  homes.push(home)
  const dir = join(home, '.gemini', 'antigravity-cli')
  mkdirSync(dir, { recursive: true })
  const secret = '/opt/secret-token-ABC123/bridge.py'
  writeFileSync(join(dir, 'settings.json'), JSON.stringify({ statusLine: { command: secret } }))

  const status = buildHarnessStatus(antigravityHarness, {
    home,
    env: { platform: process.platform },
    endpoint: 'http://127.0.0.1:4318',
  } as Parameters<typeof buildHarnessStatus>[1])
  expect(JSON.stringify(status.statusLines)).not.toContain('secret-token')
  expect(status.statusLines[0]).toBe('statusline bridge: configured')
  expect(status.statusLines[1]).toMatch(/^bridge route:/)
})
