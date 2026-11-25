---
id: archive/todo/factory-file-source-even-token-split
title: The Factory file source splits a session's tokens evenly across its assistant calls
doc-type: todo
component: KyberDash
status: archived
owner: dpalfery
last-reviewed: 2026-11-25
documentation-impact: extends-component
impact-components: [KyberDash]
---

# The Factory file source splits a session's tokens evenly across its assistant calls

Closed 2026-11-25: the even split is removed, and the question this todo raised is decided by
KyberDash's no-estimate rule.

The Factory file source used to divide the session-level `settings.tokenUsage` evenly across the
session's assistant calls and price every call at the session's latest model. That produced
apportioned per-call figures that looked measured. Decision D1 of the
[Droid tokens and per-harness end-to-end plan](../plans/2026-11-24-droid-tokens-and-per-harness-e2e.md)
settled it:

- The session totals are a session fact, stored once as session usage. No per-call or per-turn
  token figure is derived from them; Droid per-call tokens are **not measured**, and the session
  view shows the not-measurable state instead of bars.
- The rule is KyberDash-wide: it never estimates the data it collects and displays
  ([honest unobservability](../../rules/honest-unobservability.md#hard-rule-kyberdash-never-estimates)).
- The 2026-11-24 amendment to
  [ADR 0055](../../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md) withdraws the "known lossy
  approximation" that the original Context and Consequences accepted.
- Where the data lives today is in [telemetry inventory](../../dash/telemetry-inventory.md) (the Droid
  file-source row).

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| SPLIT-1 | Should the Factory file source keep dividing a session's `settings.tokenUsage` evenly across its assistant calls? | No. The even split is removed. The session totals are stored once as session usage; Droid per-call and per-turn tokens are not measured, and the session view shows the not-measurable state instead of bars. | KyberDash never estimates the data it collects and displays. An even split is an apportioned figure that looks measured, which the no-estimate rule bans. The 2026-11-24 amendment to ADR 0055 withdraws the "known lossy approximation" the original decision accepted. | ANSWERED — decision D1 of the Droid tokens and per-harness end-to-end plan; rule: honest unobservability |

## Closeout mapping

Links are written from this archive entry's location (`docs/archive/todo/`).

| ID | Destination |
|---|---|
| SPLIT-1 | [dash/architecture.md](../../dash/architecture.md#the-no-estimate-rule) (a split of a session total across calls or turns is an estimate; per-call and per-turn figures stay not measured) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) (the Droid file-source row; the Droid, Cursor and Warp mappers no longer split or guess tokens). |
| H1 | [dash/architecture.md](../../dash/architecture.md#the-no-estimate-rule), [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) |
| H2 | [honest unobservability](../../rules/honest-unobservability.md#hard-rule-kyberdash-never-estimates) |
| H3 | [ADR 0055](../../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md) |

## Design harvest

| ID | Durable item | Target component | Target doc type / location |
|---|---|---|---|
| H1 | A split of a session total across calls or turns is an estimate; Droid per-call and per-turn tokens are not measured and the session total is stored once as session usage | KyberDash | ARCHITECTURE `docs/dash/architecture.md` (the no-estimate rule); REFERENCE `docs/dash/telemetry-inventory.md` (the Droid file-source row) |
| H2 | The rule that KyberDash never estimates what it collects and displays | KyberDash | RULE `docs/rules/honest-unobservability.md` |
| H3 | The withdrawal of the "known lossy approximation" the original Factory capture decision accepted | KyberDash | ADR `docs/adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md` (2026-11-24 amendment) |
