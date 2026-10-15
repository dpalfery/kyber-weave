// Scale and equivalence for the skills listing fold (T7 blocker 2, RED).
//
// `effectiveListing` replays every session-level listing event for every turn:
// O(turns x events x skills). At 7,000 turns of 25 skills that is about 9.4 s per
// call, and buildSessionRow makes two such calls. The fold must become linear or
// near it, and it must not change a single answer. Two things are pinned here:
//
//   1. Budget. buildSkillsView and listedByTurn each complete a realistic
//      session within BUDGET_MS. The budget is generous (1500 ms) against a
//      target of a few tens of milliseconds, so it tolerates a slow CI runner
//      while still failing the quadratic fold by about six times over. Wall
//      time is the only instrument available without changing the engine, so
//      the assertion says so. Correctness is asserted first, so a fast wrong
//      answer cannot pass.
//   2. Semantics. A naive reference implementation, written in this file, is
//      compared against the engine on seeded random sequences. Any optimisation
//      must keep union for non-initial events, replace for initial events,
//      rollup listings applying to direct children, and NaN-time turns getting
//      no session-level listing.

import { describe, expect, it } from 'vitest'

import { turn } from './fixtures/records.js'
import { buildSkillsView, listedByTurn } from './skills.js'
import type { CanonicalRecord } from './types.js'

// ---------------------------------------------------------------------------
// Budget
// ---------------------------------------------------------------------------

/** Per-call wall-time budget in milliseconds, on the reference developer machine. */
const BUDGET_MS = 1500
const TURNS = 7000
const SKILLS_PER_LISTING = 25
const BASE_MS = Date.parse('2026-01-01T00:00:00.000Z')
const SESSION = 'scale-session'

/** A deterministic 25-name pool, so every run measures the same input. */
const POOL = Array.from({ length: SKILLS_PER_LISTING }, (_, index) => `skill-${String(index).padStart(2, '0')}`)

const countTokens = (text: string): number => text.length

function at(index: number): string {
  return new Date(BASE_MS + index * 1000).toISOString()
}

/** Runs `fn`, returning its result and elapsed wall time. */
function timed<T>(fn: () => T): { result: T; elapsedMs: number } {
  const start = performance.now()
  const result = fn()
  return { result, elapsedMs: performance.now() - start }
}

/**
 * Antigravity-style: every model call carries its own `gen_ai.skills` listing,
 * so the session has one session-level listing event per turn. This is the
 * shape that exposes the quadratic fold.
 */
function antigravityTurns(count: number): CanonicalRecord[] {
  const listing = `Active Skills: ${POOL.join(', ')}`
  return Array.from({ length: count }, (_, index) =>
    turn(`ag-${index}`, [], {
      source: 'antigravity',
      harness: 'antigravity',
      op: 'llm.invoke',
      kind: 'client',
      sessionId: SESSION,
      timestamp: at(index),
      raw: { 'gen_ai.skills': listing, 'gen_ai.usage.skill_tokens': 1040 },
    }),
  )
}

/**
 * Claude-style: one initial listing, then sparse non-initial listing events,
 * with plain model-call turns between them.
 */
function claudeTurns(count: number): CanonicalRecord[] {
  const records: CanonicalRecord[] = [
    turn('listing-initial', [], {
      source: 'claude-code',
      harness: 'claude-code',
      op: 'skill.listing',
      kind: 'internal',
      sessionId: SESSION,
      timestamp: at(-1),
      raw: { 'kyber.skills.listed': { isInitial: true, skills: POOL.map((name) => ({ name })) } },
    }),
  ]
  for (let index = 0; index < count; index++) {
    records.push(
      turn(`claude-turn-${index}`, [], {
        source: 'claude-code',
        harness: 'claude-code',
        op: 'llm.invoke',
        kind: 'client',
        sessionId: SESSION,
        timestamp: at(index),
        raw: {},
      }),
    )
    if (index % 100 === 99) {
      records.push(
        turn(`claude-delta-${index}`, [], {
          source: 'claude-code',
          harness: 'claude-code',
          op: 'skill.listing',
          kind: 'internal',
          sessionId: SESSION,
          timestamp: at(index + 0.5),
          raw: { 'kyber.skills.listed': { isInitial: false, skills: [{ name: POOL[index % POOL.length] }] } },
        }),
      )
    }
  }
  return records
}

// ---------------------------------------------------------------------------
// Budget tests
// ---------------------------------------------------------------------------

describe('scale: Antigravity-style listing on every turn (7,000 turns x 25 skills)', () => {
  it('buildSkillsView answers correctly and within the budget', () => {
    const turns = antigravityTurns(TURNS)

    const { result, elapsedMs } = timed(() => buildSkillsView(turns, turns, {}, countTokens))

    expect(result).toMatchObject({ measurable: true, reportedSkillTokens: 1040 })
    const rows = (result as { rows: Array<{ turnsListed: number }> }).rows
    expect(rows).toHaveLength(SKILLS_PER_LISTING)
    expect(rows.every((row) => row.turnsListed === TURNS)).toBe(true)
    expect(elapsedMs, `buildSkillsView took ${elapsedMs.toFixed(0)} ms, budget ${BUDGET_MS} ms`).toBeLessThan(BUDGET_MS)
  })

  it('listedByTurn answers correctly and within the budget', () => {
    const turns = antigravityTurns(TURNS)

    const { result, elapsedMs } = timed(() => listedByTurn(turns, turns))

    expect(result.size).toBe(TURNS)
    expect(result.get('ag-0')?.size).toBe(SKILLS_PER_LISTING)
    expect(elapsedMs, `listedByTurn took ${elapsedMs.toFixed(0)} ms, budget ${BUDGET_MS} ms`).toBeLessThan(BUDGET_MS)
  })
})

