---
id: archive/todo/kyberdash-count-unknown-window-sessions-expression-cost
title: countUnknownWindowSessions payload-expression cost
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-20
status: archived
---

# countUnknownWindowSessions payload-expression cost

> [!NOTE]
> **Closed 2026-10-20.** `countUnknownWindowSessions` no longer filters on
> `json_extract(payload,'$.context.contextLimitSource')`; it reads the new
> `session.context_limit_source` column, falling back to the payload expression
> only while the column is absent. Both copies are converted — the store's and
> the bridge's direct-DB path — and the write path materializes the column on
> every write, so a fresh corpus is not under-counted. A row with a NULL source
> is **not** counted, matching the old `NULL IN ('default','absent')` behavior.

`CanonStore.countUnknownWindowSessions` (`dash/src/canon/store.ts`) and its bridge
mirror (`dash/src/server/bridge.ts`) run `COUNT(*)` with
`json_extract(payload, '$.context.contextLimitSource')` in the `WHERE` clause. The count
is aggregate-only, but it still pages every payload blob — table-wide when no
harness is given.

Follow-up: an expression index or a materialized `contextLimitSource` column. Lower
priority, because the bridge prefers the persisted rollups and only falls back to this
scan on the coverage-failure path.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 9,
decision D21 item 4).

## Acceptance criteria

- [x] The unscoped (no-harness) count reads the materialized column first — no
  payload-blob paging on the hot path — with identical counts for materialized
  and legacy rows.
- [x] The harness-scoped behavior and the rollup-preferred bridge path are
  unchanged.
