---
id: todo/kyberdash-count-unknown-window-sessions-expression-cost
title: countUnknownWindowSessions payload-expression cost
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-20
---

# countUnknownWindowSessions payload-expression cost

`CanonStore.countUnknownWindowSessions` (`dash/src/canon/store.ts`) and its bridge
mirror (`dash/src/server/bridge.ts`) run `COUNT(*)` with
`json_extract(payload, '$.context.contextLimitSource')` in the `WHERE` clause. The count
is aggregate-only, but it still pages every scanned payload blob — table-wide when no
harness is given.

Follow-up: an expression index or a materialized `contextLimitSource` column. Lower
priority, because the bridge prefers the persisted rollups and only falls back to this
scan on the coverage-failure path.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 9,
decision D21 item 4).

## Acceptance criteria

- The unscoped (no-harness) count no longer pages payload blobs, while returning the
  same counts for materialized and legacy rows.
- The harness-scoped behavior and the rollup-preferred bridge path are unchanged.
