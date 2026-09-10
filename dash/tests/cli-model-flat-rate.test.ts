import { mkdtemp, readFile, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { spawnSync } from 'node:child_process'

import { describe, it, expect } from 'vitest'

const CLI_TIMEOUT_MS = 30_000

function runCli(args: string[], home: string) {
  return spawnSync(process.execPath, ['--import', 'tsx', 'src/cli.ts', ...args], {
    cwd: process.cwd(),
    env: {
      ...process.env,
      HOME: home,
      USERPROFILE: home,
      HOMEPATH: home,
      HOMEDRIVE: '',
    },
    encoding: 'utf-8',
  })
}

function readConfig(home: string): Promise<Record<string, unknown>> {
  return readFile(join(home, '.config', 'codeburn', 'config.json'), 'utf-8')
    .then(raw => JSON.parse(raw) as Record<string, unknown>)
}

describe('codeburn model-flat-rate command', () => {
  it('saves, lists, and removes a flat-rate mark', async () => {
    const home = await mkdtemp(join(tmpdir(), 'codeburn-cli-flat-rate-'))
    try {
      const set = runCli(['model-flat-rate', 'zz-my-pass-sku'], home)
      expect(set.status).toBe(0)
      expect(set.stdout).toContain('Flat-rate mark saved: zz-my-pass-sku')

      const saved = await readConfig(home)
      expect(saved.flatRateModels).toEqual(['zz-my-pass-sku'])

      const list = runCli(['model-flat-rate', '--list'], home)
      expect(list.status).toBe(0)
      expect(list.stdout).toContain('zz-my-pass-sku')

      const remove = runCli(['model-flat-rate', '--remove', 'zz-my-pass-sku'], home)
      expect(remove.status).toBe(0)

      const after = await readConfig(home)
      expect(after.flatRateModels).toBeUndefined()
      expect(after.flatRateModelsRemoved).toBeUndefined()
    } finally {
      await rm(home, { recursive: true, force: true })
    }
  }, CLI_TIMEOUT_MS)

  it('warns when the same model is also configured in modelAliases', async () => {
    const home = await mkdtemp(join(tmpdir(), 'codeburn-cli-flat-rate-'))
    try {
      expect(runCli(['model-alias', 'auto-genius', 'gpt-4o'], home).status).toBe(0)
      const set = runCli(['model-flat-rate', 'auto-genius'], home)
      expect(set.status).toBe(0)
      expect(set.stdout).toContain('also in modelAliases')
      expect(set.stdout).toContain('invents per-token spend')
    } finally {
      await rm(home, { recursive: true, force: true })
    }
  }, CLI_TIMEOUT_MS)

  it('rejects a remove for an unknown mark', async () => {
    const home = await mkdtemp(join(tmpdir(), 'codeburn-cli-flat-rate-'))
    try {
      const result = runCli(['model-flat-rate', '--remove', 'unknown-sku'], home)
      expect(result.status).toBe(1)
      expect(result.stderr).toContain('No flat-rate mark found')
    } finally {
      await rm(home, { recursive: true, force: true })
    }
  }, CLI_TIMEOUT_MS)

  it('opts out of a built-in SKU so the unpriced warning can fire again', async () => {
    const home = await mkdtemp(join(tmpdir(), 'codeburn-cli-flat-rate-'))
    try {
      const remove = runCli(['model-flat-rate', '--remove', 'auto-genius'], home)
      expect(remove.status).toBe(0)
      expect(remove.stdout).toContain('Removed flat-rate mark: auto-genius')
      expect(remove.stdout).toContain('Built-in SKU opted out')

      const saved = await readConfig(home)
      expect(saved.flatRateModels).toBeUndefined()
      expect(saved.flatRateModelsRemoved).toEqual(['auto-genius'])

      const list = runCli(['model-flat-rate', '--list'], home)
      expect(list.status).toBe(0)
      expect(list.stdout).toContain('auto-genius')
      expect(list.stdout).toContain('Built-in flat-rate opt-outs')

      const restore = runCli(['model-flat-rate', 'auto-genius'], home)
      expect(restore.status).toBe(0)
      const after = await readConfig(home)
      expect(after.flatRateModels).toEqual(['auto-genius'])
      expect(after.flatRateModelsRemoved).toBeUndefined()
    } finally {
      await rm(home, { recursive: true, force: true })
    }
  }, CLI_TIMEOUT_MS)
})
