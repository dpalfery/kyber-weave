---
id: todo/canon-records-raw-never-purged
title: records.raw is never purged, and a backfill re-derives content from it
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# records.raw is never purged, and a backfill re-derives content from it

The content-retention rules reach the derived content columns and nothing else. The purge empties
`content_json` and `parts_json` on records older than 14 days and leaves the row
(`dash/src/canon/retention.ts`, `dash/src/canon/store.ts`, `purgeContentBefore`). `records.raw` —
the compressed original attribute map — is never deleted, by the rule in
[ADR 0008](../adr/0008-kyberdash-single-canonical-store.md): the corpus is repairable from
retained raw payloads, and a backfill demonstrated it.

That makes the raw payload a second store of the same content, with a different lifetime. The
backfill command re-derives canonical content for every record from its stored raw payload
(`dash/src/tools/backfill.ts`, `backfillContent`), so:

- Any row that already holds content remains a content store, because the raw payload still holds
  what the content columns held.
- A strip applied at ingest is not retroactive. A row whose content was purged still has its
  content in the raw payload, and a backfill would put that content back into the content columns.
- The 14-day window bounds the content columns, not the content itself.

**What a decision must settle.** Whether raw should be retained, and for how long.

- Keep raw indefinitely, as ADR 0008 does, and say so where a reader would look: that the
  retention window bounds the derived columns, that the raw payload holds the same content for
  longer, and that a backfill can restore it. The honest statement is that content has a longer
  life than the retention window suggests.
- Keep raw for a bounded window of its own, and let the backfill's repairability argument lapse
  with it. The corpus then stops being repairable from raw once the window passes, which is the
  property ADR 0008 gave up.
- Keep raw but strip it at ingest, so the payload holds the shape of what arrived rather than its
  content. That changes what a backfill can recover, so it changes the ADR's consequence as well
  as its decision.

Whichever is chosen, the ingest-side strip and the retention rule have to agree: a strip at ingest
that leaves the raw payload intact does not shorten the content's life, and a documentation
statement that says content is purged after 14 days is wrong while the raw payload still holds it.

**Deliberately not done here.** The retention rules and the raw payload were not changed. The
retention rule is shared with every harness, the raw payload is the corpus's repairability
mechanism, and the two are ADR 0008's decision rather than a Factory one. This todo records the
consequence so a reader of the 14-day window is not misled about what it bounds.
