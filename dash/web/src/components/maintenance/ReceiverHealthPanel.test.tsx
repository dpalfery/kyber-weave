// @vitest-environment happy-dom
// Live receiver health verdict: green only on the engine's own healthy word,
// red for everything else — including a missing `receiver` key (servers
// predating the probe) and a stale probe. State is always named in text,
// never colour alone.

import { describe, expect, it } from 'vitest'

import { collectorVerdict, RECEIVER_STALE_AFTER_MS } from './ReceiverHealthPanel.js'
import type { KyberCoverage } from '../../lib/kyberApi.js'

const NOW = Date.parse('2026-10-10T12:00:00.000Z')

function coverageWith(receiver: unknown): KyberCoverage {
  return {
    refresh: { lastSuccessAt: null, lastFailure: null, inProgress: null },
    ingest: { status: 'unknown', reason: 'no receiver activity recorded', sources: [], lastReceivedAt: null },
    quarantineByReason: [],
    checkpoints: null,
    receiver: receiver as KyberCoverage['receiver'],
  }
}

const LIVE = {
  enabled: true,
  status: 'hosted',
  listening: true,
  hostedByUs: true,
  paused: false,
  healthy: true,
  endpoint: 'http://127.0.0.1:4318',
  checkedAt: new Date(NOW - 10_000).toISOString(),
  lastAcceptedAt: new Date(NOW - 20_000).toISOString(),
  failures5m: 0,
  lastError: null,
  lastCode: null,
  lastErrorAt: null,
}

describe('collectorVerdict', () => {
  it('is live only when the engine reports healthy, listening, unpaused and error-free', () => {
    const verdict = collectorVerdict(coverageWith(LIVE), NOW)
    expect(verdict.state).toBe('live')
    expect(verdict.label).toMatch(/live/i)
  })

  it('is down when the receiver key is missing (servers predating the probe)', () => {
    const { receiver: _dropped, ...rest } = coverageWith(LIVE)
    void _dropped
    const verdict = collectorVerdict(rest as KyberCoverage, NOW)
    expect(verdict.state).toBe('down')
    expect(verdict.label).toMatch(/unavailable/i)
  })

  it('is down when paused, not listening, unhealthy, or erroring', () => {
    expect(collectorVerdict(coverageWith({ ...LIVE, paused: true }), NOW).state).toBe('down')
    expect(collectorVerdict(coverageWith({ ...LIVE, paused: null }), NOW).state).toBe('down')
    expect(collectorVerdict(coverageWith({ ...LIVE, listening: false }), NOW).state).toBe('down')
    expect(collectorVerdict(coverageWith({ ...LIVE, healthy: false }), NOW).state).toBe('down')
    expect(collectorVerdict(coverageWith({ ...LIVE, enabled: false }), NOW).state).toBe('down')
    expect(collectorVerdict(coverageWith({ ...LIVE, enabled: null }), NOW).state).toBe('down')
    expect(
      collectorVerdict(
        coverageWith({ ...LIVE, failures5m: 2, lastErrorAt: 'x', lastCode: 'OTLP_500', lastError: 'boom' }),
        NOW,
      ).state,
    ).toBe('down')
  })

  it('is down when the failure count is unknown or the probe is stale', () => {
    expect(collectorVerdict(coverageWith({ ...LIVE, failures5m: null }), NOW).state).toBe('down')
    expect(collectorVerdict(coverageWith({ ...LIVE, checkedAt: null }), NOW).state).toBe('down')
    const staleAt = new Date(NOW - RECEIVER_STALE_AFTER_MS - 1_000).toISOString()
    const verdict = collectorVerdict(coverageWith({ ...LIVE, checkedAt: staleAt }), NOW)
    expect(verdict.state).toBe('down')
    expect(verdict.label).toMatch(/stale/i)
  })
})
