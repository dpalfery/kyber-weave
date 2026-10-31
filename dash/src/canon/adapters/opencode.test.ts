// The OpenCode adapter on plugin contract 3 (decision D9).
//
// The plugin stamps every span it emits with `opencode.contract_version`. The
// adapter claims only contract "3", and maps the three span kinds onto canonical
// records: the request span is a parentless `agent.invoke` root, each model step an
// `llm.invoke` turn, each tool call a `tool.invoke` named after its tool. Content
// (system prompt, tool definitions, user prompt) goes to parts, never to
// `record.raw` (D11). Native OpenCode OTel (AI SDK, OpenInference, legacy `ai.*`)
// is no longer claimed (D6).
//
// The golden bodies under plugin fixtures/v2 are the wire ground truth for the
// golden block below.
//
// RED: these tests were written before the adapter moved to contract 3. Against
// the current adapter the contract-3 spans quarantine (it names contract "2"), the
// native shapes are still attributed to opencode, and `record.raw` still holds
// the content keys.

import { readFileSync } from 'node:fs'

import { beforeAll, describe, expect, it } from 'vitest'

import antigravitySpan from './__fixtures__/antigravity-span.json' with { type: 'json' }
import { getModelCosts, loadPricing } from '../../pricing/models.js'
import {
  OPENCODE_CONTRACT_VERSION,
  OPENCODE_KEYS,
  SPAN_NAMES,
  type SpanKind,
} from '../../capture/harnesses/opencode-plugin/contract.js'
import { decodeOtlpJson, type OtlpSpan } from '../../otel/receiver.js'
import { contextLimitOf } from '../context-window.js'
import { readModelCatalogFixture } from '../fixtures/model-window-catalog/fixtures.js'
import {
  catalogWindowForRecords,
  parseVendorCatalogDocument,
  refreshModelWindowCatalog,
  resolveContextWindowForModel,
} from '../model-window-catalog.js'
import { ADAPTERS, ingestBatch, toRawSpan } from '../ingest.js'
import { readListingOf } from '../skills.js'
import { CanonStore } from '../store.js'
import {
  SESSION_LABEL_KEY,
  SKILLS_INVOKED_KEY,
  type CanonicalRecord,
  type ContentPart,
} from '../types.js'
import { opencodeAdapter } from './opencode.js'
import { AdapterRegistry } from './registry.js'
import { rawSpan } from './testing.js'

const GOLDEN_DIR = new URL('../../capture/harnesses/opencode-plugin/fixtures/v2/', import.meta.url)
const CONSENT_ON = 'otlp-v3-runA.consent-on.json'
const CONSENT_OFF = 'otlp-v3-runA.consent-off.json'

/** Run A's session, as the golden bodies carry it. */
const RUN_A_SESSION = 'ses_e74c398c7ffeVajzhRWZNgfR2n'
/** Session for unit spans. Not a golden id, so no unit test passes by reading run A. */
const UNIT_SESSION = 'ses_contract3_unit'

/** Fresh-input counters that identify run A's model steps. */
const STEP_ONE_INPUT = 10935
const STEP_TWO_INPUT = 1259

/** Sentinels from run A's content, checked as substrings of `record.raw`. */
const SYSTEM_SENTINEL = 'You are OpenCode, a coding agent'
const PROMPT_SENTINEL = 'Load the skill named kw-probe-skill'

const SPAN_KINDS: readonly SpanKind[] = ['request', 'step', 'tool']

/** The keys each span kind carries beyond the contract identity, as the plugin stamps them. */
const KIND_KEYS: Record<SpanKind, Record<string, unknown>> = {
  request: {
    [OPENCODE_KEYS.agent]: 'build',
    [OPENCODE_KEYS.promptCount]: 1,
  },
  step: {
    [OPENCODE_KEYS.agent]: 'build',
    [OPENCODE_KEYS.modelId]: 'muse-spark-1.3-contributor',
    [OPENCODE_KEYS.modelProvider]: 'opencode-go',
  },
  tool: {
    [OPENCODE_KEYS.toolName]: 'read',
    [OPENCODE_KEYS.toolCallId]: 'call_contract3_unit',
  },
}

