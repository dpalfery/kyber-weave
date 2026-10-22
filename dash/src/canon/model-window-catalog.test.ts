// P2.8 — D15 vendor-documented model-window catalog (RED). Synthetic fixtures
// only: invented model ids, no copied vendor prose, no credentials, no
// caller-supplied refresh URLs.

import { describe, expect, it } from 'vitest'

import { detectCompactionHazard } from '../analysis/findings.js'
import { DEFAULT_CONTEXT_LIMIT } from './context-window.js'
import { readModelCatalogFixture } from './fixtures/model-window-catalog/fixtures.js'
import type { CanonicalRecord } from './types.js'
import { CanonStore } from './store.js'
import { isSignalMeasurable } from '../analysis/signals.js'
import {
  MODEL_WINDOW_CATALOG_SOURCES,
  analyzeCompactionWithModelCatalog,
  catalogContextPressure,
  formatUnknownWindowCoverageHint,
  getModelCatalogSnapshot,
  lookupModelCatalogRow,
  parseVendorCatalogDocument,
  refreshModelWindowCatalog,
  resolveContextWindowForModel,
  type ModelWindowCatalogVendor,
} from './model-window-catalog.js'

const VENDOR_A: ModelWindowCatalogVendor = 'synth-vendor-a'
const VENDOR_B: ModelWindowCatalogVendor = 'synth-vendor-b'

function record(over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId: over.spanId ?? 'span-catalog',
    traceId: over.traceId ?? 'trace-catalog',
    parentSpanId: over.parentSpanId ?? null,
    source: over.source ?? 'claude-code',
    harness: over.harness ?? 'claude-code',
    sessionId: over.sessionId ?? 'sess-catalog',
    name: over.name ?? 'llm_request',
    op: over.op ?? 'llm.invoke',
    kind: over.kind ?? 'client',
    timestamp: over.timestamp ?? '2026-10-01T12:00:00.000Z',
    durationMs: over.durationMs ?? 100,
    status: over.status ?? 'ok',
    tokens: over.tokens ?? {
      freshInput: 100_000,
      cacheRead: 0,
      cacheCreation: 0,
      output: 100,
      reportedInput: 100_000,
      reportedOutput: 100,
    },
    content: over.content ?? {},
    cost: over.cost ?? { basis: 'unknown', status: 'no_rate' },
    raw: over.raw,
    ...over,
  }
}

function seedBothVendors(store: CanonStore): void {
  refreshModelWindowCatalog(store, {
    readVendor: (vendor) => {
      if (vendor === VENDOR_A) {
        return parseVendorCatalogDocument(readModelCatalogFixture('vendor-a-initial.json'))
      }
      return parseVendorCatalogDocument(readModelCatalogFixture('vendor-b-initial.json'))
    },
    now: () => '2026-10-01T10:00:00.000Z',
  })
}

