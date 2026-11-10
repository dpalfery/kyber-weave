// The Factory (Droid) session and child pairing rule.
//
// Factory is ONE harness under ONE canonical id — `droid` — reached through
// TWO capture sources:
//
//   * the PLUGIN source: the Kyber-owned Factory hook plugin, whose spans
//     arrive over OTLP and carry no token counter at all, so every record it
//     produces declares its token usage `not_measurable` with a reason;
//   * the FILE source: the Factory transcript files under the Factory root,
//     whose counters come from the per-session companion
//     `<session-id>.settings.json`.
//
// The dashboard shows Factory once, so the two sources have to come together.
// This file pins the rule that makes them come together:
//
//   * the pairing is EXACT session-id equality — no fuzzy join, no time
//     window;
//   * a paired session carries the FILE row's counters, and the plugin row's
//     own `not_measurable` declaration does not blank them;
//   * an unpaired plugin row reads `not_measurable` with a reason — never a
//     zero, never a value borrowed from another session;
//   * no token value is ever invented;
//   * a Factory child session is named by the plugin through its OWN turn
//     span — the hooks fire inside the child's own session, so the turn's
//     session id IS the child's — and it is a session of its own on disk: its
//     own transcript file and its own companion settings file, so the file
//     source discovers it as its own session and pairs it on its own session
//     id; the parent's `childInclusiveTokenUsageBySessionId` map is the
//     parent's aggregate view of its children, never a token source;
//   * the two sources own different halves of a paired session: the plugin
//     row supplies the turns and the session's structure, the file row
//     supplies the counters and their availability, and the cost of both is
//     one figure.
//
// The projection under test is the shared engine's session projection
// (`buildSessions` over the canonical store) — the table the
// `/api/kyber/sessions` endpoint reads. Both rows are built the way their own
// source builds them: the plugin row through the OTLP ingest seam and the
// droid adapter, the file row through the Factory file provider reading a
// companion settings file in a temporary Factory root.

import { describe, expect, it, beforeEach, afterEach } from 'vitest'
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

import type { OtlpSpan } from '../otel/receiver.js'
import { createDroidProvider } from '../providers/droid.js'
import type { ParsedProviderCall } from '../providers/types.js'
import { synthesizeCall } from '../synth/synth.js'
import { droidAdapter } from './adapters/droid.js'
import { ingestBatchWithAdapters } from './ingest.js'
import { buildSessions } from './sessions.js'
import { CanonStore } from './store.js'
import { PROJECTED_ATTRIBUTE_KEYS } from './store-rows.js'
import { SESSION_LABEL_KEY, type CanonicalRecord } from './types.js'

/** The Factory session both sources observed. */
const SESSION_ID = 'factory-session-1'
/** A session only the plugin observed. */
const PLUGIN_ONLY_SESSION_ID = 'factory-session-plugin-only'
/** A session id one character away from the file row's — the near miss. */
const NEAR_MISS_SESSION_ID = `${SESSION_ID}-2`
/** The child session that has a companion settings file of its own. */
const CHILD_SESSION_ID = 'factory-child-1'
/** A child session whose own companion settings file reports no usage. */
const UNMAPPED_CHILD_SESSION_ID = 'factory-child-unmapped'

/** The counters the companion file reports for the parent session. */
const FILE_INPUT = 1234
const FILE_OUTPUT = 567
const FILE_CACHE_CREATION = 89
const FILE_CACHE_READ = 21
const FILE_THINKING = 7

/** The counters the companion file reports for the child session. */
const CHILD_INPUT = 900
const CHILD_OUTPUT = 90
const CHILD_CACHE_CREATION = 9
const CHILD_CACHE_READ = 8
const CHILD_THINKING = 7

/**
 * The session totals the file row's counters produce.
 *
 * The session's input total counts every input-side class the companion file
 * reported (fresh input, cache read, cache creation), and its output total
 * counts the thinking tokens too: the file source reports thinking in its own
 * counter and the Factory provider is not one of the harnesses that bills
 * thinking inside its output count, so the two are added.
 */
const FILE_SESSION_INPUT = FILE_INPUT + FILE_CACHE_READ + FILE_CACHE_CREATION
const FILE_SESSION_OUTPUT = FILE_OUTPUT + FILE_THINKING

/** The same derivation for the child session's own usage. */
const CHILD_SESSION_INPUT = CHILD_INPUT + CHILD_CACHE_READ + CHILD_CACHE_CREATION
const CHILD_SESSION_OUTPUT = CHILD_OUTPUT + CHILD_THINKING

/**
 * The session totals the parent's aggregate view of a child produces: the
 * parent's own totals together with the child's.
 */
const AGGREGATE_SESSION_INPUT = FILE_SESSION_INPUT + CHILD_SESSION_INPUT
const AGGREGATE_SESSION_OUTPUT = FILE_SESSION_OUTPUT + CHILD_SESSION_OUTPUT

/**
 * The counters the parent's child-usage map holds for the child.
 *
 * These are the map's OWN figures, before the session totals they would
 * produce: a child that read its counters out of the map would report them,
 * and one that treated them as a session's own counters would report the
 * aggregate totals above. Neither is the child's own usage.
 */
const MAP_CHILD_INPUT = FILE_INPUT + CHILD_INPUT
const MAP_CHILD_OUTPUT = FILE_OUTPUT + CHILD_OUTPUT
const MAP_CHILD_CACHE_CREATION = FILE_CACHE_CREATION + CHILD_CACHE_CREATION
const MAP_CHILD_CACHE_READ = FILE_CACHE_READ + CHILD_CACHE_READ
const MAP_CHILD_THINKING = FILE_THINKING + CHILD_THINKING