describe('scale: Claude-style, one initial listing and sparse non-initial events (7,000 turns)', () => {
  it('buildSkillsView answers correctly and within the budget', () => {
    const records = claudeTurns(TURNS)
    const turns = records.filter((record) => record.op === 'llm.invoke')

    const { result, elapsedMs } = timed(() => buildSkillsView(records, turns, {}, countTokens))

    expect(result).toMatchObject({ measurable: true })
    const rows = (result as { rows: Array<{ turnsListed: number }> }).rows
    expect(rows).toHaveLength(SKILLS_PER_LISTING)
    expect(rows.every((row) => row.turnsListed === TURNS)).toBe(true)
    expect(elapsedMs, `buildSkillsView took ${elapsedMs.toFixed(0)} ms, budget ${BUDGET_MS} ms`).toBeLessThan(BUDGET_MS)
  })

  it('listedByTurn answers correctly and within the budget', () => {
    const records = claudeTurns(TURNS)
    const turns = records.filter((record) => record.op === 'llm.invoke')

    const { result, elapsedMs } = timed(() => listedByTurn(records, turns))

    expect(result.size).toBe(TURNS)
    expect(result.get('claude-turn-0')?.size).toBe(SKILLS_PER_LISTING)
    expect(elapsedMs, `listedByTurn took ${elapsedMs.toFixed(0)} ms, budget ${BUDGET_MS} ms`).toBeLessThan(BUDGET_MS)
  })
})

// ---------------------------------------------------------------------------
// Semantics: a naive reference, compared on seeded random sequences
// ---------------------------------------------------------------------------

/** Seeded PRNG (mulberry32), so every run sees the same sequences. */
function seeded(seed: number): () => number {
  let state = seed >>> 0
  return () => {
    state = (state + 0x6d2b79f5) >>> 0
    let value = state
    value = Math.imul(value ^ (value >>> 15), value | 1)
    value ^= value + Math.imul(value ^ (value >>> 7), value | 61)
    return ((value ^ (value >>> 14)) >>> 0) / 4294967296
  }
}

type Skill = { name: string; description?: string }
type Listing = { isInitial: boolean; skills: Skill[] }

/** The listing a record declares, read from the Claude raw key only. */
function listingOf(record: CanonicalRecord): Listing | undefined {
  const raw = record.raw as Record<string, unknown> | undefined
  const value = raw?.['kyber.skills.listed'] as Listing | undefined
  return value === undefined ? undefined : value
}

function timeOf(record: CanonicalRecord): number {
  return new Date(record.timestamp).getTime()
}

/**
 * The naive reference. It re-derives every turn's effective listing from scratch
 * with the rules written out in full, so it shares no code with the engine:
 *   - session-level events are sorted by time, then by input position;
 *   - a turn with an unparseable time gets no session-level event;
 *   - each session event at or before the turn applies in order: initial clears,
 *     then every skill is set (keeping the earlier description when it gives none);
 *   - a rollup listing (op agent.invoke) applies to its direct children after the
 *     session events, by union;
 *   - a turn that no event applies to has no entry.
 */
function referenceListedByTurn(
  records: readonly CanonicalRecord[],
  turns: readonly CanonicalRecord[],
): Map<string, Map<string, string | undefined>> {
  const seen = new Set<string>()
  const all: CanonicalRecord[] = []
  for (const record of [...records, ...turns]) {
    if (seen.has(record.spanId)) continue
    seen.add(record.spanId)
    all.push(record)
  }

  const events = all
    .map((record, position) => ({ record, position, listing: listingOf(record) }))
    .filter(
      (entry): entry is { record: CanonicalRecord; position: number; listing: Listing } =>
        entry.listing !== undefined && entry.record.op !== 'agent.invoke' && !Number.isNaN(timeOf(entry.record)),
    )
    .sort((a, b) => timeOf(a.record) - timeOf(b.record) || a.position - b.position)

  const rollups = new Map<string, Listing>()
  for (const record of all) {
    const listing = listingOf(record)
    if (listing !== undefined && record.op === 'agent.invoke') rollups.set(record.spanId, listing)
  }

  const apply = (into: Map<string, string | undefined>, skills: Skill[]) => {
    for (const skill of skills) into.set(skill.name, skill.description ?? into.get(skill.name))
  }

  const out = new Map<string, Map<string, string | undefined>>()
  const uniqueTurns = [...new Map(turns.map((turn) => [turn.spanId, turn])).values()]
  for (const turn of uniqueTurns) {
    const effective = new Map<string, string | undefined>()
    let applies = false
    const time = timeOf(turn)
    if (!Number.isNaN(time)) {
      for (const event of events) {
        const sameSession = (event.record.sessionId ?? '') === (turn.sessionId ?? '')
        if (!sameSession || timeOf(event.record) > time) continue
        applies = true
        if (event.listing.isInitial) effective.clear()
        apply(effective, event.listing.skills)
      }
    }
    const parent = turn.parentSpanId === null ? undefined : rollups.get(turn.parentSpanId)
    if (parent !== undefined) {
      applies = true
      apply(effective, parent.skills)
    }
    if (applies) out.set(turn.spanId, effective)
  }
  return out
}

