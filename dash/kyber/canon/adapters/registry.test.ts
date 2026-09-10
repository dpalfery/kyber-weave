import { describe, expect, it } from 'vitest'

import { AdapterRegistry } from './registry.js'
import {
  COPILOT_FINGERPRINT,
  PI_FINGERPRINT,
  PI_PARTIAL_FINGERPRINT,
  copilotAdapter,
  defaultRegistry,
  piAdapter,
  rawSpan,
} from './testing.js'
import { geminiAdapter } from './gemini.js'

describe('AdapterRegistry', () => {
  describe('register', () => {
    it('refuses a second adapter under the same harness name', () => {
      const registry = new AdapterRegistry([piAdapter()])
      expect(() => registry.register(piAdapter())).toThrow(/harness adapter "pi" is already registered/)
    })
  })

  describe('scoreGroup — the fingerprint vote', () => {
    it.each([
      ['gemini namespace', { 'gemini.session.id': 'g-77' }],
      ['gen_ai.system value', { 'gen_ai.system': 'gemini' }],
    ])('votes Gemini from %s vendor evidence', (_label, attributes) => {
      const registry = new AdapterRegistry([geminiAdapter])
      const spans = [rawSpan({ spanId: 'gemini', traceId: 't1', attributes })]

      expect(registry.scoreGroup('pi-abc123', 't1', spans)).toEqual({
        harness: 'gemini',
        confidence: 0.6,
      })
      expect(registry.attribute(spans).get('gemini')).toBe('gemini')
    })

    it('leaves shared GenAI usage counters below the attribution threshold', () => {
      const registry = new AdapterRegistry([geminiAdapter])
      const spans = [
        rawSpan({
          spanId: 'usage-only',
          traceId: 't1',
          attributes: { 'gen_ai.usage.input_tokens': 1_200 },
        }),
      ]

      expect(registry.scoreGroup('pi-abc123', 't1', spans)).toEqual({
        harness: 'gemini',
        confidence: 0.4,
      })
      expect(registry.attribute(spans).has('usage-only')).toBe(false)
    })

    it('normalizes the winning detect total by the group size', () => {
      const spans = [
        rawSpan({ spanId: 'a', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'b', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'c', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'd', traceId: 't1', attributes: {} }),
      ]
      // Three of four spans carry the full pi fingerprint: total 3, confidence 3/4.
      expect(defaultRegistry().scoreGroup('pi-abc123', 't1', spans)).toEqual({
        harness: 'pi',
        confidence: 0.75,
      })
    })

    it('returns undefined when no adapter finds fingerprint evidence', () => {
      const spans = [rawSpan({ spanId: 'a', traceId: 't1', attributes: {} })]
      expect(defaultRegistry().scoreGroup('pi-abc123', 't1', spans)).toBeUndefined()
    })

    it('returns undefined for an empty group', () => {
      expect(defaultRegistry().scoreGroup('pi-abc123', 't1', [])).toBeUndefined()
    })

    it('breaks score ties toward the earlier-registered adapter', () => {
      // One pi key and one copilot key: both adapters total 0.5.
      const spans = [
        rawSpan({
          spanId: 'a',
          traceId: 't1',
          attributes: { 'gen_ai.usage.input_tokens': 10, 'codeburn.provider': 'copilot' },
        }),
      ]
      expect(defaultRegistry().scoreGroup('pi-abc123', 't1', spans)).toEqual({
        harness: 'pi',
        confidence: 0.5,
      })
    })

    it('scores identical fingerprints identically whatever the source is named', () => {
      const registry = defaultRegistry()
      const asPiInstance = [
        rawSpan({ spanId: 'a', source: 'pi-abc123', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
      ]
      const underAnotherName = [
        rawSpan({
          spanId: 'b',
          source: 'copilot-zz9-plasma',
          traceId: 't2',
          attributes: { ...PI_FINGERPRINT },
        }),
      ]
      expect(registry.scoreGroup('pi-abc123', 't1', asPiInstance)).toEqual(
        registry.scoreGroup('copilot-zz9-plasma', 't2', underAnotherName),
      )
    })

    it('refuses spans that do not belong to the named group', () => {
      const spans = [
        rawSpan({ spanId: 'a', source: 'pi-abc123', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
      ]
      expect(() => defaultRegistry().scoreGroup('pi-xyz789', 't1', spans)).toThrow(
        /is not part of group/,
      )
    })
  })

  describe('attribute — pass 1: the vote claims a whole group', () => {
    it('attributes every span of a confident group, fingerprinted or not', () => {
      const spans = [
        rawSpan({ spanId: 'a', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'b', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'c', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'd', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'plain', traceId: 't1', attributes: {} }),
      ]
      // 4/5 = 0.8 confidence carries the group, and the vote is per group:
      // the attribute-less span is claimed along with the rest.
      const attributed = defaultRegistry().attribute(spans)
      expect(attributed.size).toBe(5)
      for (const span of spans) {
        expect(attributed.get(span.spanId)).toBe('pi')
      }
    })

    it('leaves a below-threshold group undecided when no confident sibling shares its source', () => {
      const spans = [
        rawSpan({ spanId: 'a', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'b', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'c', traceId: 't1', attributes: {} }),
        rawSpan({ spanId: 'd', traceId: 't1', attributes: {} }),
        rawSpan({ spanId: 'e', traceId: 't1', attributes: {} }),
      ]
      // 2/5 = 0.4, below the 0.6 threshold, and nothing else maps this source.
      const attributed = defaultRegistry().attribute(spans)
      expect(attributed.size).toBe(0)
    })

    it('honors a raised confidence threshold', () => {
      const spans = [
        rawSpan({ spanId: 'a', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'b', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'c', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'd', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'e', traceId: 't1', attributes: {} }),
      ]
      // 0.8 claims at the default threshold; at 0.9 the group is undecided.
      expect(defaultRegistry().attribute(spans).size).toBe(5)
      const strict = new AdapterRegistry([piAdapter(), copilotAdapter()], { threshold: 0.9 })
      expect(strict.attribute(spans).size).toBe(0)
    })
  })

  describe('R6.2 — attribution never derives from the telemetry source name', () => {
    it('attributes both suffixed instances of one harness by fingerprint', () => {
      // One harness, two instances, two suffixed source names: both carry
      // the same attribute fingerprint, so both attribute to pi — without
      // the suffix or the shared prefix ever being read.
      const attributed = defaultRegistry().attribute([
        rawSpan({ spanId: 'a1', source: 'pi-abc123', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'a2', source: 'pi-xyz789', traceId: 't2', attributes: { ...PI_FINGERPRINT } }),
      ])
      expect(attributed.get('a1')).toBe('pi')
      expect(attributed.get('a2')).toBe('pi')
    })

    it('never claims a span from its source name alone', () => {
      // The source says pi-…, the attributes say nothing. A pi adapter is
      // registered and the name matches it; the fingerprint does not, so
      // no claim is made.
      const attributed = defaultRegistry().attribute([
        rawSpan({ spanId: 'a', source: 'pi-abc123', traceId: 't1', attributes: {} }),
      ])
      expect(attributed.has('a')).toBe(false)
    })

    it('follows the fingerprint when the source name names another harness', () => {
      const attributed = defaultRegistry().attribute([
        rawSpan({ spanId: 'a', source: 'pi-abc123', traceId: 't1', attributes: { ...COPILOT_FINGERPRINT } }),
      ])
      expect(attributed.get('a')).toBe('copilot')
    })
  })

  describe('attribute — pass 2: source inheritance', () => {
    it('inherits the harness for undecided groups of a confidently mapped source', () => {
      // The measured case the second pass exists for (design.md): tool
      // spans carried GenAI attributes with no vendor namespace and sat
      // alone in their traces — 0.5 on their own, below threshold — while
      // their source's request group voted pi at full confidence. An
      // entirely empty span of the same source inherits the same way.
      const attributed = defaultRegistry().attribute([
        rawSpan({ spanId: 'req', source: 'pi-abc123', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'tool', source: 'pi-abc123', traceId: 't2', attributes: { ...PI_PARTIAL_FINGERPRINT } }),
        rawSpan({ spanId: 'bare', source: 'pi-abc123', traceId: 't3', attributes: {} }),
      ])
      expect(attributed.get('req')).toBe('pi')
      expect(attributed.get('tool')).toBe('pi')
      expect(attributed.get('bare')).toBe('pi')
    })

    it('does not inherit across different sources, however similar their names', () => {
      // pi-xyz789 shares a prefix with pi-abc123 and nothing else.
      // Matching on that prefix would be attributing by source name —
      // exactly what R6.2 forbids — so inheritance is exact-match only.
      const attributed = defaultRegistry().attribute([
        rawSpan({ spanId: 'req', source: 'pi-abc123', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'tool', source: 'pi-xyz789', traceId: 't2', attributes: { ...PI_PARTIAL_FINGERPRINT } }),
      ])
      expect(attributed.get('req')).toBe('pi')
      expect(attributed.get('tool')).toBeUndefined()
    })

    it('does not inherit from a source confidently mapped to two harnesses', () => {
      const attributed = defaultRegistry().attribute([
        rawSpan({ spanId: 'p', source: 'multi-42', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
        rawSpan({ spanId: 'c', source: 'multi-42', traceId: 't2', attributes: { ...COPILOT_FINGERPRINT } }),
        rawSpan({ spanId: 'u', source: 'multi-42', traceId: 't3', attributes: { ...PI_PARTIAL_FINGERPRINT } }),
      ])
      expect(attributed.get('p')).toBe('pi')
      expect(attributed.get('c')).toBe('copilot')
      expect(attributed.get('u')).toBeUndefined()
    })

    it('inherits regardless of which group the batch lists first', () => {
      // Pass 2 runs only after pass 1 has seen every group, so the
      // undecided group inherits from a confident sibling listed after it.
      const attributed = defaultRegistry().attribute([
        rawSpan({ spanId: 'tool', source: 'pi-abc123', traceId: 't2', attributes: { ...PI_PARTIAL_FINGERPRINT } }),
        rawSpan({ spanId: 'req', source: 'pi-abc123', traceId: 't1', attributes: { ...PI_FINGERPRINT } }),
      ])
      expect(attributed.get('tool')).toBe('pi')
    })
  })

  describe('the undecided remainder is the quarantine input (R6.1)', () => {
    it('returns no attribution for a group with no evidence and no confident sibling', () => {
      // A namespace no registered adapter models: the span must stay
      // undecided — quarantine with its observed namespaces — never
      // mis-attributed to a harness whose fingerprint it does not carry.
      const attributed = defaultRegistry().attribute([
        rawSpan({ spanId: 'lonely', source: 'trae-01', traceId: 't1', attributes: { 'trae.tool.name': 'bash' } }),
        rawSpan({ spanId: 'req', source: 'pi-abc123', traceId: 't2', attributes: { ...PI_FINGERPRINT } }),
      ])
      expect(attributed.has('lonely')).toBe(false)
      expect(attributed.get('req')).toBe('pi')
    })
  })
})