/** The telemetry source the Kyber-owned Factory plugin's export arrives under. */
const PLUGIN_SOURCE = 'kyberdash-factory-plugin'

/** The plugin turn span's own duration: the millis its OTLP span reports. */
const PLUGIN_TURN_DURATION_MS = 1000

/**
 * The duration the file row carries.
 *
 * The file source's own row reports no duration of its own, so a case gives it
 * one: the session's duration then says which records the session added.
 */
const FILE_ROW_DURATION_MS = 5000

/** The contract version the Kyber-owned plugin emits. */
const CONTRACT_VERSION = '1'

/** The model the companion file names for the Factory session. */
const MODEL = 'custom:gpt-5-[Proxy]-0'

/** The model the plugin's own turn names, when its turn names one. */
const PLUGIN_MODEL = 'gpt-5-codex'

/**
 * The model a companion file names, distinct from the plugin's own.
 *
 * Two distinct names let a case tell which source published the model it
 * sees.
 */
const FILE_MODEL = 'custom:glm-5.1-[Proxy]-0'

/** The model the file source publishes for that name: the wrapper stripped. */
const FILE_MODEL_PUBLISHED = 'glm-5.1'

let factoryDir: string

beforeEach(async () => {
  factoryDir = await mkdtemp(join(tmpdir(), 'kyberdash-droid-pairing-'))
  // The Factory root is redirected into the temporary directory: no case may
  // read or write the developer's real `~/.factory`.
  process.env['FACTORY_DIR'] = factoryDir
})

afterEach(async () => {
  delete process.env['FACTORY_DIR']
  await rm(factoryDir, { recursive: true, force: true })
})

/**
 * Write a Factory session and its companion settings file into the temporary
 * Factory root, the shape the real Factory writes: one transcript per session
 * id, one `<session-id>.settings.json` beside it.
 */
async function writeFactorySession(sessionId: string, settings: unknown): Promise<void> {
  const dir = join(factoryDir, 'sessions', '-tmp-my-project')
  await mkdir(dir, { recursive: true })
  const lines = [
    { type: 'session_start', id: sessionId, cwd: '/tmp/my-project', title: 'Test session' },
    {
      type: 'message',
      id: 'u1',
      timestamp: '2026-04-20T10:00:00.000Z',
      message: { role: 'user', content: [{ type: 'text', text: 'build this' }] },
    },
    {
      type: 'message',
      id: 'a1',
      timestamp: '2026-04-20T10:00:01.000Z',
      message: { role: 'assistant', content: [{ type: 'text', text: 'done' }] },
    },
  ]
  await writeFile(join(dir, `${sessionId}.jsonl`), lines.map((line) => JSON.stringify(line)).join('\n'))
  await writeFile(join(dir, `${sessionId}.settings.json`), JSON.stringify(settings))
}

/** The companion settings file's counters for the parent session. */
function parentTokenUsage(): Record<string, number> {
  return {
    inputTokens: FILE_INPUT,
    outputTokens: FILE_OUTPUT,
    cacheCreationTokens: FILE_CACHE_CREATION,
    cacheReadTokens: FILE_CACHE_READ,
    thinkingTokens: FILE_THINKING,
  }
}

/** The child session's own companion settings file's counters. */
function childTokenUsage(): Record<string, number> {
  return {
    inputTokens: CHILD_INPUT,
    outputTokens: CHILD_OUTPUT,
    cacheCreationTokens: CHILD_CACHE_CREATION,
    cacheReadTokens: CHILD_CACHE_READ,
    thinkingTokens: CHILD_THINKING,
  }
}

/**
 * The counters the PARENT's companion file carries in its child-usage map.
 *
 * The map is the parent's aggregate view of its children — the parent's own
 * totals together with the child's — so the figures it holds are neither the
 * parent's own nor the child's own, and a child that read its counters out of
 * the map would report the aggregate.
 */
function parentAggregateChildTokenUsage(): Record<string, number> {
  return {
    inputTokens: MAP_CHILD_INPUT,
    outputTokens: MAP_CHILD_OUTPUT,
    cacheCreationTokens: MAP_CHILD_CACHE_CREATION,
    cacheReadTokens: MAP_CHILD_CACHE_READ,
    thinkingTokens: MAP_CHILD_THINKING,
  }
}

/** The plugin envelope's attributes for one session. */
function pluginAttributes(sessionId: string, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    'factory.contract_version': CONTRACT_VERSION,
    'factory.session.id': sessionId,
    'factory.model.id': PLUGIN_MODEL,
    ...over,
  }
}

/**
 * The plugin's turn span for a session whose turn names no model.
 *
 * Factory's own `UserPromptSubmit` payload does NOT carry a model field, so a
 * plugin-sourced turn generally names none: the session's model then has to
 * come from the source that does know it.
 */
function pluginTurnSpanWithoutModel(
  sessionId: string,
  timestamp = '2026-04-20T10:00:02.000Z',
): OtlpSpan {
  const attributes = pluginAttributes(sessionId)
  delete attributes['factory.model.id']
  return otlpSpan(`plugin-turn-${sessionId}`, 'factory.turn', attributes, timestamp)
}

/** A decoded OTLP span, built whole so no field is left to a cast. */
function otlpSpan(
  spanId: string,
  name: string,
  attributes: Record<string, unknown>,
  timestamp: string,
): OtlpSpan {
  return {
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    name,
    kind: 'internal',
    startTimeUnixNano: '1793337355759000000',
    endTimeUnixNano: '1793337363002000000',
    timestamp,
    durationMs: PLUGIN_TURN_DURATION_MS,
    status: { code: 'unset' },
    resource: { 'service.name': PLUGIN_SOURCE },
    scope: {},
    spanId,
    attributes,
  }
}

