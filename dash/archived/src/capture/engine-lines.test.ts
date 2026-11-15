import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterEach, describe, expect, it, vi } from 'vitest'

import type { EngineEnsureResult } from '../engine/ensure.js'
import type { EngineRecord } from '../engine/instance.js'
import { useAbsentLiveReceiver } from '../../tests/setup/live-receiver-stub.js'
import { runCapture } from './index.js'

useAbsentLiveReceiver()

const homes: string[] = []
afterEach(() => {
  for (const home of homes.splice(0)) rmSync(home, { recursive: true, force: true })
  vi.unstubAllEnvs()
})
function makeHome(): string {
  const home = mkdtempSync(join(tmpdir(), 'kyber-capture-engine-'))
  homes.push(home)
  vi.stubEnv('OPENCODE_CONFIG_DIR', '')
  return home
}
const NO_LISTENER = async (): Promise<{ status: number; body: string }> => {
  throw new Error('connect ECONNREFUSED (synthetic)')
}
const live = (version: string): EngineRecord => ({
  pid: 7, exe: '/x/kyberdash', version, startedAt: '2026-10-26T00:00:00.000Z', url: 'http://127.0.0.1:4747',
})

describe('capture status engine lines (D5)', () => {
  const status = async (probe: () => Promise<EngineRecord | null | 'unresponsive'>) =>
    runCapture('status', {
      homeDir: makeHome(),
      harnessIds: ['opencode'],
      fetchHealth: NO_LISTENER,
      engine: { cliVersion: '2.1.1', probe },
    })

  it('prints the running engine version against the CLI version and the remedy when they differ', async () => {
    const result = await status(async () => live('2.1.0'))
    expect(result.stdout).toContain('engine: running 2.1.0, cli 2.1.1 (differs)')
    expect(result.stdout).toContain('kyber-weave update')
  })

  it('prints no remedy when the versions match', async () => {
    const result = await status(async () => live('2.1.1'))
    expect(result.stdout).toContain('engine: running 2.1.1, cli 2.1.1')
    expect(result.stdout).not.toContain('differs')
    expect(result.stdout).not.toContain('kyber-weave update')
  })

  it('says not-running and unresponsive in fixed words', async () => {
    expect((await status(async () => null)).stdout).toContain('engine: not-running (cli 2.1.1)')
    expect((await status(async () => 'unresponsive')).stdout).toContain('engine: unresponsive (cli 2.1.1)')
  })

  it('prints nothing about the engine when no engine seam is given', async () => {
    const result = await runCapture('status', { homeDir: makeHome(), harnessIds: ['opencode'], fetchHealth: NO_LISTENER })
    expect(result.stdout).not.toContain('engine:')
  })
})

describe('capture enable --harness opencode ensures the engine once', () => {
  const attached: EngineEnsureResult = { outcome: 'attached', record: live('2.1.1') }
  const enable = (harnessIds: string[], ensure: () => Promise<EngineEnsureResult>, extra: { dryRun?: boolean } = {}) =>
    runCapture('enable', {
      homeDir: makeHome(),
      harnessIds,
      fetchHealth: NO_LISTENER,
      engine: { cliVersion: '2.1.1', probe: async () => null, ensure },
      ...extra,
    })

  it('calls ensureEngine exactly once and prints its outcome', async () => {
    const ensure = vi.fn(async () => attached)
    const result = await enable(['opencode'], ensure)
    expect(ensure).toHaveBeenCalledTimes(1)
    expect(result.stdout).toContain('engine: attached 2.1.1')
  })

  it('prints a replaced outcome', async () => {
    const replaced: EngineEnsureResult = { outcome: 'replaced', record: live('2.1.1'), previous: live('2.1.0') }
    const result = await enable(['opencode'], async () => replaced)
    expect(result.stdout).toContain('engine: replaced 2.1.0 -> 2.1.1')
  })

  it('a failed ensure warns on stderr but does not fail the enable', async () => {
    const result = await enable(['opencode'], async () => ({ outcome: 'failed', reason: 'boom' }))
    expect(result.stderr).toContain('engine: failed boom')
    expect(result.exitCode).toBe(0)
  })

  it('does not ensure for another harness or on a dry run', async () => {
    const ensure = vi.fn(async () => attached)
    await enable(['claude-code'], ensure)
    await enable(['opencode'], ensure, { dryRun: true })
    expect(ensure).not.toHaveBeenCalled()
  })
})
