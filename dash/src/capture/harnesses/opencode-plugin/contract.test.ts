// Wire contract v3 for the OpenCode V2 plugin: one OTLP trace per OpenCode session, with request,
// step and tool spans. These tests pin the version, the attribute keys, the span names and the
// id derivation, and they check the golden OTLP bodies against that derivation. The contract
// module is written by the next task, so this file fails until contract.ts exists.

import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import { describe, expect, it } from 'vitest'

import {
  OPENCODE_CONTRACT_VERSION,
  OPENCODE_KEYS,
  SPAN_NAMES,
  spanIdFor,
  traceIdFor,
  type SpanKind,
} from './contract.js'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const FIXTURES = path.join(HERE, 'fixtures', 'v2')
const SESSION = 'ses_e74c398c7ffeVajzhRWZNgfR2n'
const KINDS: readonly SpanKind[] = ['request', 'step', 'tool']

type WireValue =
  | { stringValue: string }
  | { intValue: string }
  | { doubleValue: number }
  | { arrayValue: { values: Array<{ stringValue: string }> } }

type WireSpan = {
  traceId: string
  spanId: string
  parentSpanId?: string
  name: string
  kind: string
  startTimeUnixNano: string
  endTimeUnixNano: string
  attributes: Array<{ key: string; value: WireValue }>
  status?: { code: string }
}

type WireBody = {
  resourceSpans: Array<{ scopeSpans: Array<{ spans: WireSpan[] }> }>
}

function readFixture(name: string): string {
  return readFileSync(path.join(FIXTURES, name), 'utf8')
}

/** The golden bodies are repository-owned fixtures, written by the sanitized-fixture build. */
function goldenSpans(name: string): WireSpan[] {
  const body = JSON.parse(readFixture(name)) as WireBody
  return body.resourceSpans.flatMap((resource) => resource.scopeSpans.flatMap((scope) => scope.spans))
}

function requiredString(span: WireSpan, key: string): string {
  const attribute = span.attributes.find((candidate) => candidate.key === key)
  if (attribute === undefined || !('stringValue' in attribute.value)) {
    throw new Error(`${span.name} has no string attribute ${key}`)
  }
  return attribute.value.stringValue
}

function kindOf(span: WireSpan): SpanKind {
  const kind = requiredString(span, OPENCODE_KEYS.spanKind)
  if (kind === 'request' || kind === 'step' || kind === 'tool') return kind
  throw new Error(`${span.name} carries an unknown span kind: ${kind}`)
}

/** The session.execution.started event id in the sanitized event fixture: the request anchor. */
function executionStartedId(sessionId: string): string {
  const lines = readFixture('runA.events.jsonl')
    .split('\n')
    .filter((line) => line.trim() !== '')
  for (const line of lines) {
    const record = JSON.parse(line) as {
      payload?: { id?: unknown; type?: unknown; data?: { sessionID?: unknown } }
    }
    if (
      record.payload?.type === 'session.execution.started' &&
      record.payload.data?.sessionID === sessionId &&
      typeof record.payload.id === 'string'
    ) {
      return record.payload.id
    }
  }
  throw new Error(`no session.execution.started event for ${sessionId}`)
}

/** Every module specifier the file imports or re-exports, including type-only and dynamic imports. */
function importSpecifiers(source: string): string[] {
  const file = ts.createSourceFile('contract.ts', source, ts.ScriptTarget.Latest, true)
  const found: string[] = []
  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && ts.isStringLiteral(node.moduleSpecifier)) {
      found.push(node.moduleSpecifier.text)
    } else if (
      ts.isExportDeclaration(node) &&
      node.moduleSpecifier !== undefined &&
      ts.isStringLiteral(node.moduleSpecifier)
    ) {
      found.push(node.moduleSpecifier.text)
    } else if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments[0] !== undefined &&
      ts.isStringLiteral(node.arguments[0])
    ) {
      found.push(node.arguments[0].text)
    }
    ts.forEachChild(node, visit)
  }
  visit(file)
  return found
}