/** The plugin's turn span for one session: the session's own model call. */
function pluginTurnSpan(sessionId: string, timestamp = '2026-04-20T10:00:02.000Z'): OtlpSpan {
  return otlpSpan(`plugin-turn-${sessionId}`, 'factory.turn', pluginAttributes(sessionId), timestamp)
}

/**
 * The file source's row for one Factory session, built the way the refresh
 * path builds it: the Factory provider parses the transcript and reads the
 * companion settings file, and the synthesizer turns the parsed call into the
 * canonical record the store holds.
 */
async function fileRow(sessionId: string): Promise<CanonicalRecord> {
  const provider = createDroidProvider()
  const sources = await provider.discoverSessions()
  const source = sources.find((candidate) => candidate.path.endsWith(`/${sessionId}.jsonl`))
  if (source === undefined) throw new Error(`no Factory session file was discovered for ${sessionId}`)
  const parser = provider.createSessionParser(source, new Set<string>())
  const calls: ParsedProviderCall[] = []
  for await (const call of parser.parse()) calls.push(call)
  if (calls.length !== 1) throw new Error(`expected one Factory call for ${sessionId}, got ${calls.length}`)
  return synthesizeCall(calls[0]!)
}

/** The session's summary figures, as the dashboard projects them. */
function summaryOf(store: CanonStore, sessionId: string): Record<string, unknown> {
  const payload = store.getSessionPayload(sessionId) as
    | { summary?: Record<string, unknown> }
    | undefined
  if (payload === undefined) throw new Error(`no session payload was projected for ${sessionId}`)
  return payload.summary ?? {}
}

/** The session's published turns, as the dashboard projects them. */
function publishedTurns(store: CanonStore, sessionId: string): Array<Record<string, unknown>> {
  const payload = store.getSessionPayload(sessionId) as
    | { turns?: Array<Record<string, unknown>> }
    | undefined
  if (payload === undefined) throw new Error(`no session payload was projected for ${sessionId}`)
  return payload.turns ?? []
}

/** The reason a session's token total carries, when it carries one. */
function reasonOf(value: unknown): string {
  expect(value, 'the total must be a not_measurable declaration, not a number').toMatchObject({
    availability: 'not_measurable',
  })
  if (
    typeof value === 'object' &&
    value !== null &&
    'reason' in value &&
    typeof value.reason === 'string' &&
    value.reason !== ''
  ) {
    return value.reason
  }
  throw new Error('the not_measurable total carries no reason')
}

describe('the two Factory capture sources', () => {
  it('builds a file row carrying the companion file counters and a plugin row carrying none', async () => {
    // The two rows this file pairs are built by their own sources, and each
    // source's own behaviour is part of the rule: the file row's counters
    // come from the companion settings file, and the plugin row reports no
    // counter at all and says so.
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const file = await fileRow(SESSION_ID)
    expect(file.harness).toBe('droid')
    expect(file.source).toBe('codeburn/droid')
    expect(file.sessionId).toBe(SESSION_ID)
    expect(file.tokens.freshInput).toBe(FILE_INPUT)
    expect(file.tokens.cacheRead).toBe(FILE_CACHE_READ)
    expect(file.tokens.cacheCreation).toBe(FILE_CACHE_CREATION)
    expect(file.tokens.output).toBe(FILE_OUTPUT + FILE_THINKING)

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpan(SESSION_ID)], store, [droidAdapter])
      const plugin = store.get(`plugin-turn-${SESSION_ID}`)
      expect(plugin?.harness).toBe('droid')
      expect(plugin?.sessionId).toBe(SESSION_ID)
      expect(plugin?.tokens.freshInput).toBe(0)
      expect(plugin?.tokens.output).toBe(0)
      expect(plugin?.measurability?.token_usage).toMatchObject({ availability: 'not_measurable' })
    } finally {
      store.close()
    }
  })
})

