// Test kit for the adapter seam, shared by base.test.ts and registry.test.ts.
// The stubs implement `HarnessAdapter` exactly as the contract documents it:
// `detect` scores attribute-key presence only and never consults the source
// name, so registry tests exercise attribution the way R6.2 demands. The
// fingerprint keys mirror the harnesses of task 5.3 — pi emits the GenAI
// namespace under a `pi.*` vendor namespace, Copilot emits `codeburn.*` /
// `copilot.*` — and PI_PARTIAL_FINGERPRINT reproduces the measured case the
// second attribution pass exists for: tool spans carrying GenAI attributes
// with no vendor namespace (design.md, "Normalization layer").

import type { HarnessAdapter, RawSpan } from './base.js'
import { resolveRootByParentage, traceGroup } from './base.js'
import { AdapterRegistry, type AdapterRegistryOptions } from './registry.js'
import type { CanonicalRecord } from '../types.js'

/** A full pi fingerprint: GenAI usage plus the pi vendor namespace. */
export const PI_FINGERPRINT = {
  'gen_ai.usage.input_tokens': 4821,
  'pi.session.id': 's-9f2',
} as const

/** GenAI attributes with no vendor namespace — the alone-in-its-trace tool-span case. */
export const PI_PARTIAL_FINGERPRINT = {
  'gen_ai.usage.input_tokens': 512,
} as const

/** A full Copilot fingerprint. */
export const COPILOT_FINGERPRINT = {
  'codeburn.provider': 'github-copilot',
  'copilot.model': 'claude-sonnet-4.5',
} as const

export const PI_KEYS = Object.keys(PI_FINGERPRINT)
export const COPILOT_KEYS = Object.keys(COPILOT_FINGERPRINT)

/** Enough of a RawSpan to name; everything else takes a neutral default. */
export type RawSpanSpec = Partial<RawSpan> & Pick<RawSpan, 'spanId'>

export function rawSpan(spec: RawSpanSpec): RawSpan {
  return {
    traceId: null,
    parentSpanId: null,
    source: 'pi-abc123',
    attributes: {},
    name: 'chat turn',
    kind: 'internal',
    ...spec,
  }
}

/** A `HarnessAdapter` whose detect counts fingerprint-key presence; unrelated methods are inert. */
export function stubAdapter(spec: {
  name: string
  namespaces: string[]
  fingerprintKeys: string[]
}): HarnessAdapter {
  return {
    name: spec.name,
    namespaces: spec.namespaces,
    detect(span) {
      const hits = spec.fingerprintKeys.filter((key) => key in span.attributes).length
      return hits / spec.fingerprintKeys.length
    },
    relevance: () => 1,
    normalize(raw) {
      const record: CanonicalRecord = {
        spanId: raw.spanId,
        traceId: raw.traceId,
        parentSpanId: raw.parentSpanId,
        source: raw.source,
        harness: spec.name,
        name: raw.name,
        op: 'llm.invoke',
        kind: raw.kind,
        timestamp: '2026-08-29T12:00:00.000Z',
        durationMs: 0,
        status: 'ok',
        tokens: {
          freshInput: 0,
          cacheRead: 0,
          cacheCreation: 0,
          output: 0,
          reportedInput: 0,
          reportedOutput: 0,
        },
        content: {},
        cost: { basis: 'unknown', status: 'no_rate' },
      }
      return record
    },
    group: traceGroup,
    resolveRoot: resolveRootByParentage,
    validate: () => undefined,
    unexportedMetrics: () => ['tool_definitions'],
  }
}

export function piAdapter(): HarnessAdapter {
  return stubAdapter({ name: 'pi', namespaces: ['gen_ai', 'pi'], fingerprintKeys: PI_KEYS })
}

export function copilotAdapter(): HarnessAdapter {
  return stubAdapter({
    name: 'copilot',
    namespaces: ['codeburn', 'copilot'],
    fingerprintKeys: COPILOT_KEYS,
  })
}

/** The registry both harnesses are registered against, in a fixed order. */
export function defaultRegistry(options: AdapterRegistryOptions = {}): AdapterRegistry {
  return new AdapterRegistry([piAdapter(), copilotAdapter()], options)
}
