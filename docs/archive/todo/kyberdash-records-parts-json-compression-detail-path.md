---
id: archive/todo/kyberdash-records-parts-json-compression-detail-path
title: records.parts_json BLOB compression for the detail and digest path
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-20
status: archived
---

# records.parts_json BLOB compression for the detail and digest path

> [!NOTE]
> **Closed 2026-10-20.** `records.parts_json` is stored compressed and read
> back through a tolerant codec, and content that does not benefit from
> compression is stored as-is; the new `session_turn.turn_json` is compressed
> from birth. Measured on 40,621 realistic turns, level 6 stores 7.82 MB
> against 15.31 MB plain (0.51×), at ~364 ms of deflate and ~146 ms of inflate
> for a full-corpus read. The "does not pay" rule stores a value as plain text
> when its deflated form is not smaller — the measured boundary is ~74 bytes.
> The list path is untouched: it reads no `records` column.

Candidate follow-up for the **detail/digest** path only: compress
`records.parts_json` as a BLOB, mirroring the existing `compressRaw` pattern on
`raw`/`parts` (measured precedent: `records` I/O 79 → 6.1 MB per full refresh at
+330 ms refresh cost).

Explicitly **not** for the list path: compression leaves the JSON-parse CPU floor in
place and contradicts the list goal, which the `session_denorm_idx` covering index
already meets.

Provenance: follow-up of the sessions-list payload-independence work
(`docs/plans/2026-10-19-kyberdash-sessions-list-payload-independence.md`, §12 item 6).

## Acceptance criteria

- [x] Adopted with the measured I/O and refresh numbers above, and the list
  path untouched.