describe('the Factory session pairing rule', () => {
  it('forms one session from a plugin row and a file row of the same session, and that session carries the file row counters', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpan(SESSION_ID)], store, [droidAdapter])
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // One session, not two: the two rows share the session id.
      expect(store.sessionCount()).toBe(1)
      const rows = [...store.iterateSessions()]
      expect(rows).toHaveLength(1)
      expect(rows[0]!.sessionId).toBe(SESSION_ID)

      // And that one session carries the FILE row's counters. The plugin
      // row's own `not_measurable` declaration must not blank them.
      expect(store.sessionTokenTotals(SESSION_ID)).toEqual({
        input: FILE_SESSION_INPUT,
        output: FILE_SESSION_OUTPUT,
      })
      const summary = summaryOf(store, SESSION_ID)
      expect(summary.total_input).toBe(FILE_SESSION_INPUT)
      expect(summary.total_output).toBe(FILE_SESSION_OUTPUT)
    } finally {
      store.close()
    }
  })

  it('keeps a plugin row whose session id differs from the file row in its own session, whose tokens read not measurable', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpan(PLUGIN_ONLY_SESSION_ID)], store, [droidAdapter])
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // Two sessions: the plugin row's id is not the file row's id, so the
      // two never share a session.
      expect(store.sessionCount()).toBe(2)

      // The file row's session keeps its own counters.
      expect(store.sessionTokenTotals(SESSION_ID)).toEqual({
        input: FILE_SESSION_INPUT,
        output: FILE_SESSION_OUTPUT,
      })

      // The plugin-only session reads not measurable, with a reason — never a
      // zero, and never the other session's value.
      expect(store.sessionTokenTotals(PLUGIN_ONLY_SESSION_ID)).toBeUndefined()
      const summary = summaryOf(store, PLUGIN_ONLY_SESSION_ID)
      expect(typeof summary.total_input).not.toBe('number')
      expect(summary.total_input).not.toBe(FILE_SESSION_INPUT)
      expect(summary.total_input).not.toBe(0)
      const reason = reasonOf(summary.total_input)
      expect(reason).toMatch(/droid/i)
      expect(reason).toMatch(/token/i)
    } finally {
      store.close()
    }
  })

  it('invents no token value that neither source supplied', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [pluginTurnSpan(PLUGIN_ONLY_SESSION_ID), pluginTurnSpan(SESSION_ID)],
        store,
        [droidAdapter],
      )
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // The paired session's total is the file row's counters, or no total at
      // all — never a zero and never a sum of both sources.
      const paired = store.sessionTokenTotals(SESSION_ID)
      if (paired !== undefined) {
        expect(paired, 'the paired session reports a total no source supplied').toEqual({
          input: FILE_SESSION_INPUT,
          output: FILE_SESSION_OUTPUT,
        })
      }
      expect(paired).not.toEqual({ input: 0, output: 0 })

      // The plugin-only session has no token value at all: not a zero, and
      // not a number carried over from the session that does have one.
      expect(store.sessionTokenTotals(PLUGIN_ONLY_SESSION_ID)).toBeUndefined()
      const pluginOnly = summaryOf(store, PLUGIN_ONLY_SESSION_ID)
      expect(typeof pluginOnly.total_input).not.toBe('number')
      expect(typeof pluginOnly.total_output).not.toBe('number')
      expect(pluginOnly.total_input).not.toBe(0)
      expect(pluginOnly.total_input).not.toBe(FILE_SESSION_INPUT)
      expect(pluginOnly.total_output).not.toBe(FILE_SESSION_OUTPUT)
    } finally {
      store.close()
    }
  })

  it('joins on exact session id equality, so a near-miss id stays its own session', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpan(NEAR_MISS_SESSION_ID)], store, [droidAdapter])
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // Two sessions: the near-miss id is not the file row's id, so no
      // fuzzy join and no time-window join may bring them together.
      expect(store.sessionCount()).toBe(2)
      expect(store.sessionTokenTotals(SESSION_ID)).toEqual({
        input: FILE_SESSION_INPUT,
        output: FILE_SESSION_OUTPUT,
      })
      expect(store.sessionTokenTotals(NEAR_MISS_SESSION_ID)).toBeUndefined()
      const nearMiss = summaryOf(store, NEAR_MISS_SESSION_ID)
      expect(typeof nearMiss.total_input).not.toBe('number')
      expect(nearMiss.total_input).not.toBe(FILE_SESSION_INPUT)
      expect(reasonOf(nearMiss.total_input)).toMatch(/token/i)
    } finally {
      store.close()
    }
  })

  it('puts a plugin row and a file row of the same session in one droid harness bucket', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpan(SESSION_ID)], store, [droidAdapter])
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // One bucket for Factory: the dashboard shows the harness once, with
      // the plugin-sourced row beside the file-sourced one.
      const counts = store.countSessionsByHarness()
      expect(counts['droid'] ?? 0).toBe(1)
      expect(Object.keys(counts)).toEqual(['droid'])
      const rows = [...store.iterateSessions()]
      expect(rows).toHaveLength(1)
      expect(rows[0]!.harness).toBe('droid')
    } finally {
      store.close()
    }
  })

  it('pairs a plugin row reported under the plugin\'s own service name with the file row, so the file row is no turn and the session duration is the file-source duration', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      // The plugin row arrives under the `service.name` the plugin itself
      // reports, which is what becomes the row's source.
      ingestBatchWithAdapters([pluginTurnSpan(SESSION_ID)], store, [droidAdapter])

      // The file row carries a duration of its own, so the session's duration
      // says which records the session added.
      const file = await fileRow(SESSION_ID)
      file.durationMs = FILE_ROW_DURATION_MS
      store.upsertMany([file])

      await buildSessions(store)

      const summary = summaryOf(store, SESSION_ID)

      // The file row is the COUNTER source of the turn the plugin source
      // reported: it is not a turn of its own, so it is not a timeline entry.
      expect(summary.turn_count).toBe(1)

      // Plugin spans are zero-width, so the session's duration is the sum of
      // its file-source durations: the file row's own duration, and not the
      // plugin turn's reported millis added on top.
      expect(summary.duration_ms).toBe(FILE_ROW_DURATION_MS)
      expect(summary.duration_ms).not.toBe(PLUGIN_TURN_DURATION_MS + FILE_ROW_DURATION_MS)
    } finally {
      store.close()
    }
  })
})

