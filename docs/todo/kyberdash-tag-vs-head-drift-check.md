---
id: todo/kyberdash-tag-vs-head-drift-check
title: Tag-vs-HEAD drift check of bridge and routes against 0.1.8-rc.2
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-20
---

# Tag-vs-HEAD drift check of bridge and routes against 0.1.8-rc.2

Behavior of `dash/src/server/bridge.ts` and `dash/src/server/routes.ts` matched the
installed `0.1.8-rc.2` build on every measured path during the sessions-list work, but
the tag-vs-HEAD diff was never run. Run it and reconcile any drift — in particular
any list-reader or summary-route difference that would change what the deployed
binary serves.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 4).

## Acceptance criteria

- The diff is run and its result recorded: either no behavioral drift, or a
  dispositioned list of divergences with owners.