/** Attributes of one contract-3 span. Callers add or override keys with `over`. */
function spanAttributes(kind: SpanKind, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    [OPENCODE_KEYS.contractVersion]: OPENCODE_CONTRACT_VERSION,
    [OPENCODE_KEYS.sessionId]: UNIT_SESSION,
    [OPENCODE_KEYS.spanKind]: kind,
    ...KIND_KEYS[kind],
    ...over,
  }
}

/** A copy of `attributes` without `key`. */
function without(attributes: Record<string, unknown>, key: string): Record<string, unknown> {
  const copy = { ...attributes }
  delete copy[key]
  return copy
}

/** A decoded OTLP span, built whole so no field is left to a cast. */
function otlpSpan(
  spanId: string,
  attributes: Record<string, unknown>,
  over: Partial<OtlpSpan> = {},
): OtlpSpan {
  return {
    traceId: 'trace-contract3-unit',
    parentSpanId: null,
    name: SPAN_NAMES.step,
    kind: 'internal',
    startTimeUnixNano: '1793337355759000000',
    endTimeUnixNano: '1793337363002000000',
    timestamp: '2026-10-30T12:00:00.000Z',
    durationMs: 7243,
    status: { code: 'unset' },
    resource: { 'service.name': 'kyberdash-opencode-plugin' },
    scope: {},
    ...over,
    spanId,
    attributes,
  }
}

/** Normalize one span through the adapter seam, as ingest would hand it over. */
function normalizeSpan(spanId: string, attributes: Record<string, unknown>): CanonicalRecord {
  return opencodeAdapter.normalize(rawSpan({ spanId, attributes }))
}

/** Run `run` against a fresh in-memory store and close it afterwards. */
function withStore<T>(run: (store: CanonStore) => T): T {
  const store = new CanonStore(':memory:')
  try {
    return run(store)
  } finally {
    store.close()
  }
}

/** The stored record for a span. A missing record fails the test and names the span. */
function recordFor(store: CanonStore, spanId: string): CanonicalRecord {
  const record = store.get(spanId)
  if (record === undefined) throw new Error(`no record is stored for span ${spanId}`)
  return record
}

/** The record's raw payload as an object. A record without one fails the test. */
function rawOf(record: CanonicalRecord): Record<string, unknown> {
  const raw: unknown = record.raw
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    throw new Error(`record ${record.spanId} carries no raw object`)
  }
  return raw as Record<string, unknown>
}

/** The text of the record's parts in one content bucket, in order. */
function partTexts(record: CanonicalRecord, bucket: ContentPart['part']): string[] {
  return (record.parts ?? []).filter((part) => part.part === bucket).map((part) => part.text)
}

/** The fields a consent-on and consent-off record must share. Content is compared separately. */
function metadataOf(record: CanonicalRecord): Record<string, unknown> {
  return {
    harness: record.harness,
    op: record.op,
    name: record.name,
    traceId: record.traceId,
    parentSpanId: record.parentSpanId,
    sessionId: record.sessionId,
    durationMs: record.durationMs,
    tokens: record.tokens,
    cost: record.cost,
  }
}

/**
 * The session label is withheld from the consent-off body along with the content, so
 * it is compared only where both bodies carry it.
 */
function withoutLabel(raw: Record<string, unknown>): Record<string, unknown> {
  const { [SESSION_LABEL_KEY]: _label, ...rest } = raw
  return rest
}

/** Decode a golden body and ingest it, as the receiver hands spans to ingest. */
function ingestGolden(
  store: CanonStore,
  file: string,
): { spans: OtlpSpan[]; outcome: ReturnType<typeof ingestBatch> } {
  const spans = decodeOtlpJson(readFileSync(new URL(file, GOLDEN_DIR), 'utf8'))
  return { spans, outcome: ingestBatch(spans, store) }
}

const kindOf = (span: OtlpSpan): unknown => span.attributes[OPENCODE_KEYS.spanKind]

function spansOfKind(spans: readonly OtlpSpan[], kind: SpanKind): OtlpSpan[] {
  return spans.filter((span) => kindOf(span) === kind)
}

