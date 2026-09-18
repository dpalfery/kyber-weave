// D6 contract: Kilo is installed but its surveyed globalStorage is empty and
// its OTel wire format is undocumented. That is a concrete inability to
// collect, not evidence for a zero-valued session.
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import { afterAll, describe, expect, it, expectTypeOf } from 'vitest'

import {
  KILO_NOT_COLLECTABLE_REASON,
  kiloReader,
} from './kilo.js'
import type { ContentReader } from './types.js'

const tempRoots: string[] = []

afterAll(() => {
  for (const root of tempRoots) rmSync(root, { recursive: true, force: true })
})

describe('kiloReader', () => {
  it('exports a concrete reason instead of presenting the empty store as measurement', () => {
    expectTypeOf(kiloReader).toMatchTypeOf<ContentReader>()
    expect(KILO_NOT_COLLECTABLE_REASON).toMatch(/Kilo/i)
    expect(KILO_NOT_COLLECTABLE_REASON).toMatch(/empty|no .*session|no .*data/i)
    expect(KILO_NOT_COLLECTABLE_REASON).toMatch(/OTel|OpenTelemetry|undocumented/i)
  })

  it('does not fabricate a zero-valued turn from an empty synthetic store', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-kilo-reader-'))
    tempRoots.push(root)
    const turns = []

    for await (const turn of kiloReader.read(join(root, 'globalStorage'))) turns.push(turn)

    expect(turns).toEqual([])
  })
})
