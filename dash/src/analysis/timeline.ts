// Execution-structure analysis for KyberDash (spec: docs/specs/kyberdash,
// design.md "Analysis layer" — the timeline ported from the pipeline's
// `views.py`; R9). One session's canonical records become one tree: spans
// nest by `parentSpanId`, so tool executions and subagent invocations render
// hierarchically over a timeline (R9.1), every node carries the attributes
// the harness emitted for inspection (R9.2), subagent sessions are
// identified together with the span that spawned them (R9.3), and auxiliary
// activity such as title generation is separated from the primary
// conversation under its own group while its spend stays in the tree and in
// the session total (R9.4).
//
// Structure is never guessed. A span whose parent is absent from the input —
// or whose parentage forms a cycle — is not silently promoted to the
// conversation's root: orphans group under synthetic nodes keyed by what the
// records themselves carry (harness attribution plus span name), the same
// attribute-over-ancestry grouping the Aspire source uses for detached
// spans (design.md, R2.7). Synthetic ids are parenthesized — the convention
// `UNTRACED_GROUP` set, since a telemetry source emits hex ids — so they can
// never collide with a real span id. Parentage is resolved by span id across
// traces, which is exactly how a subagent session (its own trace) hangs off
// the span of the conversation that invoked it (R9.3).

import { sumCosts } from '../canon/cost.js'
import type { CanonicalRecord, CostBlock } from '../canon/types.js'

/**
 * One node of the rendered timeline. Real spans carry their own values;
 * synthetic containers (the session root, the auxiliary group, orphan
 * groups) carry aggregated timing and cost over their subtree and expose no
 * attributes of their own — there is no span behind them to inspect.
 */
export type TimelineNode = {
  spanId: string
  /** The record's own `parentSpanId`, verbatim — even when that parent is absent from the input. */
  parentId: string | null
  children: TimelineNode[]
  /**
   * Milliseconds from session start (the earliest span timestamp in the
   * input) to this span's start — the coordinate a renderer plots (R9.1).
   * A record without a parseable timestamp renders at session start; the
   * tree's structure never depended on it.
   */
  startMs: number
  durationMs: number
  kind: string
  name: string
  /** The harness-emitted attribute map the record preserved, for inspection (R9.2). */
  attributes: Record<string, unknown>
  /** This span carries subagent evidence (name, operation, or attribute). */
  isSubagent: boolean
  /** This span is auxiliary activity, separated from the primary conversation (R9.4). */
  isAuxiliary: boolean
  /** The record's own cost block, basis intact — never re-derived here (R5.1). */
  cost: CostBlock
}

/** Synthetic session-root id; a hex span id can never collide with it. */
export const SESSION_ROOT_ID = '(session)'

/** Synthetic id of the group auxiliary activity is separated into (R9.4). */
export const AUXILIARY_GROUP_ID = '(auxiliary)'

/** `kind` of the synthetic containers; real spans keep their own kind. */
const SYNTHETIC_KIND = 'synthetic'

/** A cost block carrying no figure; the stand-in when a sum is refused rather than blended (R5.1). */
const UNPRICED: CostBlock = { basis: 'unknown', status: 'no_rate' }

/** One identified subagent session and the span that spawned it (R9.3). */
export type SubagentSession = {
  /** Span id of the subagent invocation that roots the session. */
  spanId: string
  /** The invocation's own `parentSpanId` — the parent, named even when absent from the input. */
  parentSpanId: string | null
  name: string
}

/**
 * Evidence markers, matched case-insensitively against the span's name,
 * canonical operation, attribute keys, and string attribute values. A span
 * names itself a subagent (its name or op says so) or a harness attribute
 * does (`pi.subagent.*`, `gen_ai.operation.name: "subagent"`); auxiliary
 * activity such as title generation says so the same way (R9.3, R9.4).
 */
const SUBAGENT_MARKERS = ['subagent', 'sub-agent', 'sub_agent']
const AUXILIARY_MARKERS = ['title']

/**
 * The attributes a record preserved in `raw` (adapters store the harness
 * attribute map there — see `baseRecord`), or an empty map when `raw` holds
 * something else: an arbitrary payload is not surfaced as span attributes,
 * and absent is not guessed at.
 */
function recordAttributes(record: CanonicalRecord): Record<string, unknown> {
  if (record.raw !== null && typeof record.raw === 'object' && !Array.isArray(record.raw)) {
    return record.raw as Record<string, unknown>
  }
  return {}
}