function onlySpan(spans: readonly OtlpSpan[], predicate: (span: OtlpSpan) => boolean, what: string): OtlpSpan {
  const found = spans.find(predicate)
  if (found === undefined) throw new Error(`the golden body has no ${what}`)
  return found
}

function stepWithInput(spans: readonly OtlpSpan[], input: number): OtlpSpan {
  return onlySpan(
    spans,
    (span) => kindOf(span) === 'step' && span.attributes[OPENCODE_KEYS.usageInput] === input,
    `step with ${OPENCODE_KEYS.usageInput} ${input}`,
  )
}

function toolNamed(spans: readonly OtlpSpan[], name: string): OtlpSpan {
  return onlySpan(
    spans,
    (span) => kindOf(span) === 'tool' && span.attributes[OPENCODE_KEYS.toolName] === name,
    `tool call named ${name}`,
  )
}

/** A span's text attribute. A missing or non-text value fails the test. */
function textAttribute(span: OtlpSpan, key: string): string {
  const value = span.attributes[key]
  if (typeof value !== 'string') throw new Error(`span ${span.spanId} has no text ${key}`)
  return value
}

describe('opencode adapter registration and attribution guards', () => {
  it('registers the opencode adapter', () => {
    const adapter = ADAPTERS.find((entry) => entry.name === 'opencode')
    expect(adapter).toBeDefined()
  })

  it('claims no span that carries only shared or opencode-namespaced keys', () => {
    const adapter = ADAPTERS.find((entry) => entry.name === 'opencode')
    expect(adapter).toBeDefined()
    if (adapter === undefined) return
    const candidates = [
      rawSpan({ spanId: 'empty', attributes: {} }),
      rawSpan({ spanId: 'usage', attributes: { 'gen_ai.usage.input_tokens': 512 } }),
      rawSpan({
        spanId: 'vendor',
        attributes: { 'opencode.session.id': 'synth-session', 'gen_ai.usage.input_tokens': 512 },
      }),
    ]
    for (const span of candidates) {
      expect(adapter.detect(span), span.spanId).toBe(0)
    }
  })

  it('leaves the existing ingest votes for pi and codex unchanged', () => {
    const registry = new AdapterRegistry([...ADAPTERS])
    const attributed = registry.attribute([
      rawSpan({
        spanId: 'pi-span',
        source: 'pi-synth',
        traceId: 'trace-pi',
        attributes: { 'pi.session.id': 'synth-session', 'gen_ai.usage.input_tokens': 4821 },
      }),
      rawSpan({
        spanId: 'codex-span',
        source: 'codex-synth',
        traceId: 'trace-codex',
        attributes: { 'codex.tool.name': 'synth-tool', 'gen_ai.usage.input_tokens': 100 },
      }),
    ])
    expect(attributed.get('pi-span')).toBe('pi')
    expect(attributed.get('codex-span')).toBe('codex')
  })

  it('still attributes Antigravity corpus spans to antigravity', () => {
    const registry = new AdapterRegistry(ADAPTERS)
    const spans = [
      rawSpan({
        spanId: 'agy-vote',
        source: 'agy',
        traceId: 't-agy',
        attributes: antigravitySpan as Record<string, unknown>,
      }),
    ]
    expect(registry.attribute(spans).get('agy-vote')).toBe('antigravity')
  })

  it('still attributes a Copilot fingerprint span to copilot', () => {
    const registry = new AdapterRegistry(ADAPTERS)
    const spans = [
      rawSpan({
        spanId: 'copilot-vote',
        source: 'copilot-vscode',
        traceId: 't-copilot',
        attributes: {
          'copilot_chat.chat_session_id': '08551cf5-b064-4095-9552-8a9a0a0f78d2',
          'gen_ai.agent.name': 'antigravity',
          'gen_ai.usage.input_tokens': 251_976,
          'gen_ai.usage.cache_read.input_tokens': 243_910,
          'gen_ai.usage.output_tokens': 1_000,
        },
      }),
    ]
    expect(registry.attribute(spans).get('copilot-vote')).toBe('copilot')
  })
})

