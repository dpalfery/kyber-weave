// Skills view engine (T1). Pure: no I/O. It reads canonical records and answers
// two questions per session: which skills a turn had access to (listed), and
// which skills were invoked. Every figure is measured, derived, or declared not
// measurable. Absence of a source is never coerced to an empty list or to zero.
//
// Sources, by harness:
//   - Claude Code: `skill_listing` attachments (raw `kyber.skills.listed`,
//     session-level, initial replaces the effective list, later ones union in)
//     and `Skill` tool calls for invocations.
//   - Copilot Chat: a JSON-array string on the `invoke_agent` rollup, applying
//     to its direct children. Descriptions come from the system prompt's
//     `<available_skills>` block when it names the skill. No invocation source.
//   - Antigravity: `Active Skills: a, b, c` on the model-call span. Names only.
//     No invocation source. The harness skill-token counter is a per-turn
//     resident size (the listing's size in context, not an increment): it is
//     reported once, from the latest turn, carried whole and never split across rows.
//   - Everything else: the listing is not measurable, with a reason.

import { SKILLS_INVOKED_KEY, SKILLS_LISTED_KEY, isNotMeasurable, notMeasurable } from './types.js'
import type { CanonicalRecord, Measurability, NotMeasurable } from './types.js'

/** Copilot's listing: a JSON-array string of skill names on the rollup span. */
const COPILOT_LISTING_KEY = 'github.copilot.context.skills'
/** Antigravity's listing: `Active Skills: a, b` on the model-call span. */
const ANTIGRAVITY_LISTING_KEY = 'gen_ai.skills'
const ANTIGRAVITY_LISTING_PREFIX = 'Active Skills: '
/** Harness per-turn resident skill-token counter; never summed, never split across skills. */
const ANTIGRAVITY_SKILL_TOKENS_KEY = 'gen_ai.usage.skill_tokens'

const CLAUDE_HARNESS = 'claude-code'
const SKILL_TOOL_NAMES: ReadonlySet<string> = new Set(['Skill', 'skill'])
const SKILL_TOOL_OP = 'tool.invoke'
const AGENT_ROLLUP_OP = 'agent.invoke'

/** One listed skill. `sourceKey` identifies the listing that reported it. */
export type ListedSkill = { name: string; description?: string; sourceKey: string }

/** `extractListedSkills(record)`: a measured list, or a declared absence. */
export type ListedResult = { measurable: true; skills: ListedSkill[] } | NotMeasurable

/** One counted invocation: the skill name and the turn it was made in. */
export type SkillInvocation = { name: string; turnSpanId: string }

/** `extractInvokedSkills(records)`: one entry per counted call (deduplicated). */
export type InvokedResult = { measurable: true; invocations: SkillInvocation[] } | NotMeasurable

/** One row of the skills view. Token figures are always `derived`-named. */
export type SkillRow = {
  name: string
  listed: boolean
  /** Absent when the invocation source is not measurable, never zero. */
  invocations?: number
  turnsListed: number
  /** Tokens counted from the description through the injected `countTokens`. */
  derivedTokens?: number
}

/** The measured arm of `SkillsView`. */
export type MeasuredSkillsView = {
  measurable: true
  rows: SkillRow[]
  /**
   * The harness's skill-token counter: a per-turn resident size, never an
   * increment, so it is never summed. It is the value on the latest turn that
   * carries a finite number (by parsed timestamp, then greater spanId). Turns
   * with an unparseable timestamp are not candidates. Carried whole, never
   * split across rows. Absent, never 0, when no turn qualifies.
   */
  reportedSkillTokens?: number
  /** Set when the invocation source is unmeasurable; `invocations` is then omitted per row. */
  invocationsAvailability?: NotMeasurable
}

/** `buildSkillsView(records, turnRecords, measurability, countTokens)`. */
export type SkillsView = MeasuredSkillsView | NotMeasurable

/** A listing as the record declares it: whether it replaces or extends the list. */
type Listing = { isInitial: boolean; skills: ListedSkill[] }

type ListingResult = ({ measurable: true } & Listing) | NotMeasurable

/** A session-level listing event, placed in time order within its session. */
type ListingEvent = {
  record: CanonicalRecord
  sessionKey: string
  time: number
  order: number
  listing: Listing
}

/** Copilot-style rollup listing, applied to the rollup's direct children. */
type RollupListing = { listing: Listing }

export function extractListedSkills(record: CanonicalRecord): ListedResult {
  const listing = readListing(record)
  if ('availability' in listing) return listing
  return { measurable: true, skills: listing.skills }
}

