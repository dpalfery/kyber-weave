// Storm guard (T3): the engine context an engine verb starts an engine from fails closed. When the
// entry this process runs is not the kyberdash CLI, the resolver's reason is the error, and no
// engine context (and so no engine start) is produced.
//
// Seam: the resolver reads process.argv[1] live, so each test sets the entry for its own duration
// and restores it. No child process is started and nothing on disk is read or written.
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { engineContext } from './engine-context.js'

let priorEntry: string | undefined

beforeEach(() => {
  priorEntry = process.argv[1]
})

afterEach(() => {
  process.argv[1] = priorEntry ?? ''
})

describe('engineContext: an engine is never started from an entry that is not the CLI', () => {
  it('refuses a harness boot script and names the refused path in the error', async () => {
    process.argv[1] = '/repo/dash/e2e/harness-collection-boot.ts'

    await expect(engineContext()).rejects.toThrow(
      'spawn target refused: /repo/dash/e2e/harness-collection-boot.ts is not the kyberdash CLI entry',
    )
  })

  it('still resolves a context when the entry is the checkout CLI launcher', async () => {
    // The positive control: the same seam, given the real entry shape, produces a context. Without
    // it, the refusal above could be a broken resolver rather than a correct one.
    process.argv[1] = '/repo/dash/src/launcher.ts'

    const context = await engineContext()

    expect(context.programArgs.at(-1)).toBe('/repo/dash/src/launcher.ts')
    expect(context.program).toBe(process.execPath)
  })
})
