---
id: adr/0030-kyberdash-declared-context-window-provenance
title: Declared Context Windows as Their Own Provenance, with Derived Pressure and Inferred Compaction Findings
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-07
---

# ADR 0030: Declared Context Windows as Their Own Provenance, with Derived Pressure and Inferred Compaction Findings

## Status

Accepted, 2026-10-07. Records decisions D5 and D12 of the KyberDash context-capture plan. Extends
[ADR 0013](0013-telemetry-grounded-finding-contracts-and-waste-ranking.md), whose ranking rule
(decision 2, D6) this record applies.

## Context

Context pressure is the share of the context window a turn fills. Its denominator is the window
size, and very few harnesses report that in per-turn telemetry. Without a window, pressure is
`not_measurable`, and so is the compaction-hazard finding that depends on it.

Some harnesses declare a window in their own local configuration. Pi's `models-store.json`
carries a `contextWindow` per model, and a statusline payload can carry `context_window_size`.
The harness itself uses that number to decide when to compact, but it is a configured value, not
a measurement of the turn. A configuration that is stale or wrong produces a confident-looking
percentage that is wrong.

The other source would be a model catalog that maps model ids to published windows. A catalog
is not the harness's own view. It needs maintaining as models change, and it guesses which
variant and tier a session actually ran under.

KyberDash already keeps two vocabularies apart. Availability (`measured`, `derived`,
`not_measurable`) says where a figure came from. Measurement class (`deterministic`, `inferred`,
`coverage-gap`, ADR 0013 decision 1) says how much a finding can be trusted.

## Decision

1. **A window a harness declares in its own local config counts.** Examples are Pi
   `models-store.json` `contextWindow` and a statusline `context_window_size`.
2. **A declared window is stored with its own provenance, distinct from per-turn telemetry.** A
   consumer can always tell a declared window from a window reported for the turn.
3. **Pressure computed against a declared window is `derived`, never `measured`.**
4. **There is no model catalog.** A window is never looked up from a model id.
5. **The compaction-hazard finding fires against a declared window, with measurement class
   `inferred` and a "declared window" caveat.** Under ADR 0013 decision 2 (D6), it therefore
   ranks below deterministic findings of comparable volume.

## Alternatives Considered

- **Treat a declared window as measured.** Rejected. It is configuration, not observation. A
  stale entry would produce pressure figures and findings that look as trustworthy as counters
  the provider billed.
- **A model catalog of published windows.** Rejected. It is a second source of truth that has to
  track every model release. It also cannot know the tier, variant or harness override the
  session actually used.
- **Ignore declared windows and leave pressure `not_measurable`.** Rejected. It discards the one
  number the harness itself uses to decide when to compact, and leaves compaction hazards
  invisible for every harness that does not report a window per turn.
- **Fire the compaction finding as `deterministic` when a window is declared.** Rejected. The
  finding is only as good as the denominator, and ADR 0013 decision 2 exists so that inferred
  findings cannot outrank verified ones.

## Consequences

- The store gains a window source that is not per-turn telemetry, with provenance that every
  consumer has to carry through to the figure it renders.
- Pressure figures can appear for harnesses that had none. Each one is labelled `derived`, and
  the compaction finding carries its caveat, so the gain in coverage does not read as a gain in
  certainty.
- A wrong declared window yields wrong pressure. KyberDash cannot detect that, because it has
  nothing to check the declaration against.
- Harnesses that declare no window stay `not_measurable` for pressure. Adding a catalog later
  would need a new record superseding decision 4.

## Related

- [ADR 0013: Telemetry-Grounded Finding Contracts, Waste Ranking, and Relocation Discipline](0013-telemetry-grounded-finding-contracts-and-waste-ranking.md)
  — the ranking rule decision 5 applies
- [ADR 0011: ASAD as the Only Context View and as the Canonical Session Contract](0011-asad-only-context-view-and-payload-contract.md)
  — `not_measurable` reasons rather than zeros
- [ADR 0029: Per-Bucket, OTel-First Content Precedence](0029-kyberdash-per-bucket-otel-first-content-precedence.md)
- [KyberDash architecture](../dash/architecture.md)
