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
//   * a child session is paired only when the companion file's
//     `childInclusiveTokenUsageBySessionId` names it;
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
import type { CanonicalRecord } from './types.js'

/** The Factory session both sources observed. */
const SESSION_ID = 'factory-session-1'
/** A session only the plugin observed. */
const PLUGIN_ONLY_SESSION_ID = 'factory-session-plugin-only'
/** A session id one character away from the file row's — the near miss. */
const NEAR_MISS_SESSION_ID = `${SESSION_ID}-2`
/** The child session the companion file names. */
const CHILD_SESSION_ID = 'factory-child-1'
/** A child session the companion file does not name. */
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

/** The telemetry source the Kyber-owned Factory plugin's export arrives under. */
const PLUGIN_SOURCE = 'kyberdash-droid-plugin'

/** The contract version the Kyber-owned plugin emits. */
const CONTRACT_VERSION = '1'

/** The model the companion file names for the Factory session. */
const MODEL = 'custom:gpt-5-[Proxy]-0'

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

/** The companion settings file's counters for the child session. */
function childTokenUsage(): Record<string, number> {
  return {
    inputTokens: CHILD_INPUT,
    outputTokens: CHILD_OUTPUT,
    cacheCreationTokens: CHILD_CACHE_CREATION,
    cacheReadTokens: CHILD_CACHE_READ,
    thinkingTokens: CHILD_THINKING,
  }
}

/** The plugin envelope's attributes for one session. */
function pluginAttributes(sessionId: string, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    'factory.contract_version': CONTRACT_VERSION,
    'factory.session.id': sessionId,
    'factory.model.id': 'gpt-5-codex',
    ...over,
  }
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
    durationMs: 1000,
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

/** The plugin's child-session span: the child's id and its parent's id. */
function pluginChildSpan(childSessionId: string, parentSessionId: string): OtlpSpan {
  return otlpSpan(
    `plugin-child-${childSessionId}`,
    'factory.child_session',
    pluginAttributes(childSessionId, { 'factory.parent.session.id': parentSessionId }),
    '2026-04-20T10:00:03.000Z',
  )
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
})

describe('the Factory child session pairing rule', () => {
  it('pairs a child session the companion file names with that child own usage', async () => {
    await writeFactorySession(SESSION_ID, {
      model: MODEL,
      tokenUsage: parentTokenUsage(),
      childInclusiveTokenUsageBySessionId: { [CHILD_SESSION_ID]: childTokenUsage() },
    })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [
          pluginChildSpan(CHILD_SESSION_ID, SESSION_ID),
          pluginTurnSpan(CHILD_SESSION_ID, '2026-04-20T10:00:04.000Z'),
        ],
        store,
        [droidAdapter],
      )
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // The child session carries the child's own usage, read from the
      // companion file's child map — never the parent's counters.
      expect(store.sessionTokenTotals(CHILD_SESSION_ID)).toEqual({
        input: CHILD_SESSION_INPUT,
        output: CHILD_SESSION_OUTPUT,
      })
    } finally {
      store.close()
    }
  })

  it('reads a child session the companion file does not name as not measurable, with a reason', async () => {
    await writeFactorySession(SESSION_ID, {
      model: MODEL,
      tokenUsage: parentTokenUsage(),
      childInclusiveTokenUsageBySessionId: { [CHILD_SESSION_ID]: childTokenUsage() },
    })

    const store = new CanonStore(':memory:')
    try {
      ingestBatchWithAdapters(
        [
          pluginChildSpan(UNMAPPED_CHILD_SESSION_ID, SESSION_ID),
          pluginTurnSpan(UNMAPPED_CHILD_SESSION_ID, '2026-04-20T10:00:05.000Z'),
        ],
        store,
        [droidAdapter],
      )
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      // The companion file names no such child, so nothing pairs it: it
      // reads not measurable with a reason, and never borrows the parent's
      // or the named child's counters.
      expect(store.sessionTokenTotals(UNMAPPED_CHILD_SESSION_ID)).toBeUndefined()
      const summary = summaryOf(store, UNMAPPED_CHILD_SESSION_ID)
      expect(typeof summary.total_input).not.toBe('number')
      expect(summary.total_input).not.toBe(FILE_SESSION_INPUT)
      expect(summary.total_input).not.toBe(CHILD_SESSION_INPUT)
      const reason = reasonOf(summary.total_input)
      expect(reason).toMatch(/droid/i)
      expect(reason).toMatch(/token/i)
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
      store.upsertMany([await fileRow(SESSION_ID)])

      await buildSessions(store)

      const summary = summaryOf(store, SESSION_ID)

      // The plugin source knows the turns: the plugin's one turn is the
      // session's one turn. The file row is the counter source of that same
      // call, not a second turn of it.
      expect(summary.turn_count).toBe(1)

      // And the plugin source owns the session's structure: its span carries
      // the session's duration.
      expect(summary.duration_ms).toBe(1000)

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
})
