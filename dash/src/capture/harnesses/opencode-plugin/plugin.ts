// The OpenCode V2 capture engine: bus events and hook inputs in, finished contract-3 spans out.
//
// OpenCode 2.x gives a plugin two feeds. The bus (`ctx.event.subscribe()`) carries what happened
// and when: executions, steps, tool calls, skills, session metadata. The hooks (`prompt`,
// `context`, `model.request`, `tool.execute.before/after`) carry what only the plugin can see:
// the prompt text, the assembled context, the request kind. This engine joins the two into one
// trace per session:
//
//   request  one execution: execution.started to its terminal event
//   step     one model call: step.started to step.ended, child of its request
//   tool     one tool call: tool.called to tool.success/failed, child of its step (by message id)
//
// A span is emitted when it is complete and never before. Ids are deterministic (`contract.ts`),
// so a re-sent span is the same span. Absent stays absent: nothing here turns a missing value into
// 0 or ''.
//
// Ownership. One OpenCode service can host sessions of several project directories, and every
// instance of this plugin sees the whole bus. An instance emits a session only once one of its own
// hooks fired for it, or an event for it names this instance's directory. Events for sessions not
// yet owned wait in a small bounded buffer and replay when ownership is established.
//
// Bounds. Sessions, open steps and tools, remembered finished tools, held foreign events, and each
// text field are all capped; the oldest goes first. An open execution with no event for 30 minutes
// is emitted once as outcome `unknown`, as is each one still open when the plugin is cleaned up.
//
// Consent. Content (prompt text, session label, system prompt, tools, skills listing) is kept only
// when consent is on as the event is seen, and sent only if it is still on as the span is emitted.
// Metadata always flows.
//
// The engine owns no timers, files, sockets or globals: the caller hands it a span sink, a consent
// check and a clock (`entry.ts`). It never throws; a failure goes to `onError`.

import { normalize } from 'node:path'

import {
  MAX_FIELD_BYTES,
  MAX_PROMPT_BYTES,
  capContent,
  capUtf8,
  costOf,
  detachedCopy,
  epochMs,
  isRecord,
  jsonBytes,
  labelOf,
  mapRequestSpan,
  mapStepSpan,
  mapToolSpan,
  systemTextOf,
  textOf,
  textPartsBytes,
  toolsJsonOf,
  wholeOf,
  type RequestOutcome,
  type StepContent,
  type StepUsage,
} from './mapper.js'
import { normalizeSkillId, parseAvailableSkills } from './skills-block.js'
import type { OtlpWireSpan } from './transport/otlp-sender.js'

/** The plugin id dash CLI writes into opencode's `plugins` config keys. */
export const OPENCODE_PLUGIN_ID = 'kyberdash-opencode'

/** Opencode config keys the dash CLI manages for this plugin (never hand-edited paths). */
export const OPENCODE_PLUGIN_CONFIG_KEYS = {
  /** v2 `plugins` list in `opencode.jsonc`; v1 `plugin` entries resolve to the same file. */
  plugins: 'plugins',
} as const

/** An execution with no event for this long is emitted once as outcome `unknown`. */
export const INACTIVITY_FLUSH_MS = 30 * 60 * 1000

/** Sessions kept at once; past it the least recently active is forgotten. */
const MAX_SESSIONS = 64
/** Events for sessions this instance does not own yet, held in case a hook claims them. */
const MAX_HELD_EVENTS = 256
/** Open steps and tools remembered per session. */
const MAX_OPEN_STEPS = 16
const MAX_OPEN_TOOLS = 256
/** Finished tool call ids remembered per session so a late duplicate is not emitted twice. */
const MAX_DONE_TOOLS = 512
/** Skill ids waiting for the next step, per session. */
const MAX_PENDING_SKILLS = 64
/** Skill ids one step keeps; past it the oldest is forgotten. */
const MAX_STEP_SKILLS = 64

export type CaptureEngineOptions = {
  /** This instance's project directory; absent when the host did not say. */
  directory?: string
  /** Receives each finished span; must not throw (a throw is reported and the span is lost). */
  emit: (span: OtlpWireSpan) => void
  /** Whether content may flow right now. Called per event. */
  consent: () => boolean
  /** Wall clock in epoch milliseconds. */
  now?: () => number
  /** Tap for failures the engine swallowed. Itself runs swallowed. */
  onError?: (where: string, error: unknown) => void
}