describe('opencodeAdapter claims contract 3 only (D9)', () => {
  it.each(SPAN_KINDS.map((kind) => ({ kind })))('scores a contract-3 $kind span above the registry threshold', ({ kind }) => {
    expect(
      opencodeAdapter.detect(rawSpan({ spanId: `claim-${kind}`, attributes: spanAttributes(kind) })),
    ).toBeGreaterThanOrEqual(0.6)
  })

  it.each([
    { label: 'contract 2', version: '2' },
    { label: 'an unknown version', version: '9999-future' },
  ])('scores a span naming $label zero so no vote can claim it', ({ version }) => {
    const attributes = spanAttributes('step', { [OPENCODE_KEYS.contractVersion]: version })
    expect(opencodeAdapter.detect(rawSpan({ spanId: 'claim-old', attributes }))).toBe(0)
  })

  it('scores a span with no contract version zero', () => {
    const attributes = without(spanAttributes('step'), OPENCODE_KEYS.contractVersion)
    expect(opencodeAdapter.detect(rawSpan({ spanId: 'claim-missing', attributes }))).toBe(0)
  })

  it('never lets the source name change the score (R6.2)', () => {
    const attributes = spanAttributes('step')
    const namedOpenCode = rawSpan({ spanId: 'claim-src-a', source: 'opencode', attributes })
    const namedOtherwise = rawSpan({ spanId: 'claim-src-b', source: 'some-other-runtime', attributes })
    expect(opencodeAdapter.detect(namedOpenCode)).toBe(opencodeAdapter.detect(namedOtherwise))
    expect(opencodeAdapter.detect(namedOpenCode)).toBeGreaterThanOrEqual(0.6)
  })
})

describe('native OpenCode OTel is no longer claimed (D6)', () => {
  const nativeShapes = [
    {
      label: 'an OpenInference LLM span',
      attributes: {
        'openinference.span.kind': 'LLM',
        'llm.model_name': 'muse-spark-1.3-contributor',
        'llm.token_count.prompt': 616,
        'llm.token_count.completion': 242,
      },
    },
    {
      label: 'an AI SDK gen_ai model span',
      attributes: {
        'gen_ai.operation.name': 'ai.streamText',
        'gen_ai.request.model': 'muse-spark-1.3-contributor',
        'gen_ai.usage.input_tokens': 512,
        'gen_ai.usage.output_tokens': 96,
      },
    },
    {
      label: 'a legacy ai.* model span',
      attributes: {
        'ai.usage.promptTokens': 512,
        'ai.usage.completionTokens': 96,
      },
    },
  ]

  it.each(nativeShapes)('does not claim $label', ({ attributes }) => {
    expect(opencodeAdapter.detect(rawSpan({ spanId: 'native-detect', attributes }))).toBe(0)
  })

  it.each(nativeShapes)('does not attribute $label to opencode', ({ attributes }) => {
    const span = otlpSpan('native-span', attributes, {
      name: 'opencode.llm',
      kind: 'client',
      resource: { 'service.name': 'opencode' },
    })
    const attributed = new AdapterRegistry(ADAPTERS).attribute([toRawSpan(span)])
    expect(attributed.get('native-span')).not.toBe('opencode')
    withStore((store) => {
      ingestBatch([span], store)
      expect(store.get('native-span')?.harness).not.toBe('opencode')
    })
  })

  it('quarantines a native OpenCode Effect app span rather than filing a record', () => {
    withStore((store) => {
      const span = otlpSpan('native-app', { 'opencode.session.id': 'ses_native_app' }, { name: 'Session.load' })
      const outcome = ingestBatch([span], store)
      expect(outcome.accepted).toBe(0)
      expect(store.get('native-app')).toBeUndefined()
      expect(store.getQuarantine('native-app')).toBeDefined()
    })
  })
})

