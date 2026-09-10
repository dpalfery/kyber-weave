---
id: adr/0013-telemetry-grounded-finding-contracts-and-waste-ranking
title: Telemetry-Grounded Finding Contracts, Waste Ranking, and Relocation Discipline
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-09-05
---

# ADR 0013: Telemetry-Grounded Finding Contracts, Waste Ranking, and Relocation Discipline

## Status

Accepted, 2026-09-05. Extends [ADR 0011](0011-asad-only-context-view-and-payload-contract.md) and [ADR 0012](0012-progressive-disclosure-6-level-diagnostic-spine.md).

## Context

Previous context diagnostic reporting often suffered from three core flaws:
1. **Unsubstantiated claims**: Diagnostic tools frequently emitted generic advice ("system prompt is too long", "tools are inefficient") without referencing specific trace spans or token accounting evidence, leaving developers unable to verify or refute the diagnosis.
2. **Inverted ranking and heuristic overconfidence**: Large speculative inferences (e.g. guessing that uninvoked skills were entirely useless) would rank higher than small, indisputable deterministic waste (e.g. repeated identical schemas or post-cache breakpoint thrashing).
3. **Value judgements driving harmful deletion**: Categorizing uninvoked context items as "unnecessary" or "waste" encouraged developers to delete context that was crucial for edge-case safety, reasoning boundaries, or tool availability.

## Decision

1. **Full Finding Contract (D5)**:
   Every diagnostic finding produced by the signal engine must fulfill an explicit contract:
   - **Mechanism prose**: Clear explanation of how the pattern occurred and why it degrades efficiency or stability.
   - **Evidence rows (≥2)**: Every finding must provide at least two concrete evidence rows, each naming its source telemetry and linking directly to originating canonical record IDs.
   - **Measurement class**: Explicitly declared as `deterministic`, `inferred`, or `coverage-gap`.
   - **Confidence basis & what would raise it**: Explicit description of why the confidence was assigned and what telemetry would be required to increase it.
   - **Recommendation**: Concrete, actionable guidance with an expected improvement range and error bar.
   - **Outcome-risk caveat**: A mandatory statement explaining how applying the recommendation could negatively affect model execution or task completion.
2. **Waste Ranking Formula (D6)**:
   Findings are strictly ordered by:
   $$\text{Rank Score} = \text{Estimated Recoverable Waste} \times \text{Outcome Risk} \times \text{Confidence}$$
   - An inferred finding cannot outrank a deterministic finding of comparable token volume.
   - Recoverable waste is an estimate tied to a specific action, not an inherent property of a token.
   - Harnesses with telemetry coverage gaps emit `coverageGap` findings and can never rank as efficient.
3. **Relocation Over Deletion (D8)**:
   - Recommendations must prefer moving context (cache breakpoint stabilization, progressive disclosure, dynamic on-demand tool registration) over deleting context.
   - Relocation is reversible and does not assume unobserved context lacked utility. Deletion recommendations fail deterministic repository linting.
4. **Classification by Evidence of Use (D15)**:
   - Context items are categorized strictly by empirical observability: `evidence of use: strong / weak / none / unobserved`.
   - The product states empirical observability, never value verdicts ("avoidable", "unnecessary").
   - Unattributed residual tokens form an explicit block and are never arbitrarily distributed into other buckets.
5. **Skill Utilisation Safeguard (D16)**:
   - Skill utilisation signals are assigned low confidence and ranked last per D6, serving to motivate progressive disclosure without driving premature skill deletion.
6. **Materialized Findings with Version Stamp (D17)**:
   - Findings are materialized in `canon.db` at build time, stamped with a `detector_version` to force deterministic recomputation whenever detection algorithms change.

## Alternatives Considered

- **Arbitrary heuristic ranking by raw token count**: Rejected. A speculative 50,000-token prompt compression guess would outrank a verified 5,000-token duplicate schema loop, misleading developers into destructive prompt changes.
- **Categorizing uninvoked context as "waste"**: Rejected. Context presence often provides negative constraints or latent capabilities; absence of invocation does not imply lack of necessity.
- **Dynamic query-time finding evaluation**: Rejected. Materialization at session/run build time with version hashing guarantees instant UI rendering while `detector_version` prevents stale findings.

## Consequences

- The finding engine in `dash/kyber/analysis/findings.ts` enforces the contract; findings without requisite evidence or outcome-risk caveats fail test suites.
- UI components (`EvidenceTable`, `ConfidencePanel`, `RecommendationPanel`) present findings with full transparency, enabling developers to inspect the underlying telemetry.
- Recommendations focus on caching architecture and progressive loading rather than context removal.

## Related

- [ADR 0011: ASAD as the Only Context View and as the Canonical Session Contract](0011-asad-only-context-view-and-payload-contract.md)
- [ADR 0012: Progressive Disclosure Six-Level Diagnostic Spine](0012-progressive-disclosure-6-level-diagnostic-spine.md)
- [ADR 0014: Unclipped Turn Inspection and Copy-Out Protocol](0014-unclipped-turn-inspection-and-copy-out-protocol.md)
- [KyberDash architecture](../dash/architecture.md)
- [Plan: 2026-09-05 Diagnostic Hierarchy and Context Inspector](../plans/2026-09-05-kyberdash-diagnostic-hierarchy-and-context-inspector.md)