describe('the OpenCode wire contract v3', () => {
  it('the wire contract version is the string "3"', () => {
    expect(OPENCODE_CONTRACT_VERSION).toBe('3')
  })

  it('the attribute keys are the contract strings', () => {
    expect(OPENCODE_KEYS).toEqual({
      contractVersion: 'opencode.contract_version',
      sessionId: 'opencode.session.id',
      spanKind: 'opencode.span.kind',
      agent: 'opencode.agent',
      version: 'opencode.version',
      promptCount: 'opencode.prompt.count',
      executionOutcome: 'opencode.execution.outcome',
      executionReason: 'opencode.execution.reason',
      messageId: 'opencode.message.id',
      modelId: 'opencode.model.id',
      modelProvider: 'opencode.model.provider',
      requestKind: 'opencode.request.kind',
      stepFinish: 'opencode.step.finish',
      usageInput: 'opencode.usage.input',
      usageOutput: 'opencode.usage.output',
      usageReasoning: 'opencode.usage.reasoning',
      usageCacheRead: 'opencode.usage.cache_read',
      usageCacheWrite: 'opencode.usage.cache_write',
      cost: 'opencode.cost',
      contextLimit: 'opencode.model.context_limit',
      skillsInvoked: 'kyber.skills.invoked',
      toolName: 'opencode.tool.name',
      toolCallId: 'opencode.tool.call_id',
      toolStatus: 'opencode.tool.status',
      toolErrorType: 'opencode.tool.error_type',
      toolInputBytes: 'opencode.tool.input_bytes',
      toolResultBytes: 'opencode.tool.result_bytes',
      toolTiming: 'opencode.tool.timing',
      skillId: 'opencode.skill.id',
      promptText: 'opencode.prompt.text',
      sessionLabel: 'kyber.session.label',
      contextSystem: 'opencode.context.system',
      contextTools: 'opencode.context.tools',
      contextSkills: 'opencode.context.skills',
    })
  })

  it('the span names are opencode.request, opencode.step and opencode.tool', () => {
    expect(SPAN_NAMES).toEqual({
      request: 'opencode.request',
      step: 'opencode.step',
      tool: 'opencode.tool',
    })
  })

  it('a trace id is 32 lowercase hex characters', () => {
    expect(traceIdFor(SESSION)).toMatch(/^[0-9a-f]{32}$/)
  })

  it('a span id is 16 lowercase hex characters for every kind', () => {
    for (const kind of KINDS) {
      expect(spanIdFor(kind, SESSION, 'anchor-value'), `kind ${kind}`).toMatch(/^[0-9a-f]{16}$/)
    }
  })

  it('the trace id and the request span id are the first hex of SHA-256 over their derivation strings', () => {
    // Vectors computed outside the code under test:
    //   sha256("opencode|trace|ses_e74c398c7ffeVajzhRWZNgfR2n")[:32]
    //   sha256("opencode|request|ses_e74c398c7ffeVajzhRWZNgfR2n|evt_18b3c674c001sqPP2uumgCfXPD")[:16]
    expect(traceIdFor(SESSION)).toBe('8904c0d412ca24c747721f714f91a091')
    expect(spanIdFor('request', SESSION, 'evt_18b3c674c001sqPP2uumgCfXPD')).toBe('6153c96c6e4e5e93')
  })

  it('equal input derives equal ids', () => {
    expect(traceIdFor(SESSION)).toBe(traceIdFor(SESSION))
    expect(spanIdFor('step', SESSION, 'msg_18b3c6754001zOaRnFAQ8wClXP')).toBe(
      spanIdFor('step', SESSION, 'msg_18b3c6754001zOaRnFAQ8wClXP'),
    )
  })

  it('a span id differs by kind for the same anchor', () => {
    const ids = KINDS.map((kind) => spanIdFor(kind, SESSION, 'same-anchor'))
    expect(new Set(ids).size).toBe(KINDS.length)
  })

  it('a span id differs by anchor for the same kind', () => {
    const ids = ['anchor-a', 'anchor-b', 'anchor-c'].map((anchor) => spanIdFor('tool', SESSION, anchor))
    expect(new Set(ids).size).toBe(3)
  })

  it('contract.ts imports nothing but node: built-ins', () => {
    const source = readFileSync(path.join(HERE, 'contract.ts'), 'utf8')
    const offenders = importSpecifiers(source).filter((specifier) => !specifier.startsWith('node:'))
    expect(offenders, `contract.ts must stay import-free; found ${offenders.join(', ')}`).toEqual([])
  })

  for (const fixture of ['otlp-v3-runA.consent-on.json', 'otlp-v3-runA.consent-off.json']) {
    it(`the golden body ${fixture} derives every span and parent id from the contract`, () => {
      const requestAnchor = executionStartedId(SESSION)
      const requestId = spanIdFor('request', SESSION, requestAnchor)
      for (const span of goldenSpans(fixture)) {
        const kind = kindOf(span)
        expect(span.traceId, `${kind} trace id`).toBe(traceIdFor(SESSION))
        expect(span.name, `${kind} span name`).toBe(SPAN_NAMES[kind])
        if (kind === 'request') {
          expect(span.spanId, 'request span id').toBe(requestId)
          expect(span.parentSpanId, 'request is parentless').toBeUndefined()
        } else if (kind === 'step') {
          const messageId = requiredString(span, OPENCODE_KEYS.messageId)
          expect(span.spanId, `step ${messageId} span id`).toBe(spanIdFor('step', SESSION, messageId))
          expect(span.parentSpanId, `step ${messageId} parent`).toBe(requestId)
        } else {
          const callId = requiredString(span, OPENCODE_KEYS.toolCallId)
          const messageId = requiredString(span, OPENCODE_KEYS.messageId)
          expect(span.spanId, `tool ${callId} span id`).toBe(spanIdFor('tool', SESSION, callId))
          expect(span.parentSpanId, `tool ${callId} parent`).toBe(spanIdFor('step', SESSION, messageId))
        }
      }
    })
  }
})
