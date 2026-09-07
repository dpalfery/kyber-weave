// Integration test for Task E4 — Survey Cache and Prefix Availability Per Harness
// Asserts cache counter and prefix byte coverage across all 10 agent harnesses
// via the parity audit tools in KyberDash.

import { describe, expect, it } from 'vitest'

import {
  SURVEYED_HARNESSES,
  cacheAvailability,
  normalizeHarnessName,
  prefixAvailability,
  type CacheAvailability,
  type PrefixAvailability,
} from '../kyber/canon/measurability.js'
import {
  auditCachePrefixCoverage,
  auditCorpusCachePrefix,
  type CachePrefixCoverageReport,
  type CorpusCachePrefixAudit,
} from '../kyber/tools/parity.js'
import type { CanonicalRecord } from '../kyber/canon/types.js'

function stubRecord(overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId: 'sp-test-1',
    traceId: 'tr-test-1',
    parentSpanId: null,
    source: 'test-source',
    harness: 'copilot',
    name: 'test span',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-09-05T12:00:00.000Z',
    durationMs: 500,
    status: 'ok',
    tokens: {
      freshInput: 100,
      cacheRead: 200,
      cacheCreation: 50,
      output: 50,
      reportedInput: 350,
      reportedOutput: 50,
    },
    content: {},
    cost: { basis: 'published', status: 'priced', value: 0.005, currency: 'USD' },
    ...overrides,
  }
}