/** Every term evidence markers are matched against, lowercased. */
function evidenceTerms(record: CanonicalRecord): string {
  const attributes = recordAttributes(record)
  const keys = Object.keys(attributes)
  const values = Object.values(attributes).filter(
    (value): value is string => typeof value === 'string'
  )
  return [record.name, record.op, ...keys, ...values].join(' ').toLowerCase()
}

/** Whether a span carries subagent evidence (R9.3). */
function isSubagentRecord(record: CanonicalRecord): boolean {
  const terms = evidenceTerms(record)
  return SUBAGENT_MARKERS.some((marker) => terms.includes(marker))
}

/** Whether a span is auxiliary activity, separated from the primary conversation (R9.4). */
function isAuxiliaryRecord(record: CanonicalRecord): boolean {
  const terms = evidenceTerms(record)
  return AUXILIARY_MARKERS.some((marker) => terms.includes(marker))
}

/** The record's start time in epoch ms, or NaN when the timestamp does not parse. */
function timestampMs(record: CanonicalRecord): number {
  const ms =
    record.timestamp instanceof Date
      ? record.timestamp.getTime()
      : Date.parse(String(record.timestamp))
  return Number.isFinite(ms) ? ms : Number.NaN
}

/** Sort siblings into the order a timeline plots them: start, then span id for determinism. */
function sortChildren(node: TimelineNode): void {
  node.children.sort((a, b) => a.startMs - b.startMs || (a.spanId < b.spanId ? -1 : 1))
}

/**
 * Fill a synthetic container's timing and cost from its subtree (R9.1,
 * R9.4): it spans its children, and its cost totals theirs through
 * `sumCosts`, which refuses to blend bases — a refused sum stays a block
 * with no figure rather than a fabricated one (R5.1, R5.4).
 */
function finalizeSynthetic(node: TimelineNode, costs: CostBlock[]): void {
  if (node.children.length > 0) {
    const start = Math.min(...node.children.map((child) => child.startMs))
    const end = Math.max(...node.children.map((child) => child.startMs + child.durationMs))
    node.startMs = start
    node.durationMs = Math.max(0, end - start)
  }
  const total = sumCosts(costs)
  node.cost = total.ok ? total.total : UNPRICED
}

type Entry = { record: CanonicalRecord; node: TimelineNode; auxiliary: boolean }

/**
 * Build the session's timeline (R9.1). Spans nest by `parentSpanId` within
 * their population; auxiliary spans (R9.4) are partitioned out before the
 * tree is built and land under the auxiliary group, their internal ancestry
 * preserved, so the primary conversation and the auxiliary work are two
 * disjoint subtrees while both spend into the session total.
 *
 * Primary spans whose parent is missing, auxiliary, or part of a parentage
 * cycle group under synthetic attribute-keyed nodes instead of being
 * promoted — structure is reported as it was observed, not repaired.
 */
