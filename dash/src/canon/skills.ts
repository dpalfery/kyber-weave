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
//   - No listing key but a `<skills>`/`<available_skills>` block in the system prompt
//     (VS Code Copilot): the listing is read from that block.
//   - Everything else: the listing is not measurable, with a reason.

import { harnessFamily } from './measurability.js'
import { SKILLS_INVOKED_KEY, SKILLS_LISTED_KEY, isNotMeasurable, notMeasurable } from './types.js'
import type { CanonicalRecord, ContentPart, Measurability, NotMeasurable } from './types.js'

/** Copilot's listing: a JSON-array string of skill names on the rollup span. */
const COPILOT_LISTING_KEY = 'github.copilot.context.skills'
/** Antigravity's listing: `Active Skills: a, b` on the model-call span. */
const ANTIGRAVITY_LISTING_KEY = 'gen_ai.skills'
/** OpenCode's listing: a JSON-array string of skill names on the context span (plan 2026-10-23 §5-T5, D6). */
const OPENCODE_LISTING_KEY = 'opencode.context.skills'
const ANTIGRAVITY_LISTING_PREFIX = 'Active Skills: '
/** Harness per-turn resident skill-token counter; never summed, never split across skills. */
const ANTIGRAVITY_SKILL_TOKENS_KEY = 'gen_ai.usage.skill_tokens'

/**
 * The Claude family's display id. Ingest stamps records `claude-cli` or
 * `claude-desktop`; `harnessFamily` folds every Claude front-end onto this id.
 */
const CLAUDE_FAMILY = 'claude-code'
/** Antigravity's family id; `harnessFamily` folds `antigravity-cli` and `antigravity-ide` onto it. */
const ANTIGRAVITY_FAMILY = 'antigravity'
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
  /** The spanIds of the turns the skill was listed on, in `turnRecords` order. */
  turnSpanIds: string[]
  /** `turnSpanIds.length`. */
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
  /**
   * Set only for an Antigravity session where some turns carry the invoked key and
   * some do not (D30): the invocation figures cover `measuredTurns` of `totalTurns`.
   * Absent when every turn, or no turn, is covered, so the figures read as before.
   */
  invocationsCoverage?: { measuredTurns: number; totalTurns: number }
}

/** `buildSkillsView(records, turnRecords, measurability, countTokens)`. */
export type SkillsView = MeasuredSkillsView | NotMeasurable

/** A listing as the record declares it: whether it replaces or extends the list. */
type Listing = { isInitial: boolean; skills: ListedSkill[] }

/** What a record's listing reads as: a measured list, or a declared absence. */
export type ListingResult = ({ measurable: true } & Listing) | NotMeasurable

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

/**
 * The listing a record states, read from the parts the caller holds.
 *
 * WHY the parts are a parameter: the projection reduces a record's parts into
 * the values the derived views read and releases the text, so the listing —
 * which is read out of the system prompt's text — is derived where the text is
 * in hand and handed on as a value. A caller with no reduced listing reads
 * the record's own parts.
 */
export function readListingOf(
  record: CanonicalRecord,
  parts: readonly ContentPart[] | undefined,
): ListingResult {
  const raw = rawOf(record)
  if (raw[SKILLS_LISTED_KEY] !== undefined) return readClaudeListing(record, raw[SKILLS_LISTED_KEY])
  if (raw[COPILOT_LISTING_KEY] !== undefined) return readCopilotListing(record, raw[COPILOT_LISTING_KEY], parts)
  if (raw[ANTIGRAVITY_LISTING_KEY] !== undefined) return readAntigravityListing(record, raw[ANTIGRAVITY_LISTING_KEY])
  if (raw[OPENCODE_LISTING_KEY] !== undefined) return readOpencodeListing(record, raw[OPENCODE_LISTING_KEY])
  const fromPrompt = readSystemPromptListing(record, parts)
  if (fromPrompt !== undefined) return fromPrompt
  const fromBody = readRequestBodyListing(record, parts)
  if (fromBody !== undefined) return fromBody
  return notMeasurable(
    `No skill listing source is recorded for harness ${record.harness}: the record carries none of ${SKILLS_LISTED_KEY}, ${COPILOT_LISTING_KEY}, ${ANTIGRAVITY_LISTING_KEY} or ${OPENCODE_LISTING_KEY}, so the skills listed are not measurable.`,
  )
}

