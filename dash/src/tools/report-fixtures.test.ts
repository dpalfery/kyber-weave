// The committed fixtures must match what the generator produces (task 5.4).
//
// Stream C's renderers and the tray are written against these files. If regenerating
// produced a different document than the one committed, a renderer test would be diffing
// against a file nobody could reproduce — and the first person to run
// `npm run report:fixtures` would get an unexplained diff. This fails instead, naming the
// fixture, so the answer is always "re-run the script and commit the result".

import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import { isUnmeasurable, type ContextReport } from '../analysis/report/types.js'
import { generateFixtures } from './report-fixtures.js'

const FIXTURE_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'analysis', 'report', 'fixtures')
const committed = (name: string): ContextReport =>
  JSON.parse(readFileSync(join(FIXTURE_DIR, `${name}.json`), 'utf8')) as ContextReport

const generated = generateFixtures()

describe('committed report fixtures', () => {
  it('covers the six cases a renderer has to handle', () => {
    expect(Object.keys(generated).sort()).toEqual([
      'empty', 'full', 'mixed-basis-cost', 'no-findings', 'stale', 'unmeasurable-pressure',
    ])
  })

  for (const name of Object.keys(generated)) {
    it(`${name}.json matches the generator, so regenerating is a no-op`, () => {
      expect(committed(name)).toEqual(generated[name])
    })
  }

  it('is deterministic: two generations agree', () => {
    expect(generateFixtures()).toEqual(generateFixtures())
  })
})

describe('what the fixtures are for', () => {
  it('full carries every section, including detection', () => {
    const report = committed('full')
    for (const key of ['coverage', 'detection', 'findings', 'harnesses', 'latestSession', 'cost']) {
      expect(report, key).toHaveProperty(key)
    }
  })

  it('empty still tells the reader what to run (R11.4)', () => {
    expect(committed('empty').coverage!.hints.join(' ')).toContain('kyberdash dash refresh')
  })

  it('stale keeps the last success visible beside the later failure (R10.5)', () => {
    const refresh = committed('stale').coverage!.refresh
    expect(refresh.lastSuccessAt).not.toBeNull()
    expect(refresh.lastFailure).not.toBeNull()
  })

  it('unmeasurable-pressure has no zero standing in for an absent figure (R8.4, R14.1)', () => {
    const turn = committed('unmeasurable-pressure').latestSession!.latestTurn
    for (const figure of [turn.pressure, turn.contextWindow, turn.residual, ...Object.values(turn.buckets)]) {
      expect(isUnmeasurable(figure)).toBe(true)
      expect(figure.value).not.toBe(0)
    }
  })

  it('no-findings carries an empty list rather than omitting the section', () => {
    expect(committed('no-findings').findings).toEqual([])
  })

  it('mixed-basis-cost keeps its bases apart and never shows their sum (R14.2)', () => {
    const rows = committed('mixed-basis-cost').cost!
    expect(rows.length).toBeGreaterThanOrEqual(2)
    const priced = rows.map((row) => row.amountUsd.value).filter((v): v is number => typeof v === 'number')
    expect(priced.length).toBeGreaterThanOrEqual(2)
    const blended = priced.reduce((a, b) => a + b, 0)
    expect(rows.some((row) => row.amountUsd.value === blended)).toBe(false)
  })

  it('puts cost last in every fixture, so no surface can lead with it', () => {
    for (const name of Object.keys(generated)) {
      const keys = Object.keys(committed(name))
      if (keys.includes('cost')) expect(keys.at(-1), name).toBe('cost')
    }
  })
})
