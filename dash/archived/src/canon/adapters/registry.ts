// Adapter registry and two-pass attribution for KyberDash (spec:
// docs/specs/kyberdash, design.md "Normalization layer" flowchart).
//
// Pass 1 — fingerprint vote: spans are grouped by (source, trace) and every
// adapter's `detect` scores every span in the group; the adapter with the
// highest total claims the whole group when its span-count-normalized score
// meets the confidence threshold.
//
// Pass 2 — source inheritance: a group the vote could not decide inherits
// from another group of the *same source* that was decided, matching the
// source exactly. This is the pass the Python pipeline added for the 15
// tool-execution spans that carried GenAI attributes with no vendor
// namespace and sat alone in their traces: each scored 0.5 alone, below
// threshold, while their source's request groups voted full confidence.
//
// Undecided after both passes is the registry's way of saying "quarantine
// me" (R6.1) — those spans are absent from the attribution map, and the
// quarantine writer (task 5.2) consumes that remainder with the observed
// namespaces.
//
// R6.2 is enforced structurally, not by convention: the source name
// participates in the group key and in inheritance lookup only. `scoreGroup`
// never reads it as evidence, `detect` receives it on the span but the
// adapter contract documents it as non-admissible, and inheritance is
// exact-match so a shared prefix between two source names can never transfer
// a harness. The suffixed-source world (`pi-abc123` and `pi-xyz789` as two
// instances of one harness) still attributes correctly because both
// instances carry the same attribute fingerprint.

import type { HarnessAdapter, RawSpan } from './base.js'
import { UNTRACED_GROUP } from './base.js'

/** Confidence a fingerprint vote must reach before its adapter claims the group. */
export const DEFAULT_CONFIDENCE_THRESHOLD = 0.6

/** One group's fingerprint-vote result: the winning harness and its confidence. */
export type GroupScore = {
  /** The winning adapter's harness name. */
  harness: string
  /** The winner's detect total divided by the group's span count, 0..1. */
  confidence: number
}

/** Construction options for {@link AdapterRegistry}. */
export type AdapterRegistryOptions = {
  /** Overrides `DEFAULT_CONFIDENCE_THRESHOLD` for the pass-1 vote. */
  threshold?: number
}

/** One (source, trace) group as the registry partitions a batch into them. */
type SpanGroup = {
  source: string
  traceId: string | null
  spans: RawSpan[]
}

/** Clamp an adapter's detect score into [0, 1]; a non-finite score counts as no evidence. */
function clampScore(score: number): number {
  return Number.isFinite(score) ? Math.min(1, Math.max(0, score)) : 0
}

export class AdapterRegistry {
  private readonly adapters: HarnessAdapter[] = []
  private readonly threshold: number

  constructor(
    adapters: Iterable<HarnessAdapter> = [],
    options: AdapterRegistryOptions = {},
  ) {
    this.threshold = options.threshold ?? DEFAULT_CONFIDENCE_THRESHOLD
    for (const adapter of adapters) this.register(adapter)
  }

  /**
   * Register an adapter. Harness names are unique across the registry; a
   * duplicate is a wiring bug, so it fails loudly instead of shadowing the
   * first adapter's vote.
   */
  register(adapter: HarnessAdapter): void {
    if (this.adapters.some((existing) => existing.name === adapter.name)) {
      throw new Error(`harness adapter "${adapter.name}" is already registered`)
    }
    this.adapters.push(adapter)
  }

  /** The key a (source, trace) group files under; untraced spans get their own group per source. */
  groupKey(source: string, traceId: string | null): string {
    return `${source}::${traceId ?? UNTRACED_GROUP}`
  }

  /**
   * Pass 1 over one group: sum every adapter's `detect` across the spans,
   * take the maximum total, and normalize it by span count — `detect` is
   * 0..1 per span, so confidence is the average fingerprint strength of the
   * winning adapter across the group.
   *
   * `source` and `traceId` name the group the caller is voting on and guard
   * that the spans belong to it; they contribute nothing to the score
   * (R6.2). Returns undefined when there is nothing to vote on or no
   * adapter found any fingerprint evidence at all. A tie stays with the
   * earlier-registered adapter, so the vote is deterministic regardless of
   * span order.
   */
  scoreGroup(source: string, traceId: string | null, spans: RawSpan[]): GroupScore | undefined {
    const expected = this.groupKey(source, traceId)
    for (const span of spans) {
      const actual = this.groupKey(span.source, span.traceId)
      if (actual !== expected) {
        throw new Error(`span ${span.spanId} (${actual}) is not part of group ${expected}`)
      }
    }
    if (spans.length === 0) return undefined

    let winner: HarnessAdapter | undefined
    let winnerTotal = 0
    for (const adapter of this.adapters) {
      let total = 0
      for (const span of spans) {
        total += clampScore(adapter.detect(span))
      }
      if (total > winnerTotal) {
        winner = adapter
        winnerTotal = total
      }
    }
    if (winner === undefined) return undefined
    return { harness: winner.name, confidence: winnerTotal / spans.length }
  }

  /**
   * Attribute a batch of raw spans through both passes and return
   * spanId → harness for every span the system could decide. A group whose
   * vote meets the threshold claims all of its spans — fingerprinted or
   * not — because the vote is per group, not per span; a group below
   * threshold inherits when its source was confidently mapped elsewhere in
   * the same batch.
   *
   * Absence from the map is the quarantine signal (R6.1): no adapter
   * claimed the span's group with sufficient confidence and no same-source
   * group was confidently mapped. The quarantine writer (task 5.2) consumes
   * exactly that remainder.
   */
  attribute(spans: RawSpan[]): Map<string, string> {
    const groups = new Map<string, SpanGroup>()
    for (const span of spans) {
      const key = this.groupKey(span.source, span.traceId)
      const group = groups.get(key)
      if (group === undefined) {
        groups.set(key, { source: span.source, traceId: span.traceId, spans: [span] })
      } else {
        group.spans.push(span)
      }
    }

    const attributed = new Map<string, string>()
    // Harnesses each source was confidently mapped to in pass 1. Inheritance
    // (pass 2) fires only on an exact source match with exactly one harness:
    // a shared prefix is not a match, and an ambiguous source inherits
    // nothing — the name alone must never transfer attribution (R6.2).
    const sourceHarnesses = new Map<string, Set<string>>()
    const undecided: SpanGroup[] = []

    for (const group of groups.values()) {
      const score = this.scoreGroup(group.source, group.traceId, group.spans)
      if (score !== undefined && score.confidence >= this.threshold) {
        for (const span of group.spans) attributed.set(span.spanId, score.harness)
        const harnesses = sourceHarnesses.get(group.source)
        if (harnesses === undefined) {
          sourceHarnesses.set(group.source, new Set([score.harness]))
        } else {
          harnesses.add(score.harness)
        }
      } else {
        undecided.push(group)
      }
    }

    // Pass 2 runs only after pass 1 has seen every group, so an undecided
    // group inherits from a confident sibling listed later in the batch.
    for (const group of undecided) {
      const harnesses = sourceHarnesses.get(group.source)
      if (harnesses === undefined || harnesses.size !== 1) continue
      for (const harness of harnesses) {
        for (const span of group.spans) attributed.set(span.spanId, harness)
      }
    }

    return attributed
  }
}
