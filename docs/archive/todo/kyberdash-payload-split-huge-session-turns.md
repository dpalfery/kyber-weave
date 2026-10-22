---
id: archive/todo/kyberdash-payload-split-huge-session-turns
title: Payload split for huge-session detail and turns
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-20
status: archived
---

# Payload split for huge-session detail and turns

> [!NOTE]
> **Closed 2026-10-20.** The split is adopted: turns live in a dedicated
> `session_turn` table — one row per turn (`session_id`, `turn_index`,
> `turn_json`, with the non-unique index `session_turn_by_session`) — and
> `session.payload` is rewritten without the `turns` key, every other top-level
> key byte-identical in its original order. The schema went to **v22** via the
> presence-guarded, idempotent, one-transaction-per-step migration, and the turn
> rows are stored **zlib-compressed at level 6** from birth; readers
> discriminate the two stored forms by SQLite's storage class, so a row written
> before the change still reads. Measured corpus: 336 sessions, 79.6 MB of
> payload, 40,621 turns; after the split the payload total is 48.3 MB and
> `session_turn` holds 40,719 rows over 9,031 pages (36,990,976 bytes).

The measured Σ turns is **40,621** across 336 session rows. A payload split would
create roughly that many turn rows — comparable to the 40,490 `records` rows — at an
estimated 1.2–1.5× more btree pages per rebuild plus per-session delete/reinsert
churn.

Feasible (there is one payload writer, `upsertSession`) but a real
write-amplification trade, and it belongs to the detail/turns path: the list path no
longer reads `payload` at all. Decide with the turn-row estimate and the measured
page cost on the table, not as an adjunct to list work.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 1).

## Acceptance criteria

- [x] Adopted: turns live in `session_turn`, `payload` is metadata-only, schema
  v22 — with the measured corpus above as the recorded trade
  (compression is the offset against the estimated page growth and
  delete/reinsert churn).
