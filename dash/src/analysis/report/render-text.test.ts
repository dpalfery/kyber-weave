// Snapshot and display-rule coverage for the text renderer (R11.8, R14.1–R14.4).
//
// The fixtures are the contract: a renderer that invented a 0, paraphrased a
// recommendation, or led with cost would look plausible in a snapshot of one
// happy path and still be wrong. Every fixture is snapshotted, and the rules
// are asserted independently of the snapshots so a regenerated snapshot cannot
// silently bless a regression.

import { readdirSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { describe, expect, it } from 'vitest'

import { NOT_MEASURABLE, formatMeasured, isUnmeasurable, type ContextReport, type Measured } from './types.js'
import { renderText, shouldColorText } from './render-text.js'

const FIXTURE_DIR = join(dirname(fileURLToPath(import.meta.url)), 'fixtures')
const ANSI = /\u001b\[[0-9;]*m/

const FIXTURE_NAMES = [
  'full',
  'empty',
  'stale',
  'unmeasurable-pressure',
  'no-findings',
  'mixed-basis-cost',
] as const

function loadFixture(name: string): ContextReport {
  return JSON.parse(readFileSync(join(FIXTURE_DIR, `${name}.json`), 'utf8')) as ContextReport
}

function collectUnmeasurable(value: unknown, out: Array<Measured<unknown>> = []): Array<Measured<unknown>> {
  if (Array.isArray(value)) {
    for (const item of value) collectUnmeasurable(item, out)
    return out
  }
  if (value !== null && typeof value === 'object') {
    if ('value' in value && ((value as Measured<unknown>).value === null || 'reason' in value)) {
      const measured = value as Measured<unknown>
      if (isUnmeasurable(measured)) out.push(measured)
      return out
    }
    for (const item of Object.values(value)) collectUnmeasurable(item, out)
  }
  return out
}

describe('renderText snapshots', () => {
  it('covers every fixture file', () => {
    const files = readdirSync(FIXTURE_DIR).filter((name) => name.endsWith('.json')).sort()
    expect(files).toEqual([...FIXTURE_NAMES].map((name) => `${name}.json`).sort())
  })

  it.each([...FIXTURE_NAMES])('renders %s without colour', (name) => {
    expect(renderText(loadFixture(name), { color: false })).toMatchSnapshot()
  })
})

describe('renderText display rules', () => {
  it('renders a null Measured as the dash and reason, never as 0 (R14.1)', () => {
    const report = loadFixture('unmeasurable-pressure')
    const out = renderText(report, { color: false })
    for (const figure of collectUnmeasurable(report)) {
      if (!isUnmeasurable(figure)) continue
      const rendered = formatMeasured(figure)
      expect(out).toContain(rendered)
      expect(rendered.startsWith(NOT_MEASURABLE)).toBe(true)
      expect(rendered).not.toBe('0')
    }
    expect(out).not.toMatch(/Pressure:\s*0\b/)
  })

  it('places the Cost section after every token figure (R14.2)', () => {
    for (const name of FIXTURE_NAMES) {
      const out = renderText(loadFixture(name), { color: false })
      const costAt = out.indexOf('\nCost\n')
      expect(costAt, name).toBeGreaterThan(-1)
      const coverageAt = out.indexOf('\nData coverage\n')
      const findingsAt = out.indexOf('\nFindings\n')
      const harnessAt = out.indexOf('\nHarness dimensions\n')
      const latestAt = out.indexOf('\nLatest session\n')
      for (const [label, at] of [
        ['coverage', coverageAt],
        ['findings', findingsAt],
        ['harnesses', harnessAt],
        ['latest', latestAt],
      ] as const) {
        if (at >= 0) expect(costAt, `${name} ${label}`).toBeGreaterThan(at)
      }
    }
  })

  it('does not compute or show a composite score, index or grade (R14.3)', () => {
    const out = renderText(loadFixture('full'), { color: false })
    expect(out).not.toMatch(/\b(composite|overall score|health index|health grade)\b/i)
    expect(out).toContain('Context Hygiene')
    expect(out).toContain('Cache Efficiency')
    expect(out).toContain('Tool Yield')
    expect(out).toContain('Skill Utilisation')
    expect(out).toContain('Delegation Overhead')
    expect(out).toContain('Continuity')
  })

  it('prints the engine recommendation verbatim (R14.4)', () => {
    const report = loadFixture('full')
    const out = renderText(report, { color: false })
    for (const finding of report.findings ?? []) {
      expect(out).toContain(finding.recommendation)
    }
  })

  it('is coloured only when colour is requested (R11.8)', () => {
    const report = loadFixture('full')
    const coloured = renderText(report, { color: true })
    const plain = renderText(report, { color: false })
    expect(coloured).toMatch(ANSI)
    expect(plain).not.toMatch(ANSI)
  })

  it('defaults to colour only for a TTY with NO_COLOR unset (R11.8)', () => {
    expect(shouldColorText({ isTTY: true, noColor: undefined })).toBe(true)
    expect(shouldColorText({ isTTY: true, noColor: '1' })).toBe(false)
    expect(shouldColorText({ isTTY: false, noColor: undefined })).toBe(false)
  })
})