describe('the Factory child session pairing rule', () => {
  it('pairs a child session with the child own companion file, so the child carries the child own usage', async () => {
    // The parent session is written the way Factory writes it: its own
    // transcript, its own companion settings file, and the child-usage map
    // that file carries as the parent's aggregate view of its children. The
    // map is not a token source.
    await writeFactorySession(SESSION_ID, {
      model: MODEL,
      tokenUsage: parentTokenUsage(),
      childInclusiveTokenUsageBySessionId: { [CHILD_SESSION_ID]: parentAggregateChildTokenUsage() },
    })

    // The child session is a session of its own on disk, exactly like any
    // other Factory session: its own transcript file, whose session_start
    // names the child session id, and its own companion settings file,
    // carrying the child's own counters.
    await writeFactorySession(CHILD_SESSION_ID, { model: MODEL, tokenUsage: childTokenUsage() })

    // The file source DISCOVERS the child as a session of its own — the
    // child's own transcript file, on disk under the child's own session id.
    const discovered = await createDroidProvider().discoverSessions()
    expect(
      discovered.some((candidate) => candidate.path.endsWith(`/${CHILD_SESSION_ID}.jsonl`)),
      'the file source discovered the child session on disk',
    ).toBe(true)

    const store = new CanonStore(':memory:')
    try {
      // The plugin names the child session the only way it can: through its
      // OWN turn span. The hooks fire inside the child's own session, so the
      // turn's `factory.session.id` IS the child's session id. The plugin
      // names no parent for it — it cannot, and the child needs no parent
      // to be a session of its own.
      ingestBatchWithAdapters(
        [
          pluginTurnSpan(CHILD_SESSION_ID, '2026-04-20T10:00:04.000Z'),
          pluginTurnSpan(SESSION_ID, '2026-04-20T10:00:03.000Z'),
        ],
        store,
        [droidAdapter],
      )
      store.upsertMany([await fileRow(SESSION_ID), await fileRow(CHILD_SESSION_ID)])

      await buildSessions(store)

      // Two sessions, not one: the child is a session of its own, and the
      // file source discovered it as one.
      expect(store.sessionCount()).toBe(2)

      // The child session is carried by the plugin's ordinary turn span for
      // the child — a turn like any other, naming the child's own session
      // id, and by nothing that names a parent for it.
      const childTurn = store.get(`plugin-turn-${CHILD_SESSION_ID}`)
      expect(childTurn, "the plugin's own turn span must carry the child session").toBeDefined()
      expect(childTurn?.sessionId).toBe(CHILD_SESSION_ID)
      expect(childTurn?.op).toBe('llm.invoke')
      const childSession = store.getSession(CHILD_SESSION_ID)
      expect(childSession?.harness).toBe('droid')
      // Nothing links the child to the parent session: the child is a
      // session of its own, not a child of the parent's session.
      expect(childSession?.parentSession).toBeNull()
      expect(childSession?.isSubagent).toBe(false)

      // The child session is paired with the child's OWN usage, read from
      // the child's own companion file.
      expect(store.sessionTokenTotals(CHILD_SESSION_ID)).toEqual({
        input: CHILD_SESSION_INPUT,
        output: CHILD_SESSION_OUTPUT,
      })
      const child = summaryOf(store, CHILD_SESSION_ID)
      expect(child.total_input).toBe(CHILD_SESSION_INPUT)
      expect(child.total_output).toBe(CHILD_SESSION_OUTPUT)

      // And the child never shows the parent's counters, nor the aggregate
      // the parent's child-usage map carries — neither the map's own
      // counters nor the session totals they would produce.
      expect(child.total_input).not.toBe(FILE_SESSION_INPUT)
      expect(child.total_output).not.toBe(FILE_SESSION_OUTPUT)
      expect(child.total_input).not.toBe(AGGREGATE_SESSION_INPUT)
      expect(child.total_output).not.toBe(AGGREGATE_SESSION_OUTPUT)
      expect(child.total_input).not.toBe(MAP_CHILD_INPUT)
      expect(child.total_output).not.toBe(MAP_CHILD_OUTPUT)

      // The parent's own counters are unaffected by the child's presence:
      // the parent's session still carries the parent's own usage, not the
      // child's and not a sum of both.
      expect(store.sessionTokenTotals(SESSION_ID)).toEqual({
        input: FILE_SESSION_INPUT,
        output: FILE_SESSION_OUTPUT,
      })
      const parent = summaryOf(store, SESSION_ID)
      expect(parent.total_input).toBe(FILE_SESSION_INPUT)
      expect(parent.total_output).toBe(FILE_SESSION_OUTPUT)
      expect(parent.total_input).not.toBe(CHILD_SESSION_INPUT)
      expect(parent.total_output).not.toBe(CHILD_SESSION_OUTPUT)
      expect(parent.total_input).not.toBe(AGGREGATE_SESSION_INPUT)
      expect(parent.total_output).not.toBe(AGGREGATE_SESSION_OUTPUT)
    } finally {
      store.close()
    }
  })

  it('reads a child session whose own companion file reports no usage as not measurable, with a reason', async () => {
    // The parent's companion file names that child in its child-usage map,
    // and the map holds figures that are not the child's own. The map is the
    // parent's aggregate view, not a token source.
    await writeFactorySession(SESSION_ID, {
      model: MODEL,
      tokenUsage: parentTokenUsage(),
      childInclusiveTokenUsageBySessionId: { [UNMAPPED_CHILD_SESSION_ID]: parentAggregateChildTokenUsage() },
    })

    // The child session is on disk as a session of its own, but its own
    // companion settings file reports no usage, so the file source measures
    // nothing for it.
    await writeFactorySession(UNMAPPED_CHILD_SESSION_ID, { model: MODEL })

    // The file source DISCOVERS that child — it is a session of its own on
    // disk — so what leaves the child unmeasurable is its own companion file
    // reporting no usage, not the child being invisible to the file source.
    const provider = createDroidProvider()
    const discovered = await provider.discoverSessions()
    expect(
      discovered.some((candidate) => candidate.path.endsWith(`/${UNMAPPED_CHILD_SESSION_ID}.jsonl`)),
      'the file source discovered the child session on disk',
    ).toBe(true)

    const store = new CanonStore(':memory:')
    try {
      // The plugin names the child session through its OWN turn span — the
      // hooks fire inside the child's own session, so the turn's
      // `factory.session.id` IS the child's session id. The plugin names no
      // parent for it, so nothing links the child to the parent.
      ingestBatchWithAdapters(
        [pluginTurnSpan(UNMAPPED_CHILD_SESSION_ID, '2026-04-20T10:00:05.000Z')],
        store,
        [droidAdapter],
      )
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // The child session exists because of the plugin's own turn span for
      // the child, and it is a session of its own: nothing links it to the
      // parent session, so it borrows nothing from the parent either.
      const childTurn = store.get(`plugin-turn-${UNMAPPED_CHILD_SESSION_ID}`)
      expect(childTurn, "the plugin's own turn span must carry the child session").toBeDefined()
      expect(childTurn?.sessionId).toBe(UNMAPPED_CHILD_SESSION_ID)
      const childSession = store.getSession(UNMAPPED_CHILD_SESSION_ID)
      expect(childSession?.harness).toBe('droid')
      expect(childSession?.parentSession).toBeNull()
      expect(childSession?.isSubagent).toBe(false)

      // Nothing measured the child, so it reads not measurable with a
      // reason, exactly as any other session that measured nothing: no
      // total at all, and a declaration that says so.
      expect(store.sessionTokenTotals(UNMAPPED_CHILD_SESSION_ID)).toBeUndefined()
      const summary = summaryOf(store, UNMAPPED_CHILD_SESSION_ID)
      expect(typeof summary.total_input).not.toBe('number')
      expect(typeof summary.total_output).not.toBe('number')
      const reason = reasonOf(summary.total_input)
      expect(reason).toMatch(/droid/i)
      expect(reason).toMatch(/token/i)

      // It borrows nothing: not the parent's counters, not the child's own
      // counters, and not the aggregate the parent's map carries — neither
      // the map's own counters nor the session totals they would produce.
      // The parent's own totals are untouched by the child that measured
      // nothing.
      expect(summary.total_input).not.toBe(FILE_SESSION_INPUT)
      expect(summary.total_output).not.toBe(FILE_SESSION_OUTPUT)
      expect(summary.total_input).not.toBe(CHILD_SESSION_INPUT)
      expect(summary.total_output).not.toBe(CHILD_SESSION_OUTPUT)
      expect(summary.total_input).not.toBe(AGGREGATE_SESSION_INPUT)
      expect(summary.total_output).not.toBe(AGGREGATE_SESSION_OUTPUT)
      expect(summary.total_input).not.toBe(MAP_CHILD_INPUT)
      expect(summary.total_output).not.toBe(MAP_CHILD_OUTPUT)
      expect(store.sessionTokenTotals(SESSION_ID)).toEqual({
        input: FILE_SESSION_INPUT,
        output: FILE_SESSION_OUTPUT,
      })
    } finally {
      store.close()
    }
  })
})