export type CaptureEngine = {
  /** One bus event. */
  onEvent: (raw: unknown) => void
  onPrompt: (raw: unknown) => void
  onContext: (raw: unknown) => void
  onModelRequest: (raw: unknown) => void
  onToolBefore: (raw: unknown) => void
  onToolAfter: (raw: unknown) => void
  /** Emits every execution idle for `INACTIVITY_FLUSH_MS` as `unknown`. */
  sweep: () => void
  /** Milliseconds until the next execution falls idle; undefined when none is open. */
  nextSweepDelayMs: () => number | undefined
  /** Emits every still-open execution once as `unknown` (cleanup). */
  flushOpenExecutions: () => void
}

// ---------------------------------------------------------------------------------------------
// Input parsing: every host value is `unknown` until narrowed here

// The guards themselves live in `mapper.ts`, shared with the wire mapping. `text` is an identifier
// or an opaque string kept as given; `label` is a host string cut to `MAX_FIELD_BYTES`.
const text = textOf
const label = labelOf
const whole = wholeOf

/**
 * A directory reduced to one spelling: separators collapsed, `.` and `..` resolved and no trailing
 * separator. It is text only: the engine reads no file system, so letter case and symlinks are not
 * resolved, and two spellings that differ only there stay different.
 */
export function normalizeDirectory(directory: string): string {
  const cleaned = normalize(directory)
  const trimmed = cleaned.replace(/[\\/]+$/, '')
  // The root (`/`, `C:\`) is all separator or ends in a drive colon; keep it as normalize left it.
  return trimmed === '' || /^[A-Za-z]:$/.test(trimmed) ? cleaned : trimmed
}

/** What one bus event means to this plugin, reduced to the small values it needs. */
type Fact =
  | { kind: 'session.created'; version?: string }
  | { kind: 'session.renamed'; title?: string }
  | { kind: 'execution.started' }
  | { kind: 'execution.ended'; outcome: Exclude<RequestOutcome, 'unknown'>; reason?: string }
  | { kind: 'step.started'; messageId: string; agent?: string; modelId?: string; modelProvider?: string }
  | { kind: 'step.ended'; messageId: string; finish?: string; cost?: number; usage: StepUsage }
  | { kind: 'tool.input'; callId: string; name?: string }
  | { kind: 'tool.called'; callId: string; messageId?: string; inputBytes?: number; inputId?: string }
  | { kind: 'tool.succeeded'; callId: string; messageId?: string; resultBytes?: number }
  | { kind: 'tool.failed'; callId: string; messageId?: string; errorType?: string }
  | { kind: 'skill.activated'; skillId: string }
  | { kind: 'other' }

type BusEvent = {
  sessionId: string
  /** The host's event id; the request span's anchor when this starts an execution. */
  id?: string
  /** Epoch milliseconds the host stamped on the event. */
  created: number
  directory?: string
  fact: Fact
}

function usageOf(tokens: unknown): StepUsage {
  if (!isRecord(tokens)) return {}
  const cache = isRecord(tokens['cache']) ? tokens['cache'] : undefined
  const usage: StepUsage = {}
  const input = whole(tokens['input'])
  const output = whole(tokens['output'])
  const reasoning = whole(tokens['reasoning'])
  const cacheRead = whole(cache?.['read'])
  const cacheWrite = whole(cache?.['write'])
  if (input !== undefined) usage.input = input
  if (output !== undefined) usage.output = output
  if (reasoning !== undefined) usage.reasoning = reasoning
  if (cacheRead !== undefined) usage.cacheRead = cacheRead
  if (cacheWrite !== undefined) usage.cacheWrite = cacheWrite
  return usage
}