describe('contract-3 golden body, consent on (run A)', () => {
  it('ingests all six spans as records and quarantines none', () => {
    withStore((store) => {
      expect(ingestGolden(store, CONSENT_ON).outcome).toEqual({ accepted: 6, quarantined: 0, rejected: 0 })
    })
  })

  it('normalizes the request span to a parentless agent.invoke root timed at 13367 ms', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      const root = onlySpan(spans, (span) => kindOf(span) === 'request', 'request span')
      expect(recordFor(store, root.spanId)).toMatchObject({
        harness: 'opencode',
        op: 'agent.invoke',
        parentSpanId: null,
        durationMs: 13367,
        sessionId: RUN_A_SESSION,
      })
    })
  })

  it('splits the first step into disjoint token classes and measures all four', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      const step = stepWithInput(spans, STEP_ONE_INPUT)
      const record = recordFor(store, step.spanId)

      expect(record.op).toBe('llm.invoke')
      // output 69 + reasoning 957 = 1026; reportedInput 10935 + 1521 + 0 = 12456.
      expect(record.tokens).toMatchObject({
        freshInput: 10935,
        cacheRead: 1521,
        cacheCreation: 0,
        output: 1026,
        reasoning: 957,
        reportedInput: 12456,
      })
      for (const metric of ['token_usage', 'output', 'cache_read', 'cache_creation']) {
        expect(record.measurability?.[metric], metric).toBe('measured')
      }
    })
  })

  it('parents every step to the request root', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      const root = onlySpan(spans, (span) => kindOf(span) === 'request', 'request span')
      for (const step of spansOfKind(spans, 'step')) {
        expect(recordFor(store, step.spanId).parentSpanId, step.spanId).toBe(root.spanId)
      }
    })
  })

  it('carries each step cost as the harness reported it, a reported zero included', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      for (const step of spansOfKind(spans, 'step')) {
        expect(recordFor(store, step.spanId).cost, step.spanId).toEqual({
          basis: 'harness',
          status: 'priced',
          value: 0,
          currency: 'USD',
        })
      }
    })
  })

  it('files the skill and read calls as tool.invoke records named after their tool, under the step that made them', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      const stepOne = stepWithInput(spans, STEP_ONE_INPUT)
      const stepTwo = stepWithInput(spans, STEP_TWO_INPUT)

      expect(recordFor(store, toolNamed(spans, 'skill').spanId)).toMatchObject({
        op: 'tool.invoke',
        name: 'skill',
        parentSpanId: stepOne.spanId,
      })
      expect(recordFor(store, toolNamed(spans, 'read').spanId)).toMatchObject({
        op: 'tool.invoke',
        name: 'read',
        parentSpanId: stepTwo.spanId,
      })
    })
  })

  it('gives each tool call its real duration', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      for (const tool of spansOfKind(spans, 'tool')) {
        expect(recordFor(store, tool.spanId).durationMs, tool.spanId).toBeGreaterThan(0)
      }
    })
  })

  it('claims no token counts for a tool call', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      const record = recordFor(store, toolNamed(spans, 'read').spanId)
      expect(record.tokens).toMatchObject({ freshInput: 0, cacheRead: 0, cacheCreation: 0, output: 0 })
      expect(record.measurability?.token_usage).toMatchObject({ availability: 'not_measurable' })
    })
  })

  it('keeps the skill on the step that called the skill tool and on no other step', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      const stepOne = stepWithInput(spans, STEP_ONE_INPUT)
      const stepTwo = stepWithInput(spans, STEP_TWO_INPUT)

      expect(rawOf(recordFor(store, stepOne.spanId))).toMatchObject({
        [SKILLS_INVOKED_KEY]: ['kw-probe-skill'],
      })
      expect(rawOf(recordFor(store, stepTwo.spanId))).not.toHaveProperty(SKILLS_INVOKED_KEY)
    })
  })

  it('hands the skills listing to the skills reader as the listing the step carries', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      const stepOne = stepWithInput(spans, STEP_ONE_INPUT)
      const record = recordFor(store, stepOne.spanId)
      const listed = JSON.parse(textAttribute(stepOne, OPENCODE_KEYS.contextSkills)) as string[]

      const listing = readListingOf(record, record.parts)
      expect(listing, 'the step carries a measurable skills listing').toMatchObject({ measurable: true })
      const names = 'skills' in listing ? listing.skills.map((skill) => skill.name) : []
      expect([...names].sort()).toEqual([...listed].sort())
    })
  })

  it('files each step system prompt and tool definitions as parts of that step', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      for (const step of spansOfKind(spans, 'step')) {
        const record = recordFor(store, step.spanId)
        expect(partTexts(record, 'system_prompt'), step.spanId).toEqual([
          textAttribute(step, OPENCODE_KEYS.contextSystem),
        ])
        expect(partTexts(record, 'tool_definitions'), step.spanId).toEqual([
          textAttribute(step, OPENCODE_KEYS.contextTools),
        ])
        expect(record.content.system_prompt, step.spanId).toBe(textAttribute(step, OPENCODE_KEYS.contextSystem))
      }
    })
  })

  it('files the user prompt as conversation history on the request root', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      const root = onlySpan(spans, (span) => kindOf(span) === 'request', 'request span')
      const record = recordFor(store, root.spanId)
      expect(partTexts(record, 'conversation_history')).toEqual([textAttribute(root, OPENCODE_KEYS.promptText)])
    })
  })

  it('keeps no opencode.context.* key and no prompt text key in record.raw', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      for (const span of spans) {
        const keys = Object.keys(rawOf(recordFor(store, span.spanId)))
        const offending = keys.filter(
          (key) => key.startsWith('opencode.context.') || key === OPENCODE_KEYS.promptText,
        )
        expect(offending, span.spanId).toEqual([])
      }
    })
  })

  it('keeps the system prompt and the user prompt text out of record.raw entirely', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      for (const span of spans) {
        const serialized = JSON.stringify(rawOf(recordFor(store, span.spanId)))
        expect(serialized, span.spanId).not.toContain(SYSTEM_SENTINEL)
        expect(serialized, span.spanId).not.toContain(PROMPT_SENTINEL)
      }
    })
  })

  it('keeps the session label on the request root raw', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      const root = onlySpan(spans, (span) => kindOf(span) === 'request', 'request span')
      expect(rawOf(recordFor(store, root.spanId))).toMatchObject({
        [SESSION_LABEL_KEY]: textAttribute(root, OPENCODE_KEYS.sessionLabel),
      })
    })
  })

  it('bridges the step model id onto gen_ai.request.model', () => {
    withStore((store) => {
      const { spans } = ingestGolden(store, CONSENT_ON)
      const stepOne = stepWithInput(spans, STEP_ONE_INPUT)
      expect(rawOf(recordFor(store, stepOne.spanId))).toMatchObject({
        'gen_ai.request.model': 'muse-spark-1.3-contributor',
      })
    })
  })
})

