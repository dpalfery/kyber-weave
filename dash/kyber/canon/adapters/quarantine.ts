// Quarantine and problem capture for KyberDash (spec: docs/specs/kyberdash,
// design.md "Normalization layer" flowchart and "Error Handling" table).
//
// The undecided remainder of `AdapterRegistry.attribute` — spans absent from
// the attribution map — is this module's entire input (R6.1). Each such span
// is held out of the corpus with the attribute namespaces it actually
// carried, observed verbatim: nothing here consults a registered adapter, a
// source name, or a prefix heuristic, because recording "probably pi" would
// be exactly the guess R6.1 forbids. The namespaces are the raw material for
// the view that names the missing adapter (R6.3): `trae.*` attributes
// quarantined under a registry with no trae adapter is a work order, not
// noise. A bare `gen_ai.*` span quarantined rather than claimed by the pi
// adapter that recognizes the namespace is the same rule holding — partial
// evidence below confidence is quarantine, not attribution.
//
// Validation failures take the other path (R4.3, R4.4): a record whose token
// decomposition cannot hold is rejected, and its problem — severity, code,
// location — is persisted for the problems view (R6.4) rather than logged
// and lost. A validation failure is never quarantined as an unclaimed
// harness: the span WAS attributed; its decomposition is what failed.

import type { CanonStore, QuarantineEntry, SpanProblem } from '../store.js'
import type { CanonicalRecord, Problem, ProblemSeverity } from '../types.js'
import { validateTokens } from '../types.js'
import type { RawSpan } from './base.js'

/** Reason stamped on spans no adapter claimed with sufficient confidence (R6.1). */
export const UNCLAIMED_REASON = 'unclaimed'

/**
 * The attribute namespaces one span actually carried: each attribute key's
 * segment before its first `.`, observed verbatim and sorted —
 * `trae.tool.name` reports `trae`, `gen_ai.usage.input_tokens` reports
 * `gen_ai`. A key with no dot is its own namespace. Empty when the span
 * carried no attributes at all; that emptiness is itself evidence for the
 * view, so it is preserved rather than papered over.
 */
export function observedNamespaces(attributes: Record<string, unknown>): string[] {
  const namespaces = new Set<string>()
  for (const key of Object.keys(attributes)) {
    namespaces.add(key.split('.', 1)[0])
  }
  return [...namespaces].sort()
}

/**
 * Quarantine every span the attribution map did not claim (R6.1). Spans
 * present in `attributed` are skipped — they have a harness — and each
 * unclaimed span is written with the observed namespaces of its attributes
 * and `UNCLAIMED_REASON`. Returns the quarantined span ids in input order so
 * a caller can report how many spans the run held out. Re-running over the
 * same batch replaces each entry rather than duplicating it.
 */
export function quarantineUnclaimed(
  spans: readonly RawSpan[],
  attributed: ReadonlyMap<string, string>,
  store: CanonStore,
): string[] {
  const quarantined: string[] = []
  for (const span of spans) {
    if (attributed.has(span.spanId)) continue
    store.quarantine(span.spanId, observedNamespaces(span.attributes), UNCLAIMED_REASON)
    quarantined.push(span.spanId)
  }
  return quarantined
}

/** One record-level check: the problem that rejects the record, or undefined when the record holds. */
export type RecordValidator = (record: CanonicalRecord) => Problem | undefined

/**
 * The default record validation (R4.1, R4.4): `validateTokens` over the
 * disjoint token classes, with the record's span id as the problem's
 * location. Adapters with harness-specific record checks compose this with
 * their own `validate` through a custom {@link RecordValidator}.
 */
export function tokenValidator(record: CanonicalRecord): Problem | undefined {
  const result = validateTokens(record.tokens, record.spanId)
  return result.valid ? undefined : result.problem
}

/**
 * Validate normalized records and persist a problem for every failure
 * (R4.4): severity, code and location land in the store's problems table,
 * where the R6.4 view reads them back through `getProblems` and
 * `problemSummary` — surfaced, not logged and lost. Failing records are
 * rejected (design.md "Error Handling"): the return value is the accepted
 * subset, exactly what the caller may store. A rejected record is not
 * quarantined — quarantine is for spans with no harness, and these spans
 * have one; their decomposition is what failed.
 */
export function recordValidationProblems(
  records: readonly CanonicalRecord[],
  store: CanonStore,
  validate: RecordValidator = tokenValidator,
): CanonicalRecord[] {
  const accepted: CanonicalRecord[] = []
  for (const record of records) {
    const problem = validate(record)
    if (problem === undefined) {
      accepted.push(record)
      continue
    }
    store.recordProblem({
      ...problem,
      spanId: record.spanId,
      location: problem.location ?? record.spanId,
    })
  }
  return accepted
}

/** Every quarantined span with its namespaces — the row list behind the R6.3 view. */
export function getQuarantined(store: CanonStore): QuarantineEntry[] {
  return store.listQuarantine()
}

/** Quarantined spans grouped by namespace signature, with the span count of each (R6.3). */
export type QuarantineNamespaceGroup = {
  /** The signature every span in this group carried, sorted. */
  namespaces: string[]
  /** How many quarantined spans carry exactly this signature. */
  count: number
}

/** The counts-and-namespaces view over quarantine (R6.3): total spans held out, grouped by signature. */
export function quarantineSummary(store: CanonStore): {
  total: number
  byNamespaces: QuarantineNamespaceGroup[]
} {
  const groups = new Map<string, QuarantineNamespaceGroup>()
  for (const entry of store.listQuarantine()) {
    const key = entry.namespaces.join(',')
    const group = groups.get(key)
    if (group === undefined) {
      groups.set(key, { namespaces: entry.namespaces, count: 1 })
    } else {
      group.count += 1
    }
  }
  const byNamespaces = [...groups.values()].sort(
    (a, b) =>
      b.count - a.count || a.namespaces.join(',').localeCompare(b.namespaces.join(',')),
  )
  return { total: byNamespaces.reduce((n, group) => n + group.count, 0), byNamespaces }
}

/** Recorded problems in written order — the row list behind the R6.4 view. */
export function getProblems(store: CanonStore): SpanProblem[] {
  return store.getProblems()
}

/** One recorded problem code with how many times it fired (R6.4). */
export type ProblemGroup = {
  code: string
  /** The severities recorded under this code, worst first. */
  severities: ProblemSeverity[]
  count: number
}

/** The grouped view over recorded problems (R6.4): total problems, counted by code. */
export function problemSummary(store: CanonStore): { total: number; byCode: ProblemGroup[] } {
  const groups = new Map<string, ProblemGroup>()
  for (const problem of store.getProblems()) {
    const group = groups.get(problem.code)
    if (group === undefined) {
      groups.set(problem.code, {
        code: problem.code,
        severities: [problem.severity],
        count: 1,
      })
    } else {
      if (!group.severities.includes(problem.severity)) group.severities.push(problem.severity)
      group.count += 1
    }
  }
  const byCode = [...groups.values()].sort(
    (a, b) => b.count - a.count || a.code.localeCompare(b.code),
  )
  for (const group of byCode) {
    // `error` sorts before `warning` alphabetically, which is also worst-first.
    group.severities.sort()
  }
  return { total: byCode.reduce((n, group) => n + group.count, 0), byCode }
}
