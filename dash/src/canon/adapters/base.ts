// Harness adapter contract for KyberDash (spec: docs/specs/kyberdash,
// design.md "Normalization layer"). One adapter per harness knows that
// harness's attribute fingerprint, token convention, root-span shape and
// export gaps; the registry (registry.ts) runs adapters as fingerprint
// voters, never as name matchers.
//
// The load-bearing rule is R6.2: attribution is decided by attribute
// fingerprint only. A telemetry source name ("pi-abc123", "codeburn-prod-7")
// carries per-instance suffixes, does not track content, and is not stable
// across reconfiguration, so no method in this contract accepts a source
// name as evidence. `RawSpan.source` rides along for grouping and
// inheritance lookup only — AdapterRegistry is where that line is drawn and
// kept.

import type { CanonicalRecord, Problem } from '../types.js'

/** A span as it arrives from a receiver or synthesizer, before any adapter claims it. */
export type RawSpan = {
  spanId: string
  /** Null when the span arrives detached from any trace. */
  traceId: string | null
  /** Null when the span is the root of its trace. */
  parentSpanId: string | null
  /**
   * Telemetry source name, e.g. `pi-abc123`. A grouping and inheritance key
   * only — never attribution evidence (R6.2).
   */
  source: string
  /** Harness-emitted attributes; the only admissible fingerprint evidence. */
  attributes: Record<string, unknown>
  /** Span name as emitted, e.g. `pi.agent.chat`. */
  name: string
  /** Span kind as emitted, e.g. `internal`. */
  kind: string
}

/**
 * The contract every harness adapter implements (design.md: "Every adapter
 * implements the same interface — detect, relevance, normalize, group,
 * resolve a root, validate, and declare what the harness does not export").
 * Concrete adapters (Copilot, pi, Gemini) arrive with task 5.3; this file
 * fixes the seam they plug into.
 */
export interface HarnessAdapter {
  /** Harness identity stamped on canonical records, e.g. `pi`. */
  name: string

  /**
   * Attribute namespaces this adapter recognizes, e.g. `['gen_ai', 'pi']`.
   * A quarantined span reports its observed namespaces against these, which
   * is what tells the quarantine view "you are missing a trae.* adapter"
   * (R6.1, R6.3).
   */
  namespaces: string[]

  /**
   * Fingerprint score for one span, 0 (no evidence) to 1 (full fingerprint),
   * per this adapter's semantics: which attribute namespaces and keys the
   * harness emits (`gen_ai.usage.input_tokens` with harness-specific
   * handling, `codeburn.provider`, vendor namespaces such as `pi.*`). This
   * is the only input to attribution (R6.2).
   */
  detect(span: RawSpan): number

  /**
   * How much one span matters to the harness's per-request accounting, 0..1
   * — a request span carrying token counts ranks above a child tool span.
   * Consumed by per-request reconciliation (R4.5, task 5.3); deliberately
   * outside the attribution vote, which `detect` alone drives.
   */
  relevance(span: RawSpan): number

  /**
   * Claim a span this adapter won the vote for: convert the harness's token
   * convention into the disjoint classes (R4.2), map content onto canonical
   * keys, and stamp `harness` with this adapter's `name`.
   */
  normalize(raw: RawSpan): CanonicalRecord

  /**
   * Grouping key the adapter reconciles per-request sums over (R4.5) — the
   * trace id by default. This is the adapter's own grouping for
   * reconciliation; attribution groups spans by (source, trace) before any
   * adapter is chosen, in AdapterRegistry.
   */
  group(span: RawSpan): string

  /**
   * The span id a group's per-turn sums must reconcile against (R4.5), or
   * undefined when no root is resolvable — which marks the records orphans
   * (R4.3) rather than guessing. `resolveRootByParentage` is the default
   * implementation.
   */
  resolveRoot(spans: RawSpan[]): string | undefined

  /**
   * Record-level validation after `normalize` (R4.3, R4.4): the problem that
   * rejects the record, or undefined when the record holds.
   */
  validate(record: CanonicalRecord): Problem | undefined

  /**
   * Canonical metric names this harness cannot export, e.g.
   * `['tool_definitions']` for pi, which invoked 14 tools across 368 calls
   * while exporting none. This is what turns a blank view into a stated
   * limitation (R7.6, R8.5, R10.2): absent is not zero, and the reason
   * matters.
   */
  unexportedMetrics(): string[]
}

/**
 * Group key for spans with no trace id. OTLP trace ids are hex strings, so
 * the parentheses keep a real trace id from ever colliding with it.
 */
export const UNTRACED_GROUP = '(untraced)'

/** Default `group`: one group per trace. */
export function traceGroup(span: RawSpan): string {
  return span.traceId ?? UNTRACED_GROUP
}

/**
 * Default `resolveRoot`: a span that declares itself parentless is the root;
 * failing that, the first span whose parent was dropped from the group is
 * the best remaining candidate (a fragment root). undefined when the group
 * is empty or a pure cycle — no root is invented, and the records are
 * handled as orphans (R4.3). First match in span order wins, so the walk is
 * deterministic.
 */
export function resolveRootByParentage(spans: RawSpan[]): string | undefined {
  if (spans.length === 0) return undefined

  const ids = new Set(spans.map((span) => span.spanId))

  const declared = spans.find((span) => span.parentSpanId === null)
  if (declared !== undefined) return declared.spanId

  const fragment = spans.find(
    (span) => span.parentSpanId !== null && !ids.has(span.parentSpanId),
  )
  return fragment?.spanId
}