describe('contract-3 golden body, consent off (run A)', () => {
  it('yields the same records as consent on, with no content on any of them', () => {
    withStore((onStore) => {
      withStore((offStore) => {
        const on = ingestGolden(onStore, CONSENT_ON)
        const off = ingestGolden(offStore, CONSENT_OFF)
        expect(off.outcome).toEqual({ accepted: 6, quarantined: 0, rejected: 0 })

        for (const span of on.spans) {
          const withContent = recordFor(onStore, span.spanId)
          const withoutContent = recordFor(offStore, span.spanId)
          expect(metadataOf(withoutContent), span.spanId).toEqual(metadataOf(withContent))
          expect(withoutLabel(rawOf(withoutContent)), span.spanId).toEqual(withoutLabel(rawOf(withContent)))
          expect(withoutContent.parts ?? [], span.spanId).toEqual([])
          expect(withoutContent.content, span.spanId).toEqual({})
        }
      })
    })
  })
})

describe('contract-3 step cost (D4)', () => {
  const PUBLISHED_MODEL = 'claude-opus-4-6'

  beforeAll(async () => {
    await loadPricing()
  })

  it('carries a reported opencode.cost verbatim as a harness-priced figure', () => {
    const record = normalizeSpan('cost-reported', spanAttributes('step', { [OPENCODE_KEYS.cost]: 0.0123 }))
    expect(record.cost).toEqual({ basis: 'harness', status: 'priced', value: 0.0123, currency: 'USD' })
  })

  it('gives a step with no opencode.cost an unknown, no-rate cost even when its model has a published rate', () => {
    expect(getModelCosts(PUBLISHED_MODEL), 'precondition: the model has a published rate').not.toBeNull()
    const record = normalizeSpan(
      'cost-absent',
      spanAttributes('step', { [OPENCODE_KEYS.modelId]: PUBLISHED_MODEL, [OPENCODE_KEYS.modelProvider]: 'anthropic' }),
    )
    expect(record.cost).toEqual({ basis: 'unknown', status: 'no_rate' })
  })
})

