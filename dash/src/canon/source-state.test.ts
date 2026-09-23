import { describe, expect, it } from 'vitest'

import {
  checkpointIsReusable,
  uncoveredIntervals,
  type CoverageInterval,
  type SourceCheckpoint,
} from './source-state.js'

const TWO_WEEKS: CoverageInterval = {
  fromUtc: '2026-08-29T00:00:00.000Z',
  throughUtc: '2026-09-12T00:00:00.000Z',
}

const SIX_WEEKS: CoverageInterval = {
  fromUtc: '2026-08-01T00:00:00.000Z',
  throughUtc: '2026-09-12T00:00:00.000Z',
}

function checkpoint(overrides: Partial<SourceCheckpoint> = {}): SourceCheckpoint {
  return {
    harnessId: 'pi',
    sourceKey: 'session:agent-7f3',
    providerId: 'pi',
    parserId: 'pi-jsonl',
    parserContractVersion: '1',
    format: 'jsonl',
    sourceRootLabel: '~/.pi/agent/sessions',
    revisionToken: 'inode:1:mtime:100:size:40',
    coveredFromUtc: TWO_WEEKS.fromUtc,
    coveredThroughUtc: TWO_WEEKS.throughUtc,
    lastAttemptUtc: '2026-09-12T00:00:00.000Z',
    lastSuccessUtc: '2026-09-12T00:00:01.000Z',
    lastStatus: 'ok',
    lastErrorCode: null,
    unitCount: 1,
    recordCount: 4,
    ...overrides,
  }
}

describe('uncoveredIntervals', () => {
  it('treats a missing checkpoint as the full requested window', () => {
    expect(uncoveredIntervals(undefined, TWO_WEEKS, { revisionToken: 'r', parserContractVersion: '1' })).toEqual([
      TWO_WEEKS,
    ])
  })

  it('returns nothing when the same revision already covers the request', () => {
    expect(
      uncoveredIntervals(checkpoint(), TWO_WEEKS, {
        revisionToken: checkpoint().revisionToken,
        parserContractVersion: '1',
      }),
    ).toEqual([])
  })

  it('returns only the newly requested prefix when the history window expands', () => {
    expect(
      uncoveredIntervals(checkpoint(), SIX_WEEKS, {
        revisionToken: checkpoint().revisionToken,
        parserContractVersion: '1',
      }),
    ).toEqual([{ fromUtc: SIX_WEEKS.fromUtc, throughUtc: TWO_WEEKS.fromUtc }])
  })

  it('returns a suffix when commandStartedAt moves forward over an unchanged unit', () => {
    const later: CoverageInterval = {
      fromUtc: TWO_WEEKS.fromUtc,
      throughUtc: '2026-09-19T00:00:00.000Z',
    }
    expect(
      uncoveredIntervals(checkpoint(), later, {
        revisionToken: checkpoint().revisionToken,
        parserContractVersion: '1',
      }),
    ).toEqual([{ fromUtc: TWO_WEEKS.throughUtc, throughUtc: later.throughUtc }])
  })

  it('reprocesses the full requested window when the native revision changes', () => {
    expect(
      uncoveredIntervals(checkpoint(), TWO_WEEKS, {
        revisionToken: 'inode:1:mtime:200:size:80',
        parserContractVersion: '1',
      }),
    ).toEqual([TWO_WEEKS])
  })

  it('reprocesses the full requested window when the parser contract version changes', () => {
    expect(
      uncoveredIntervals(checkpoint(), TWO_WEEKS, {
        revisionToken: checkpoint().revisionToken,
        parserContractVersion: '2',
      }),
    ).toEqual([TWO_WEEKS])
  })
})

describe('checkpointIsReusable', () => {
  it('is false for an invalidated checkpoint even when revision tokens match', () => {
    expect(
      checkpointIsReusable(checkpoint({ lastStatus: 'invalidated' }), {
        revisionToken: checkpoint().revisionToken,
        parserContractVersion: '1',
      }),
    ).toBe(false)
  })
})