/** A plain, comparable form of a listedByTurn-shaped map. */
function plain(map: Map<string, Map<string, string | undefined>>): Record<string, Record<string, string | null>> {
  return Object.fromEntries(
    [...map].map(([spanId, skills]) => [
      spanId,
      Object.fromEntries([...skills].map(([name, description]) => [name, description ?? null])),
    ]),
  )
}

/** One seeded random sequence: turns, session events, and rollups, with ties and bad times. */
function randomSequence(random: () => number): { records: CanonicalRecord[]; turns: CanonicalRecord[] } {
  const pick = <T,>(items: readonly T[]): T => items[Math.floor(random() * items.length)]!
  const times = ['2026-01-01T00:00:00.000Z', '2026-01-01T00:00:01.000Z', '2026-01-01T00:00:02.000Z', '2026-01-01T00:00:03.000Z']
  const timeFor = () => (random() < 0.08 ? 'not-a-timestamp' : pick(times))
  const sessions = ['S1', 'S2']
  const names = ['a', 'b', 'c', 'd', 'e', 'f']

  const turnCount = 1 + Math.floor(random() * 6)
  const turns = Array.from({ length: turnCount }, (_, index) =>
    turn(`t${index}`, [], {
      source: 'claude-code',
      harness: 'claude-code',
      op: 'llm.invoke',
      kind: 'client',
      sessionId: pick(sessions),
      timestamp: timeFor(),
      parentSpanId: null,
      raw: {},
    }),
  )

  const rollupCount = Math.floor(random() * 3)
  const rollups = Array.from({ length: rollupCount }, (_, index) => {
    const skills = names.filter(() => random() < 0.4).map((name) => ({ name }))
    return turn(`r${index}`, [], {
      source: 'github-copilot',
      harness: 'copilot',
      op: 'agent.invoke',
      kind: 'internal',
      sessionId: pick(sessions),
      timestamp: timeFor(),
      raw: { 'kyber.skills.listed': { isInitial: true, skills } },
    })
  })
  // Children of a rollup: some turns point at a rollup span, some at nothing.
  for (const child of turns) {
    if (rollupCount > 0 && random() < 0.5) child.parentSpanId = pick(rollups).spanId
  }

  const eventCount = Math.floor(random() * 6)
  const events = Array.from({ length: eventCount }, (_, index) => {
    const skills = names.filter(() => random() < 0.5).map((name) => ({
      name,
      ...(random() < 0.5 ? { description: `desc-${name}-${index}` } : {}),
    }))
    return turn(`e${index}`, [], {
      source: 'claude-code',
      harness: 'claude-code',
      op: 'skill.listing',
      kind: 'internal',
      sessionId: pick(sessions),
      timestamp: timeFor(),
      raw: { 'kyber.skills.listed': { isInitial: random() < 0.4, skills } },
    })
  })

  const records = [...events, ...rollups, ...turns].sort(() => random() - 0.5)
  return { records, turns: [...turns].sort(() => random() - 0.5) }
}

describe('equivalence: the optimised fold matches a naive reference on random sequences', () => {
  it('listedByTurn matches the reference on 300 seeded sequences', () => {
    for (let seed = 1; seed <= 300; seed++) {
      const { records, turns } = randomSequence(seeded(seed))
      const actual = plain(listedByTurn(records, turns))
      const expected = plain(referenceListedByTurn(records, turns))
      expect(actual, `seed ${seed}`).toEqual(expected)
    }
  })

  it('buildSkillsView turnSpanIds match the reference, in turnRecords order', () => {
    for (let seed = 1; seed <= 300; seed++) {
      const { records, turns } = randomSequence(seeded(seed))
      const view = buildSkillsView(records, turns, {}, countTokens)
      if (!('rows' in view)) continue
      const reference = referenceListedByTurn(records, turns)

      const expectedNames = new Set<string>()
      for (const skills of reference.values()) for (const name of skills.keys()) expectedNames.add(name)
      const listedRows = view.rows.filter((row) => row.listed).map((row) => row.name)
      expect(new Set(listedRows), `seed ${seed}`).toEqual(expectedNames)

      for (const row of view.rows.filter((candidate) => candidate.listed)) {
        const expectedIds = turns
          .filter((turn) => reference.get(turn.spanId)?.has(row.name) === true)
          .map((turn) => turn.spanId)
        expect(row.turnSpanIds, `seed ${seed}, ${row.name}`).toEqual(expectedIds)
      }
    }
  })
})