/** The session's context analysis, as the dashboard projects it. */
function contextOf(store: CanonStore, sessionId: string): Record<string, unknown> {
  const payload = store.getSessionPayload(sessionId) as
    | { context?: Record<string, unknown> }
    | undefined
  if (payload === undefined) throw new Error(`no session payload was projected for ${sessionId}`)
  return payload.context ?? {}
}

/** The session's cost block, as the dashboard projects it. */
function costOf(store: CanonStore, sessionId: string): Record<string, unknown> {
  const cost = summaryOf(store, sessionId)['cost']
  if (typeof cost !== 'object' || cost === null) {
    throw new Error(`the session ${sessionId} carries no cost block`)
  }
  return cost as Record<string, unknown>
}

/** The context bucket's reported input, as the dashboard projects it. */
function reportedInputOf(bucket: unknown): unknown {
  if (typeof bucket !== 'object' || bucket === null) {
    throw new Error('the session carries no context bucket')
  }
  return (bucket as { reported_input?: unknown }).reported_input
}

describe('the Factory source precedence rule', () => {
  it('takes the turns and the session structure from the plugin records and the counters from the file records', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpan(SESSION_ID)], store, [droidAdapter])
      // The file row's duration is set explicitly so the session's duration is
      // a known figure: it is the sum of the file-source durations.
      const file = await fileRow(SESSION_ID)
      file.durationMs = FILE_ROW_DURATION_MS
      store.upsertMany([file])

      await buildSessions(store)

      const summary = summaryOf(store, SESSION_ID)

      // The plugin source knows the turns: the plugin's one turn is the
      // session's one turn. The file row is the counter source of that same
      // call, not a second turn of it.
      expect(summary.turn_count).toBe(1)

      // The session's duration is the sum of its file-source durations; the
      // plugin turn's zero-width span adds nothing to it.
      expect(summary.duration_ms).toBe(FILE_ROW_DURATION_MS)

      // The file source knows the counters: the paired session reports the
      // file row's own totals and their own availability, never the plugin
      // row's absent ones.
      expect(summary.total_input).toBe(FILE_SESSION_INPUT)
      expect(summary.total_output).toBe(FILE_SESSION_OUTPUT)
      expect(summary.total_cache_read).toBe(FILE_CACHE_READ)
      expect(summary.total_cache_creation).toBe(FILE_CACHE_CREATION)
      expect(store.sessionTokenTotals(SESSION_ID)).toEqual({
        input: FILE_SESSION_INPUT,
        output: FILE_SESSION_OUTPUT,
      })
    } finally {
      store.close()
    }
  })

  it('sums the cost of both sources into one figure', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const file = await fileRow(SESSION_ID)
    if (typeof file.cost.value !== 'number') {
      throw new Error('the file source did not price the Factory session')
    }
    const priced = file.cost.value

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpan(SESSION_ID)], store, [droidAdapter])
      store.upsertMany([file])

      await buildSessions(store)

      // The two sources are summed, never blended and never refused: the
      // file source priced the session and the plugin source had no rate to
      // add, so the figure the dashboard shows is the priced one — not a
      // blank, and not a zero.
      const cost = costOf(store, SESSION_ID)
      expect(cost['basis']).toBe('harness')
      expect(['priced', 'partial']).toContain(cost['status'])
      expect(cost['currency']).toBe('USD')
      expect(cost['value']).toBeCloseTo(priced, 12)
    } finally {
      store.close()
    }
  })

  it('falls back to the file counters for the context input the plugin turns do not report', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpan(SESSION_ID)], store, [droidAdapter])
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // The plugin's turn reports no input at all, so the context analysis
      // has no input of its own to chart. It falls back to the file
      // counters: the session's context input is the file row's, not a zero
      // and not the plugin's absent counter.
      const context = contextOf(store, SESSION_ID)
      expect(reportedInputOf(context['first'])).toBe(FILE_SESSION_INPUT)
      expect(reportedInputOf(context['last'])).toBe(FILE_SESSION_INPUT)
    } finally {
      store.close()
    }
  })

  it('leaves a session only the file source observed with its own turn, counters and priced cost', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const file = await fileRow(SESSION_ID)
    if (typeof file.cost.value !== 'number') {
      throw new Error('the file source did not price the Factory session')
    }
    const priced = file.cost.value

    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([file])

      await buildSessions(store)

      // No plugin record joins this session, so the file row stays the
      // session's own turn and the session's own counters: the precedence
      // rule must not empty a session the plugin never observed.
      const summary = summaryOf(store, SESSION_ID)
      expect(summary.turn_count).toBe(1)
      expect(summary.total_input).toBe(FILE_SESSION_INPUT)
      expect(summary.total_output).toBe(FILE_SESSION_OUTPUT)
      expect(store.sessionTokenTotals(SESSION_ID)).toEqual({
        input: FILE_SESSION_INPUT,
        output: FILE_SESSION_OUTPUT,
      })

      // And the cost the file source priced is still the session's cost.
      const cost = costOf(store, SESSION_ID)
      expect(cost['basis']).toBe('harness')
      expect(cost['status']).toBe('priced')
      expect(cost['value']).toBeCloseTo(priced, 12)

      // The context input comes from the same counters.
      const context = contextOf(store, SESSION_ID)
      expect(reportedInputOf(context['first'])).toBe(FILE_SESSION_INPUT)
    } finally {
      store.close()
    }
  })

  it('publishes the model the file source knows when the plugin turn names none', async () => {
    await writeFactorySession(SESSION_ID, { model: FILE_MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpanWithoutModel(SESSION_ID)], store, [droidAdapter])
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // Factory's own turn payload names no model, so the session's model is
      // the one the source that knows it published: the companion file's,
      // under its own name and not under the wrapper the file wrote.
      expect(summaryOf(store, SESSION_ID)['models']).toEqual([FILE_MODEL_PUBLISHED])
    } finally {
      store.close()
    }
  })

  it('publishes the turn model of a paired session and not the file source model beside it', async () => {
    await writeFactorySession(SESSION_ID, { model: FILE_MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([pluginTurnSpan(SESSION_ID)], store, [droidAdapter])
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // The paired session's turn is the plugin's own turn, so the model that
      // turn names is the session's model. The file row is the counter source
      // of that same call, not a second turn of it, so the model its
      // companion file names is not a second model of the session.
      expect(summaryOf(store, SESSION_ID)['models']).toEqual([PLUGIN_MODEL])
      expect(publishedTurns(store, SESSION_ID).map((turn) => turn['model'])).not.toContain(
        FILE_MODEL_PUBLISHED,
      )
    } finally {
      store.close()
    }
  })

  it('publishes no model for a plugin session whose own turn names none', async () => {
    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [pluginTurnSpanWithoutModel(PLUGIN_ONLY_SESSION_ID)],
        store,
        [droidAdapter],
      )

      await buildSessions(store)

      // The plugin's turn names no model and no other source observed this
      // session, so the session publishes none: no model in the summary, and
      // no model on its turn.
      expect(summaryOf(store, PLUGIN_ONLY_SESSION_ID)['models']).toEqual([])
      const turns = publishedTurns(store, PLUGIN_ONLY_SESSION_ID)
      expect(turns).toHaveLength(1)
      expect(turns[0]?.['model'] ?? null).toBeNull()

      // And the record itself carries no model attribute: the adapter never
      // defaults one, so an absent model stays absent all the way down.
      const record = store.get(`plugin-turn-${PLUGIN_ONLY_SESSION_ID}`)
      const raw = record?.raw
      expect(raw, 'the plugin record was stored').toBeTypeOf('object')
      const attributes = (raw ?? {}) as Record<string, unknown>
      expect(
        Object.keys(attributes).filter((key) => key.includes('model')),
        'the plugin turn carries no model attribute',
      ).toEqual([])
    } finally {
      store.close()
    }
  })

  it('leaves a session only the file source observed with the model its companion file names', async () => {
    await writeFactorySession(SESSION_ID, { model: FILE_MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // No plugin record joins this session, so the file row stays the
      // session's own turn and the session's own model: the precedence rule
      // must not empty or rename what the plugin never observed.
      expect(summaryOf(store, SESSION_ID)['models']).toEqual([FILE_MODEL_PUBLISHED])
    } finally {
      store.close()
    }
  })
})