function factOf(type: string, data: Record<string, unknown>): Fact {
  switch (type) {
    case 'session.created': {
      const version = label(data['version'])
      return { kind: 'session.created', ...(version !== undefined ? { version } : {}) }
    }
    case 'session.renamed': {
      const title = typeof data['title'] === 'string' ? data['title'] : undefined
      return { kind: 'session.renamed', ...(title !== undefined ? { title } : {}) }
    }
    case 'session.execution.started':
      return { kind: 'execution.started' }
    case 'session.execution.succeeded':
      return { kind: 'execution.ended', outcome: 'succeeded' }
    case 'session.execution.failed':
      return { kind: 'execution.ended', outcome: 'failed' }
    case 'session.execution.interrupted': {
      const reason = label(data['reason'])
      return { kind: 'execution.ended', outcome: 'interrupted', ...(reason !== undefined ? { reason } : {}) }
    }
    case 'session.step.started': {
      const messageId = text(data['assistantMessageID'])
      if (messageId === undefined) return { kind: 'other' }
      const model = isRecord(data['model']) ? data['model'] : undefined
      const agent = label(data['agent'])
      const modelId = label(model?.['id'])
      const modelProvider = label(model?.['providerID'])
      return {
        kind: 'step.started',
        messageId,
        ...(agent !== undefined ? { agent } : {}),
        ...(modelId !== undefined ? { modelId } : {}),
        ...(modelProvider !== undefined ? { modelProvider } : {}),
      }
    }
    case 'session.step.ended': {
      const messageId = text(data['assistantMessageID'])
      if (messageId === undefined) return { kind: 'other' }
      const finish = label(data['finish'])
      const cost = costOf(data['cost'])
      return {
        kind: 'step.ended',
        messageId,
        usage: usageOf(data['tokens']),
        ...(finish !== undefined ? { finish } : {}),
        ...(cost !== undefined ? { cost } : {}),
      }
    }
    case 'session.tool.input.started': {
      const callId = text(data['id'])
      if (callId === undefined) return { kind: 'other' }
      const name = label(data['name'])
      return { kind: 'tool.input', callId, ...(name !== undefined ? { name } : {}) }
    }
    case 'session.tool.called': {
      const callId = text(data['id'])
      if (callId === undefined) return { kind: 'other' }
      const messageId = text(data['assistantMessageID'])
      const inputBytes = data['input'] === undefined ? undefined : jsonBytes(data['input'])
      const inputId = isRecord(data['input']) ? normalizeSkillId(data['input']['id']) : undefined
      return {
        kind: 'tool.called',
        callId,
        ...(messageId !== undefined ? { messageId } : {}),
        ...(inputBytes !== undefined ? { inputBytes } : {}),
        ...(inputId !== undefined ? { inputId } : {}),
      }
    }
    case 'session.tool.success': {
      const callId = text(data['id'])
      if (callId === undefined) return { kind: 'other' }
      const messageId = text(data['assistantMessageID'])
      const resultBytes = textPartsBytes(data['content'])
      return {
        kind: 'tool.succeeded',
        callId,
        ...(messageId !== undefined ? { messageId } : {}),
        ...(resultBytes !== undefined ? { resultBytes } : {}),
      }
    }
    case 'session.tool.failed': {
      const callId = text(data['id'])
      if (callId === undefined) return { kind: 'other' }
      const messageId = text(data['assistantMessageID'])
      const errorType = isRecord(data['error']) ? label(data['error']['type']) : undefined
      return {
        kind: 'tool.failed',
        callId,
        ...(messageId !== undefined ? { messageId } : {}),
        ...(errorType !== undefined ? { errorType } : {}),
      }
    }
    case 'session.skill.activated': {
      const skillId = normalizeSkillId(data['id']) ?? normalizeSkillId(data['name'])
      return skillId === undefined ? { kind: 'other' } : { kind: 'skill.activated', skillId }
    }
    default:
      return { kind: 'other' }
  }
}

/** A bus event reduced to the session it names and the fact it carries; undefined when it names no session. */
function parseEvent(raw: unknown): BusEvent | undefined {
  if (!isRecord(raw)) return undefined
  const data = isRecord(raw['data']) ? raw['data'] : {}
  const durable = isRecord(raw['durable']) ? raw['durable'] : undefined
  const sessionId = text(data['sessionID']) ?? text(durable?.['aggregateID'])
  const type = text(raw['type'])
  // A time outside the epoch range is no time at all: the event is dropped, not stamped on a span.
  const rawCreated = raw['created']
  const created = epochMs(rawCreated) ? rawCreated : undefined
  if (sessionId === undefined || type === undefined || created === undefined) return undefined
  const top = isRecord(raw['location']) ? raw['location'] : undefined
  const inner = isRecord(data['location']) ? data['location'] : undefined
  const rawDirectory = text(top?.['directory']) ?? text(inner?.['directory'])
  const directory = rawDirectory === undefined ? undefined : normalizeDirectory(rawDirectory)
  const id = text(raw['id'])
  return {
    sessionId,
    created,
    ...(id !== undefined ? { id } : {}),
    ...(directory !== undefined ? { directory } : {}),
    fact: factOf(type, data),
  }
}

// ---------------------------------------------------------------------------------------------
// State

type Prompts = { count: number; text?: string }

type PendingContext = {
  agent?: string
  modelId?: string
  modelProvider?: string
  content?: StepContent
}

type StepState = {
  messageId: string
  startMs: number
  agent?: string
  modelId?: string
  modelProvider?: string
  requestKind: string
  skills: Set<string>
  content?: StepContent
}

type HookDone = { endMs: number; status: 'completed' | 'error'; errorType?: string; resultBytes?: number }

