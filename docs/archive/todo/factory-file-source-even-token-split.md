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
  ([honest unobservability](../rules/honest-unobservability.md#hard-rule-kyberdash-never-estimates)).
- The 2026-11-24 amendment to
  [ADR 0055](../adr/0055-factory-capture-is-a-hooks-plugin-pipeline.md) withdraws the "known lossy
  approximation" that the original Context and Consequences accepted.
- Where the data lives today is in [telemetry inventory](../dash/telemetry-inventory.md) (the Droid
  file-source row).
