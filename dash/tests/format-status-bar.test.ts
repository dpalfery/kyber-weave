import { describe, it, expect } from 'vitest'
import stripAnsi from 'strip-ansi'

import { formatCost, renderStatusBar } from '../src/format.js'
import type { ProjectSummary } from '../src/types.js'

// Copilot supplementary accounting calls (shutdown rollups, residuals, store
// rows paired with an already-counted per-turn call) carry real cost but are
// not distinct requests. The status bar must spend their cost and count zero
// calls for them, matching the session summaries and sealed daily history.
describe('renderStatusBar supplementary accounting', () => {
  function call(costUSD: number, timestamp: string, supplementaryAccounting: boolean) {
    return {
      provider: 'copilot',
      model: 'claude-sonnet-4-5',
      usage: {
        inputTokens: supplementaryAccounting ? 40 : 0,
        outputTokens: 0,
        cacheCreationInputTokens: 0,
        cacheReadInputTokens: 0,
        cachedInputTokens: 0,
        reasoningTokens: 0,
        webSearchRequests: 0,
      },
      costUSD,
      tools: [],
      mcpTools: [],
      skills: [],
      hasAgentSpawn: false,
      hasPlanMode: false,
      speed: 'standard',
      timestamp,
      bashCommands: [],
      deduplicationKey: `copilot-${timestamp}`,
      supplementaryAccounting,
    }
  }

  it('counts only behavioral calls while keeping every call\'s cost', () => {
    // Local noon today, so both turns bucket into today/month on any machine TZ.
    const now = new Date()
    const at = (minute: number) => new Date(now.getFullYear(), now.getMonth(), now.getDate(), 12, minute).toISOString()

    const projects = [{
      sessions: [{
        turns: [
          // One real request plus its paired store row.
          {
            timestamp: at(0),
            assistantCalls: [call(1.0, at(0), false), call(0.5, at(1), true)],
          },
          // Rollup-only turn: cost lands, no request is counted. The raw
          // zero-call gate in renderStatusBar keeps this turn in the totals.
          {
            timestamp: at(2),
            assistantCalls: [call(0.25, at(2), true)],
          },
        ],
      }],
    }] as ProjectSummary[]

    const out = stripAnsi(renderStatusBar(projects))
    expect(out).toContain(`Today  ${formatCost(1.75)}  1 calls`)
    expect(out).toContain(`Month  ${formatCost(1.75)}  1 calls`)
  })
})
