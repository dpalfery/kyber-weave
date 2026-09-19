import { mkdtemp, rm } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { spawnSync } from 'node:child_process'

import { afterEach, describe, expect, it } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { allProviderNames, getAllProviders } from '../providers/index.js'

const homes: string[] = []

afterEach(async () => {
  while (homes.length > 0) {
    const h = homes.pop()
    if (h) await rm(h, { recursive: true, force: true })
  }
})

async function makeHome(): Promise<string> {
  const home = await mkdtemp(join(tmpdir(), 'codeburn-provider-cli-'))
  homes.push(home)
  return home
}

function runCli(args: string[], home: string) {
  return spawnSync(process.execPath, ['--import', 'tsx', 'src/launcher.ts', ...args], {
    cwd: process.cwd(),
    env: { ...process.env, HOME: home, CLAUDE_CONFIG_DIR: join(home, '.claude'), TZ: 'UTC' },
    encoding: 'utf-8',
    timeout: 30_000,
  })
}

describe('allProviderNames', () => {
  it('is non-empty, sorted, and excludes the "all" sentinel', () => {
    const names = allProviderNames()
    expect(names.length).toBeGreaterThan(0)
    expect([...names]).toEqual([...names].sort())
    expect(names).not.toContain('all')
  })

  it('covers every loadable provider (guards against lazy-list drift)', async () => {
    const loaded = (await getAllProviders()).map(p => p.name)
    const names = allProviderNames()
    for (const name of loaded) {
      expect(names).toContain(name)
    }
  })
})

describe('kyberdash report --harness validation', () => {
  it('rejects an unknown harness with a nearest-match hint and exit 2, before the store opens', async () => {
    const home = await makeHome()
    const db = join(home, 'must-not-be-created', 'canon.db')
    const res = runCli(['report', '--harness', 'claud', '--db', db], home)
    expect(res.status).toBe(2)
    expect(res.stderr).toContain('unknown harness "claud"')
    expect(res.stderr).toMatch(/Did you mean "claude-/)
    expect(existsSync(join(home, 'must-not-be-created'))).toBe(false)
  })

  it('accepts a valid harness', async () => {
    const home = await makeHome()
    const db = join(home, 'canon.db')
    const store = new CanonStore(db)
    store.close()
    const res = runCli(['report', '--harness', 'claude-cli', '--format', 'json', '--db', db], home)
    expect(res.status, res.stderr).toBe(0)
    expect(res.stderr).not.toContain('unknown harness')
  })
})