export function extractInvokedSkills(records: readonly CanonicalRecord[]): InvokedResult {
  const hasInvocationSource = records.some(
    (record) => record.harness === CLAUDE_HARNESS || isSkillToolCall(record) || SKILLS_INVOKED_KEY in rawOf(record),
  )
  if (!hasInvocationSource) {
    return notMeasurable(
      `No skill invocation is recorded: the records carry no Skill tool call and no ${SKILLS_INVOKED_KEY} key, so skills invoked are not measurable.`,
    )
  }

  // One call is counted once. Per turn and skill, the count is the larger of
  // the Skill tool calls under that turn and the turn-level key (which holds a
  // name once, whatever the number of calls).
  const turns = new Map<string, { toolCounts: Map<string, number>; keyed: Set<string> }>()
  const entryFor = (spanId: string) => {
    let entry = turns.get(spanId)
    if (entry === undefined) {
      entry = { toolCounts: new Map(), keyed: new Set() }
      turns.set(spanId, entry)
    }
    return entry
  }

  for (const record of records) {
    if (isSkillToolCall(record)) {
      const name = skillArgument(rawOf(record).arguments)
      if (name !== undefined) {
        const entry = entryFor(record.parentSpanId ?? record.spanId)
        entry.toolCounts.set(name, (entry.toolCounts.get(name) ?? 0) + 1)
      }
    }
    const keyed = rawOf(record)[SKILLS_INVOKED_KEY]
    if (Array.isArray(keyed)) {
      const entry = entryFor(record.spanId)
      for (const name of keyed) {
        if (typeof name === 'string' && name !== '') entry.keyed.add(name)
      }
    }
  }

  const invocations: SkillInvocation[] = []
  for (const [turnSpanId, entry] of turns) {
    const names = new Set<string>([...entry.toolCounts.keys(), ...entry.keyed])
    for (const name of names) {
      const count = Math.max(entry.toolCounts.get(name) ?? 0, entry.keyed.has(name) ? 1 : 0)
      for (let call = 0; call < count; call++) invocations.push({ name, turnSpanId })
    }
  }
  return { measurable: true, invocations }
}

export function buildSkillsView(
  records: readonly CanonicalRecord[],
  turnRecords: readonly CanonicalRecord[],
  measurability: Measurability,
  countTokens: (text: string) => number,
): SkillsView {
  const declaredListing = measurability.skills_listed
  if (declaredListing !== undefined && isNotMeasurable(declaredListing)) return declaredListing

  const all = uniqueBySpan([...records, ...turnRecords])
  const turns = uniqueBySpan(turnRecords)
  if (turns.length === 0) {
    return notMeasurable('No model turns were recorded, so skills listed cannot be attributed to any turn.')
  }

  const { sessionEvents, rollups } = collectListings(all)

  const listedByName = new Map<string, { turnsListed: number; description?: string }>()
  let turnsWithListing = 0
  for (const turn of turns) {
    const effective = effectiveListing(turn, sessionEvents, rollups)
    if (effective === undefined) continue
    turnsWithListing++
    for (const [name, description] of effective) {
      const row = listedByName.get(name) ?? { turnsListed: 0 }
      row.turnsListed++
      row.description = description ?? row.description
      listedByName.set(name, row)
    }
  }

  if (turnsWithListing === 0) {
    return notMeasurable(firstListingReason(turns))
  }

  const declaredInvoked = measurability.skills_invoked
  const invoked: InvokedResult =
    declaredInvoked !== undefined && isNotMeasurable(declaredInvoked)
      ? declaredInvoked
      : extractInvokedSkills(all)

  const invocationCounts = new Map<string, number>()
  let invocationsAvailability: NotMeasurable | undefined
  if ('availability' in invoked) {
    invocationsAvailability = invoked
  } else {
    for (const invocation of invoked.invocations) {
      invocationCounts.set(invocation.name, (invocationCounts.get(invocation.name) ?? 0) + 1)
    }
  }

  const names = new Set<string>([...listedByName.keys(), ...invocationCounts.keys()])
  const rows: SkillRow[] = [...names].map((name) => {
    const listed = listedByName.get(name)
    const row: SkillRow = { name, listed: listed !== undefined, turnsListed: listed?.turnsListed ?? 0 }
    if (invocationsAvailability === undefined) row.invocations = invocationCounts.get(name) ?? 0
    if (listed?.description !== undefined) row.derivedTokens = countTokens(listed.description)
    return row
  })

  const view: MeasuredSkillsView = { measurable: true, rows }
  const reportedSkillTokens = latestReportedSkillTokens(turns)
  if (reportedSkillTokens !== undefined) view.reportedSkillTokens = reportedSkillTokens
  if (invocationsAvailability !== undefined) view.invocationsAvailability = invocationsAvailability
  return view
}

// ---------------------------------------------------------------------------
// Listing reads
// ---------------------------------------------------------------------------