export function extractInvokedSkills(records: readonly CanonicalRecord[]): InvokedResult {
  const hasInvocationSource = records.some(
    // An invoked key counts as a measured source only when it holds an array, `[]`
    // included (measured, none invoked). A null, string, number or object under the
    // key is corrupt and is never read as a measured zero.
    (record) =>
      isClaudeFamily(record) || isSkillToolCall(record) || Array.isArray(rawOf(record)[SKILLS_INVOKED_KEY]),
  )
  if (!hasInvocationSource) {
    // Antigravity has no Skill tool call: its invocations come from the transcript
    // (see providers/antigravity-transcript.ts). A session with no such key has no
    // transcript behind it, so the reason says where the figure would have come from.
    if (records.some((record) => harnessFamily(record.harness) === ANTIGRAVITY_FAMILY)) {
      return notMeasurable(
        `No skill invocation is recorded: the Antigravity records carry no ${SKILLS_INVOKED_KEY} key. Skill invocations are read from the conversation transcript, and none was available for this session, so skills invoked are not measurable.`,
      )
    }
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
  listings?: ReadonlyMap<string, ListingResult>,
): SkillsView {
  return skillsForSession(records, turnRecords, measurability, countTokens, listings).view
}

/** A session's skills view and its per-turn listing, produced by one shared fold. */
export type SessionSkills = {
  view: SkillsView
  /**
   * Each turn's effective listing, exactly as `listedByTurn` returns it. Set
   * exactly when `view` is measurable; absent otherwise.
   */
  listed: Map<string, Map<string, string | undefined>> | undefined
}

/**
 * `buildSkillsView` and `listedByTurn` from a single fold over the session. A
 * caller that needs both (the session payload) calls this once, not twice.
 */
export function skillsForSession(
  records: readonly CanonicalRecord[],
  turnRecords: readonly CanonicalRecord[],
  measurability: Measurability,
  countTokens: (text: string) => number,
  listings?: ReadonlyMap<string, ListingResult>,
): SessionSkills {
  const declaredListing = measurability.skills_listed
  if (declaredListing !== undefined && isNotMeasurable(declaredListing)) return { view: declaredListing, listed: undefined }

  const all = uniqueBySpan([...records, ...turnRecords])
  const turns = uniqueBySpan(turnRecords)
  if (turns.length === 0) {
    return {
      view: notMeasurable('No model turns were recorded, so skills listed cannot be attributed to any turn.'),
      listed: undefined,
    }
  }

  const byTurn = effectiveListingsOf(all, turns, listings)
  if (byTurn.size === 0) {
    return { view: notMeasurable(firstListingReason(turns, listings)), listed: undefined }
  }

  return { view: measuredView(all, turns, byTurn, measurability, countTokens), listed: byTurn }
}

/** The measured view, given a session's effective listing per turn. */
function measuredView(
  all: readonly CanonicalRecord[],
  turns: readonly CanonicalRecord[],
  byTurn: ReadonlyMap<string, Map<string, string | undefined>>,
  measurability: Measurability,
  countTokens: (text: string) => number,
): MeasuredSkillsView {
  const listedByName = new Map<string, { turnSpanIds: string[]; description?: string }>()
  for (const turn of turns) {
    const effective = byTurn.get(turn.spanId)
    if (effective === undefined) continue
    for (const [name, description] of effective) {
      const row = listedByName.get(name) ?? { turnSpanIds: [] }
      row.turnSpanIds.push(turn.spanId)
      row.description = description ?? row.description
      listedByName.set(name, row)
    }
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
    const turnSpanIds = listed?.turnSpanIds ?? []
    const row: SkillRow = { name, listed: listed !== undefined, turnSpanIds, turnsListed: turnSpanIds.length }
    if (invocationsAvailability === undefined) row.invocations = invocationCounts.get(name) ?? 0
    if (listed?.description !== undefined) row.derivedTokens = countTokens(listed.description)
    return row
  })

  const view: MeasuredSkillsView = { measurable: true, rows }
  const reportedSkillTokens = latestReportedSkillTokens(turns)
  if (reportedSkillTokens !== undefined) view.reportedSkillTokens = reportedSkillTokens
  if (invocationsAvailability !== undefined) view.invocationsAvailability = invocationsAvailability
  // Antigravity counts an invocation only on a turn that carries the key (D30), so
  // a partly covered session says how many turns its figures cover.
  if (invocationsAvailability === undefined) {
    const antigravityTurns = turns.filter((turn) => harnessFamily(turn.harness) === ANTIGRAVITY_FAMILY)
    const measuredTurns = antigravityTurns.filter((turn) => Array.isArray(rawOf(turn)[SKILLS_INVOKED_KEY])).length
    if (measuredTurns > 0 && measuredTurns < antigravityTurns.length) {
      view.invocationsCoverage = { measuredTurns, totalTurns: antigravityTurns.length }
    }
  }
  return view
}

