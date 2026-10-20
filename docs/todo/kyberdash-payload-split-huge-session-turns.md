---
id: todo/kyberdash-payload-split-huge-session-turns
title: Payload split for huge-session detail and turns
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-20
---

# Payload split for huge-session detail and turns

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

- The split is adopted or declined with the measured write-amplification trade
  (page growth per rebuild, delete/reinsert churn) recorded.