describe('P2.8: model-window catalog refresh (last-known-good per vendor)', () => {
  it('uses fixed official HTTPS sources and rejects caller-supplied URLs', () => {
    expect(MODEL_WINDOW_CATALOG_SOURCES[VENDOR_A].documentationUrl).toMatch(/^https:\/\//)
    expect(MODEL_WINDOW_CATALOG_SOURCES[VENDOR_B].documentationUrl).toMatch(/^https:\/\//)
    expect(Object.keys(MODEL_WINDOW_CATALOG_SOURCES).sort()).toEqual([VENDOR_A, VENDOR_B].sort())
  })

  it('replaces a vendor atomically after parse/validate and keeps prior rows when another vendor fails', () => {
    const store = new CanonStore(':memory:')
    seedBothVendors(store)

    const before = getModelCatalogSnapshot(store)
    expect(lookupModelCatalogRow(store, 'zz-synth-alpha-1')?.contextWindow).toBe(128_000)
    expect(lookupModelCatalogRow(store, 'zz-synth-beta-9')?.contextWindow).toBe(256_000)

    const rebuilt = { count: 0 }
    const result = refreshModelWindowCatalog(store, {
      readVendor: (vendor) => {
        if (vendor === VENDOR_A) {
          return parseVendorCatalogDocument(readModelCatalogFixture('vendor-a-updated.json'))
        }
        return parseVendorCatalogDocument(readModelCatalogFixture('vendor-b-invalid.json'))
      },
      now: () => '2026-10-01T11:00:00.000Z',
      rebuildDerived: () => {
        rebuilt.count += 1
      },
    })

    expect(result.vendorsUpdated).toEqual([VENDOR_A])
    expect(result.vendorsFailed).toEqual([
      { vendor: VENDOR_B, error: expect.stringMatching(/positive|invalid|window/i) },
    ])

    expect(lookupModelCatalogRow(store, 'zz-synth-alpha-1')?.contextWindow).toBe(200_000)
    expect(lookupModelCatalogRow(store, 'zz-synth-beta-9')?.contextWindow).toBe(256_000)

    const after = getModelCatalogSnapshot(store)
    expect(after.vendors[VENDOR_B].status).toBe('error')
    expect(after.vendors[VENDOR_B].rowCount).toBe(1)
    expect(after.rowCount).toBe(before.rowCount)
    expect(rebuilt.count).toBe(1)
    store.close()
  })
})

describe('P2.8: resolveContextWindowForModel precedence and measurability', () => {
  it('resolves an exact catalog id and explicit alias, never fuzzy match', () => {
    const store = new CanonStore(':memory:')
    seedBothVendors(store)

    const byId = resolveContextWindowForModel([], 'zz-synth-alpha-1', store)
    expect(byId).toEqual({
      measurable: true,
      contextLimit: 128_000,
      contextLimitSource: 'catalog',
    })

    const byAlias = resolveContextWindowForModel([], 'zz-synth-alpha-1-alias', store)
    expect(byAlias.measurable).toBe(true)
    if (byAlias.measurable) {
      expect(byAlias.contextLimitSource).toBe('catalog')
      expect(byAlias.contextLimit).toBe(128_000)
    }

    const fuzzy = resolveContextWindowForModel([], 'zz-synth-alpha-1-extra', store)
    expect(fuzzy.measurable).toBe(false)

    const near = lookupModelCatalogRow(store, 'zz-synth-alpha-1')
    expect(near?.lookupId).toBe('zz-synth-alpha-1')
    expect(lookupModelCatalogRow(store, 'zz-synth-alpha-1x')).toBeUndefined()
    store.close()
  })

  it('prefers reported and declared windows over catalog', () => {
    const store = new CanonStore(':memory:')
    seedBothVendors(store)

    const reported = resolveContextWindowForModel(
      [
        record({
          raw: { 'gen_ai.request.max_context_tokens': 64_000 },
        }),
      ],
      'zz-synth-alpha-1',
      store,
    )
    expect(reported).toEqual({
      measurable: true,
      contextLimit: 64_000,
      contextLimitSource: 'reported',
    })

    const declared = resolveContextWindowForModel(
      [
        record({
          raw: { declaredContextWindow: 96_000 },
        }),
      ],
      'zz-synth-alpha-1',
      store,
    )
    expect(declared).toEqual({
      measurable: true,
      contextLimit: 96_000,
      contextLimitSource: 'declared',
    })
    store.close()
  })

  it('stays not_measurable without model identity or an exact catalog row', () => {
    const store = new CanonStore(':memory:')
    seedBothVendors(store)

    const noModel = resolveContextWindowForModel([], undefined, store)
    expect(noModel.measurable).toBe(false)

    const missingRow = resolveContextWindowForModel([], 'zz-synth-missing-404', store)
    expect(missingRow.measurable).toBe(false)

    const noRecordsNoCatalog = resolveContextWindowForModel([], 'zz-synth-missing-404', store)
    if (!noRecordsNoCatalog.measurable) {
      expect(noRecordsNoCatalog.reason).toMatch(/model|catalog|measurable/i)
    }
    store.close()
  })

  it('does not treat the 200K default fallback as catalog data', () => {
    const store = new CanonStore(':memory:')
    seedBothVendors(store)

    const fallbackOnly = resolveContextWindowForModel([record()], 'zz-synth-missing-404', store)
    expect(fallbackOnly.measurable).toBe(false)

    const defaultWindow = resolveContextWindowForModel([record()], undefined, store)
    expect(defaultWindow.measurable).toBe(false)
    expect(DEFAULT_CONTEXT_LIMIT).toBe(200_000)
    store.close()
  })
})

describe('P2.8: catalog-derived pressure and compaction findings', () => {
  it('marks catalog pressure as derived, never measured', () => {
    const store = new CanonStore(':memory:')
    seedBothVendors(store)
    const window = resolveContextWindowForModel([], 'zz-synth-alpha-1', store)
    expect(window.measurable).toBe(true)
    if (!window.measurable || window.contextLimitSource !== 'catalog') {
      throw new Error('expected catalog window')
    }

    const pressure = catalogContextPressure(110_000, window)
    expect(isSignalMeasurable(pressure)).toBe(true)
    if (isSignalMeasurable(pressure)) {
      expect(pressure.status).toBe('derived')
      expect(pressure.measurementClass).not.toBe('deterministic')
      expect(pressure.value).toBeCloseTo(110_000 / 128_000, 5)
    }
    store.close()
  })

  it('emits an inferred compaction finding with a vendor-catalog caveat, never measured', () => {
    const store = new CanonStore(':memory:')
    seedBothVendors(store)

    const turn1 = record({
      spanId: 'turn-early',
      tokens: {
        freshInput: 100_000,
        cacheRead: 0,
        cacheCreation: 0,
        output: 100,
        reportedInput: 100_000,
        reportedOutput: 100,
      },
    })
    const turn2 = record({
      spanId: 'turn-peak',
      tokens: {
        freshInput: 120_000,
        cacheRead: 0,
        cacheCreation: 0,
        output: 100,
        reportedInput: 120_000,
        reportedOutput: 100,
      },
    })

    const analyzed = analyzeCompactionWithModelCatalog({
      records: [turn1, turn2],
      modelId: 'zz-synth-alpha-1',
      store,
    })

    expect(analyzed.findings.length).toBe(1)
    const finding = analyzed.findings[0]!
    expect(finding.detectorId).toBe('compaction-hazard')
    expect(finding.measurementClass).toBe('inferred')
    expect(finding.confidence).toBe('heuristic')
    expect(finding.outcomeRiskCaveat.toLowerCase()).toContain('vendor catalog')
    expect(finding.payload?.contextLimitSource).toBe('catalog')

    const withoutCatalog = detectCompactionHazard({ records: [turn1, turn2] })
    expect(withoutCatalog).toHaveLength(0)
    store.close()
  })
})

describe('P2.8: unknown-window coverage copy', () => {
  it('names reported, declared, and catalog coverage without implying harness-only reporting', () => {
    const copy = formatUnknownWindowCoverageHint()
    expect(copy.toLowerCase()).toMatch(/reported/)
    expect(copy.toLowerCase()).toMatch(/declared/)
    expect(copy.toLowerCase()).toMatch(/catalog/)
    expect(copy.toLowerCase()).not.toMatch(/only a harness-reported/i)
  })
})