describe('Task E4 — Harness Cache and Prefix Survey Parity Integration', () => {
  it('covers exactly the 10 surveyed agent harnesses', () => {
    const expected = [
      'copilot',
      'claude-code',
      'cursor',
      'windsurf',
      'roo-code',
      'cline',
      'aider',
      'codex',
      'gemini',
      'opencode',
    ]
    expect([...SURVEYED_HARNESSES].sort()).toEqual(expected.sort())
  })

  it('declares verified cache counter support for Copilot, Claude Code, Roo Code, Cline, and Codex', () => {
    const verifiedSupported = ['copilot', 'claude-code', 'roo-code', 'cline', 'codex']
    for (const harness of verifiedSupported) {
      const cache = cacheAvailability(harness)
      expect(cache.status).toBe('supported')
      expect(cache.confidence).toBe('verified')
      expect(cache.cacheRead).toBe(true)
    }

    // Claude Code, Roo Code, Cline, Copilot support both cacheRead and cacheCreation
    expect(cacheAvailability('claude-code').cacheCreation).toBe(true)
    expect(cacheAvailability('roo-code').cacheCreation).toBe(true)
    expect(cacheAvailability('cline').cacheCreation).toBe(true)
    expect(cacheAvailability('copilot').cacheCreation).toBe(true)

    // Codex has implicit cache creation without a separate counter
    expect(cacheAvailability('codex').cacheCreation).toBe(false)
  })

  it('declares partial cache availability for Gemini with verified confidence', () => {
    const gemini = cacheAvailability('gemini')
    expect(gemini.status).toBe('partial')
    expect(gemini.confidence).toBe('verified')
    expect(gemini.cacheRead).toBe(true)
    expect(gemini.cacheCreation).toBe(false)
    expect(gemini.reason).toMatch(/cache-creation/i)
  })

  it('declares unsupported or not_measurable for harnesses lacking cache counters', () => {
    expect(cacheAvailability('cursor').status).toBe('unsupported')
    expect(cacheAvailability('cursor').confidence).toBe('verified')

    expect(cacheAvailability('windsurf').status).toBe('unsupported')
    expect(cacheAvailability('windsurf').confidence).toBe('documented')

    expect(cacheAvailability('aider').status).toBe('unsupported')
    expect(cacheAvailability('aider').confidence).toBe('documented')

    expect(cacheAvailability('opencode').status).toBe('not_measurable')
    expect(cacheAvailability('opencode').confidence).toBe('documented')
  })

  it('declares prefix byte availability and fallback accurately per harness', () => {
    // Harnesses with full prefix byte reconstruction
    const codex = prefixAvailability('codex')
    expect(codex.status).toBe('supported')
    expect(codex.confidence).toBe('verified')
    expect(codex.prefixBytes).toBe(true)

    const copilot = prefixAvailability('copilot')
    expect(copilot.status).toBe('supported')
    expect(copilot.confidence).toBe('verified')
    expect(copilot.prefixBytes).toBe(true)

    // Harnesses with fallback: detect-but-cannot-locate (cache counters present, prefix bytes unavailable)
    const fallbackHarnesses = ['claude-code', 'roo-code', 'cline', 'gemini']
    for (const harness of fallbackHarnesses) {
      const prefix = prefixAvailability(harness)
      expect(prefix.prefixBytes).toBe(false)
      expect(prefix.fallback).toBe('detect-but-cannot-locate')
    }

    // Harnesses with fallback: none
    const noFallbackHarnesses = ['cursor', 'windsurf', 'aider', 'opencode']
    for (const harness of noFallbackHarnesses) {
      const prefix = prefixAvailability(harness)
      expect(prefix.prefixBytes).toBe(false)
      expect(prefix.fallback).toBe('none')
    }
  })

  it('normalizes known aliases for all harnesses', () => {
    expect(normalizeHarnessName('Claude')).toBe('claude-code')
    expect(normalizeHarnessName('copilot-chat')).toBe('copilot')
    expect(normalizeHarnessName('cursor-agent')).toBe('cursor')
    expect(normalizeHarnessName('cascade')).toBe('windsurf')
    expect(normalizeHarnessName('roo')).toBe('roo-code')
    expect(normalizeHarnessName('cline-cli')).toBe('cline')
    expect(normalizeHarnessName('antigravity')).toBe('gemini')
    expect(normalizeHarnessName('agy')).toBe('gemini')
  })

  it('runs auditCachePrefixCoverage across all harnesses producing typed parity audit report', () => {
    const report: CachePrefixCoverageReport = auditCachePrefixCoverage()
    expect(report.surveyedCount).toBe(10)
    expect(report.supportedCacheCount).toBe(6) // 5 supported + 1 partial (gemini)
    expect(report.supportedPrefixCount).toBe(2) // copilot, codex
    expect(report.fallbackPrefixCount).toBe(5) // copilot, claude-code, roo-code, cline, gemini

    for (const [harnessName, item] of Object.entries(report.harnesses)) {
      expect(item.harness).toBe(harnessName)
      expect(item.cache.reason).toBeTruthy()
      expect(item.prefix.reason).toBeTruthy()
    }
  })

  it('runs auditCorpusCachePrefix over multi-harness spans and associates declared availability', () => {
    const corpus: CanonicalRecord[] = [
      stubRecord({
        spanId: 'sp-1',
        harness: 'claude-code',
        tokens: {
          freshInput: 500,
          cacheRead: 1500,
          cacheCreation: 200,
          output: 100,
          reportedInput: 2200,
          reportedOutput: 100,
        },
      }),
      stubRecord({
        spanId: 'sp-2',
        harness: 'codex',
        tokens: {
          freshInput: 300,
          cacheRead: 700,
          cacheCreation: 0,
          output: 80,
          reportedInput: 1000,
          reportedOutput: 80,
        },
        content: { system_prompt: 'System prompt instructions' },
      }),
      stubRecord({
        spanId: 'sp-3',
        harness: 'cursor',
        tokens: {
          freshInput: 400,
          cacheRead: 0,
          cacheCreation: 0,
          output: 120,
          reportedInput: 400,
          reportedOutput: 120,
        },
      }),
    ]

    const audit: CorpusCachePrefixAudit = auditCorpusCachePrefix(corpus)
    expect(audit.recordCount).toBe(3)
    expect(audit.totalCacheReadTokens).toBe(2200)
    expect(audit.totalCacheCreationTokens).toBe(200)

    expect(audit.byHarness['claude-code']).toBeDefined()
    expect(audit.byHarness['claude-code'].cacheReadTokens).toBe(1500)
    expect(audit.byHarness['claude-code'].cacheCreationTokens).toBe(200)
    expect(audit.byHarness['claude-code'].declaredCache.status).toBe('supported')
    expect(audit.byHarness['claude-code'].declaredPrefix.fallback).toBe('detect-but-cannot-locate')

    expect(audit.byHarness['codex']).toBeDefined()
    expect(audit.byHarness['codex'].hasPrefixContent).toBe(true)
    expect(audit.byHarness['codex'].declaredPrefix.status).toBe('supported')

    expect(audit.byHarness['cursor']).toBeDefined()
    expect(audit.byHarness['cursor'].declaredCache.status).toBe('unsupported')
    expect(audit.byHarness['cursor'].declaredPrefix.fallback).toBe('none')
  })
})
