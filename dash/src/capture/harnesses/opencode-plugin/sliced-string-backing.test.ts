// F2 SUPERVISOR-4 regression: capped diagnostics must not retain parent backing.
//
// `slice` on a long string keeps the parent allocation alive (V8
// SlicedString), so a capped tail can retain megabytes behind a short
// length. These tests prove the detachment: capped failure messages and
// capped entry content release their parents under GC. The GC measurements
// run only with `--expose-gc`; the exactness checks always run.

import { randomBytes } from 'node:crypto'
import { describe, expect, it } from 'vitest'

import { createInstalledOpencodePlugin } from './plugin.js'
import { createOpencodePlugin, detachedCopy } from './plugin.js'
import { createSetup } from './entry.js'

const hasGc = typeof (globalThis as { gc?: unknown }).gc === 'function'

async function settleGc(): Promise<void> {
  const gc = (globalThis as { gc?: () => void }).gc
  if (gc === undefined) return
  await new Promise<void>((resolve) => setImmediate(resolve))
  gc()
  gc()
}

const externalBytes = (): number => process.memoryUsage().external

describe('detached capped diagnostics (F2 sliced-string backing)', () => {
  it('detachedCopyPreservesCodeUnitsExactly', () => {
    expect(detachedCopy('plain')).toBe('plain')
    expect(detachedCopy('é🎉漢')).toBe('é🎉漢')
    // Lone surrogates survive: a UTF-8 round-trip would replace them, so
    // this pins the JSON round-trip choice, not just any copy.
    expect(detachedCopy('a�b��c')).toBe('a�b��c')
    expect(detachedCopy('')).toBe('')
  })

  it.skipIf(!hasGc)('cappedFailureMessagesReleaseTheirParents', async () => {
    let current = ''
    const plugin = createOpencodePlugin({
      now: () => '2026-10-30T00:00:00.000Z',
      emit: async () => {
        throw new Error(current)
      },
    })
    await settleGc()
    const baseline = externalBytes()
    for (let i = 0; i < 10; i += 1) {
      // 1M-char messages: a bare slice would retain the ~1MB parent backing
      // each (SUPERVISOR-4 repro), so ten failures would hold ~10MB here.
      current = randomBytes(512 * 1024).toString('hex')
      await plugin.hooks['session.start']({ sessionId: `session-slice-${i}` })
      current = ''
    }
    await settleGc()
    // Ten normalized 2K-char records retain kilobytes, not the ~10MB of parents.
    expect(plugin.failures).toHaveLength(10)
    expect(externalBytes() - baseline).toBeLessThan(512 * 1024)

    plugin.failures.length = 0
    await settleGc()
    expect(externalBytes() - baseline).toBeLessThan(512 * 1024)
  })

  it.skipIf(!hasGc)('cappedEntryContentReleasesItsParent', async () => {
    const installed = createInstalledOpencodePlugin({
      fetchFn: async () => new Response('{}', { status: 200 }),
      sleepFn: async () => {},
      now: () => '2026-10-30T00:00:00.000Z',
    })
    let callback!: (input: unknown) => Promise<void>
    const ctx = {
      session: {
        hook: async (_name: string, cb: (input: never) => Promise<void>) => {
          callback = cb as (input: unknown) => Promise<void>
          return { dispose: async () => undefined }
        },
      },
    }
    await createSetup({
      installed,
      env: { KYBERDASH_OPENCODE_CONTEXT: '1' },
      consentPath: '/dev/null',
      lookupTimeoutMs: 1,
    })(ctx as never)
    const tiny = {
      sessionID: 'session-slice-warmup',
      model: { id: 'm', providerID: 'p' },
      agent: 'build',
      system: [{ text: 'warmup' }],
      tools: {},
    }
    // Warm up one-time native Response/stream machinery (a first-use ~2.5MB
    // external init, not a leak) so the baseline below excludes it.
    await callback(tiny)
    await installed.outbox.drain()
    installed.mappedSpans.length = 0
    installed.outcomeLogs.length = 0
    installed.plugin.events.length = 0

    await settleGc()
    const baseline = externalBytes()
    // A 1MiB prompt caps to 256KiB: the tail must hold the prefix, not the parent.
    let big: string | undefined = randomBytes(1_048_576).toString('base64')
    await callback({
      sessionID: 'session-slice-entry',
      model: { id: 'm', providerID: 'p' },
      agent: 'build',
      system: [{ text: big }],
      tools: {},
    })
    // Drop the test's own handle on the parent: only the retained prefix may survive.
    big = undefined
    await installed.outbox.drain()
    await installed.outbox.close()
    installed.mappedSpans.length = 0
    installed.outcomeLogs.length = 0
    await settleGc()

    expect(installed.plugin.events).toHaveLength(1)
    expect(installed.plugin.events[0]?.systemPrompt?.length ?? 0).toBeGreaterThan(262_000)
    expect(externalBytes() - baseline).toBeLessThan(600 * 1024)
  })
})