export function buildTimeline(spans: CanonicalRecord[]): TimelineNode {
  const known = spans.map(timestampMs).filter((ms) => Number.isFinite(ms))
  const sessionStart = known.length > 0 ? Math.min(...known) : 0

  const entries: Entry[] = spans.map((record) => {
    const ms = timestampMs(record)
    const attributes = recordAttributes(record)
    return {
      record,
      auxiliary: isAuxiliaryRecord(record),
      node: {
        spanId: record.spanId,
        parentId: record.parentSpanId,
        children: [],
        startMs: Number.isFinite(ms) ? Math.max(0, ms - sessionStart) : 0,
        durationMs: record.durationMs,
        kind: record.kind,
        name: record.name,
        attributes,
        isSubagent: isSubagentRecord(record),
        isAuxiliary: false,
        cost: record.cost,
      },
    }
  })

  // --- primary population: attach by parentage, then keep only what is
  // reachable from the declared roots. Everything else — missing parent,
  // auxiliary parent, or a parentage cycle — becomes an orphan group.
  const primary = entries.filter((entry) => !entry.auxiliary)
  const primaryById = new Map(primary.map((entry) => [entry.record.spanId, entry]))
  const parentOf = new Map<string, TimelineNode>()

  for (const entry of primary) {
    const parent =
      entry.record.parentSpanId !== null ? primaryById.get(entry.record.parentSpanId) : undefined
    if (parent !== undefined && parent.record.spanId !== entry.record.spanId) {
      parent.node.children.push(entry.node)
      parentOf.set(entry.record.spanId, parent.node)
    }
  }

  const reachable = new Set<string>()
  const visit = (node: TimelineNode): void => {
    reachable.add(node.spanId)
    for (const child of node.children) visit(child)
  }
  for (const entry of primary) {
    if (entry.record.parentSpanId === null) visit(entry.node)
  }

  const orphaned = primary.filter((entry) => !reachable.has(entry.record.spanId))
  for (const entry of orphaned) {
    // Detach from a cycle-mate, if any, so no unreachable edge survives.
    const parent = parentOf.get(entry.record.spanId)
    if (parent !== undefined) {
      parent.children = parent.children.filter((child) => child.spanId !== entry.record.spanId)
    }
  }

  const orphanGroups = new Map<string, TimelineNode>()
  for (const entry of orphaned) {
    const key = `${entry.record.harness}:${entry.record.name}`
    let group = orphanGroups.get(key)
    if (group === undefined) {
      group = {
        spanId: `(orphan:${key})`,
        parentId: null,
        children: [],
        startMs: 0,
        durationMs: 0,
        kind: SYNTHETIC_KIND,
        name: entry.record.name,
        attributes: {},
        isSubagent: false,
        isAuxiliary: false,
        cost: UNPRICED,
      }
      orphanGroups.set(key, group)
    }
    group.children.push(entry.node)
  }
  for (const group of orphanGroups.values()) {
    sortChildren(group)
    finalizeSynthetic(
      group,
      orphaned
        .filter((entry) => group.children.includes(entry.node))
        .map((entry) => entry.record.cost)
    )
  }

  // --- auxiliary population: internal ancestry preserved; anything whose
  // parent is primary or missing sits at the group's top level (R9.4).
  const auxiliary = entries.filter((entry) => entry.auxiliary)
  const auxiliaryById = new Map(auxiliary.map((entry) => [entry.record.spanId, entry]))
  const auxiliaryGroup: TimelineNode = {
    spanId: AUXILIARY_GROUP_ID,
    parentId: null,
    children: [],
    startMs: 0,
    durationMs: 0,
    kind: SYNTHETIC_KIND,
    name: 'auxiliary activity',
    attributes: {},
    isSubagent: false,
    isAuxiliary: false,
    cost: UNPRICED,
  }
  for (const entry of auxiliary) {
    entry.node.isAuxiliary = true
    const parent =
      entry.record.parentSpanId !== null
        ? auxiliaryById.get(entry.record.parentSpanId)
        : undefined
    if (parent !== undefined && parent.record.spanId !== entry.record.spanId) {
      parent.node.children.push(entry.node)
    } else {
      auxiliaryGroup.children.push(entry.node)
    }
  }
  if (auxiliaryGroup.children.length > 0) {
    sortChildren(auxiliaryGroup)
    finalizeSynthetic(auxiliaryGroup, auxiliary.map((entry) => entry.record.cost))
  }

  // --- session root over everything, spend included from both populations.
  const root: TimelineNode = {
    spanId: SESSION_ROOT_ID,
    parentId: null,
    children: [],
    startMs: 0,
    durationMs: 0,
    kind: SYNTHETIC_KIND,
    name: 'session',
    attributes: {},
    isSubagent: false,
    isAuxiliary: false,
    cost: UNPRICED,
  }
  for (const entry of primary) {
    if (entry.record.parentSpanId === null) root.children.push(entry.node)
  }
  root.children.push(...orphanGroups.values())
  if (auxiliaryGroup.children.length > 0) root.children.push(auxiliaryGroup)
  sortChildren(root)
  finalizeSynthetic(root, entries.map((entry) => entry.record.cost))
  return root
}

/**
 * The session's subagent sessions with the span that spawned each (R9.3).
 * Identification is evidence on the invocation span itself, so a session is
 * named with its parent even when the parent never arrived in the input —
 * `parentSpanId` is the record's own claim, not a lookup.
 */
export function subagentSessions(root: TimelineNode): SubagentSession[] {
  const sessions: SubagentSession[] = []
  const walk = (node: TimelineNode): void => {
    if (node.isSubagent) {
      sessions.push({ spanId: node.spanId, parentSpanId: node.parentId, name: node.name })
    }
    for (const child of node.children) walk(child)
  }
  walk(root)
  return sessions
}

/**
 * Total cost of the auxiliary activity (R9.4): the spend separation must
 * still report. Totaled through `sumCosts`, so mixed bases are refused
 * rather than blended (R5.1); an empty auxiliary population yields a block
 * with no figure, because nothing was spent — which is not $0.00 of
 * anything (R5.4).
 */
export function auxiliarySpend(root: TimelineNode): CostBlock {
  const costs: CostBlock[] = []
  const walk = (node: TimelineNode): void => {
    if (node.isAuxiliary) costs.push(node.cost)
    for (const child of node.children) walk(child)
  }
  walk(root)
  const total = sumCosts(costs)
  return total.ok ? total.total : UNPRICED
}