describe('contract-3 tool span (B1 port)', () => {
  it('keeps a tool span a tool call even when it also names a model', () => {
    const record = normalizeSpan(
      'tool-with-model',
      spanAttributes('tool', { [OPENCODE_KEYS.modelId]: 'claude-opus-4-6', [OPENCODE_KEYS.modelProvider]: 'anthropic' }),
    )
    expect(record.op).toBe('tool.invoke')
    expect(opencodeAdapter.validate(record)).toBeUndefined()
  })
})

describe('contract-3 counters: measurability follows the parsed value, not the key', () => {
  // The readable input is the control: it proves the contract-3 counters are read at
  // all, so the unreadable value is the only thing these two tests isolate.
  it('does not declare output measured when the output counter is unreadable', () => {
    const record = normalizeSpan(
      'usage-bad-output',
      spanAttributes('step', { [OPENCODE_KEYS.usageInput]: 100, [OPENCODE_KEYS.usageOutput]: 'n/a' }),
    )
    expect(record.tokens.freshInput, 'readable input is read').toBe(100)
    expect(record.measurability?.output).toMatchObject({ availability: 'not_measurable' })
    expect(record.tokens.output).toBe(0)
  })

  it('does not declare a cache class measured when its counter is unreadable', () => {
    const record = normalizeSpan(
      'usage-bad-cache',
      spanAttributes('step', { [OPENCODE_KEYS.usageInput]: 100, [OPENCODE_KEYS.usageCacheRead]: 'n/a' }),
    )
    expect(record.tokens.freshInput, 'readable input is read').toBe(100)
    expect(record.measurability?.cache_read).toMatchObject({ availability: 'not_measurable' })
    expect(record.tokens.cacheRead).toBe(0)
  })

  it('keeps a reported zero cache read measured', () => {
    const record = normalizeSpan(
      'usage-zero-cache',
      spanAttributes('step', {
        [OPENCODE_KEYS.usageInput]: 100,
        [OPENCODE_KEYS.usageOutput]: 0,
        [OPENCODE_KEYS.usageCacheRead]: 0,
      }),
    )
    expect(record.measurability?.cache_read).toBe('measured')
    expect(record.measurability?.output).toBe('measured')
    expect(record.tokens.cacheRead).toBe(0)
  })

  it('declares counters not measurable on a step that reports none, rather than fabricating zeros', () => {
    const record = normalizeSpan('usage-none', spanAttributes('step'))
    expect(record.op).toBe('llm.invoke')
    expect(record.tokens.freshInput).toBe(0)
    expect(record.measurability?.token_usage).toMatchObject({ availability: 'not_measurable' })
    expect(record.measurability?.cache_read).toMatchObject({ availability: 'not_measurable' })
  })
})