// ---------------------------------------------------------------------------
// Tools, duration and title for paired Droid sessions
// ---------------------------------------------------------------------------

/** A zeroed token usage, for a tool record that carries no tokens of its own. */
const ZERO_TOKENS = {
  freshInput: 0,
  cacheRead: 0,
  cacheCreation: 0,
  output: 0,
  reportedInput: 0,
  reportedOutput: 0,
}

/**
 * One file-source `tool.invoke` record under a parent file-source record.
 * Built from the parent so the tool carries the same harness and source the
 * file row does; the parent's counters are not repeated on the tool. The tool
 * is not parented under the file row: a nested session's duration is its
 * request roots' only, and these cases measure the tools' own durations.
 */
function fileToolRowOf(
  parent: CanonicalRecord,
  index: number,
  name: string,
  durationMs: number,
): CanonicalRecord {
  return {
    ...parent,
    spanId: `${parent.spanId}:tool:${index}`,
    parentSpanId: null,
    name,
    op: 'tool.invoke',
    durationMs,
    tokens: ZERO_TOKENS,
  }
}

/** A plugin span for one tool call, carrying the tool name the plugin reports. */
function pluginToolSpan(sessionId: string, toolName: string): OtlpSpan {
  return {
    ...otlpSpan(
      `plugin-tool-${sessionId}-${toolName}`,
      'factory.tool',
      { ...pluginAttributes(sessionId), 'factory.tool.name': toolName },
      '2026-04-20T10:00:03.000Z',
    ),
    durationMs: 0,
  }
}