function readListing(record: CanonicalRecord): ListingResult {
  const raw = rawOf(record)
  if (raw[SKILLS_LISTED_KEY] !== undefined) return readClaudeListing(record, raw[SKILLS_LISTED_KEY])
  if (raw[COPILOT_LISTING_KEY] !== undefined) return readCopilotListing(record, raw[COPILOT_LISTING_KEY])
  if (raw[ANTIGRAVITY_LISTING_KEY] !== undefined) return readAntigravityListing(record, raw[ANTIGRAVITY_LISTING_KEY])
  return notMeasurable(
    `No skill listing source is recorded for harness ${record.harness}: the record carries none of ${SKILLS_LISTED_KEY}, ${COPILOT_LISTING_KEY} or ${ANTIGRAVITY_LISTING_KEY}, so the skills listed are not measurable.`,
  )
}

function readClaudeListing(record: CanonicalRecord, value: unknown): ListingResult {
  if (!isObject(value) || typeof value.isInitial !== 'boolean' || !Array.isArray(value.skills)) {
    return notMeasurable(`${SKILLS_LISTED_KEY} on this record is not a skill listing, so the skills listed are not measurable.`)
  }
  const skills: ListedSkill[] = []
  for (const entry of value.skills as unknown[]) {
    if (!isObject(entry)) continue
    const name = nonEmptyString(entry.name)
    if (name === undefined) continue
    skills.push(listedSkill(record, name, nonEmptyString(entry.description)))
  }
  return { measurable: true, isInitial: value.isInitial, skills: uniqueByName(skills) }
}

function readCopilotListing(record: CanonicalRecord, value: unknown): ListingResult {
  if (typeof value !== 'string') {
    return notMeasurable(`${COPILOT_LISTING_KEY} is not a string, so the Copilot skill listing is not measurable.`)
  }
  let parsed: unknown
  try {
    parsed = JSON.parse(value)
  } catch {
    return notMeasurable(`${COPILOT_LISTING_KEY} is not valid JSON, so the Copilot skill listing is not measurable.`)
  }
  if (!Array.isArray(parsed) || !parsed.every((name) => typeof name === 'string')) {
    return notMeasurable(
      `${COPILOT_LISTING_KEY} is not a JSON array of skill names, so the Copilot skill listing is not measurable.`,
    )
  }
  const descriptions = xmlDescriptions(systemPromptText(record))
  const skills = (parsed as string[])
    .filter((name) => name !== '')
    .map((name) => listedSkill(record, name, descriptions.get(name)))
  return { measurable: true, isInitial: true, skills: uniqueByName(skills) }
}

function readAntigravityListing(record: CanonicalRecord, value: unknown): ListingResult {
  if (typeof value !== 'string' || !value.startsWith(ANTIGRAVITY_LISTING_PREFIX)) {
    return notMeasurable(
      `${ANTIGRAVITY_LISTING_KEY} does not start with "${ANTIGRAVITY_LISTING_PREFIX}", so the Antigravity skill listing is not measurable.`,
    )
  }
  const skills = value
    .slice(ANTIGRAVITY_LISTING_PREFIX.length)
    .split(',')
    .map((name) => name.trim())
    .filter((name) => name !== '')
    .map((name) => listedSkill(record, name))
  return { measurable: true, isInitial: true, skills: uniqueByName(skills) }
}

/** `<skill><name/><description/>` pairs from the system prompt's XML block. */
function xmlDescriptions(text: string): Map<string, string> {
  const found = new Map<string, string>()
  for (const match of text.matchAll(/<skill>\s*<name>([^<]*)<\/name>\s*<description>([^<]*)<\/description>/g)) {
    const [, name = '', description = ''] = match
    if (name !== '') found.set(name, description)
  }
  return found
}

/** The system prompt text: the `system_prompt` parts when present, else the flat bucket. */
function systemPromptText(record: CanonicalRecord): string {
  const fromParts = (record.parts ?? [])
    .filter((part) => part.part === 'system_prompt')
    .map((part) => part.text)
    .join('\n')
  if (fromParts !== '') return fromParts
  return record.content.system_prompt ?? ''
}

// ---------------------------------------------------------------------------
// Listing timeline
// ---------------------------------------------------------------------------

/**
 * Split the measured listings into session-level events (fold in time order)
 * and rollup listings (applied to their direct children only).
 */
function collectListings(records: readonly CanonicalRecord[]): {
  sessionEvents: ListingEvent[]
  rollups: Map<string, RollupListing>
} {
  const sessionEvents: ListingEvent[] = []
  const rollups = new Map<string, RollupListing>()
  records.forEach((record, order) => {
    const read = readListing(record)
    if ('availability' in read) return
    if (record.op === AGENT_ROLLUP_OP) {
      rollups.set(record.spanId, { listing: read })
      return
    }
    const time = timeOf(record)
    if (Number.isNaN(time)) return
    sessionEvents.push({ record, sessionKey: sessionKeyOf(record), time, order, listing: read })
  })
  sessionEvents.sort((a, b) => a.time - b.time || a.order - b.order)
  return { sessionEvents, rollups }
}

