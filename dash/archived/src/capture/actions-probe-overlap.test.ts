// Regression: the receiver liveness probe must not sit inside the capture mutex.
//
// The probe is a read-only network call whose result only feeds the CLI text that
// `runCaptureAction` discards. Running it under the lock made N overlapping requests pay N
// probe round trips back to back, which pushed the second response of two concurrent
// dashboard POSTs past the route test's 50ms settle window on a loaded Linux runner
// (an empty body, then `Cannot read properties of undefined (reading 'outcome')`).

import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'

import { afterEach, describe, expect, it } from 'vitest'

import { runCaptureAction } from './actions.js'
import { claudeCodeHarness } from './harnesses/claude-code.js'
import type { PathEnv } from './paths.js'
import { receiptPathForHome } from './receipt.js'

const homes: string[] = []

afterEach(() => {
  for (const home of homes.splice(0)) rmSync(home, { recursive: true, force: true })
})

describe('runCaptureAction: probe outside the mutex', () => {
  it('lets overlapping requests probe concurrently while the writes stay serialized', async () => {
    const home = mkdtempSync(join(tmpdir(), 'kyber-capture-probe-overlap-'))
    homes.push(home)
    const env: PathEnv = { platform: process.platform, appData: join(home, 'AppData', 'Roaming') }
    const configPath = claudeCodeHarness.resolvePath(home, env)
    mkdirSync(dirname(configPath), { recursive: true })
    writeFileSync(configPath, '{}\n')

    let inFlight = 0
    let peak = 0
    const slow = async (): Promise<{ status: number; body: string }> => {
      inFlight += 1
      peak = Math.max(peak, inFlight)
      await new Promise((resolve) => setTimeout(resolve, 20))
      inFlight -= 1
      return { status: 200, body: JSON.stringify({ service: 'kyberdash-otlp' }) }
    }
    const request = {
      action: 'enable' as const,
      harnessId: 'claude-code',
      dryRun: false,
      homeDir: home,
      platform: process.platform,
      appDataDir: env.appData,
      fetchHealth: slow,
    }

    const results = await Promise.all([runCaptureAction(request), runCaptureAction(request)])

    expect(peak).toBe(2)
    expect(results.map((result) => result.outcome).sort()).toEqual(['noop', 'wrote'])
    const receipt = JSON.parse(readFileSync(receiptPathForHome(home), 'utf8')) as { files: unknown[] }
    expect(receipt.files).toHaveLength(1)
  })

  it('does not probe a request that is a usage failure', async () => {
    const home = mkdtempSync(join(tmpdir(), 'kyber-capture-probe-overlap-'))
    homes.push(home)
    let probes = 0
    const result = await runCaptureAction({
      action: 'enable',
      harnessId: 'claude-code',
      dryRun: false,
      homeDir: home,
      endpoint: 'ftp://not-otlp.example',
      fetchHealth: async () => {
        probes += 1
        return { status: 200, body: '{}' }
      },
    })
    expect(result.outcome).toBe('refused')
    expect(probes).toBe(0)
  })
})
