// Snapshot and display-rule coverage for the Markdown renderer (R11.9, R14.1–R14.4).
//
// Markdown is what a person pastes into a pull request or an agent reads as a
// file, so it has to stay GitHub-flavoured and free of terminal chrome. The
// same fixtures drive the text renderer: a figure that is unmeasurable in
// one surface cannot become a 0 in the other.

import { readdirSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

import { describe, expect, it } from 'vitest'

import { NOT_MEASURABLE, formatMeasured, isUnmeasurable, type ContextReport, type Measured } from './types.js'
import { renderMarkdown } from './render-markdown.js'

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

describe('renderMarkdown snapshots', () => {
  it('covers every fixture file', () => {
    const files = readdirSync(FIXTURE_DIR).filter((name) => name.endsWith('.json')).sort()
    expect(files).toEqual([...FIXTURE_NAMES].map((name) => `${name}.json`).sort())
  })

  it.each([...FIXTURE_NAMES])('renders %s', (name) => {
    expect(renderMarkdown(loadFixture(name))).toMatchSnapshot()
  })
})

describe('renderMarkdown display rules', () => {
  it('renders a null Measured as the dash and reason, never as 0 (R14.1)', () => {
    const report = loadFixture('unmeasurable-pressure')
    const out = renderMarkdown(report)
    for (const figure of collectUnmeasurable(report)) {
      if (!isUnmeasurable(figure)) continue
      const rendered = formatMeasured(figure)
      expect(out).toContain(rendered)
      expect(rendered.startsWith(NOT_MEASURABLE)).toBe(true)
      expect(rendered).not.toBe('0')
    }
    expect(out).not.toMatch(/\*\*Pressure:\*\*\s*0\b/)
  })

  it('places the Cost section after every token figure (R14.2)', () => {
    for (const name of FIXTURE_NAMES) {
      const out = renderMarkdown(loadFixture(name))
      const costAt = out.indexOf('\n## Cost\n')
      expect(costAt, name).toBeGreaterThan(-1)
      for (const [label, heading] of [
        ['coverage', '\n## Data coverage\n'],
        ['findings', '\n## Findings\n'],
        ['harnesses', '\n## Harness dimensions\n'],
        ['latest', '\n## Latest session\n'],
      ] as const) {
        const at = out.indexOf(heading)
        if (at >= 0) expect(costAt, `${name} ${label}`).toBeGreaterThan(at)
      }
    }
  })

  it('does not compute or show a composite score, index or grade (R14.3)', () => {
    const out = renderMarkdown(loadFixture('full'))
    expect(out).not.toMatch(/\b(composite|overall score|health index|health grade)\b/i)
    expect(out).toContain('Context Hygiene')
    expect(out).toContain('Continuity')
  })

  it('prints the engine recommendation verbatim (R14.4)', () => {
    const report = loadFixture('full')
    const out = renderMarkdown(report)
    for (const finding of report.findings ?? []) {
      expect(out).toContain(finding.recommendation)
    }
  })

  it('contains no terminal escape sequences (R11.9)', () => {
    for (const name of FIXTURE_NAMES) {
      expect(renderMarkdown(loadFixture(name)), name).not.toMatch(ANSI)
    }
  })

  it('prints the tracked coverage window as last N weeks with its bounds (T3)', () => {
    const report = loadFixture('full')
    report.coverage!.refresh = {
      ...report.coverage!.refresh,
      historyWeeks: 2,
      coveredFrom: '2026-09-05T11:00:00.000Z',
      coveredThrough: '2026-09-19T11:00:00.000Z',
    }
    const out = renderMarkdown(report)
    expect(out).toContain(
      '**Coverage window:** last 2 weeks (2026-09-05T11:00:00.000Z → 2026-09-19T11:00:00.000Z)',
    )
  })

  it('renders an untracked window as unknown with its reason, never 2 or 0 (T3)', () => {
    const report = loadFixture('full')
    report.coverage!.refresh = {
      ...report.coverage!.refresh,
      historyWeeks: null,
      coveredFrom: null,
      coveredThrough: null,
    }
    const out = renderMarkdown(report)
    expect(out).toContain('**Coverage window:** unknown (recorded before window tracking)')
    expect(out).not.toContain('last 2 weeks')
    expect(out).not.toContain('last 0 weeks')
  })

  it('distinguishes a fresh store (no successful refresh) from a legacy tracked run (T3)', () => {
    // Review PR #230 (copilot numqo): lastSuccessAt null means no refresh
    // was ever recorded — that must not read as a legacy pre-window run.
    const report = loadFixture('full')
    report.coverage!.refresh = {
      ...report.coverage!.refresh,
      lastSuccessAt: null,
      historyWeeks: null,
      coveredFrom: null,
      coveredThrough: null,
    }
    const out = renderMarkdown(report)
    expect(out).toContain('**Coverage window:** unknown (no successful refresh recorded)')
    expect(out).not.toContain('recorded before window tracking')
  })

  it.each([0, -1, 1.5, Number.NaN])('never renders a non-positive-integer window %p as last N weeks', (historyWeeks) => {
    // Review PR #230 (kilo nux5v, markdown twin): the same single-minded
    // !== null guard lives here — both renderers share one formatter now.
    const report = loadFixture('full')
    report.coverage!.refresh = {
      ...report.coverage!.refresh,
      historyWeeks,
      coveredFrom: '2026-09-19T11:00:00.000Z',
      coveredThrough: '2026-09-19T11:00:00.000Z',
    }
    const out = renderMarkdown(report)
    expect(out).not.toContain(`last ${String(historyWeeks)} week`)
    expect(out).toContain('**Coverage window:** unknown')
  })
})