describe('contract-3 model identity bridge', () => {
  it('bridges opencode.model.id onto gen_ai.request.model', () => {
    const record = normalizeSpan('bridge-id', spanAttributes('step', { [OPENCODE_KEYS.modelId]: 'claude-opus-4-6' }))
    expect(rawOf(record)).toMatchObject({ 'gen_ai.request.model': 'claude-opus-4-6' })
  })

  it('never overwrites an already-canonical gen_ai.request.model', () => {
    const record = normalizeSpan(
      'bridge-canonical-wins',
      spanAttributes('step', {
        'gen_ai.request.model': 'already-canonical-model',
        [OPENCODE_KEYS.modelId]: 'claude-opus-4-6',
      }),
    )
    expect(rawOf(record)).toMatchObject({ 'gen_ai.request.model': 'already-canonical-model' })
  })

  it('bridges nothing when the model id is absent', () => {
    const record = normalizeSpan(
      'bridge-absent',
      without(spanAttributes('step'), OPENCODE_KEYS.modelId),
    )
    expect(rawOf(record)).not.toHaveProperty('gen_ai.request.model')
  })

  it('bridges nothing when the model id is a number, including 0', () => {
    for (const value of [0, 42]) {
      const record = normalizeSpan(`bridge-numeric-${value}`, spanAttributes('step', { [OPENCODE_KEYS.modelId]: value }))
      expect(rawOf(record), String(value)).not.toHaveProperty('gen_ai.request.model')
    }
  })
})

// T7 (R1, D6): the context window of a contract-3 step. A reported
// `opencode.model.context_limit` is the `reported` window; without it the KyberDash
// model catalog answers by `opencode.model.id` with source `catalog`; an unknown
// model is a named not-measurable reason, never a guessed window.
describe('opencode context window on contract 3 (T7, D6)', () => {
  const windowStep = (over: Record<string, unknown> = {}): Record<string, unknown> =>
    spanAttributes('step', {
      [OPENCODE_KEYS.modelId]: 'zz-synth-alpha-1',
      [OPENCODE_KEYS.modelProvider]: 'synth',
      [OPENCODE_KEYS.contextSystem]: 'you are a build agent',
      ...over,
    })

  const seededStore = (): CanonStore => {
    const store = new CanonStore(':memory:')
    refreshModelWindowCatalog(store, {
      readVendor: (vendor) =>
        parseVendorCatalogDocument(
          readModelCatalogFixture(vendor === 'synth-vendor-a' ? 'vendor-a-initial.json' : 'vendor-b-initial.json'),
        ),
      now: () => '2026-10-01T10:00:00.000Z',
    })
    return store
  }

  it('reads opencode.model.context_limit as a reported window', () => {
    const record = normalizeSpan('win-reported', windowStep({ [OPENCODE_KEYS.contextLimit]: 400_000 }))
    expect(contextLimitOf([record])).toEqual({ contextLimit: 400_000, contextLimitSource: 'reported' })
  })

  it('a reported window outranks the catalog row for the same model', () => {
    const store = seededStore()
    const record = normalizeSpan('win-both', windowStep({ [OPENCODE_KEYS.contextLimit]: 400_000 }))
    expect(resolveContextWindowForModel([record], 'zz-synth-alpha-1', store)).toEqual({
      measurable: true,
      contextLimit: 400_000,
      contextLimitSource: 'reported',
    })
    store.close()
  })

  it('falls back to the catalog window by opencode.model.id, labelled catalog', () => {
    const store = seededStore()
    const record = normalizeSpan('win-catalog', windowStep())
    expect(catalogWindowForRecords([record], store)).toEqual({
      contextLimit: 128_000,
      contextLimitSource: 'catalog',
    })
    store.close()
  })

  it('an unknown model id has no window and names why', () => {
    const store = seededStore()
    const record = normalizeSpan('win-unknown', windowStep({ [OPENCODE_KEYS.modelId]: 'zz-synth-missing-404' }))
    // Precondition: the window reader only reads model calls, so the step must be one.
    expect(record.op, 'step normalizes to a model call').toBe('llm.invoke')
    expect(catalogWindowForRecords([record], store)).toBeUndefined()
    const resolved = resolveContextWindowForModel([record], 'zz-synth-missing-404', store)
    expect(resolved.measurable).toBe(false)
    if (!resolved.measurable) expect(resolved.reason).toContain('No exact model catalog row')
    store.close()
  })

  it('a non-positive reported limit is not a window and does not fall to the catalog', () => {
    const store = seededStore()
    const record = normalizeSpan('win-bad', windowStep({ [OPENCODE_KEYS.contextLimit]: 0 }))
    expect(resolveContextWindowForModel([record], 'zz-synth-alpha-1', store).measurable).toBe(false)
    store.close()
  })
})
