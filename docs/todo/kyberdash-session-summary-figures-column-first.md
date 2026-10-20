---
id: todo/kyberdash-session-summary-figures-column-first
title: sessionSummaryFigures column-first conversion, both copies together
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-20
---

# sessionSummaryFigures column-first conversion, both copies together

`CanonStore.sessionSummaryFigures` (`dash/src/canon/store.ts`) and the duplicate copy of
the same payload-extraction SQL in the bridge's direct-DB path
(`KyberBridge.sessionSummaryFigures`, `dash/src/server/bridge.ts`) still read figures out
of `payload` with `json_extract`. Convert both to the materialized `summary_json` column
(schema 21) in one task.

This is a multi-row read over a bounded, caller-supplied id set (deduplicated, chunked at
900 placeholders, `WHERE session_id IN (…)`), not a table scan — an optimization, not a
hazard removal. Converting only the store method would leave two implementations of one
reader that could disagree, so both copies convert together or not at all.

Merge semantics to preserve: a missing figure stays **omitted**, not zero;
`costStatus`/`costCurrency` are taken only when they are strings; non-finite numbers are
dropped (see the `figure()` guards and `pricedUsd` in both copies).

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 7,
decision D21 item 1), which deliberately left this reader column-unconverted.

## Acceptance criteria

- Both copies read `summary_json` first with the same per-row payload fallback the list
  reader uses, and serve identical figures for materialized and legacy rows.
- The omit-not-zero merge semantics and the `pricedUsd` currency gating are unchanged.