type ToolState = {
  callId: string
  messageId?: string
  name?: string
  /** `session.tool.called` time on the host's clock. */
  calledMs?: number
  /** `execute.before` time on the plugin's clock. */
  hookStartMs?: number
  inputBytes?: number
  /** `input.id` of the call: the skill id when the tool is `skill`. */
  inputId?: string
  /** The `execute.after` outcome, kept until the bus reports the result or the step ends. */
  hookDone?: HookDone
}

type ExecutionState = {
  /** The execution-started event id. */
  anchorId: string
  startMs: number
  /** The latest host event time seen for the execution; where an `unknown` request ends. */
  lastCreatedMs: number
  agent?: string
  prompts: Prompts
}

type SessionState = {
  id: string
  version?: string
  label?: string
  execution?: ExecutionState
  /** The last anchor made up for an execution-started event that carried no id. */
  madeUpAnchor?: { createdMs: number; repeat: number }
  /** Prompts seen while no execution was open: they belong to the next one. */
  pendingPrompts: Prompts
  pendingSkills: Set<string>
  pendingContext?: PendingContext
  pendingKind?: string
  steps: Map<string, StepState>
  tools: Map<string, ToolState>
  doneTools: Set<string>
  /** Plugin-clock time of the last event or hook for the session. */
  lastActivityMs: number
}

function oldest<K>(keys: IterableIterator<K>): K | undefined {
  const first = keys.next()
  return first.done === true ? undefined : first.value
}

/** Notes a skill on a step, forgetting the oldest once the step holds `MAX_STEP_SKILLS`. */
function noteStepSkill(step: StepState, skillId: string): void {
  step.skills.add(skillId)
  if (step.skills.size > MAX_STEP_SKILLS) {
    const forgotten = oldest(step.skills.values())
    if (forgotten !== undefined) step.skills.delete(forgotten)
  }
}

/**
 * The anchor for an execution-started event the host gave no id: its time, and a repeat number when
 * the previous such event in the session had the same time, so two executions never share a span id.
 */
function madeUpAnchor(state: SessionState, createdMs: number): string {
  const repeat = state.madeUpAnchor?.createdMs === createdMs ? state.madeUpAnchor.repeat + 1 : 0
  state.madeUpAnchor = { createdMs, repeat }
  return repeat === 0 ? `created:${createdMs}` : `created:${createdMs}#${repeat}`
}

/**
 * The head of a prompt that can survive the cap. Every UTF-16 unit takes at least one UTF-8 byte, so
 * the first `MAX_PROMPT_BYTES` units already hold every byte that can be kept; the rest of a huge
 * prompt is never copied or encoded. The copy is independent of the host string.
 */
function promptHead(promptText: string): string {
  return promptText.length > MAX_PROMPT_BYTES ? detachedCopy(promptText.slice(0, MAX_PROMPT_BYTES)) : promptText
}

/** Adds one prompt: always counted; its text kept (capped, blank-line joined) only when content may be kept. */
function addPrompt(prompts: Prompts, promptText: string | undefined, keepContent: boolean): void {
  prompts.count += 1
  if (!keepContent || promptText === undefined || promptText === '') return
  if (prompts.text === undefined) {
    prompts.text = capUtf8(promptHead(promptText), MAX_PROMPT_BYTES)
  } else if (Buffer.byteLength(prompts.text, 'utf8') < MAX_PROMPT_BYTES) {
    prompts.text = capUtf8(`${prompts.text}\n\n${promptHead(promptText)}`, MAX_PROMPT_BYTES)
  }
}

// ---------------------------------------------------------------------------------------------
// Engine

