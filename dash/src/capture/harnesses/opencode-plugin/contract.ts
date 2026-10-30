// Wire contract v3 for the OpenCode V2 plugin: one OTLP trace per OpenCode session, with
// request, step and tool spans.
//
// This module is the single source of contract-3 strings, shared by the plugin (bundled) and
// the canon adapter. It is a LEAF: it may import only `node:` built-ins, so the plugin bundle
// never drags engine code in and canon never depends on the plugin
// (see src/architecture/opencode-leaves.test.ts).

import { createHash } from 'node:crypto'

/** The wire contract version the plugin stamps on every span and the adapter checks. */
export const OPENCODE_CONTRACT_VERSION = '3'

/**
 * Span attribute keys. `skillsInvoked` and `sessionLabel` repeat the canon literals
 * (`SKILLS_INVOKED_KEY`, `SESSION_LABEL_KEY`) because a leaf cannot value-import canon.
 */
export const OPENCODE_KEYS = {
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
} as const

/** The three span kinds of one session trace. */
export type SpanKind = 'request' | 'step' | 'tool'

/** Span names, one per kind. */
export const SPAN_NAMES = {
  request: 'opencode.request',
  step: 'opencode.step',
  tool: 'opencode.tool',
} as const satisfies Record<SpanKind, string>

function sha256Hex(text: string): string {
  return createHash('sha256').update(text).digest('hex')
}

/** The trace id of a session: the first 32 hex of SHA-256 over `opencode|trace|<session>`. */
export function traceIdFor(sessionId: string): string {
  return sha256Hex(`opencode|trace|${sessionId}`).slice(0, 32)
}

/**
 * The span id for a kind and anchor within a session: the first 16 hex of SHA-256 over
 * `opencode|<kind>|<session>|<anchor>`. The anchor is the request's execution-started event id,
 * a step's message id, or a tool's call id.
 */
export function spanIdFor(kind: SpanKind, sessionId: string, anchor: string): string {
  return sha256Hex(`opencode|${kind}|${sessionId}|${anchor}`).slice(0, 16)
}
