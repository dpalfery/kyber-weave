// Contract tests for the OpenCode live-gate comparator (dash/scripts/opencode-live-gate/compare.mjs).
//
// The live gate runs a real OpenCode session, reads kyberDash's session API and OpenCode's own
// database read-only, and asks the comparator whether the two agree line by line. These tests pin
// that contract before the comparator exists, so they fail until compare.mjs is written (RED).
//
// Exported API the tests import from ./compare.mjs:
//
//   compareSession({ kyberSession, opencode, gate }) -> { pass, rows }
//
//   kyberSession  parsed JSON of GET /api/kyber/session/<id>. Shape is today's API plus the fields
//                 the rebuild adds (see fixtures/v2/runA.kyberdash-api-session.v3-expected.json).
//                 A missing field is a failure, never a skip.
//   opencode      OpenCodeReading (below): what the gate read from OpenCode's database.
//   gate          GateRun (below): what the gate sent and measured.
//   pass          true only when every row passes.
//   rows          [{ field, kyberdash, opencode, pass, reason }], one per comparator line.
//                 A failing row names its field; the tests assert on `field`.
//
// Field names used in rows: total_input, total_output, turns_reasoning, total_cache_read,
// total_cache_creation, cost, duration_ms, request_count, turn_count, requests[].turns,
// requests[<i>].turns, tools_offered, tool_calls, tools_invoked, context.measurable,
// system_prompt, requests[<i>].prompt, skills.listed, skills.invoked, models, label.

import { readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import { compareSession } from './compare.mjs'

/** One row of OpenCode's session_v2 table, named by its columns. */
export interface SessionV2Row {
  tokens_input: number
  tokens_output: number
  tokens_reasoning: number
  tokens_cache_read: number
  tokens_cache_write: number
  cost: number
}

/** Token sums for one assistant message. */
export interface MessageTokens {
  input: number
  output: number
  reasoning: number
  cache_read: number
  cache_write: number
}

/** What the live gate read from OpenCode's database, read-only. */
export interface OpenCodeReading {
  /** The session_v2 row for the session under test. */
  session: SessionV2Row
  /** Count of user session_message rows: prompts the session recorded. */
  userMessages: number
  /** One entry per assistant session_message row, in order, with that message's token sums. */
  assistantMessages: MessageTokens[]
  /** Tool parts found in assistant content, in order. */
  toolParts: Array<{ name: string; status: string }>
  /** Model ids on the assistant messages. */
  models: string[]
}

/** What the gate sent and measured. */
export interface GateRun {
  /** Prompt texts sent, in order. */
  prompts: string[]
  /** The skill the prompts attach or load. */
  skill: string
  /** Measured wall-clock for all prompts together, in milliseconds. */
  wallClockMs: number
  /** The model the session was expected to use. */
  expectedModel: string
  /** The title the session was given. */
  expectedTitle: string
}

export interface CompareRow {
  field: string
  kyberdash: unknown
  opencode: unknown
  pass: boolean
  reason: string
}

export interface CompareResult {
  pass: boolean
  rows: CompareRow[]
}

/**
 * The parts of the session API the comparator reads. Every key is optional on purpose: a missing
 * key is exactly what the failure cases below construct, and the comparator must name it.
 */
interface KyberSession {
  label?: unknown
  summary: {
    turn_count?: unknown
    request_count?: unknown
    total_input?: unknown
    total_output?: unknown
    total_cache_read?: unknown
    total_cache_creation?: unknown
    duration_ms?: unknown
    models?: unknown
    cost?: unknown
    tools_offered?: unknown
    tool_calls?: unknown
    tools_invoked?: unknown
  }
  turns?: Array<{ reasoning?: unknown }>
  requests?: Array<{ turns?: unknown; prompt?: unknown }>
  context?: { measurable?: unknown; first?: { buckets?: Record<string, unknown> } }
  skills?: {
    measurable?: unknown
    rows?: Array<{ name: string; listed?: unknown; invocations?: unknown }>
  }
}

const HERE = path.dirname(fileURLToPath(import.meta.url))
const FIXTURES = path.resolve(HERE, '../../src/capture/harnesses/opencode-plugin/fixtures/v2')

/**
 * The prompt the runA session was sent: prompt 1 of the live gate, quotes
 * included. OpenCode's own hook event carries the quotes literally, and the
 * projection previews them as recorded (architect ruling D27).
 */
const RUN_A_PROMPT =
  "\"Load the skill named kw-probe-skill using the skill tool, then read the file notes.txt with the read tool, then follow the skill's instruction. Do not do anything else.\""

/*
 * The runA pair is the two JSON files. runA.opencode-db.json carries the session totals and the
 * three steps only, so the message counts, tool parts and model id below come from the captured
 * bus events and context: the prompt hook (runA.events.jsonl line 3), the two tool calls and their
 * completions (lines 17 and 20, 33 and 37), and the model on each step (runA.context.json). The live
 * gate reads these from the database itself; these constants are the fixture stand-in until the
 * database fixture carries them.
 */
const RUN_A_USER_MESSAGES = 1
const RUN_A_MODELS = ['muse-spark-1.3-contributor']
const RUN_A_TOOL_PARTS = [
  { name: 'skill', status: 'completed' },
  { name: 'read', status: 'completed' },
]

/**
 * Wall-clock from session.created (runA.events.jsonl line 1) to the last step.ended (line 49), which
 * is 13376 ms. The gate's own timer for a live run replaces this figure.
 */
const RUN_A_WALL_CLOCK_MS = 13376

const RUN_A_GATE: GateRun = {
  prompts: [RUN_A_PROMPT],
  skill: 'kw-probe-skill',
  wallClockMs: RUN_A_WALL_CLOCK_MS,
  expectedModel: 'muse-spark-1.3-contributor',
  expectedTitle: 'kw-probe runA shared-service',
}

/** Every comparator line the passing runA pair must report, by field name. */
const COMPARATOR_FIELDS = [
  'total_input',
  'total_output',
  'turns_reasoning',
  'total_cache_read',
  'total_cache_creation',
  'cost',
  'duration_ms',
  'request_count',
  'turn_count',
  'requests[].turns',
  'requests[0].turns',
  'tools_offered',
  'tool_calls',
  'tools_invoked',
  'context.measurable',
  'system_prompt',
  'requests[0].prompt',
  'skills.listed',
  'skills.invoked',
  'models',
  'label',
]

function loadJson<T>(file: string): T {
  return JSON.parse(readFileSync(path.join(FIXTURES, file), 'utf8')) as T
}

/** The runA OpenCode reading, built from runA.opencode-db.json and the constants above. */
function readRunAOpenCode(): OpenCodeReading {
  const db = loadJson<RunADbFixture>('runA.opencode-db.json')
  return {
    session: {
      tokens_input: db.totals.input,
      tokens_output: db.totals.output,
      tokens_reasoning: db.totals.reasoning,
      tokens_cache_read: db.totals.cache_read,
      tokens_cache_write: db.totals.cache_write,
      cost: db.totals.cost,
    },
    userMessages: RUN_A_USER_MESSAGES,
    assistantMessages: db.steps.map((step) => ({
      input: step.input,
      output: step.output,
      reasoning: step.reasoning,
      cache_read: step.cache_read,
      cache_write: step.cache_write,
    })),
    toolParts: RUN_A_TOOL_PARTS.map((part) => ({ ...part })),
    models: [...RUN_A_MODELS],
  }
}

interface RunADbFixture {
  totals: { input: number; output: number; reasoning: number; cache_read: number; cache_write: number; cost: number }
  steps: Array<{
    assistantMessageID: string
    finish: string
    cost: number
    input: number
    output: number
    reasoning: number
    cache_read: number
    cache_write: number
  }>
}

function passingKyberSession(): KyberSession {
  return loadJson<KyberSession>('runA.kyberdash-api-session.v3-expected.json')
}

/** A deep copy of the passing session with one mutation applied. */
function mutated(mutate: (session: KyberSession) => void): KyberSession {
  const session = structuredClone(passingKyberSession())
  mutate(session)
  return session
}

function compare(kyberSession: unknown, opencode: OpenCodeReading = readRunAOpenCode(), gate: GateRun = RUN_A_GATE): CompareResult {
  return compareSession({ kyberSession, opencode, gate }) as CompareResult
}

function failingFields(result: CompareResult): string[] {
  return result.rows.filter((row) => !row.pass).map((row) => row.field)
}

function failureReport(result: CompareResult): string {
  return result.rows
    .filter((row) => !row.pass)
    .map((row) => `${row.field}: ${row.reason}`)
    .join('\n')
}

describe('OpenCode live-gate comparator', () => {
  describe('runA pair', () => {
    it('the runA database fixture steps add up to its session totals', () => {
      const db = loadJson<RunADbFixture>('runA.opencode-db.json')
      const sum = (key: 'input' | 'output' | 'reasoning' | 'cache_read' | 'cache_write') =>
        db.steps.reduce((total, step) => total + step[key], 0)

      expect(sum('input')).toBe(db.totals.input)
      expect(sum('output')).toBe(db.totals.output)
      expect(sum('reasoning')).toBe(db.totals.reasoning)
      expect(sum('cache_read')).toBe(db.totals.cache_read)
      expect(sum('cache_write')).toBe(db.totals.cache_write)
      expect(db.steps).toHaveLength(3)
    })

    it('a v3 expected session passes every comparator line against the runA database reading', () => {
      const result = compare(passingKyberSession())

      expect(failureReport(result), 'a passing pair must report no failing line').toBe('')
      expect(result.pass).toBe(true)
    })

    it('the passing report carries every comparator line under the field name the failure cases use', () => {
      const fields = compare(passingKyberSession()).rows.map((row) => row.field)

      expect(fields, `rows: ${fields.join(', ')}`).toEqual(expect.arrayContaining(COMPARATOR_FIELDS))
    })

    it('the runA v0 API shape fails the comparator on the fields it does not carry yet', () => {
      const v0 = loadJson<unknown>('runA.kyberdash-api-session.v0.json')
      const result = compare(v0)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toEqual(
        expect.arrayContaining(['total_input', 'cost', 'duration_ms', 'tools_offered', 'skills.listed', 'skills.invoked', 'system_prompt', 'label']),
      )
    })
  })

  describe('a failing line names its field', () => {
    it('a missing total_cache_read fails the comparator and names total_cache_read', () => {
      const session = mutated((s) => {
        delete s.summary.total_cache_read
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('total_cache_read')
    })

    it('counting cache reads out of total_input fails the comparator and names total_input', () => {
      const session = mutated((s) => {
        s.summary.total_input = 12633
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('total_input')
    })

    it('a cost that differs from session_v2.cost fails the comparator and names cost', () => {
      const session = mutated((s) => {
        s.summary.cost = { basis: 'harness', status: 'priced', value: 0.5, currency: 'USD' }
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('cost')
    })

    it('a zero duration_ms fails the comparator and names duration_ms', () => {
      const session = mutated((s) => {
        s.summary.duration_ms = 0
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('duration_ms')
    })

    it('a request that shows one turn while OpenCode recorded three assistant messages fails and names requests[0].turns', () => {
      const session = mutated((s) => {
        s.requests![0]!.turns = 1
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('requests[0].turns')
    })

    it('turn reasoning that does not sum to tokens_reasoning fails the comparator and names turns_reasoning', () => {
      const session = mutated((s) => {
        s.turns![0]!.reasoning = 0
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('turns_reasoning')
    })

    it('an empty tools_offered fails the comparator and names tools_offered', () => {
      const session = mutated((s) => {
        s.summary.tools_offered = []
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('tools_offered')
    })

    it('a tool name missing from tools_invoked fails the comparator and names tools_invoked', () => {
      const session = mutated((s) => {
        s.summary.tools_invoked = ['skill']
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('tools_invoked')
    })

    it('an OpenCode tool part without a name fails the comparator and names tool_calls', () => {
      const opencode = readRunAOpenCode()
      opencode.toolParts = [
        { name: 'skill', status: 'completed' },
        { name: '', status: 'completed' },
      ]
      const result = compare(passingKyberSession(), opencode)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('tool_calls')
    })

    it('a missing system prompt bucket fails the comparator and names system_prompt', () => {
      const session = mutated((s) => {
        delete s.context!.first!.buckets!.system_prompt
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('system_prompt')
    })

    it('a missing user prompt fails the comparator and names requests[0].prompt', () => {
      const session = mutated((s) => {
        s.requests![0]!.prompt = null
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('requests[0].prompt')
    })

    it('a skills listing without the gate skill fails the comparator and names skills.listed', () => {
      const session = mutated((s) => {
        s.skills!.rows = s.skills!.rows!.filter((row) => row.name !== 'kw-probe-skill')
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('skills.listed')
    })

    it('a gate skill that was listed but never invoked fails the comparator and names skills.invoked', () => {
      const session = mutated((s) => {
        const row = s.skills!.rows!.find((candidate) => candidate.name === 'kw-probe-skill')!
        row.invocations = 0
      })
      const result = compare(session)

      expect(result.pass).toBe(false)
      expect(failingFields(result), failureReport(result)).toContain('skills.invoked')
    })
  })
})
