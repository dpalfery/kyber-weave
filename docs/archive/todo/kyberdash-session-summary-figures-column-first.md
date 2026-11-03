---
id: archive/todo/kyberdash-session-summary-figures-column-first
title: sessionSummaryFigures column-first conversion, both copies together
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-20
status: archived
---

# sessionSummaryFigures column-first conversion, both copies together

> [!NOTE]
> **Closed 2026-10-20.** The figures reader now reads the materialized
> `summary_json` column instead of extracting from the `payload` blob, in both
> copies at once: `CanonStore.sessionSummaryFigures` (`dash/src/canon/store.ts`)
> and the bridge's direct-DB copy (`KyberBridge.sessionSummaryFigures`,
> `dash/src/server/bridge.ts`) now share one implementation,
> `readSessionSummaryFigures`, in the canon layer — so there is one reader, not
> two copies that could disagree. A legacy row (`summary_json` NULL) falls back
> to a payload read scoped to the rows the column could not serve. Merge
> semantics preserved: a missing figure is omitted, not zeroed;
> `costStatus`/`costCurrency` are taken only when they are strings; non-finite
> numbers are dropped.

`CanonStore.sessionSummaryFigures` (`dash/src/canon/store.ts`) and the duplicate copy of
the same payload-extraction SQL in the bridge's direct-DB path
(`KyberBridge.sessionSummaryFigures`, `dash/src/server/bridge.ts`) read their figures out of
the materialized `summary_json` column (schema 21) through the single shared
`readSessionSummaryFigures` implementation, in one task.

This is a multi-row read over a bounded, caller-supplied id set (deduplicated, chunked at
900 placeholders, `WHERE session_id IN (…)`), not a table scan — an optimization, not a
hazard removal. Converting only the store method would have left two implementations of one
reader that could disagree, so both copies converted together.

Merge semantics preserved: a missing figure stays **omitted**, not zero;
`costStatus`/`costCurrency` are taken only when they are strings; non-finite numbers are
dropped (see the `figure()` guards and `pricedUsd` in the shared implementation).

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 7,
decision D21 item 1), which deliberately left this reader column-unconverted.

## Acceptance criteria

- [x] Both copies read `summary_json` first with the same per-row payload fallback the list
  reader uses, and serve identical figures for materialized and legacy rows.
- [x] The omit-not-zero merge semantics and the `pricedUsd` currency gating are unchanged.