/** Build the capture engine. Construction touches no I/O and starts no timer. */
export function createCaptureEngine(options: CaptureEngineOptions): CaptureEngine {
  const { emit } = options
  const directory = options.directory === undefined ? undefined : normalizeDirectory(options.directory)
  const now = options.now ?? ((): number => Date.now())
  const sessions = new Map<string, SessionState>()
  const held: BusEvent[] = []

  const report = (where: string, error: unknown): void => {
    try {
      options.onError?.(where, error)
    } catch {
      // The tap is observability only; a failing tap has nowhere further to report.
    }
  }

  const consentNow = (): boolean => {
    try {
      return options.consent()
    } catch (error) {
      report('consent', error)
      return false
    }
  }

  const send = (span: OtlpWireSpan): void => {
    try {
      emit(span)
    } catch (error) {
      report('emit', error)
    }
  }

  // --- ownership ------------------------------------------------------------------------------

  /** The state of a session this instance owns, creating it (and replaying held events) on first claim. */
  const claim = (sessionId: string): SessionState => {
    const existing = sessions.get(sessionId)
    if (existing !== undefined) {
      // Re-insert so the Map's order is least-recently-active first.
      sessions.delete(sessionId)
      sessions.set(sessionId, existing)
      existing.lastActivityMs = now()
      return existing
    }
    const state: SessionState = {
      id: sessionId,
      pendingPrompts: { count: 0 },
      pendingSkills: new Set(),
      steps: new Map(),
      tools: new Map(),
      doneTools: new Set(),
      lastActivityMs: now(),
    }
    sessions.set(sessionId, state)
    while (sessions.size > MAX_SESSIONS) {
      const forgotten = oldest(sessions.keys())
      if (forgotten === undefined) break
      sessions.delete(forgotten)
    }
    for (let index = 0; index < held.length; ) {
      const event = held[index]
      if (event !== undefined && event.sessionId === sessionId) {
        held.splice(index, 1)
        applyEvent(state, event)
      } else {
        index += 1
      }
    }
    return state
  }

  const hold = (event: BusEvent): void => {
    held.push(event)
    if (held.length > MAX_HELD_EVENTS) held.shift()
  }

  // --- spans ----------------------------------------------------------------------------------

  const emitRequest = (
    state: SessionState,
    execution: ExecutionState,
    outcome: RequestOutcome,
    endMs: number,
    reason?: string,
  ): void => {
    const hasContent = execution.prompts.text !== undefined || state.label !== undefined
    const keepContent = hasContent && consentNow()
    send(
      mapRequestSpan({
        sessionId: state.id,
        anchorId: execution.anchorId,
        startMs: execution.startMs,
        endMs,
        outcome,
        ...(reason !== undefined ? { reason } : {}),
        ...(execution.agent !== undefined ? { agent: execution.agent } : {}),
        ...(state.version !== undefined ? { version: state.version } : {}),
        ...(execution.prompts.count > 0 ? { promptCount: execution.prompts.count } : {}),
        ...(keepContent && execution.prompts.text !== undefined ? { promptText: execution.prompts.text } : {}),
        ...(keepContent && state.label !== undefined ? { label: state.label } : {}),
      }),
    )
  }

  /** Ends the open execution: steps and tools that never finished are dropped, not guessed at. */
  const closeExecution = (state: SessionState): void => {
    state.execution = undefined
    state.steps.clear()
    state.tools.clear()
  }

  const flushUnknown = (state: SessionState): void => {
    const execution = state.execution
    if (execution === undefined) return
    try {
      emitRequest(state, execution, 'unknown', execution.lastCreatedMs)
    } finally {
      closeExecution(state)
    }
  }

  const lastOpenStep = (state: SessionState): StepState | undefined => {
    let last: StepState | undefined
    for (const step of state.steps.values()) last = step
    return last
  }

  const addSkill = (state: SessionState, messageId: string | undefined, skillId: string): void => {
    const step = messageId !== undefined ? state.steps.get(messageId) : lastOpenStep(state)
    if (step !== undefined) noteStepSkill(step, skillId)
  }

  const holdSkill = (state: SessionState, skillId: string): void => {
    if (state.pendingSkills.size < MAX_PENDING_SKILLS) state.pendingSkills.add(skillId)
  }

  // --- tools ----------------------------------------------------------------------------------

  /** The open tool state for a call, creating it; undefined for a call already finished. */
  const toolFor = (state: SessionState, callId: string): ToolState | undefined => {
    if (state.doneTools.has(callId)) return undefined
    let tool = state.tools.get(callId)
    if (tool === undefined) {
      tool = { callId }
      state.tools.set(callId, tool)
      if (state.tools.size > MAX_OPEN_TOOLS) {
        const forgotten = oldest(state.tools.keys())
        if (forgotten !== undefined) state.tools.delete(forgotten)
      }
    }
    return tool
  }

  const finishTool = (
    state: SessionState,
    tool: ToolState,
    result: { status: 'completed' | 'error'; startMs: number; endMs: number; errorType?: string; resultBytes?: number; timing?: 'hook' },
  ): void => {
    state.tools.delete(tool.callId)
    state.doneTools.add(tool.callId)
    if (state.doneTools.size > MAX_DONE_TOOLS) {
      const forgotten = oldest(state.doneTools.values())
      if (forgotten !== undefined) state.doneTools.delete(forgotten)
    }
    const isSkill = tool.name === 'skill' && tool.inputId !== undefined
    send(
      mapToolSpan({
        sessionId: state.id,
        callId: tool.callId,
        startMs: result.startMs,
        endMs: result.endMs,
        status: result.status,
        ...(tool.messageId !== undefined ? { messageId: tool.messageId } : {}),
        ...(tool.name !== undefined ? { name: tool.name } : {}),
        ...(result.errorType !== undefined ? { errorType: result.errorType } : {}),
        ...(tool.inputBytes !== undefined ? { inputBytes: tool.inputBytes } : {}),
        ...(result.resultBytes !== undefined ? { resultBytes: result.resultBytes } : {}),
        ...(result.timing !== undefined ? { timing: result.timing } : {}),
        ...(isSkill && tool.inputId !== undefined ? { skillId: tool.inputId } : {}),
      }),
    )
    // A skill the tool failed to load was asked for, not invoked.
    if (isSkill && tool.inputId !== undefined && result.status === 'completed') {
      addSkill(state, tool.messageId, tool.inputId)
    }
  }

  /** Emits a tool the bus never reported a result for, from its `execute.after` hook alone. */
  const finishToolFromHook = (state: SessionState, tool: ToolState, done: HookDone): void => {
    finishTool(state, tool, {
      status: done.status,
      startMs: tool.hookStartMs ?? done.endMs,
      endMs: done.endMs,
      timing: 'hook',
      ...(done.errorType !== undefined ? { errorType: done.errorType } : {}),
      ...(done.resultBytes !== undefined ? { resultBytes: done.resultBytes } : {}),
    })
  }

  // --- bus events -----------------------------------------------------------------------------

  const applyEvent = (state: SessionState, event: BusEvent): void => {
    state.lastActivityMs = now()
    const { fact } = event
    if (fact.kind !== 'execution.started' && state.execution !== undefined && event.created > state.execution.lastCreatedMs) {
      state.execution.lastCreatedMs = event.created
    }
    switch (fact.kind) {
      case 'session.created':
        if (fact.version !== undefined) state.version = fact.version
        return
      case 'session.renamed':
        // The title is content: kept only while consent allows it.
        if (fact.title !== undefined && fact.title !== '' && consentNow()) {
          state.label = detachedCopy(capUtf8(fact.title, MAX_FIELD_BYTES))
        }
        return
      case 'execution.started':
        flushUnknown(state)
        state.execution = {
          anchorId: event.id ?? madeUpAnchor(state, event.created),
          startMs: event.created,
          lastCreatedMs: event.created,
          prompts: state.pendingPrompts,
        }
        state.pendingPrompts = { count: 0 }
        return
      case 'execution.ended': {
        const execution = state.execution
        if (execution === undefined) return
        try {
          emitRequest(state, execution, fact.outcome, event.created, fact.reason)
        } finally {
          closeExecution(state)
        }
        return
      }
      case 'step.started': {
        if (state.steps.has(fact.messageId)) return
        const context = state.pendingContext
        const agent = fact.agent ?? context?.agent
        const modelId = fact.modelId ?? context?.modelId
        const modelProvider = fact.modelProvider ?? context?.modelProvider
        const step: StepState = {
          messageId: fact.messageId,
          startMs: event.created,
          requestKind: state.pendingKind ?? 'primary',
          skills: new Set(state.pendingSkills),
          ...(agent !== undefined ? { agent } : {}),
          ...(modelId !== undefined ? { modelId } : {}),
          ...(modelProvider !== undefined ? { modelProvider } : {}),
          ...(context?.content !== undefined ? { content: context.content } : {}),
        }
        state.pendingContext = undefined
        state.pendingKind = undefined
        state.pendingSkills.clear()
        state.steps.set(step.messageId, step)
        if (state.steps.size > MAX_OPEN_STEPS) {
          const forgotten = oldest(state.steps.keys())
          if (forgotten !== undefined) state.steps.delete(forgotten)
        }
        if (state.execution !== undefined && state.execution.agent === undefined && agent !== undefined) {
          state.execution.agent = agent
        }
        return
      }
      case 'step.ended': {
        const step = state.steps.get(fact.messageId)
        if (step === undefined) return
        state.steps.delete(fact.messageId)
        for (const tool of [...state.tools.values()]) {
          if (tool.messageId !== fact.messageId) continue
          if (tool.hookDone !== undefined) finishToolFromHook(state, tool, tool.hookDone)
          else state.tools.delete(tool.callId)
        }
        const content = step.content !== undefined && consentNow() ? step.content : undefined
        send(
          mapStepSpan({
            sessionId: state.id,
            messageId: step.messageId,
            startMs: step.startMs,
            endMs: event.created,
            requestKind: step.requestKind,
            usage: fact.usage,
            skills: [...step.skills],
            ...(state.execution !== undefined ? { requestAnchorId: state.execution.anchorId } : {}),
            ...(step.agent !== undefined ? { agent: step.agent } : {}),
            ...(step.modelId !== undefined ? { modelId: step.modelId } : {}),
            ...(step.modelProvider !== undefined ? { modelProvider: step.modelProvider } : {}),
            ...(fact.finish !== undefined ? { finish: fact.finish } : {}),
            ...(fact.cost !== undefined ? { cost: fact.cost } : {}),
            ...(content !== undefined ? { content } : {}),
          }),
        )
        return
      }
      case 'tool.input': {
        const tool = toolFor(state, fact.callId)
        if (tool !== undefined && tool.name === undefined && fact.name !== undefined) tool.name = fact.name
        return
      }
      case 'tool.called': {
        const tool = toolFor(state, fact.callId)
        if (tool === undefined) return
        tool.calledMs = event.created
        if (fact.messageId !== undefined) tool.messageId = fact.messageId
        if (fact.inputBytes !== undefined) tool.inputBytes = fact.inputBytes
        if (fact.inputId !== undefined) tool.inputId = fact.inputId
        return
      }
      case 'tool.succeeded':
      case 'tool.failed': {
        const tool = toolFor(state, fact.callId)
        if (tool === undefined) return
        if (fact.messageId !== undefined) tool.messageId = fact.messageId
        const ok = fact.kind === 'tool.succeeded'
        finishTool(state, tool, {
          status: ok ? 'completed' : 'error',
          startMs: tool.calledMs ?? tool.hookStartMs ?? event.created,
          endMs: event.created,
          ...(fact.kind === 'tool.failed' && fact.errorType !== undefined ? { errorType: fact.errorType } : {}),
          ...(fact.kind === 'tool.succeeded' && fact.resultBytes !== undefined ? { resultBytes: fact.resultBytes } : {}),
        })
        return
      }
      case 'skill.activated': {
        const step = lastOpenStep(state)
        if (step !== undefined) noteStepSkill(step, fact.skillId)
        else holdSkill(state, fact.skillId)
        return
      }
      case 'other':
        return
    }
  }

  // --- hooks ----------------------------------------------------------------------------------

  const noteAgent = (state: SessionState, agent: string | undefined): void => {
    if (state.execution !== undefined && state.execution.agent === undefined && agent !== undefined) {
      state.execution.agent = agent
    }
  }

  const guard = (where: string, work: () => void): void => {
    try {
      work()
    } catch (error) {
      report(where, error)
    }
  }

  return {
    onEvent: (raw) =>
      guard('event', () => {
        const event = parseEvent(raw)
        if (event === undefined) return
        if (sessions.has(event.sessionId) || (directory !== undefined && event.directory === directory)) {
          applyEvent(claim(event.sessionId), event)
        } else if (directory !== undefined && event.directory !== undefined) {
          // The event names another directory: someone else's session, dropped.
        } else {
          // Not ours yet, and nothing says it is someone else's: a hook may still claim it.
          hold(event)
        }
      }),

    onPrompt: (raw) =>
      guard('prompt', () => {
        if (!isRecord(raw)) return
        const sessionId = text(raw['sessionID'])
        if (sessionId === undefined) return
        const state = claim(sessionId)
        const prompt = isRecord(raw['prompt']) ? raw['prompt'] : undefined
        if (prompt === undefined) return
        const promptText = typeof prompt['text'] === 'string' ? prompt['text'] : undefined
        addPrompt(state.execution?.prompts ?? state.pendingPrompts, promptText, promptText !== undefined && consentNow())
        if (Array.isArray(prompt['skills'])) {
          for (const skill of prompt['skills'] as unknown[]) {
            const skillId = isRecord(skill) ? normalizeSkillId(skill['id']) : undefined
            if (skillId !== undefined) holdSkill(state, skillId)
          }
        }
      }),

    onContext: (raw) =>
      guard('context', () => {
        if (!isRecord(raw)) return
        const sessionId = text(raw['sessionID'])
        if (sessionId === undefined) return
        const state = claim(sessionId)
        const model = isRecord(raw['model']) ? raw['model'] : undefined
        const agent = label(raw['agent'])
        const modelId = label(model?.['id'])
        const modelProvider = label(model?.['providerID'])
        let content: StepContent | undefined
        if (consentNow()) {
          const system = systemTextOf(raw['system'])
          const listing = system === undefined ? undefined : parseAvailableSkills(system)
          const systemContent = system === undefined ? undefined : capContent(system)
          const tools = toolsJsonOf(raw['tools'])
          const skills = listing?.ok === true && listing.ids.length > 0 ? listing.ids.map((id) => detachedCopy(id)) : undefined
          if (systemContent !== undefined || tools !== undefined || skills !== undefined) {
            content = {
              ...(systemContent !== undefined ? { system: systemContent } : {}),
              ...(tools !== undefined ? { tools } : {}),
              ...(skills !== undefined ? { skills } : {}),
            }
          }
        }
        state.pendingContext = {
          ...(agent !== undefined ? { agent } : {}),
          ...(modelId !== undefined ? { modelId } : {}),
          ...(modelProvider !== undefined ? { modelProvider } : {}),
          ...(content !== undefined ? { content } : {}),
        }
        noteAgent(state, agent)
      }),

    onModelRequest: (raw) =>
      guard('model.request', () => {
        if (!isRecord(raw)) return
        const sessionId = text(raw['sessionID'])
        if (sessionId === undefined) return
        const state = claim(sessionId)
        const kind = label(raw['kind'])
        if (kind !== undefined) state.pendingKind = kind
        noteAgent(state, label(raw['agent']))
      }),

    onToolBefore: (raw) =>
      guard('tool.execute.before', () => {
        if (!isRecord(raw)) return
        const sessionId = text(raw['sessionID'])
        const callId = text(raw['id'])
        if (sessionId === undefined || callId === undefined) return
        const state = claim(sessionId)
        const tool = toolFor(state, callId)
        if (tool === undefined) return
        const name = label(raw['tool'])
        const messageId = text(raw['messageID'])
        if (tool.name === undefined && name !== undefined) tool.name = name
        if (tool.messageId === undefined && messageId !== undefined) tool.messageId = messageId
        if (tool.hookStartMs === undefined) tool.hookStartMs = now()
        if (tool.inputBytes === undefined && raw['input'] !== undefined) tool.inputBytes = jsonBytes(raw['input'])
        if (tool.inputId === undefined && isRecord(raw['input'])) tool.inputId = normalizeSkillId(raw['input']['id'])
        noteAgent(state, label(raw['agent']))
      }),

    onToolAfter: (raw) =>
      guard('tool.execute.after', () => {
        if (!isRecord(raw)) return
        const sessionId = text(raw['sessionID'])
        const callId = text(raw['id'])
        if (sessionId === undefined || callId === undefined) return
        const state = claim(sessionId)
        const tool = toolFor(state, callId)
        if (tool === undefined) return
        const name = label(raw['tool'])
        const messageId = text(raw['messageID'])
        if (tool.name === undefined && name !== undefined) tool.name = name
        if (tool.messageId === undefined && messageId !== undefined) tool.messageId = messageId
        if (tool.inputBytes === undefined && raw['input'] !== undefined) tool.inputBytes = jsonBytes(raw['input'])
        if (tool.inputId === undefined && isRecord(raw['input'])) tool.inputId = normalizeSkillId(raw['input']['id'])
        noteAgent(state, label(raw['agent']))
        const error = isRecord(raw['error']) ? raw['error'] : undefined
        const failed = raw['status'] === 'error' || error !== undefined
        const errorType = failed ? label(error?.['type']) : undefined
        const resultBytes = !failed && isRecord(raw['result']) ? textPartsBytes(raw['result']['content']) : undefined
        const done: HookDone = {
          endMs: now(),
          status: failed ? 'error' : 'completed',
          ...(errorType !== undefined ? { errorType } : {}),
          ...(resultBytes !== undefined ? { resultBytes } : {}),
        }
        // With a bus `tool.called` for this call, the bus result carries the better timing and
        // size: wait for it (or the end of the step). Without one, the hooks are all there is.
        if (tool.calledMs !== undefined) tool.hookDone = done
        else finishToolFromHook(state, tool, done)
      }),

    sweep: () =>
      guard('sweep', () => {
        const current = now()
        for (const state of [...sessions.values()]) {
          if (state.execution !== undefined && current - state.lastActivityMs >= INACTIVITY_FLUSH_MS) flushUnknown(state)
        }
      }),

    nextSweepDelayMs: () => {
      const current = now()
      let next: number | undefined
      for (const state of sessions.values()) {
        if (state.execution === undefined) continue
        const remaining = Math.max(0, INACTIVITY_FLUSH_MS - (current - state.lastActivityMs))
        if (next === undefined || remaining < next) next = remaining
      }
      return next
    },

    flushOpenExecutions: () =>
      guard('flush', () => {
        for (const state of [...sessions.values()]) flushUnknown(state)
      }),
  }
}