/**
 * Each turn's EFFECTIVE listing with descriptions, keyed by the turn's spanId.
 * A turn no measured listing applies to has no entry (`has` is false), never an
 * empty map: an empty map would read as "measured, none". Entries map each
 * effective skill to its last description, or undefined when no listing gave one.
 */
export function listedByTurn(
  records: readonly CanonicalRecord[],
  turnRecords: readonly CanonicalRecord[],
  listings?: ReadonlyMap<string, ListingResult>,
): Map<string, Map<string, string | undefined>> {
  return effectiveListingsOf(uniqueBySpan([...records, ...turnRecords]), uniqueBySpan(turnRecords), listings)
}

/**
 * The shared fold behind `listedByTurn`, `buildSkillsView` and `skillsForSession`.
 *
 * One time-ordered pass per session carries the running effective listing, so the
 * cost is linear in turns and events (plus one copy per listed turn), not turns x
 * events. Rules: an initial listing replaces the running list and a non-initial one
 * unions in; a turn sees the session-level events at or before its time; a turn
 * whose time cannot be parsed sees none of them; a rollup listing applies to its
 * direct children on top of the running list. A turn no measured listing applies
 * to has no entry.
 */
function effectiveListingsOf(
  all: readonly CanonicalRecord[],
  turns: readonly CanonicalRecord[],
  listings?: ReadonlyMap<string, ListingResult>,
): Map<string, Map<string, string | undefined>> {
  const { sessionEvents, rollups } = collectListings(all, listings)

  const eventsBySession = new Map<string, ListingEvent[]>()
  for (const event of sessionEvents) {
    const list = eventsBySession.get(event.sessionKey) ?? []
    list.push(event)
    eventsBySession.set(event.sessionKey, list)
  }

  const placedBySession = new Map<string, Array<{ turn: CanonicalRecord; time: number }>>()
  const unplaced: CanonicalRecord[] = []
  for (const turn of turns) {
    const time = timeOf(turn)
    if (Number.isNaN(time)) {
      unplaced.push(turn)
      continue
    }
    const key = sessionKeyOf(turn)
    const list = placedBySession.get(key) ?? []
    list.push({ turn, time })
    placedBySession.set(key, list)
  }

  const byTurn = new Map<string, Map<string, string | undefined>>()
  for (const [sessionKey, placed] of placedBySession) {
    // Stable sort: turns sharing a time keep their input order.
    placed.sort((a, b) => a.time - b.time)
    const events = eventsBySession.get(sessionKey) ?? []
    const running = new Map<string, string | undefined>()
    let next = 0
    let sessionApplied = false
    for (const { turn, time } of placed) {
      for (let event = events[next]; event !== undefined && event.time <= time; event = events[next]) {
        next++
        sessionApplied = true
        if (event.listing.isInitial) running.clear()
        applySkills(running, event.listing.skills)
      }
      const parent = parentSkills(turn, rollups)
      if (!sessionApplied && parent === undefined) continue
      const effective = new Map(running)
      if (parent !== undefined) applySkills(effective, parent)
      byTurn.set(turn.spanId, effective)
    }
  }

  for (const turn of unplaced) {
    const parent = parentSkills(turn, rollups)
    if (parent === undefined) continue
    const effective = new Map<string, string | undefined>()
    applySkills(effective, parent)
    byTurn.set(turn.spanId, effective)
  }
  return byTurn
}

