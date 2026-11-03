/**
 * The latest-session panel's absent-versus-zero rendering (D8, ADR 0033).
 *
 * The decision that a bucket was never observed is made in `buildContextReport`; the
 * tray only displays it. These cases pin the display: a bucket the report says is
 * unmeasurable shows its reason, never a "0", and a measured zero still reads as zero.
 */

import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'

import type { ContextReport } from '../../../../src/analysis/report/types.ts'
import { SessionPanel } from './SessionPanel'

const REASON = "no system_prompt part was observed in this session's captured messages"

function reportOf(systemPrompt: { value: number; unit: string } | { value: null; reason: string }): ContextReport {
  return {
    schemaVersion: 1,
    generatedAt: '2026-09-19T12:00:00.000Z',
    kyberdashVersion: '0.0.0',
    scope: { days: 7 },
    latestSession: {
      sessionId: 'oc-1',
      harness: 'opencode',
      project: 'kyber-weave',
      lastActivityAt: '2026-09-19T11:00:00.000Z',
      turnCount: 1,
      view: 'session/oc-1',
      latestTurn: {
        index: 1,
        pressure: { value: 0.2 },
        contextWindow: { value: 200_000, unit: 'tokens' },
        buckets: {
          system_prompt: systemPrompt,
          tool_definitions: { value: 9000, unit: 'tokens' },
          instruction_context: { value: null, reason: 'no instruction_context part was observed in this session\'s captured messages' },
          conversation_history: { value: 30_000, unit: 'tokens' },
          tool_result_content: { value: null, reason: 'no tool_result_content part was observed in this session\'s captured messages' },
        },
        residual: { value: 1000, unit: 'tokens' },
        cacheInvalidation: false,
      },
      toolDefinitionSources: [],
      cacheInvalidationTurns: [],
    },
  }
}

/** The text of one bucket's value cell. */
function cell(html: string, bucket: string): string {
  const match = new RegExp(`<dd data-bucket="${bucket}">([^<]*)</dd>`).exec(html)
  if (match === null) throw new Error(`no ${bucket} cell in: ${html}`)
  return (match[1] ?? '').replaceAll('&#x27;', "'")
}

const render = (report: ContextReport): string =>
  renderToStaticMarkup(<SessionPanel report={report} onOpenView={() => {}} />)

describe('SessionPanel absent versus zero', () => {
  it('shows the reason for a never-observed bucket, not a zero', () => {
    const html = render(reportOf({ value: null, reason: REASON }))

    const text = cell(html, 'system_prompt')
    expect(text).toContain(REASON)
    expect(text).not.toMatch(/^0\b/)
  })

  it('does not draw a never-observed bucket into the composition bar', () => {
    const html = render(reportOf({ value: null, reason: REASON }))

    expect(html).not.toContain('composition-bar__part--system_prompt')
    expect(html).toContain('composition-bar__part--tool_definitions')
  })

  it('still reads a measured zero as zero', () => {
    const html = render(reportOf({ value: 0, unit: 'tokens' }))

    expect(cell(html, 'system_prompt')).toMatch(/^0\b/)
    expect(cell(html, 'system_prompt')).not.toContain('observed')
  })
})