/**
 * The skills a turn had access to, with the description last given for each,
 * or undefined when no listing applies to the turn at all.
 */
function effectiveListing(
  turn: CanonicalRecord,
  sessionEvents: readonly ListingEvent[],
  rollups: ReadonlyMap<string, RollupListing>,
): Map<string, string | undefined> | undefined {
  const effective = new Map<string, string | undefined>()
  let applies = false
  const sessionKey = sessionKeyOf(turn)
  const time = timeOf(turn)

  // A turn whose time cannot be parsed cannot be placed against a session-level
  // listing, so no session-level listing is shown to apply to it. Rollup listings
  // below do not depend on time and still apply.
  if (!Number.isNaN(time)) {
    for (const event of sessionEvents) {
      if (event.sessionKey !== sessionKey || event.time > time) continue
      applies = true
      if (event.listing.isInitial) effective.clear()
      applySkills(effective, event.listing.skills)
    }
  }

  const parent = turn.parentSpanId === null ? undefined : rollups.get(turn.parentSpanId)
  if (parent !== undefined) {
    applies = true
    applySkills(effective, parent.listing.skills)
  }

  return applies ? effective : undefined
}

function applySkills(effective: Map<string, string | undefined>, skills: readonly ListedSkill[]): void {
  for (const skill of skills) effective.set(skill.name, skill.description ?? effective.get(skill.name))
}

/** The reason a turn's listing is not measurable, naming the first turn that declares one. */
function firstListingReason(turns: readonly CanonicalRecord[]): string {
  for (const turn of turns) {
    const read = readListing(turn)
    if ('availability' in read) return read.reason
  }
  return 'No skill listing applies to these turns, so the skills listed are not measurable.'
}

/**
 * The harness skill-token figure: the value on the latest turn that carries a
 * finite number. "Latest" is by parsed timestamp, with the greater spanId as the
 * tiebreak. Turns with an unparseable timestamp cannot be placed, so they are
 * not candidates. Never summed (the counter is a resident size) and never 0 when
 * absent: undefined when no turn qualifies.
 */
function latestReportedSkillTokens(turns: readonly CanonicalRecord[]): number | undefined {
  let latest: { time: number; spanId: string; value: number } | undefined
  for (const turn of turns) {
    const value = rawOf(turn)[ANTIGRAVITY_SKILL_TOKENS_KEY]
    if (typeof value !== 'number' || !Number.isFinite(value)) continue
    const time = timeOf(turn)
    if (Number.isNaN(time)) continue
    const later =
      latest === undefined || time > latest.time || (time === latest.time && turn.spanId > latest.spanId)
    if (later) latest = { time, spanId: turn.spanId, value }
  }
  return latest?.value
}

// ---------------------------------------------------------------------------
// Invocation reads
// ---------------------------------------------------------------------------

function isSkillToolCall(record: CanonicalRecord): boolean {
  return record.op === SKILL_TOOL_OP && SKILL_TOOL_NAMES.has(record.name)
}

/** The skill a Skill call names: `arguments.skill`, else `arguments.name`. */
function skillArgument(args: unknown): string | undefined {
  let value: unknown = args
  if (typeof value === 'string') {
    try {
      value = JSON.parse(value)
    } catch {
      return undefined
    }
  }
  if (!isObject(value)) return undefined
  return nonEmptyString(value.skill) ?? nonEmptyString(value.name)
}

// ---------------------------------------------------------------------------
// Shared helpers
// ---------------------------------------------------------------------------

function rawOf(record: CanonicalRecord): Record<string, unknown> {
  return isObject(record.raw) ? record.raw : {}
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function nonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined
}

function listedSkill(record: CanonicalRecord, name: string, description?: string): ListedSkill {
  return description === undefined
    ? { name, sourceKey: record.spanId }
    : { name, description, sourceKey: record.spanId }
}

/** Keeps the first occurrence of each name, in order. */
function uniqueByName<T extends { name: string }>(items: readonly T[]): T[] {
  const seen = new Set<string>()
  return items.filter((item) => {
    if (seen.has(item.name)) return false
    seen.add(item.name)
    return true
  })
}

/** Keeps the first record for each span, so a record passed twice counts once. */
function uniqueBySpan(records: readonly CanonicalRecord[]): CanonicalRecord[] {
  const seen = new Set<string>()
  return records.filter((record) => {
    if (seen.has(record.spanId)) return false
    seen.add(record.spanId)
    return true
  })
}

function sessionKeyOf(record: CanonicalRecord): string {
  return record.sessionId ?? record.traceId ?? ''
}

function timeOf(record: CanonicalRecord): number {
  return new Date(record.timestamp).getTime()
}