/** The skills a rollup listing gives its direct children, or undefined when the turn has none. */
function parentSkills(turn: CanonicalRecord, rollups: ReadonlyMap<string, RollupListing>): ListedSkill[] | undefined {
  if (turn.parentSpanId === null) return undefined
  return rollups.get(turn.parentSpanId)?.listing.skills
}

// ---------------------------------------------------------------------------
// Listing reads
// ---------------------------------------------------------------------------

function readListing(record: CanonicalRecord): ListingResult {
  return readListingOf(record, record.parts)
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

function readCopilotListing(
  record: CanonicalRecord,
  value: unknown,
  parts: readonly ContentPart[] | undefined,
): ListingResult {
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
  const descriptions = xmlDescriptions(systemPromptText(record, parts))
  const skills = (parsed as string[])
    .filter((name) => name !== '')
    .map((name) => listedSkill(record, name, descriptions.get(name)))
  return { measurable: true, isInitial: true, skills: uniqueByName(skills) }
}

/**
 * The opencode context listing (D6): a JSON array of skill names, the Copilot
 * `github.copilot.context.skills` pattern. The context span is a request-assembly
 * event, so its listing is the session's initial list. A malformed value names
 * its key in the not-measurable reason, never an empty list.
 */
function readOpencodeListing(record: CanonicalRecord, value: unknown): ListingResult {
  if (typeof value !== 'string') {
    return notMeasurable(`${OPENCODE_LISTING_KEY} is not a string, so the opencode skill listing is not measurable.`)
  }
  let parsed: unknown
  try {
    parsed = JSON.parse(value)
  } catch {
    return notMeasurable(`${OPENCODE_LISTING_KEY} is not valid JSON, so the opencode skill listing is not measurable.`)
  }
  if (!Array.isArray(parsed) || !parsed.every((name) => typeof name === 'string')) {
    return notMeasurable(
      `${OPENCODE_LISTING_KEY} is not a JSON array of skill names, so the opencode skill listing is not measurable.`,
    )
  }
  const skills = (parsed as string[])
    .filter((name) => name !== '')
    .map((name) => listedSkill(record, name))
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

/**
 * The listing as the system prompt itself states it, for a record with no
 * listing key (VS Code Copilot sends a `<skills>` block). It is what the model was
 * given, so it is measured; a prompt with no such block says nothing either way.
 */
function readSystemPromptListing(
  record: CanonicalRecord,
  parts: readonly ContentPart[] | undefined,
): ListingResult | undefined {
  // Parts are the source of truth when present; the flat `content` is only for records without them.
  const text = parts !== undefined ? partsSystemPrompt(parts) : systemPromptText(record, parts)
  if (!/<(?:available_)?skills>/.test(text)) return undefined
  const skills = [...xmlDescriptions(text)].map(([name, description]) => listedSkill(record, name, description))
  if (skills.length === 0) return undefined
  return { measurable: true, isInitial: true, skills: uniqueByName(skills) }
}

/** The marker Claude Code writes before the `- name: description` skill entries. */
const BODY_LISTING_MARKER = 'The following skills are available for use with the Skill tool:'

/**
 * The listing a Claude request body carries (D14, D21): from `system_prompt`
 * parts and system-role `conversation_history` parts only, so text a user or
 * the assistant quoted, and parts with no recorded role, never count. A body
 * that did not parse produced no parts, so there is nothing partial to read.
 */
function readRequestBodyListing(
  record: CanonicalRecord,
  parts: readonly ContentPart[] | undefined,
): ListingResult | undefined {
  if (!isClaudeFamily(record) || parts === undefined) return undefined
  for (const part of parts) {
    const eligible = part.part === 'system_prompt' || (part.part === 'conversation_history' && part.role === 'system')
    if (!eligible) continue
    const skills = parseBodyListing(record, part.text)
    if (skills.length > 0) return { measurable: true, isInitial: true, skills: uniqueByName(skills) }
  }
  return undefined
}

/**
 * The `- name: description` entries that follow the marker. The name is the
 * non-whitespace run before the first colon that is followed by whitespace or
 * the end of the line, so a plugin-namespaced `plugin:skill` stays whole and a
 * description may contain colons. An indented line continues the previous
 * entry's description (joined with one space); a blank line or any other
 * unindented non-entry line ends the listing.
 */
function parseBodyListing(record: CanonicalRecord, text: string): ListedSkill[] {
  const at = text.indexOf(BODY_LISTING_MARKER)
  if (at < 0) return []
  const entries: Array<{ name: string; description: string }> = []
  for (const line of text.slice(at + BODY_LISTING_MARKER.length).split('\n')) {
    const trimmed = line.trim()
    if (trimmed === '') {
      if (entries.length > 0) break
      continue
    }
    const last = entries[entries.length - 1]
    if (last !== undefined && /^\s/.test(line)) {
      last.description = last.description === '' ? trimmed : `${last.description} ${trimmed}`
      continue
    }
    const entry = /^- (\S+?):(?:\s+(.*))?$/.exec(line.trimEnd())
    if (entry === null) {
      if (entries.length > 0) break
      continue
    }
    entries.push({ name: entry[1]!, description: (entry[2] ?? '').trim() })
  }
  return entries.map((entry) => listedSkill(record, entry.name, nonEmptyString(entry.description)))
}

/** Skill names and optional descriptions from the system prompt's `<skill>` XML blocks. */
function xmlDescriptions(text: string): Map<string, string | undefined> {
  const found = new Map<string, string | undefined>()
  for (const match of text.matchAll(/<skill\b[^>]*>([\s\S]*?)<\/skill>/gi)) {
    const block = match[1] ?? ''
    const nameMatch = block.match(/<name\b[^>]*>([\s\S]*?)<\/name>/i)
    if (!nameMatch) continue
    const name = nameMatch[1]?.trim()
    if (!name) continue

    const descMatch = block.match(/<description\b[^>]*>([\s\S]*?)<\/description>/i)
    const description = descMatch !== null ? nonEmptyString(descMatch[1]?.trim()) : undefined
    found.set(name, description)
  }
  return found
}

/** The system prompt text: the `system_prompt` parts when present, else the flat bucket. */
function partsSystemPrompt(parts: readonly ContentPart[]): string {
  return parts
    .filter((part) => part.part === 'system_prompt')
    .map((part) => part.text)
    .join('\n')
}

function systemPromptText(record: CanonicalRecord, parts: readonly ContentPart[] | undefined): string {
  const fromParts = partsSystemPrompt(parts ?? [])
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
function collectListings(
  records: readonly CanonicalRecord[],
  listings?: ReadonlyMap<string, ListingResult>,
): {
  sessionEvents: ListingEvent[]
  rollups: Map<string, RollupListing>
} {
  const sessionEvents: ListingEvent[] = []
  const rollups = new Map<string, RollupListing>()
  records.forEach((record, order) => {
    const read = listings?.get(record.spanId) ?? readListing(record)
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

function applySkills(effective: Map<string, string | undefined>, skills: readonly ListedSkill[]): void {
  for (const skill of skills) effective.set(skill.name, skill.description ?? effective.get(skill.name))
}

/** The reason a turn's listing is not measurable, naming the first turn that declares one. */
function firstListingReason(
  turns: readonly CanonicalRecord[],
  listings?: ReadonlyMap<string, ListingResult>,
): string {
  for (const turn of turns) {
    const read = listings?.get(turn.spanId) ?? readListing(turn)
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

/** True for every Claude front-end identity (`claude-code`, `claude-cli`, `claude-desktop`, ...). */
function isClaudeFamily(record: CanonicalRecord): boolean {
  return harnessFamily(record.harness) === CLAUDE_FAMILY
}

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