/** A plugin turn span whose own duration is zero, as the plugin reports it. */
function zeroDurationPluginTurnSpan(sessionId: string): OtlpSpan {
  return { ...pluginTurnSpan(sessionId), durationMs: 0 }
}

describe('paired droid tools, duration and title', () => {
  it('counts the file tool.invoke records of a paired session, not the single plugin tool span', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [zeroDurationPluginTurnSpan(SESSION_ID), pluginToolSpan(SESSION_ID, 'Read')],
        store,
        [droidAdapter],
      )
      const parent = await fileRow(SESSION_ID)
      store.upsertMany([
        parent,
        fileToolRowOf(parent, 1, 'Read', 0),
        fileToolRowOf(parent, 2, 'Edit', 0),
        fileToolRowOf(parent, 3, 'Execute', 0),
      ])

      await buildSessions(store)

      const summary = summaryOf(store, SESSION_ID)
      expect(summary.tool_calls, 'tool calls counted from the file records').toBe(3)
      expect([...(summary.tools_invoked as string[])].sort()).toEqual(['Edit', 'Execute', 'Read'])
    } finally {
      store.close()
    }
  })

  it('keeps the plugin tool count for a paired session that has no file tool record', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [zeroDurationPluginTurnSpan(SESSION_ID), pluginToolSpan(SESSION_ID, 'Read')],
        store,
        [droidAdapter],
      )
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      expect(summaryOf(store, SESSION_ID).tool_calls).toBe(1)
    } finally {
      store.close()
    }
  })

  it('sums the file-source durations for a paired session, while every plugin record reports zero', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [zeroDurationPluginTurnSpan(SESSION_ID), pluginToolSpan(SESSION_ID, 'Read')],
        store,
        [droidAdapter],
      )
      const parent = { ...(await fileRow(SESSION_ID)), durationMs: 0 }
      store.upsertMany([
        parent,
        fileToolRowOf(parent, 1, 'Read', 1500),
        fileToolRowOf(parent, 2, 'Edit', 2500),
        fileToolRowOf(parent, 3, 'Execute', 500),
      ])

      await buildSessions(store)

      expect(summaryOf(store, SESSION_ID).duration_ms).toBe(4500)
    } finally {
      store.close()
    }
  })

  it('keeps the tool count and duration it has today for a Droid session only the file source observed', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      const parent = { ...(await fileRow(SESSION_ID)), durationMs: 0 }
      store.upsertMany([
        parent,
        fileToolRowOf(parent, 1, 'Read', 1500),
        fileToolRowOf(parent, 2, 'Edit', 2500),
      ])

      await buildSessions(store)

      const summary = summaryOf(store, SESSION_ID)
      expect(summary.tool_calls).toBe(2)
      expect(summary.duration_ms).toBe(4000)
    } finally {
      store.close()
    }
  })

  it('keeps the tool count and duration it has today for a non-Droid session', async () => {
    const CLAUDE_SESSION_ID = 'claude-session-1'
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      const fileParent = await fileRow(SESSION_ID)
      const parent: CanonicalRecord = {
        ...fileParent,
        spanId: 'claude-llm-1',
        parentSpanId: null,
        harness: 'claude',
        source: 'codeburn/claude',
        sessionId: CLAUDE_SESSION_ID,
        durationMs: 0,
      }
      store.upsertMany([
        parent,
        fileToolRowOf(parent, 1, 'Read', 300),
        fileToolRowOf(parent, 2, 'Bash', 700),
      ])

      await buildSessions(store)

      const summary = summaryOf(store, CLAUDE_SESSION_ID)
      expect(summary.tool_calls).toBe(2)
      expect(summary.duration_ms).toBe(1000)
    } finally {
      store.close()
    }
  })

  it('carries the session title from the file record into the session label', async () => {
    await writeFactorySession(SESSION_ID, { model: MODEL, tokenUsage: parentTokenUsage() })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters([zeroDurationPluginTurnSpan(SESSION_ID)], store, [droidAdapter])
      const file: CanonicalRecord = { ...(await fileRow(SESSION_ID)), raw: { [SESSION_LABEL_KEY]: 'Fix login bug' } }
      store.upsertMany([file])

      await buildSessions(store)

      const payload = store.getSessionPayload(SESSION_ID) as { label?: unknown } | undefined
      expect(payload?.label).toBe('Fix login bug')
    } finally {
      store.close()
    }
  })

  it('projects kyber.session.label through the store attribute allowlist', () => {
    expect(PROJECTED_ATTRIBUTE_KEYS.has(SESSION_LABEL_KEY)).toBe(true)
  })
})
