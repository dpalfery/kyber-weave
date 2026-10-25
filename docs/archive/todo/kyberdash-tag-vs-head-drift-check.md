---
id: archive/todo/kyberdash-tag-vs-head-drift-check
title: Tag-vs-HEAD drift check of bridge and routes against 0.1.8-rc.2
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-20
status: archived
---

# Tag-vs-HEAD drift check of bridge and routes against 0.1.8-rc.2

> [!NOTE]
> **Closed 2026-10-20.** The diff was run and its answer is a finding, not a
> port list: the installed `0.1.8-rc.2` build is **not behind HEAD in any way
> that needs porting**. `v0.1.8-rc.2` is commit `b303d420` (#388), built 71
> minutes later as a Node SEA. The detail path (`getSessionPayload`, `_clip`,
> and the detail route handler) is byte-identical between the installed build
> and HEAD; every difference in `bridge.ts`/`routes.ts` is HEAD-only and
> additive. Store-level compatibility holds both ways: HEAD's writer populates
> the materialized columns, the tag's reader ignores them and derives from the
> payload, and HEAD's reader falls back per column when they are NULL — so a
> store last written by the tag still reads correctly, and self-heals on the
> next HEAD projection. Two operational facts ride with this finding: **the
> running engine is still the old build**, so the live sessions list still pays
> the old whole-corpus payload cost (measured 17.61 s cold on a copy of the
> same corpus, where HEAD's reader measures 0.86 s cold / 62 ms warm) — and
> **the server and the client ship together**, because the HEAD web bundle must
> not be served by the `0.1.8-rc.2` engine (its ancestry lookup would 404; the
> old engine serves its own embedded old web asset, so the old pair is
> self-consistent).

Behavior of `dash/src/server/bridge.ts` and `dash/src/server/routes.ts` matched the
installed `0.1.8-rc.2` build on every measured path during the sessions-list work, but the
tag-vs-HEAD diff had never been run. It was run, and reconciled as above: no behavioral
drift, only additive HEAD-side differences with no porting owner needed.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 4).

## Acceptance criteria

- [x] The diff is run and its result recorded: no behavioral drift — a finding with
  the version, build, and compatibility facts above, and no divergences needing owners.
