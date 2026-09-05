// D6 contract: the surveyed OpenCode installation has no enabled telemetry.
// A missing source must surface that fact, never become a zero-token session.
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, describe, expect, it, expectTypeOf } from 'vitest'

import {
  OPENCODE_NOT_COLLECTABLE_REASON,
  opencodeReader,
} from './opencode.js'
import type { ContentReader } from './types.js'

const tempRoots: string[] = []

afterAll(() => {
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

describe('opencodeReader', () => {
  it('is a public ContentReader while OpenCode telemetry remains disabled', () => {
    expectTypeOf(opencodeReader).toMatchTypeOf<ContentReader>()
    expect(OPENCODE_NOT_COLLECTABLE_REASON).toMatch(/OpenCode/i)
    expect(OPENCODE_NOT_COLLECTABLE_REASON).toMatch(/OpenTelemetry|telemetry/i)
    expect(OPENCODE_NOT_COLLECTABLE_REASON).toMatch(/disabled|not enabled/i)
  })

  it('does not fabricate a zero-valued turn from an empty synthetic source', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-opencode-reader-'))
    tempRoots.push(root)
    const turns = []

    for await (const turn of opencodeReader.read(join(root, 'no-session-evidence.json'))) {
      turns.push(turn)
    }

    expect(turns).toEqual([])
  })
})
